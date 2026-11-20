using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class BudgetFixture
{
    // Each target is visited at most once, so each changed target pair independently
    // counts exactly one processed element. A finite budget exhausts each of the 526
    // providers partway through its 2,114-element range.
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

    internal static void Run(Jobs jobs, NativeWorker candidate)
    {
        if (jobs.Baseline == null) throw new ArgumentException("--budget requires an earlier mod as baseline.");
        using var pass = new Pass(Create());
        pass.Execute(jobs.Vanilla);
        pass.Check();
        byte[] expected = pass.Outputs();
        var floats = MemoryMarshal.Cast<byte,float>(expected);
        int observed = 0;
        for (int i = 0; i < floats.Length; i += 2)
            if (floats[i] > 0 && floats[i + 1] > 0) observed++;
        if (observed != 510220) throw new InvalidDataException("Unexpected vanilla processed count: " + observed);
        var workers = new[] { jobs.Vanilla, jobs.Baseline, jobs.Candidate };
        foreach (var worker in workers)
        {
            pass.Reset();
            pass.Execute(worker);
            pass.Check();
            if (!pass.Outputs().AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("Budget-limited output differed from vanilla.");
        }
        var actual = candidate.LastWorkload();
        if (actual != (Providers, Available, observed, Available))
            throw new InvalidDataException($"Native workload counters {actual} did not match the independently counted {observed} writes.");
        Console.WriteLine($"BUDGET,providers={actual.Providers},available={actual.Available},processed={actual.Processed},reserved={actual.Reserved},changed-vanilla-targets={observed}");
        NativeTimings.Compare("Budget-limited park", 526, Available, workers, worker =>
        {
            pass.Reset();
            long start = Stopwatch.GetTimestamp();
            pass.Execute(worker);
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        });
    }
}
