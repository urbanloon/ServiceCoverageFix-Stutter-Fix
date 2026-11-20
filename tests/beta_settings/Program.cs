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
var flags = new[] {"HousingSearch", "ResourceSellerSearch", "WaterFlow", "RouteSearch"}.Select(n => settings.GetProperty(n)!).ToArray();
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
void Set(object o, int mask) { for (int i=0;i<flags.Length;i++) flags[i].SetValue(o, (mask & (1<<i))!=0); }
object? Save(object source, object? previous){ var result=diff.Invoke(null,new[]{source,defaults,previous,previous,Type.Missing}); if(result==null)return null;
    var text=(string)toJson.Invoke(result, toJson.GetParameters().Select(_=>(object)Type.Missing).ToArray())!; return load.Invoke(null,new object[]{text}); }
for(int old=0;old<16;old++){
    Set(live, old);var previous=Save(live,null);
    for(int mask=0;mask<16;mask++){
        Set(live, mask);var encoded=Save(live,previous);var next=Activator.CreateInstance(settings,owner)!;
        if(encoded!=null)write.Invoke(null,new object[]{encoded,next});
        for(int i=0;i<4;i++)if((bool)flags[i].GetValue(next)! != ((mask&(1<<i))!=0))throw new Exception($"Persistence mismatch {old}->{mask}, {flags[i].Name}");
        if(!ReferenceEquals(registry[Id(next)],next))throw new Exception("Live registry entry was displaced on load");
    }
}
Console.WriteLine("PASS real game settings registry and Disk JSON round trip, all 256 previous-to-current toggle combinations");
