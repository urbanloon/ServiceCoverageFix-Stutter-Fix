using System.Diagnostics;
using System.Text.Json;
class Program
{
    static void Main(string[] args)
    {
        int seed = int.Parse(args[0]), rounds = int.Parse(args[1]); string arch = args[2]; string[] names = args[3..]; string baseDir = Environment.GetEnvironmentVariable("SELECTOR_WORKERS") ?? throw new ArgumentException("Set SELECTOR_WORKERS to the worker directories described in README.md.");
        NativeWorker Load(string name) { var dir = Path.Combine(baseDir, name, arch); using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "worker.json"))); return new NativeWorker(Path.Combine(dir, "coverage.so"), json.RootElement.GetProperty("symbol").GetString()!, name != "vanilla"); }
        var oracle = Load("vanilla"); var workers = names.Select(Load).ToArray(); var random = new Random(seed); var order = Enumerable.Range(0, names.Length).ToArray();
        foreach (var (n, p, sh) in new[] { (510220, 526, "inter"), (1111964, 526, "budget"), (510220, 526, "ties"), (510220, 526, "richinter"), (510220, 526, "mixed"), (510220, 526, "runs"), (32000, 64, "mixed"), (32000, 64, "richinter"), (32000, 64, "runs"), (32000, 64, "ties"), (8192, 16, "mixed"), (4096, 32, "inter"), (128, 1, "inter") })
        {
            var input = sh == "budget" ? Create() : sh == "inter" || sh == "ties" ? Inputs.Make(seed, p, n / p, sh == "ties" ? 1 : 0, true) : Corpus(seed, p, n, sh);
            using var pass = new Pass(input); pass.Execute(oracle.Execute); pass.Check(); var expected = pass.Outputs();
            (int, int, int, int)? counts = null;
            foreach (var w in workers) { pass.Reset(); pass.Execute(w.Execute); pass.Check(); if (!pass.Outputs().AsSpan().SequenceEqual(expected)) throw new Exception($"oracle mismatch: {names[Array.IndexOf(workers, w)]} {p} {sh}"); var c = w.LastWorkload(); if (counts != null && counts != c) throw new Exception("counters differ"); counts = c; }
            var batch = Enumerable.Range(0, Math.Clamp(4096 / n, 1, 32)).Select(_ => new Pass(input)).ToArray();
            for (int round = -2; round < rounds; round++) { if (workers.Length != 2 || (round & 1) == 0) random.Shuffle(order); else Array.Reverse(order); foreach (int i in order) { foreach (var item in batch) item.Reset(); var s = Stopwatch.GetTimestamp(); foreach (var item in batch) item.Execute(workers[i].Execute); var ms = Stopwatch.GetElapsedTime(s).TotalMilliseconds / batch.Length; if (round >= 0) Console.WriteLine($"{seed},{p},{sh},{round},{names[i]},{ms:F7}"); } }
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
                if (shape == "ties") key = MathF.Floor(key * 64) / 64;
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
