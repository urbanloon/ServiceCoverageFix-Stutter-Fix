internal static class WorkloadOracle
{
    internal static void Verify(Jobs jobs, string oraclePath)
    {
        var worker = (NativeWorker)jobs.Candidate.Target!;
        using var oracle = new StreamReader(oraclePath);
        int cases = 0;
        long visited = 0;
        foreach (var (label, input) in AllCases.Create())
        {
            var expected = oracle.ReadLine()!.Split(',');
            if (expected[0] != label) throw new InvalidDataException("Input/oracle order changed");
            int count = int.Parse(expected[1]);
            using var pass = new Pass(input);
            pass.Execute(jobs.Vanilla);
            pass.Check();
            var target = pass.Outputs();
            pass.Reset();
            pass.Execute(jobs.Candidate);
            pass.Check();
            if (!pass.Outputs().AsSpan().SequenceEqual(target)) throw new Exception($"target mismatch {label} {cases}");
            var active = input.Providers.Where(p => p.Count != 0 && !(p.Remaining <= 0f)).ToArray();
            var counts = (active.Length, active.Sum(p => p.Count), count, input.Elements.Length);
            if (worker.LastWorkload() != counts) throw new Exception($"counter mismatch {label} {cases} observed {worker.LastWorkload()} expected {counts}");
            visited += count;
            cases++;
        }
        if (!oracle.EndOfStream) throw new Exception("Unused oracle rows");
        Console.WriteLine($"PASS {cases} native workloads. All four counters matched input metadata and {visited} independently instrumented stock visits. Native target bytes, input elements and guards matched.");
    }
}
