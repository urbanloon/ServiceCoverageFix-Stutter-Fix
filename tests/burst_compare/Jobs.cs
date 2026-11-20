using System.Runtime.InteropServices;

internal sealed class Jobs
{
    internal Action<nint, nint> Vanilla { get; }
    internal Action<nint, nint> Candidate { get; }
    internal Action<nint, nint>? Baseline { get; }

    internal Jobs(string managedDirectory, string candidate, string? baseline)
    {
        var workers = new VerifiedWorkers(managedDirectory);
        string? mode = Environment.GetEnvironmentVariable("CS2_BURST_DENORMAL_MODE");
        if (mode != null)
        {
            int flags = mode switch {
                "preserve" => 0, "daz" => 0x40, "ftz" => 0x8000, "daz-ftz" => 0x8040,
                _ => throw new ArgumentException("Unknown CS2_BURST_DENORMAL_MODE: " + mode)
            };
            if (SetDenormalFlags(flags) != flags) throw new InvalidOperationException("Could not set MXCSR.");
            Console.WriteLine("Worker denormal mode: " + mode);
        }
        string vanilla = Environment.GetEnvironmentVariable("CS2_BURST_VANILLA") ??
            throw new ArgumentException("Set CS2_BURST_VANILLA to the compiled vanilla worker's directory.");
        Vanilla = workers.Load(vanilla, "vanilla").Execute;
        Candidate = workers.Load(candidate, "mod").Execute;
        if (baseline != null) Baseline = workers.Load(baseline, null).Execute;
        string? oraclePath = Environment.GetEnvironmentVariable("CS2_BURST_WORKLOAD_ORACLE");
        if (oraclePath != null) WorkloadOracle.Verify(this, oraclePath);
        if (Environment.GetCommandLineArgs().Contains("--shapes")) ShapeTimings.Run(this);
        if (Environment.GetCommandLineArgs().Contains("--budget"))
            BudgetFixture.Run(this, (NativeWorker)Candidate.Target!);
        if (Environment.GetCommandLineArgs().Contains("--three-way"))
            NativeTimings.Run(this);
    }

    [DllImport("scf_test_mxcsr", EntryPoint = "set_denormal_flags")]
    private static extern int SetDenormalFlags(int flags);
}
