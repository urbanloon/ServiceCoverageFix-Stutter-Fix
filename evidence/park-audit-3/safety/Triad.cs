internal static class Triad
{
    internal static void Verify(Jobs jobs)
    {
        var baseline = jobs.Baseline ?? throw new ArgumentException("Specify a 0.3.6 worker for comparison.");
        int cases = 0;
        long processed = 0;
        foreach (var (label, input) in AllCases.Create())
        {
            using var pass = new Pass(input);
            pass.Execute(jobs.Vanilla);
            pass.Check();
            var expected = pass.Outputs();
            pass.Reset();
            pass.Execute(baseline);
            pass.Check();
            if (!pass.Outputs().AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"0.3.6/stock output {label} #{cases}");
            var counts = ((NativeWorker)baseline.Target!).LastWorkload();
            pass.Reset();
            pass.Execute(jobs.Candidate);
            pass.Check();
            if (!pass.Outputs().AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"Candidate/stock output {label} #{cases}");
            var observed = ((NativeWorker)jobs.Candidate.Target!).LastWorkload();
            if (counts != observed)
                throw new InvalidDataException($"Candidate/0.3.6 workload {label} #{cases}: {counts} {observed}");
            cases++;
            processed += observed.Processed;
        }
        Console.WriteLine($"PASS {cases} triads, {processed} visits reported. Stock, 0.3.6 and candidate destination bytes, guard bytes, input elements, and both mods' workload counters matched.");
    }
}
