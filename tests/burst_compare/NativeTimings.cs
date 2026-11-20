
internal static class NativeTimings
{
    internal static void Run(Jobs jobs)
    {
        if (jobs.Baseline == null) throw new ArgumentException("--three-way requires a previous mod.");
        var workers = new[] { jobs.Vanilla, jobs.Baseline, jobs.Candidate };
        foreach (var (name, providers, length, mode, repetitions) in new[] {
            ("Single",1,32,0,3000), ("Two providers",2,32,0,1500), ("Four providers",4,32,0,1000),
            ("Light",12,64,0,200), ("Mixed",49,128,0,30), ("Park interleaved",526,970,0,1),
            ("Park dense ties",526,970,1,1), ("Twice the park providers",1052,970,0,1) })
        {
            if (Environment.GetCommandLineArgs().Contains("--park-only") && providers != 526) continue;
            var input = Inputs.Make(351, providers, length, mode, true);
            using var pass = new Pass(input);
            pass.Execute(jobs.Vanilla);
            pass.Check();
            byte[] expected = pass.Outputs();
            foreach (var other in new[] { jobs.Baseline, jobs.Candidate })
            {
                pass.Reset();
                pass.Execute(other);
                pass.Check();
                if (!pass.Outputs().AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException("Native mismatch on timing workload: " + name);
            }
            using var group = new PassGroup(input, repetitions);
            Compare(name, providers, input.Elements.Length, workers, group.Timed);
        }
    }

    internal static void Compare(string name, int providers, int available, Action<nint,nint>[] workers,
        Func<Action<nint,nint>, double> time)
    {
        var orders = new[] { new[] {0,1,2}, new[] {1,2,0}, new[] {2,0,1},
            new[] {2,1,0}, new[] {1,0,2}, new[] {0,2,1} };
        var samples = new List<double>[] { new(), new(), new() };
        var sim = new List<double>();
        var preview = new List<double>();
        for (int round = -2; round < 24; round++)
        {
            var ms = new double[3];
            foreach (int index in orders[(round + 6) % 6]) ms[index] = time(workers[index]);
            if (round < 0) continue;
            for (int i = 0; i < samples.Length; i++) samples[i].Add(ms[i]);
            sim.Add(ms[1] / ms[2]);
            preview.Add(ms[0] / ms[2]);
            Console.WriteLine($"SAMPLE,{name},{round},{ms[0]:F8},{ms[1]:F8},{ms[2]:F8}");
        }
        static double Median(List<double> values)
        {
            values.Sort();
            return (values[11] + values[12]) / 2;
        }
        double simMedian = Median(sim), previewMedian = Median(preview);
        Console.WriteLine($"THREE_WAY,{name},{providers},{available},{Median(samples[0]):F6},{Median(samples[1]):F6},{Median(samples[2]):F6},{simMedian:F4},{sim[2]:F4},{sim[21]:F4},{previewMedian:F4},{preview[2]:F4},{preview[21]:F4}");
    }
}
