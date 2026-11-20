using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Stamp { public long Seconds, Nanos; }

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clock, out Stamp value);

    private static long ThreadNanoseconds()
    {
        if (ClockGetTime(3, out var stamp) != 0)
            throw new InvalidOperationException("clock_gettime failed");
        return stamp.Seconds * 1_000_000_000L + stamp.Nanos;
    }

    private static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        int seed = int.Parse(args[0]);
        int rounds = int.Parse(args[1]);
        string architecture = args[2];
        string[] names = args[3..];
        string directory = Environment.GetEnvironmentVariable("SELECTOR_WORKERS")
            ?? throw new ArgumentException("Set SELECTOR_WORKERS to the worker directory.");

        NativeWorker Load(string name)
        {
            string dir = Path.Combine(directory, name, architecture);
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "worker.json")));
            return new NativeWorker(Path.Combine(dir, "coverage.so"),
                metadata.RootElement.GetProperty("symbol").GetString()!, name != "vanilla");
        }

        var oracle = Load("vanilla");
        var workers = names.Select(Load).ToArray();
        var random = new Random(seed);
        var order = Enumerable.Range(0, names.Length).ToArray();
        var shapes = new[]
        {
            (510220, 526, "inter"), (1111964, 526, "budget"), (510220, 526, "ties"),
            (510220, 526, "richinter"), (510220, 526, "mixed"), (510220, 526, "runs"),
            (32000, 64, "mixed"), (32000, 64, "richinter"), (32000, 64, "runs"),
            (32000, 64, "ties"), (8192, 16, "mixed"), (4096, 32, "inter"), (128, 1, "inter")
        };
        if (Environment.GetEnvironmentVariable("SCREEN_SAVE") == "1")
        {
            shapes = (from p in new[] { 49, 524, 533, 1154 }
                      from shape in new[] { "richinter", "mixed", "runs", "quantized" }
                      select (p * 970, p, shape)).ToArray();
        }
        bool roadProbe = Environment.GetEnvironmentVariable("SCREEN_ROAD") == "1";
        if (roadProbe)
            shapes = new[] { "global", "local", "ties", "tight" }.Select(s => (1111964, 526, s)).ToArray();
        bool countSwitches = Environment.GetEnvironmentVariable("COUNT_SWITCHES") == "1";
        foreach (var (count, providers, shape) in shapes)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("SCREEN_MIN_N"), out int min) && count < min)
                continue;
            if (countSwitches && providers <= 16) continue;
            var input = roadProbe ? RoadProbe.Make(seed, shape) : shape switch
            {
                "budget" => Create(),
                "inter" or "ties" => Inputs.Make(seed, providers, count / providers, shape == "ties" ? 1 : 0, true),
                _ => Corpus(seed, providers, count, shape)
            };
            using var oraclePass = new Pass(input);
            oraclePass.Execute(oracle.Execute);
            oraclePass.Check();
            var expected = oraclePass.Outputs();
            (int, int, int, int)? counts = null;
            foreach (var worker in workers)
            {
                oraclePass.Reset();
                oraclePass.Execute(worker.Execute);
                oraclePass.Check();
                if (!oraclePass.Outputs().AsSpan().SequenceEqual(expected))
                    throw new InvalidOperationException($"Stock target mismatch at {providers}/{shape}.");
                var actual = worker.LastWorkload();
                if (counts != null && counts != actual)
                    throw new InvalidOperationException("Workload counters differ.");
                counts = actual;
                if (countSwitches)
                    Console.WriteLine($"{seed},{providers},{shape},{actual.Processed},{actual.Reserved},{actual.Available}");
            }
            if (countSwitches) continue;

            var batch = Enumerable.Range(0, Math.Clamp(4096 / count, 1, 32)).Select(_ => new Pass(input)).ToArray();
            for (int round = -2; round < rounds; round++)
            {
                if (workers.Length != 2 || (round & 1) == 0) random.Shuffle(order);
                else Array.Reverse(order);
                foreach (int index in order)
                {
                    foreach (var item in batch) item.Reset();
                    long cpuStart = ThreadNanoseconds();
                    long start = Stopwatch.GetTimestamp();
                    foreach (var item in batch) item.Execute(workers[index].Execute);
                    double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds / batch.Length;
                    double cpuMs = (ThreadNanoseconds() - cpuStart) * .000001 / batch.Length;
                    if (round >= 0)
                        Console.WriteLine($"{seed},{providers},{shape},{round},{names[index]},{ms:F7},{cpuMs:F7}");
                }
            }
            foreach (var item in batch) item.Dispose();
        }
    }

    private const int Providers = 526;
    private const int PerProvider = 2114;
    private const int Available = Providers * PerProvider;

    private static Inputs Create()
    {
        var input = Inputs.Make(1878, Providers, PerProvider, 0, true);
        for (int i = 0; i < Providers; i++)
            input.Providers[i].Total = input.Providers[i].Remaining = 746.25f;
        for (int i = 0; i < Available; i++)
        {
            ref var element = ref input.Elements[i];
            element.X = element.Y = element.Priority = 0.5f + 0.5f * element.Priority;
            element.Density = element.Length = 1f;
            // This multiplier is coprime to Available and permutes all target indices.
            element.Target = (nint)(((long)i * 15485863 + 3121) % Available);
        }
        return new Inputs(input.Providers, input.Elements, new float[2 * Available]);
    }

    private static Inputs Corpus(int seed, int providerCount, int count, string shape)
    {
        var random = new Random(seed);
        var providers = new Provider[providerCount];
        var elements = new Element[count];
        int targets = Math.Max(8, count / 7);
        for (int i = 0; i < providers.Length; i++)
        {
            int start = (int)((long)count * i / providerCount);
            int end = (int)((long)count * (i + 1) / providerCount);
            providers[i] = new Provider { Entity = i, Start = start, Count = end - start, Total = 10000, Remaining = 10000 };
            float offset = shape == "mixed" ? random.NextSingle() * .8f : shape == "runs" ? i * .9f / providerCount : 0;
            float span = shape == "mixed" ? .2f : shape == "runs" ? .013f : 1;
            for (int j = start; j < end; j++)
            {
                float key = offset + random.NextSingle() * span;
                if (shape == "quantized" || shape == "ties") key = MathF.Floor(key * 64) / 64;
                float value = .01f + Math.Min(.98f, key);
                float bias = (random.NextSingle() - .5f) * .1f * value;
                float x = value - bias, y = value + bias;
                elements[j] = new Element
                {
                    Target = random.Next(targets),
                    X = x,
                    Y = y,
                    Priority = (x + y) * .5f,
                    Density = random.Next(2) == 0 ? .5f : 1f,
                    Length = .1f + random.NextSingle() * 4
                };
            }
            Array.Sort(elements, start, end - start,
                Comparer<Element>.Create((left, right) => right.Priority.CompareTo(left.Priority)));
        }
        random.Shuffle(providers);
        return new Inputs(providers, elements, new float[targets * 2]);
    }
}
