using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

unsafe class Program
{
    // Mirrors Game.Simulation.ServiceCoverageSystem/CoverageElement's x64 field offsets.
    [StructLayout(LayoutKind.Sequential)]
    struct Element
    {
        public long Id;
        public float X, Y, Priority, Density, Length;
        public int Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Descriptor : IComparable<Descriptor>
    {
        public float Priority;
        public int Source;
        public int CompareTo(Descriptor other) =>
            Priority == other.Priority ? 0 : Priority < other.Priority ? 1 : -1;
    }

    delegate void ActualSort(Element* first, int count);
    static ActualSort vanilla;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void NativePass(Element* data, int* starts, int providers, Descriptor* keys, Element* ordered, int method);
    static NativePass burst;
    static ActualSort BindSort(string managed)
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            string path = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
        };
        var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "Game.dll"));
        var system = game.GetType("Game.Simulation.ServiceCoverageSystem", true);
        var element = system.GetNestedType("CoverageElement", BindingFlags.NonPublic | BindingFlags.Public);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        foreach (var job in new[]{"ClearCoverageJob", "PrepareCoverageJob", "ProcessCoverageJob", "CoverageElement"})
        {
            var type = system.GetNestedType(job, BindingFlags.NonPublic | BindingFlags.Public);
            Console.Error.WriteLine($"{job} {string.Join(" ", type.GetFields(flags).Select(f => $"{f.Name}@{Marshal.OffsetOf(type, f.Name)}"))}");
        }
        if (Marshal.SizeOf(element) != sizeof(Element) || (int)Marshal.OffsetOf(element, "m_AverageCoverage") != 16)
            throw new Exception("CoverageElement ABI mismatch");
        var sort = typeof(Program).GetMethod(nameof(SortGameRange), BindingFlags.Static | BindingFlags.NonPublic);
        foreach (var dll in new[] {game.Location, typeof(NativeSortExtension).Assembly.Location})
            Console.Error.WriteLine($"{Path.GetFileName(dll)} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant()}");
        return sort.MakeGenericMethod(element).CreateDelegate<ActualSort>();
    }

    static void SortGameRange<T>(Element* first, int count) where T : unmanaged, IComparable<T>
    {
        // ProcessCoverageJob sorts a borrowed NativeArray subrange.
        NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(first, count, Allocator.None).Sort();
    }

    enum Method { Vanilla, StrictGuard, CompactCopy, CompactCycle, UniqueRadix }
    static Method[] methodsInRun = {Method.Vanilla, Method.StrictGuard, Method.CompactCopy, Method.CompactCycle};
    static void RunSort(Method method, Element* first, int n, Descriptor* descriptors, Element* ordered)
    {
        if (n < 2) return;
        if (method == Method.Vanilla)
        {
            vanilla(first, n);
            return;
        }
        if (method == Method.StrictGuard)
        {
            // Strict descending only. Unity's partition can permute equal elements.
            for (int i = 1; i < n; i++)
            {
                if (!(first[i - 1].Priority > first[i].Priority))
                {
                    vanilla(first, n);
                    break;
                }
            }
            return;
        }
        for (int i = 0; i < n; i++)
            descriptors[i] = new Descriptor { Priority = first[i].Priority, Source = i };
        NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<Descriptor>(descriptors, n, Allocator.None).Sort();
        if (method == Method.CompactCopy)
        {
            for (int i = 0; i < n; i++)
                ordered[i] = first[descriptors[i].Source];
            new ReadOnlySpan<Element>(ordered, n).CopyTo(new Span<Element>(first, n));
        }
        else
        {
            // Apply p[destination] = source in-place, closing each disjoint cycle once.
            for (int i = 0; i < n; i++)
            {
                if (descriptors[i].Source == i) continue;
                Element original = first[i];
                int dest = i;
                int source;
                while ((source = descriptors[dest].Source) != i)
                {
                    first[dest] = first[source];
                    descriptors[dest].Source = dest;
                    dest = source;
                }
                first[dest] = original;
                descriptors[dest].Source = dest;
            }
        }
    }

    static Element[] Input(int n, int[] starts, string shape, int seed)
    {
        var data = new Element[n];
        var random = new Random(seed);
        for (int p = 0; p + 1 < starts.Length; p++)
        {
            int count = starts[p + 1] - starts[p];
            int[] ranks = Enumerable.Range(0, count).Reverse().ToArray();
            if (shape == "shuffled" || shape == "shuffled-ties" || shape == "single-tie" || shape == "fractional") random.Shuffle(ranks);
            if (shape == "nearly-sorted")
                for (int j = 0; j < Math.Max(1, count / 100); j++)
                {
                    int a = random.Next(count), b = random.Next(count);
                    (ranks[a], ranks[b]) = (ranks[b], ranks[a]);
                }
            if (shape == "single-tie" && count > 1) ranks[count-1] = ranks[0];
            for (int i = 0; i < count; i++)
            {
                int rank = ranks[i];
                float key = shape switch
                {
                    "ascending" => i,
                    "descending-ties" or "shuffled-ties" => rank / 12,
                    "all-equal" => 1,
                    "fractional" => 1.0f - MathF.Pow((rank+1f)/(count+1f), 2f),
                    _ => rank
                };
                data[starts[p] + i] = new Element { Id = starts[p] + i + 1, Priority = key,
                    X = rank * 0.37f, Y = i * 0.13f, Length = i % 11, Density = rank % 6,
                    Padding = random.Next() };
            }
        }
        return data;
    }

    static int[] Starts(int count, int elements)
    {
        var starts = new int[count + 1];
        var r = new Random(7709);
        var weights = Enumerable.Range(0, count).Select(_ => 1 + r.NextDouble() * 4).ToArray();
        double total = weights.Sum(), cumulative = 0;
        for (int i = 1; i <= count; i++)
        {
            cumulative += weights[i - 1];
            starts[i] = (int)Math.Round(elements * cumulative / total);
        }
        starts[count] = elements;
        return starts;
    }

    static long Pass(Method method, Element[] work, int[] starts, Descriptor[] keys, Element[] dest, bool oracle = false)
    {
        fixed (Element* rows = work, ordered = dest)
        fixed (Descriptor* descriptors = keys)
        fixed (int* offsets = starts)
        {
            long before = Stopwatch.GetTimestamp();
            if (burst != null && !oracle)
                burst(rows, offsets, starts.Length - 1, descriptors, ordered, (int)method);
            else
                for (int i = 0; i < starts.Length - 1; i++)
                    RunSort(method, rows + starts[i], starts[i + 1] - starts[i], descriptors, ordered);
            return Stopwatch.GetTimestamp() - before;
        }
    }

    static bool Same(Element[] left, Element[] right) =>
        MemoryMarshal.AsBytes(left.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(right.AsSpan()));

    static void CheckOddValues()
    {
        var random = new Random(7320);
        uint[] values = {0, 0x80000000, 1, 0x80000001, 0x7f800000, 0xff800000,
            0x7fc00023, 0xffc01234, 0x3f800000, 0xbf800000};
        for (int n = 0; n <= 160; n++)
        {
            int[] starts = {0,n};
            var input = new Element[n];
            for (int i = 0; i < n; i++)
                input[i] = new Element { Id = i, Priority = BitConverter.UInt32BitsToSingle(values[random.Next(values.Length)]), Padding = random.Next() };
            var expected = (Element[])input.Clone();
            var keys = new Descriptor[n];
            var dest = new Element[n];
            Pass(Method.Vanilla, expected, starts, keys, dest, oracle: true);
            foreach (var method in methodsInRun)
            {
                var work = (Element[])input.Clone();
                Pass(method, work, starts, keys, dest);
                if (!Same(work, expected)) throw new Exception($"Nonfinite/edge mismatch: {method}, n={n}");
            }
        }
        var monotoneTies = Input(17, new[]{0,17}, "all-equal", 1);
        var after = (Element[])monotoneTies.Clone();
        Pass(Method.Vanilla, after, new[]{0,17}, new Descriptor[17], new Element[17]);
        Console.Error.WriteLine($"17 equal priorities, input IDs 1..17; vanilla IDs {string.Join(",",after.Select(e=>e.Id))}");
        Console.Error.WriteLine("161 exact edge cases passed");
    }

    static void CheckFiniteKeys()
    {
        var random = new Random(12731);
        for (int trial = 0; trial < 200; trial++)
        {
            int count = 17 + random.Next(4096);
            var keys = new Descriptor[count];
            var scratch = new Element[count];
            var input = new Element[count];
            int[] starts = {0, count};
            var used = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                uint bits;
                do bits = (uint)random.NextInt64(1L << 32);
                while ((bits & 0x7f800000) == 0x7f800000 || !used.Add(bits));
                input[i] = new Element { Id = i, Priority = BitConverter.UInt32BitsToSingle(bits), Padding = random.Next() };
            }
            if (trial % 4 == 1) input[count-1].Priority = input[0].Priority;
            if (trial % 4 == 2) {input[count-1].Priority = 0f; input[0].Priority = BitConverter.UInt32BitsToSingle(0x80000000);}
            if (trial % 4 == 3) {input[count-1].Priority = float.PositiveInfinity; input[0].Priority = float.NegativeInfinity;}
            var expected = (Element[])input.Clone();
            Pass(Method.Vanilla, expected, starts, keys, scratch, oracle: true);
            foreach (var method in methodsInRun)
            {
                var work = (Element[])input.Clone();
                Pass(method, work, starts, keys, scratch);
                if (!Same(work, expected)) throw new Exception($"Bitwise mismatch, {method} in mixed-sign trial {trial}");
            }
        }
        Console.Error.WriteLine("200 mixed-sign finite / single-tie / infinity differential trials passed");
    }

    static void Main(string[] args)
    {
        string managed = Path.GetFullPath(args[0]);
        int providers = int.Parse(args[1]), elements = int.Parse(args[2]), rounds = int.Parse(args[3]);
        vanilla = BindSort(managed);
        if (args.Length > 4)
        {
            string lib = Path.GetFullPath(args[4]);
            string export = File.ReadAllText(Path.Combine(Path.GetDirectoryName(lib), "export.txt")).Trim();
            burst = Marshal.GetDelegateForFunctionPointer<NativePass>(NativeLibrary.GetExport(NativeLibrary.Load(lib), export));
            Console.Error.WriteLine($"Burst native pass {export} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(lib))).ToLowerInvariant()}");
        }
        bool radix = args.Length > 5 && args[5] == "radix";
        if (radix && burst == null) throw new Exception("Radix needs the native library");
        if (radix) methodsInRun = new[]{ Method.Vanilla, Method.UniqueRadix };
        bool verify = args.Length > 5 && args[5] == "verify";
        if (verify && burst != null) methodsInRun = Enum.GetValues<Method>();
        CheckOddValues();
        if (verify) { CheckFiniteKeys(); return; }
        var starts = Starts(providers, elements);
        int max = starts.Zip(starts.Skip(1), (a,b) => b-a).Max();
        var keys = new Descriptor[max];
        var dest = new Element[max];
        Console.WriteLine("providers,elements,shape,round,method,ms");
        string[] shapes = {"shuffled","shuffled-ties","descending","descending-ties","nearly-sorted","ascending","all-equal"};
        if (radix) shapes = shapes.Concat(new[]{"single-tie","fractional"}).ToArray();
        foreach (string shape in shapes)
        {
            var input = Input(elements, starts, shape, 9217);
            var expected = (Element[])input.Clone();
            var work = (Element[])input.Clone();
            Pass(Method.Vanilla, expected, starts, keys, dest, oracle: true);
            var methods = (Method[])methodsInRun.Clone();
            var random = new Random(48492);
            for (int r = -1; r < rounds; r++)
            {
                random.Shuffle(methods);
                foreach (var method in methods)
                {
                    Array.Copy(input, work, elements);
                    var ticks = Pass(method, work, starts, keys, dest);
                    if (!Same(work, expected)) throw new Exception($"Full-byte mismatch {method} {shape} {r}");
                    if (r >= 0) Console.WriteLine(FormattableString.Invariant($"{providers},{elements},{shape},{r},{method},{ticks*1000.0/Stopwatch.Frequency:F6}"));
                }
            }
        }
    }
}
