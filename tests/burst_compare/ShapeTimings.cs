using System.Diagnostics;

internal static class ShapeTimings
{
    internal static void Run(Jobs jobs)
    {
        var oracle = jobs.Vanilla;
        var previous = jobs.Baseline ?? throw new ArgumentException("--shapes requires the previous mod.");
        bool small = Environment.GetCommandLineArgs().Contains("--small-shapes");
        var implementations = small
            ? new[] { previous, jobs.Candidate, jobs.Vanilla }
            : new[] { previous, jobs.Candidate };
        int seed = int.Parse(Environment.GetEnvironmentVariable("CS2_SHAPE_SEED") ?? "371");
        int rounds = int.Parse(Environment.GetEnvironmentVariable("CS2_SHAPE_ROUNDS") ?? "23");
        var order = Enumerable.Range(0, implementations.Length).ToArray();
        var random = new Random(seed);
        Console.WriteLine("SHAPE,seed,providers,elements,shape,round,candidate,ns");
        var sizes = small
            ? new[] { (1, 32), (2, 8), (2, 128), (4, 32), (4, 1024), (5, 80), (8, 256),
                      (8, 8192), (12, 768), (16, 1024), (16, 8192) }
            : new[] { (2, 8), (2, 128), (4, 32), (8, 256), (8, 8192), (16, 1024),
                      (64, 32000), (421, 427319), (526, 510220) };
        if (Environment.GetEnvironmentVariable("CS2_SHAPE_SAVE_SCALE") == "1")
            sizes = new[] { (49, 49 * 970), (524, 524 * 970), (533, 533 * 970), (1154, 1154 * 970) };
        foreach (var (p, n) in sizes)
        {
            foreach (string shape in new[] { "interleaved", "ties", "mixed", "runs" })
            {
                Inputs input = Corpus(seed, p, n, shape);
                using var reference = new Pass(input);
                reference.Execute(oracle);
                reference.Check();
                byte[] expected = reference.Outputs();
                int batchSize = Math.Clamp(10000 / n, 1, 256);
                var batch = Enumerable.Range(0, batchSize).Select(_ => new Pass(input)).ToArray();
                try
                {
                    foreach (var job in implementations)
                    {
                        reference.Reset();
                        reference.Execute(job);
                        reference.Check();
                        if (!reference.Outputs().AsSpan().SequenceEqual(expected))
                            throw new InvalidOperationException($"Coverage diverged for {p}/{n}/{shape}.");
                        for (int warmup = 0; warmup < 3; warmup++) Time(job, batch);
                    }
                    for (int round = 0; round < rounds; round++)
                    {
                        random.Shuffle(order);
                        foreach (int index in order)
                        {
                            double elapsed = Time(implementations[index], batch);
                            Console.WriteLine($"SHAPE,{seed},{p},{n},{shape},{round},{index},{elapsed:F1}");
                        }
                    }
                    foreach (var pass in batch) pass.Check();
                }
                finally
                {
                    foreach (var pass in batch) pass.Dispose();
                }
            }
        }
    }

    private static double Time(Action<nint, nint> job, Pass[] batch)
    {
        foreach (var pass in batch) pass.Reset();
        long before = Stopwatch.GetTimestamp();
        foreach (var pass in batch) pass.Execute(job);
        return Stopwatch.GetElapsedTime(before).TotalNanoseconds / batch.Length;
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
                elements[j] = new Element { Target = random.Next(targets), X = x, Y = y,
                    Priority = (x + y) * .5f, Density = random.Next(2) == 0 ? .5f : 1f,
                    Length = .1f + random.NextSingle() * 4 };
            }
            Array.Sort(elements, start, end - start,
                Comparer<Element>.Create((left, right) => right.Priority.CompareTo(left.Priority)));
        }
        random.Shuffle(providers);
        return new Inputs(providers, elements, new float[targets * 2]);
    }
}
