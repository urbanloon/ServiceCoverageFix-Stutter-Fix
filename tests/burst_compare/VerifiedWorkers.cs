using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed class VerifiedWorkers
{
    private readonly Assembly game;
    private readonly string gameHash;
    private string? target;

    internal VerifiedWorkers(string managedDirectory)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !Sse42.IsSupported)
            throw new PlatformNotSupportedException("Use Linux x64 with SSE4.2 for this fixture.");
        string managed = Path.GetFullPath(managedDirectory);
        string gameFile = Path.Combine(managed, "Game.dll");
        game = AssemblyLoadContext.Default.LoadFromAssemblyPath(gameFile);
        gameHash = Digest(gameFile);
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            string file = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
    }

    internal NativeWorker Load(string directory, string? expectedKind)
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
        return new NativeWorker(binary, Value("symbol"), mod);
    }

    private static string Digest(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
}
