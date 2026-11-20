using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

// Use the same diff, JSON, and write-into APIs as the game's SettingAsset and AssetDatabase.
if (args.Length != 2) throw new ArgumentException("BetaSettings <game Managed directory> <candidate DLL>");
var managed = Path.GetFullPath(args[0]);
var context = AssemblyLoadContext.Default;
context.Resolving += (_, name) => {
    var path = Path.Combine(managed, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
var mod = context.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
var game = Assembly.Load("Game");
var core = Assembly.Load("Colossal.Core");
var settings = mod.GetType("ServiceCoverageFix.ServiceCoverageStutterFixSettings", true)!;
var owner = Activator.CreateInstance(mod.GetType("ServiceCoverageFix.Mod", true)!)!;
var oldFlags = new[] { "HousingSearch", "ResourceSellerSearch", "WaterFlow", "RouteSearch", "CompanyPropertySearch" };
var flags = oldFlags.Skip(2).Select(name => settings.GetProperty(name)!).ToArray();
if (oldFlags.Take(2).Any(name => settings.GetProperty(name) != null))
    throw new Exception("A retired option can still be set.");
var defaults = Activator.CreateInstance(settings, owner)!;
var live = Activator.CreateInstance(settings, owner)!;
var registry = (IDictionary)game.GetType("Game.Modding.ModSetting", true)!.GetProperty("instances", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
object Id(object o) => settings.GetProperty("id")!.GetValue(o)!;
if (!ReferenceEquals(registry[Id(live)], live) || !settings.CustomAttributes.Any(a => a.AttributeType.Name == "FileLocationAttribute")) throw new Exception("Persisted settings owner or file location mismatch");
var diff = core.GetType("Colossal.Json.DiffUtility", true)!.GetMethod("DiffWithPersistent")!;
var json = core.GetType("Colossal.Json.JSON", true)!;
var load = json.GetMethod("Load", new[]{typeof(string)})!;
var write = json.GetMethods().Single(m => m.Name == "WriteInto" && m.IsGenericMethod && m.GetParameters()[1].ParameterType.IsByRef).MakeGenericMethod(typeof(object));
var variant = core.GetType("Colossal.Json.Variant", true)!;
var toJson = variant.GetMethods().First(m => m.Name == "ToJSON" && m.GetParameters().All(p => p.IsOptional));
void Set(object instance, int mask)
{
    for (int i = 0; i < flags.Length; i++) flags[i].SetValue(instance, (mask & (1 << i)) != 0);
}

void Check(object instance, int mask)
{
    for (int i = 0; i < flags.Length; i++)
        if ((bool)flags[i].GetValue(instance)! != ((mask & (1 << i)) != 0))
            throw new Exception($"Persistence mismatch at mask {mask}, {flags[i].Name}");
    if (!ReferenceEquals(registry[Id(instance)], instance))
        throw new Exception("Live registry entry was displaced on load.");
}

object Restore(object? encoded)
{
    var next = Activator.CreateInstance(settings, owner)!;
    if (encoded != null) write.Invoke(null, new[] { encoded, next });
    return next;
}

object? Save(object source, object? previous)
{
    object? result = diff.Invoke(null, new[] { source, defaults, previous, previous, Type.Missing });
    if (result == null) return null;
    string text = (string)toJson.Invoke(result, toJson.GetParameters().Select(_ => (object)Type.Missing).ToArray())!;
    return load.Invoke(null, new object[] { text });
}

for (int old = 0; old < 32; old++)
{
    var legacy = new Dictionary<string, bool>();
    for (int i = 0; i < oldFlags.Length; i++)
        if ((old & (1 << i)) != 0) legacy.Add(oldFlags[i], true);
    var previous = load.Invoke(null, new object[] { System.Text.Json.JsonSerializer.Serialize(legacy) });
    var migrated = Restore(previous);
    Check(migrated, old >> 2);
    for (int mask = 0; mask < 8; mask++)
    {
        Set(migrated, mask);
        Check(Restore(Save(migrated, previous)), mask);
    }
}
for (int old = 0; old < 8; old++)
{
    Set(live, old);
    object? previous = Save(live, null);
    for (int mask = 0; mask < 8; mask++)
    {
        Set(live, mask);
        Check(Restore(Save(live, previous)), mask);
    }
}
Console.WriteLine("PASS: real game JSON loaded all 32 legacy settings masks, preserved the three supported values, and round-tripped all 256 migrated and 64 current setting changes.");
