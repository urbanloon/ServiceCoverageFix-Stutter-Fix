using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed class Jobs
{
    internal Action<nint, nint> Vanilla { get; }
    internal Action<nint, nint> Candidate { get; }
    internal Action<nint, nint>? Baseline { get; }

    internal Jobs(string managedDirectory, string candidate, string? baseline)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !Sse42.IsSupported)
            throw new PlatformNotSupportedException("Use Linux x64 with SSE4.2 for this fixture.");
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
        string managed = Path.GetFullPath(managedDirectory);
        string gameFile = Path.Combine(managed, "Game.dll");
        var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(gameFile);
        string gameHash = Digest(gameFile);
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            string file = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };

        string? target = null;
        Action<nint, nint> Load(string directory, string? expectedKind)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "worker.json")));
            string Value(string key) => manifest.RootElement.GetProperty(key).GetString()!;
            string kind = Value("kind");
            bool mod = kind == "mod";
            target ??= Value("target");
            if (Value("target") != target || (target == "AVX2" && !Avx2.IsSupported))
                throw new InvalidDataException("Native workers and CPU target must match.");
            string source = Value("assembly");
            string binary = Path.GetFullPath(Path.Combine(directory, "coverage.so"));
            if ((kind != "vanilla" && !mod) || (expectedKind != null && expectedKind != kind) ||
                Value("platform") != "Linux" || (target != "X64_SSE4" && target != "AVX2") ||
                Value("sourceSha256") != Digest(source) || Value("binarySha256") != Digest(binary) ||
                Value("gameSha256") != gameHash)
                throw new InvalidDataException("Worker manifest or input hashes do not match: " + directory);
            Assembly assembly = game;
            if (mod)
            {
                var context = new AssemblyLoadContext(directory);
                context.Resolving += (_, name) => AssemblyLoadContext.Default.LoadFromAssemblyName(name);
                assembly = context.LoadFromAssemblyPath(source);
            }
            Type job = assembly.GetType(mod ? "ServiceCoverageFix.OptimizedApplyCoverageJob" :
                "Game.Simulation.ServiceCoverageSystem+ApplyCoverageJob", true)!;
            NativeWorker.ValidateLayout(job, mod);
            return new NativeWorker(binary, Value("symbol"), mod).Execute;
        }

        string vanilla = Environment.GetEnvironmentVariable("CS2_BURST_VANILLA") ??
            throw new ArgumentException("Set CS2_BURST_VANILLA to the compiled vanilla worker's directory.");
        Vanilla = Load(vanilla, "vanilla");
        Candidate = Load(candidate, "mod");
        if (baseline != null) Baseline = Load(baseline, null);
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

    private static string Digest(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
}
