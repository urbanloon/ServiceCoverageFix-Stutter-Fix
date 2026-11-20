using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

internal static class Program
{
    private const int RoadCount = 6128;
    private const int Providers = 526;
    private const int VisitsPerProvider = 2114;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec { public long Seconds, Nanoseconds; }

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clock, out Timespec time);

    // CLOCK_THREAD_CPUTIME_ID on Linux x64, the host for this native fixture.
    private static long ThreadNanoseconds()
    {
        if (ClockGetTime(3, out var time) != 0) throw new InvalidOperationException("clock_gettime failed.");
        return time.Seconds * 1_000_000_000L + time.Nanoseconds;
    }

    // Counts come from a saved city and a separate live observation. The
    // specific road lists, road layout, budgets and priorities are synthetic.
    private static Inputs Make(int seed, string shape)
    {
        var rng = new Random(seed);
        var providers = new Provider[Providers];
        var elements = new Element[Providers * VisitsPerProvider];
        const int slots = 9;
        const int park = 3;
        string layout = Environment.GetEnvironmentVariable("PARK_OVERLAP_LAYOUT") ?? "packed";
        var offsets = new int[RoadCount];
        int used = 0;
        var layoutRng = new Random(seed ^ 0x4ac1);
        for (int i = 0; i < RoadCount; ++i)
        {
            offsets[i] = used;
            used += slots + (layout switch { "packed" => 0, "scattered" => layoutRng.Next(3, 126),
                _ => throw new ArgumentException("Unknown PARK_OVERLAP_LAYOUT") });
        }
        if (layout == "scattered") layoutRng.Shuffle(offsets.AsSpan());
        var targets = new float[2 * used];
        Array.Fill(targets, -0.321f); // Adjacent slots must survive unchanged.
        for (int r = 0; r < RoadCount; ++r)
            Array.Clear(targets, 2 * (offsets[r] + park), 2);

        int window = shape == "local" ? 2560 : RoadCount;
        var choices = new int[window];
        for (int i = 0; i < Providers; ++i)
        {
            int start = i * VisitsPerProvider;
            // The final scenario forces retirement, even in a crowded city.
            bool tight = shape == "tight" || (shape == "mixed" && i % 2 == 0);
            float budget = tight ? 0.25f + rng.NextSingle() : 200f + rng.NextSingle() * 800f;
            providers[i] = new Provider { Entity = i, Start = start, Count = VisitsPerProvider,
                Total = budget, Remaining = budget };
            int anchor = rng.Next(RoadCount);
            for (int j = 0; j < window; ++j) choices[j] = j;
            rng.Shuffle(choices.AsSpan());
            for (int j = 0; j < VisitsPerProvider; ++j)
            {
                int road = (anchor + choices[j]) % RoadCount;
                float value = shape == "ties" ? rng.Next(1, 17) : 0.05f + 20f * rng.NextSingle();
                float skew = shape == "ties" ? 0f : 0.04f * rng.NextSingle();
                float x = value + skew, y = value - skew;
                elements[start + j] = new Element { Target = offsets[road] + park,
                    X = x, Y = y, Priority = (x + y) * 0.5f,
                    Density = j % 3 == 0 ? 1f : 0.1f, Length = 1f + 16f * rng.NextSingle() };
            }
            Array.Sort(elements, start, VisitsPerProvider,
                Comparer<Element>.Create((a,b) => b.Priority.CompareTo(a.Priority)));
        }
        return new Inputs(providers, elements, targets);
    }

    // rounds seed comma-separated-shapes vanilla=WORKER_DIR v036=WORKER_DIR [candidate=WORKER_DIR ...]
    private static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        int rounds = int.Parse(args[0]), seed = int.Parse(args[1]);
        var verified = new VerifiedWorkers(Environment.GetEnvironmentVariable("GAME_MANAGED")
            ?? throw new ArgumentException("Set GAME_MANAGED to the game reference directory."));
        var workers = args.Skip(3).Select((arg, index) =>
        {
            var pair = arg.Split('=', 2);
            bool mod = index > 0;
            return (label: pair[0], job: verified.Load(pair[1], mod ? "mod" : "vanilla"), mod);
        }).ToArray();
        if (workers.Length < 2 || workers[0].mod || !workers.Skip(1).All(w => w.mod))
            throw new ArgumentException("Supply vanilla first, then the baseline and candidates.");
        var timed = Environment.GetEnvironmentVariable("PARK_TIME_STOCK") == "0" ? workers[1..] : workers;
        Console.WriteLine("seed,shape,providers,available,processed,round,worker,wall_ms,thread_ms");
        foreach (var shape in args[2].Split(','))
        {
            if (shape is not ("local" or "citywide" or "ties" or "tight" or "mixed"))
                throw new ArgumentException(shape);
            var input = Make(seed, shape);
            using var pass = new Pass(input);
            pass.Execute(workers[0].job.Execute);
            pass.Check();
            byte[] expected = pass.Outputs();
            pass.Reset();
            pass.Execute(workers[1].job.Execute);
            var work = workers[1].job.LastWorkload();
            if (work.Providers != Providers || work.Available != input.Elements.Length || work.Reserved != work.Available)
                throw new InvalidDataException("Unexpected baseline workload.");
            // Rotate the first worker each round and reverse each complete rotation.
            for (int round = -2; round < rounds; ++round)
            {
                for (int position = 0; position < timed.Length; ++position)
                {
                    int rotation = Math.Max(0, round);
                    int index = rotation / timed.Length % 2 == 0 ? position : timed.Length - 1 - position;
                    var worker = timed[(index + rotation) % timed.Length];
                    pass.Reset();
                    long cpu = ThreadNanoseconds();
                    long t = Stopwatch.GetTimestamp();
                    pass.Execute(worker.job.Execute);
                    double ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                    double cpuMs = (ThreadNanoseconds() - cpu) / 1e6;
                    if (!expected.AsSpan().SequenceEqual(pass.Outputs()) ||
                        (worker.mod && worker.job.LastWorkload() != work))
                        throw new InvalidDataException($"Mismatch in {worker.label}/{shape}.");
                    if (round == -2 || round == rounds - 1) pass.Check();
                    if (round >= 0)
                        Console.WriteLine($"{seed},{shape},{work.Providers},{work.Available},{work.Processed},{round},{worker.label},{ms:F5},{cpuMs:F5}");
                }
            }
        }
    }
}
