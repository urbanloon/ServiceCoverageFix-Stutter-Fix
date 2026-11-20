using System.Reflection;
using System.Runtime.Loader;

// Run the supplied game's settings factories, widget update and JSON writer
// against the compiled mod. No game process, world or Unity jobs are started.
if (args.Length != 2) throw new ArgumentException("OptionsWidgets <game Managed directory> <candidate DLL>");
string managed = Path.GetFullPath(args[0]);
var context = AssemblyLoadContext.Default;
context.Resolving += (_, name) =>
{
    string path = Path.Combine(managed, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
var game = context.LoadFromAssemblyPath(Path.Combine(managed, "Game.dll"));
var mod = context.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
var diagnosticSettings = mod.GetType("ServiceCoverageFix.DiagnosticSettings", true)!;
var settingsType = mod.GetType("ServiceCoverageFix.ServiceCoverageStutterFixSettings") ?? diagnosticSettings;
var modType = mod.GetType("ServiceCoverageFix.Mod", true)!;
var owner = Activator.CreateInstance(modType)!;
var settings = Activator.CreateInstance(settingsType, owner)!;
string prefix = (string)settingsType.GetProperty("id")!.GetValue(settings)!;
var automatic = game.GetType("Game.UI.Menu.AutomaticSettings", true)!;
var multiData = game.GetType("Game.UI.Widgets.MultilineTextSettingItemData", true)!;
const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
var diagnostic = mod.GetType("ServiceCoverageFix.RuntimeDiagnostics", true)!;
var localeType = mod.GetType("ServiceCoverageFix.DiagnosticLocale", true)!;
var locale = Activator.CreateInstance(localeType, all, null, new[] {settings}, null)!;
var labels = ((IEnumerable<KeyValuePair<string,string>>)localeType.GetMethod("ReadEntries")!
    .Invoke(locale, new object?[] {null, null})!).ToDictionary();
var widgets = new Dictionary<string, object>();
var proxies = new Dictionary<string, object>();
foreach (var property in settingsType.GetProperties(all).Where(p => p.DeclaringType == diagnosticSettings))
{
    var proxy = Activator.CreateInstance(automatic.GetNestedType("ProxyProperty", all)!, all, null, new object[] {property}, null)!;
    proxies.Add(property.Name, proxy);
    var kind = automatic.GetMethod("GetWidgetType", all)!.Invoke(null, new[] {proxy})!;
    bool multiline = kind.ToString()!.Contains("Multiline", StringComparison.OrdinalIgnoreCase);
    var arguments = multiline ? new[] {settings, proxy, prefix} : new[] {kind, settings, proxy, prefix};
    var data = Activator.CreateInstance(multiline ? multiData : automatic.GetNestedType("SettingItemData", all)!,
        all, null, arguments, null)!;
    widgets.Add(property.Name, data.GetType().GetMethod("GetWidget", all)!.Invoke(data, null)!);
}
var writerInterface = context.LoadFromAssemblyName(new AssemblyName("Colossal.UI.Binding"))
    .GetType("Colossal.UI.Binding.IJsonWriter", true)!;
RecordingWriter Render(string name)
{
    object widget = widgets[name];
    widget.GetType().GetMethod("Update", all)!.Invoke(widget, null);
    var writer = (RecordingWriter)DispatchProxy.Create(writerInterface, typeof(RecordingWriter));
    widget.GetType().GetMethod("WriteProperties", all)!.Invoke(widget, new[] {(object)writer});
    return writer;
}
void Expect(string property, string pattern)
{
    var output = Render(property).Strings;
    if (!output.Any(s => s.Contains(pattern)))
        throw new Exception($"{property} widget did not serialize '{pattern}'. Observed {string.Join(" | ", output)}");
}
Expect("ExecutionStatus", "Inactive");
modType.GetField("_owner", all)!.SetValue(null, owner);
modType.GetField("_compatibilityInactiveReason", all)!.SetValue(owner, "Inactive. Widget fixture contract mismatch.");
Expect("ExecutionStatus", "Inactive. Widget fixture contract mismatch.");
Expect("WorkloadByService", "Simulation replacement inactive");
modType.GetField("_compatibilityInactiveReason", all)!.SetValue(owner, null);
Expect("ExecutionCounts", "scheduled=0");
Expect("PreviewExecutionCounts", "Inactive");
Expect("PreviewWorkloadByService", "Preview replacement inactive");
Expect("WorkloadByService", "Park — Awaiting a completed pass");
diagnostic.GetMethod("MarkInstalled", all)!.Invoke(null, null);
diagnostic.GetMethod("MarkPreviewInstalled", all)!.Invoke(null, null);
Expect("ExecutionStatus", "Awaiting coverage update");
diagnostic.GetMethod("StartLoadedSession", all)!.Invoke(null, new[] {owner});
// Seed one known completed sample in the managed history. Worker publication
// and overlapping schedules are exercised separately by the runtime harness.
var simulation = diagnostic.GetField("_simulation", all)!.GetValue(null)!;
var latest = (Array)simulation.GetType().GetField("Latest", all)!.GetValue(simulation)!;
var counts = Activator.CreateInstance(mod.GetType("ServiceCoverageFix.WorkloadCounts", true)!)!;
foreach (var (name, value) in new[] {("Providers", 2), ("Available", 12), ("Processed", 7), ("Reserved", 18)})
    counts.GetType().GetField(name, all)!.SetValue(counts, value);
var sample = Activator.CreateInstance(latest.GetType().GetElementType()!)!;
sample.GetType().GetField("Submission", all)!.SetValue(sample, 4L);
sample.GetType().GetField("Workload", all)!.SetValue(sample, counts);
int park = Convert.ToInt32(Enum.Parse(game.GetType("Game.Net.CoverageService", true)!, "Park"));
latest.SetValue(sample, park);
Expect("WorkloadByService", "Since the last completed game load");
Expect("WorkloadByService", "Park — pass 4, 2 providers, 12 available, 7 processed, 18 reserved");
Expect("PreviewWorkloadByService", "Latest tool and infoview preview workloads");
diagnostic.GetMethod("RecordFallback", all)!.Invoke(null, new object[] {new Exception("simulation-widget-test"), false});
diagnostic.GetMethod("RecordFallback", all)!.Invoke(null, new object[] {new Exception("preview-widget-test"), true});
Expect("ExecutionCounts", "Fallback observed");
Expect("PreviewExecutionCounts", "fallbacks=1");
Expect("LastFailure", "simulation-widget-test");
Expect("PreviewLastFailure", "preview-widget-test");
var switches = new[] {"WaterFlow", "ResourceSellerSearch", "RouteSearch", "HousingSearch", "CompanyPropertySearch"};
if (widgets.ContainsKey("BetaStatus"))
{
    Expect("BetaStatus", "Off");
    Expect("BetaCompatibility", "These beta options may be incompatible with Big City Performance.");
    foreach (var key in switches)
    {
        var widget = Render(key);
        if (widget.Booleans["value"]) throw new Exception(key + " should default to off");
        var requiredIds = new[] {"Options.OPTION[", "Options.OPTION_DESCRIPTION["};
        foreach (var id in requiredIds)
            if (!widget.Strings.Any(text => text.StartsWith(id) && labels.TryGetValue(text, out var value) && value.Length > 0))
                throw new Exception($"Missing localized widget text for {key}: {id}");
    }
    for (int mask = 0; mask < 1 << switches.Length; mask++)
    {
        for (int index = 0; index < switches.Length; index++)
        {
            var key = switches[index];
            if (widgets[key].GetType().FullName != "Game.UI.Widgets.ToggleField")
                throw new Exception(key + " was not classified as a toggle");
            // The game's ToggleField setter first writes through this proxy, then calls
            // the live asset database asynchronously. This offline harness tests the
            // setting binding and JSON frontend readback without starting that database.
            var proxy = proxies[key];
            proxy.GetType().GetMethod("SetValue")!.Invoke(proxy, new object[] {settings, (mask & (1 << index)) != 0});
        }
        for (int index = 0; index < switches.Length; index++)
        {
            var key = switches[index];
            bool expected = (mask & (1 << index)) != 0;
            if (!Equals(settingsType.GetProperty(key)!.GetValue(settings), expected) ||
                Render(key).Booleans["value"] != expected)
                throw new Exception($"Option {key} did not round-trip using its game widget at mask {mask}");
        }
    }
    settingsType.GetMethod("SetDefaults")!.Invoke(settings, null);
    foreach (var key in switches)
        if (Render(key).Booleans["value"]) throw new Exception(key + " stayed on after resetting defaults");
    Console.WriteLine("PASS: five game toggle properties and their widget JSON matched in all 32 combinations and reset to off.");
}
Console.WriteLine($"PASS: {widgets.Count} game Options widgets. Simulation and preview diagnostics serialized the expected values.");

public class RecordingWriter : DispatchProxy
{
    public readonly List<string> Strings = new();
    public readonly Dictionary<string, bool> Booleans = new();
    private string _property = "";
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == "PropertyName" && args is [string name]) _property = name;
        if (targetMethod.Name == "Write" && args is [string text]) Strings.Add(text);
        if (targetMethod.Name == "Write" && args is [bool value]) Booleans[_property] = value;
        if (targetMethod.ReturnType == typeof(void)) return null;
        return targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}
