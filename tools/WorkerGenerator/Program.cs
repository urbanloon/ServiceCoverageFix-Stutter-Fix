using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

if (args.Length != 2) throw new ArgumentException("WorkerGenerator <game Managed directory> <generated source directory>");
string managed = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
string relative = Path.GetRelativePath(managed, output);
if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
    !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
    throw new ArgumentException("The generated source directory must be outside the game installation.");
string[] modules = { "Game", "Colossal.Collections", "Colossal.Core", "Colossal.Mathematics",
    "Unity.Burst", "Unity.Burst.Unsafe", "Unity.Collections", "Unity.Entities", "Unity.Mathematics", "UnityEngine.CoreModule" };
var marks = new Dictionary<string,string>();
using var combined = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
foreach (string name in modules)
{
    using var input = File.OpenRead(Path.Combine(managed, name + ".dll"));
    byte[] digest = SHA256.HashData(input);
    combined.AppendData(digest);
    if (name == "Game") marks["GameSha256"] = Convert.ToHexString(digest).ToLowerInvariant();
    input.Position = 0;
    using var pe = new PEReader(input);
    var reader = pe.GetMetadataReader();
    marks["Module." + name] = reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("D");
}
const string reviewed = "71c46419ce0821b99a23700108243a2e761c6a6660d756ce1c42013c92a48d83";
bool qualified = Convert.ToHexString(combined.GetHashAndReset()).Equals(reviewed, StringComparison.OrdinalIgnoreCase);
Directory.CreateDirectory(output);
string staging = Path.Combine(output, ".worker-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(staging);
try
{
    if (qualified)
    {
        RouteGenerator.Build(new AssemblyModel(Path.Combine(managed, "Game.dll")), staging);
        const string projectedHash = "9d2c5229ac6b2dd8a435b4d6713dcbfef0e26838d92acb26702e55714a6b1f8f";
        using var projected = File.OpenRead(Path.Combine(staging, "RouteBody.g.cs"));
        if (!Convert.ToHexString(SHA256.HashData(projected)).Equals(projectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Generated route source does not match the verified November projection.");
        foreach (string feature in new[] { "Housing", "Seller", "Route", "Water" }) marks.Add(feature, "v1");
    }
    else
    {
        RouteGenerator.Fallback(staging);
        marks.Clear();
        Console.WriteLine("Unreviewed game reference set. The four beta options will be unavailable.");
    }
    var manifest = new StringBuilder("// Generated compatibility metadata.\n");
    foreach (var entry in marks)
        manifest.AppendLine($"[assembly: System.Reflection.AssemblyMetadata(\"ServiceCoverageFix.Beta.{entry.Key}\", \"{entry.Value}\")]");
    File.WriteAllText(Path.Combine(staging, "WorkerManifest.g.cs"), manifest.ToString(), new UTF8Encoding(false));
    foreach (string file in Directory.EnumerateFiles(staging, "*.g.cs"))
    {
        string destination = Path.Combine(output, Path.GetFileName(file));
        if (!File.Exists(destination) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(destination)))
            File.Move(file, destination, true);
    }
}
finally { Directory.Delete(staging, true); }
