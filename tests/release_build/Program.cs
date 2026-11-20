using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using HarmonyLib;

if (args.Length != 2) throw new ArgumentException("ReleaseBuild <Cities2_Data/Managed> <candidate mod.dll>");
string managed = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (context, name) => {
    string path = Path.Combine(managed, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
var game = Assembly.LoadFrom(Path.Combine(managed, "Game.dll"));
var mod = Assembly.LoadFrom(Path.GetFullPath(args[1]));
Require(mod.GetName().Version == new Version(0, 4, 1, 0), "release assembly version");
var pdb = Path.ChangeExtension(mod.Location, ".pdb");
if (File.Exists(pdb))
{
    using var executable = File.OpenRead(mod.Location);
    using var pe = new PEReader(executable);
    using var symbols = File.OpenRead(pdb);
    using var provider = MetadataReaderProvider.FromPortablePdbStream(symbols);
    var reader = provider.GetMetadataReader();
    var entry = pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
    Require(new BlobContentId(reader.DebugMetadataHeader!.Id).Guid == pe.ReadCodeViewDebugDirectoryData(entry).Guid,
        "portable PDB must match release DLL");
    Console.WriteLine($"Matching release PDB, {reader.Documents.Count} documents");
}
var entities = Assembly.LoadFrom(Path.Combine(managed, "Unity.Entities.dll"));
const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var manifest = mod.GetType("ServiceCoverageFix.BetaManifest", true)!;
var featureType = mod.GetType("ServiceCoverageFix.BetaFeature", true)!;
var verify = manifest.GetMethods(all).Single(m => m.Name == "Validate" && m.GetParameters().Length == 2);
var patches = mod.GetType("ServiceCoverageFix.BetaPatches", true)!;
var enabled = patches.GetMethod("IsUsable", all)!;
var originals = (Type?[])patches.GetField("Originals", all)!.GetValue(null)!;
var registration = Reader.Read(patches.GetMethod("Validate", all)!,
    new DynamicMethod("ReadProducerRegistration", typeof(void), Type.EmptyTypes).GetILGenerator(), inspectOnly: true)
    .Where(instruction => instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method &&
        method.IsGenericMethod && method.Name == "EarlyJobInit")
    .Select(instruction => ((MethodInfo)instruction.operand).GetGenericArguments().Single()).ToArray();
var assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(game.Location))).ToLowerInvariant();
foreach (var (feature, owner, factory, sourceName, workerName) in new[] {
    ("Housing", "Game.Simulation.CitizenPathfindSetup", "SetupFindHome", "Game.Simulation.CitizenPathfindSetup+SetupFindHomeJob", "HousingJob"),
    ("Seller", "Game.Simulation.ResourcePathfindSetup", "SetupResourceSeller", "Game.Simulation.ResourcePathfindSetup+SetupResourceSellerJob", "SellerJob"),
    ("Water", "Game.Simulation.WaterPipeFlowSystem", "ScheduleFlowJob", "Game.Simulation.WaterPipeFlowJob", "OptimizedWaterFlowJob"),
    ("Route", "Game.Pathfind.PathfindQueueSystem", "ScheduleWorkerJobs", "Game.Pathfind.PathfindQueueSystem+PathfindWorkerJob", "RouteJob"),
    ("CompanyCommercial", "Game.Simulation.CommercialFindPropertySystem", "OnUpdate", "Game.Buildings.PropertyUtils+CompanyFindPropertyJob", "CompanyPropertyJob"),
    ("CompanyIndustrial", "Game.Simulation.IndustrialFindPropertySystem", "OnUpdate", "Game.Buildings.PropertyUtils+CompanyFindPropertyJob", "CompanyPropertyJob")
})
{
    var f = Enum.Parse(featureType, feature);
    verify.Invoke(null, new[] { f, assemblyHash });
    var stock = game.GetType(sourceName, true)!;
    var mirror = mod.GetType("ServiceCoverageFix." + workerName, true)!;
    Require(registration.Count(type => type == mirror) == 1, "missing concrete producer registration: " + feature);
    var sourceFields = stock.GetFields(fields);
    if (feature is "Water" or "CompanyCommercial" or "CompanyIndustrial")
    {
        var inner = mirror.GetFields(fields).Single();
        if (feature.StartsWith("Company")) Require(inner.Name == "Source", "stock company field name");
        Require(inner.FieldType == stock && Offset(inner) == 0 && Size(stock) == Size(mirror), "stock job in replacement: " + feature);
    }
    else
    {
        Require(mirror.GetFields(fields).Length == sourceFields.Length && Size(stock) == Size(mirror), "worker field count/size");
        foreach (var field in sourceFields)
        {
            var other = mirror.GetField(field.Name, fields)!;
            Require(other.FieldType == field.FieldType && Offset(field) == Offset(other) &&
                SafetyAttrs(other).SequenceEqual(SafetyAttrs(field)), "field layout/access: " + field.Name);
        }
    }
    string wrapperBytes = "";
    if (feature is "Seller" or "Housing")
    {
        var wrapper = entities.GetType("Unity.Entities.JobChunkExtensions+JobChunkWrapper`1", true)!;
        var a = wrapper.MakeGenericType(stock); var b = wrapper.MakeGenericType(mirror);
        Require(Size(a) == Size(b), "producer wrapper size");
        foreach (var field in a.GetFields(fields))
        {
            var other = b.GetField(field.Name, fields)!;
            Require((field.Name == "JobData" ? other.FieldType == mirror : other.FieldType == field.FieldType)
                && Offset(field) == Offset(other), "producer wrapper " + field.Name);
        }
        wrapperBytes = $", chunk producer wrapper {Size(b)} B";
    }

    var method = game.GetType(owner, true)!.GetMethods(all).Single(x => x.Name == factory);
    var transpiler = patches.GetMethod(feature, all)!;
    // The live path initializes this slot after Unity's native producer registration succeeds.
    // Supply the reviewed Type expressly for the offline Harmony reader/emitter test.
    originals[Convert.ToInt32(f)] = stock;
    HarmonyEmission.Check(method, transpiler, feature, "BetaSchedule");
    var il = new DynamicMethod("ReadStockSchedule", typeof(void), Type.EmptyTypes).GetILGenerator();
    foreach (var local in method.GetMethodBody()!.LocalVariables) il.DeclareLocal(local.LocalType, local.IsPinned);
    var code = Reader.Read(method, il);
    List<CodeInstruction> Transform(List<CodeInstruction> body) =>
        ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] {body})!).ToList();
    int site = code.FindIndex(i => i.operand is MethodInfo m && m.IsGenericMethod &&
        m.GetGenericArguments().FirstOrDefault() == stock && m.Name is "Schedule" or "ScheduleParallel");
    Require(site >= 0, "missing source schedule");
    var output = Transform(code.Select(i => new CodeInstruction(i)).ToList());
    Require(output.Count == code.Count && (bool)enabled.Invoke(null, new[] { f })!, "exactly one replacement");
    for (int i = 0; i < code.Count; ++i)
        Require(output[i].opcode == code[i].opcode && (i == site || Equals(output[i].operand, code[i].operand)),
            "unexpected instruction change");
    var target = (MethodInfo)output[site].operand;
    var original = (MethodInfo)code[site].operand;
    Require(target.DeclaringType!.FullName == "ServiceCoverageFix.BetaSchedule" && target.Name == feature &&
        target.ReturnType == original.ReturnType &&
        target.GetParameters().Select(p => p.ParameterType).SequenceEqual(original.GetParameters().Select(p => p.ParameterType)),
        "bridge stack signature");
    RuntimeHelpers.PrepareMethod(target.MethodHandle);
    // On a missing or ambiguous match the production transpiler must keep all stock instructions.
    foreach (bool duplicate in new[] { false, true })
    {
        var invalid = code.Select(i => new CodeInstruction(i)).ToList();
        if (duplicate) invalid.Insert(site, new CodeInstruction(code[site])); else invalid.RemoveAt(site);
        var expected = invalid.Select(i => (i.opcode, i.operand)).ToArray();
        var result = Transform(invalid);
        Require(!(bool)enabled.Invoke(null, new[] { f })! && result.Select(i => (i.opcode, i.operand)).SequenceEqual(expected), "changed caller should retain stock");
    }
    Console.WriteLine($"{feature} passed: {sourceFields.Length} stock fields, worker {Size(mirror)} B{wrapperBytes}, genuine Harmony stream and serialized bridge, JIT, two invalid callers");
}

static string[] SafetyAttrs(FieldInfo field) => field.CustomAttributes.Where(a =>
    a.AttributeType.Namespace?.StartsWith("Unity.", StringComparison.Ordinal) == true)
    .Select(a => a.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToArray();
static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
static int Size(Type t)
{
    var m = new DynamicMethod("Size", typeof(int), Type.EmptyTypes, t, true); var il = m.GetILGenerator();
    il.Emit(OpCodes.Sizeof, t); il.Emit(OpCodes.Ret); return m.CreateDelegate<Func<int>>()();
}
static int Offset(FieldInfo field)
{
    var owner = field.DeclaringType!;
    var m = new DynamicMethod("Offset", typeof(int), Type.EmptyTypes, owner, true); var il = m.GetILGenerator();
    var v = il.DeclareLocal(owner);
    il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Ldflda, field); il.Emit(OpCodes.Conv_I);
    il.Emit(OpCodes.Ldloca, v); il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_I4);
    il.Emit(OpCodes.Ret); return m.CreateDelegate<Func<int>>()();
}
