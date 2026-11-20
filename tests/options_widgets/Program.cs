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
var widgetInterface = game.GetType("Game.UI.Widgets.IWidget", true)!;
RecordingWriter RenderWidget(object widget)
{
    widgetInterface.GetMethod("Update")!.Invoke(widget, null);
    var writer = (RecordingWriter)DispatchProxy.Create(writerInterface, typeof(RecordingWriter));
    widgetInterface.GetMethod("WriteProperties")!.Invoke(widget, new[] {(object)writer});
    return writer;
}
RecordingWriter Render(string name) => RenderWidget(widgets[name]);
void Expect(string property, string pattern)
{
    var output = Render(property).Strings;
    if (!output.Contains(pattern))
        throw new Exception($"{property} widget did not serialize '{pattern}'. Observed {string.Join(" | ", output)}");
}
var switches = new Dictionary<string, string>
{
    { "WaterFlow", "Water flow" },
    { "RouteSearch", "Route search" },
    { "CompanyPropertySearch", "Company property search" }
};
var expected = new HashSet<string> { "ExecutionStatus", "WorkloadByService", "Refresh", "BetaCompatibility" };
expected.UnionWith(switches.Keys);
if (!expected.SetEquals(widgets.Keys))
    throw new Exception("Unexpected options menu: " + string.Join(", ", widgets.Keys));
if (widgets.Values.Count(w => w.GetType().FullName == "Game.UI.Widgets.ToggleField") != 3)
    throw new Exception("Expected exactly three toggle widgets.");

var row = widgets["Refresh"];
var children = (Array)row.GetType().GetProperty("children")!.GetValue(row)!;
if (row.GetType().FullName != "Game.UI.Widgets.ButtonRow" || children.Length != 1)
    throw new Exception("Refresh must be a game button.");
var button = children.GetValue(0)!;
if (button.GetType().FullName != "Game.UI.Widgets.Button" ||
    !RenderWidget(button).Strings.Any(key => labels.GetValueOrDefault(key) == "Refresh"))
    throw new Exception("Refresh button label is missing.");
int Captures() => (int)diagnostic.GetField("_nextVersion", all)!.GetValue(null)!;
void Press()
{
    int before = Captures();
    button.GetType().GetMethod("Invoke")!.Invoke(button, null);
    if (Captures() != before + 1) throw new Exception("A game button press must take exactly one snapshot.");
}

const string unsampledRows = "Open Diagnostics or press Refresh for a reading.";
Expect("ExecutionStatus", "Not checked yet");
Expect("WorkloadByService", unsampledRows);
if (Captures() != 0) throw new Exception("Registration captured a diagnostic snapshot.");
modType.GetField("_owner", all)!.SetValue(null, owner);
modType.GetField("_compatibilityInactiveReason", all)!.SetValue(owner, "Inactive. Widget fixture contract mismatch.");
diagnosticSettings.GetMethod("SetOptionsRegistered", all)!.Invoke(settings, new object[]{true});
var core = Assembly.Load("Colossal.Core");
var defaults = Activator.CreateInstance(settingsType, owner)!;
var diff = core.GetType("Colossal.Json.DiffUtility", true)!.GetMethod("DiffWithPersistent")!;
diff.Invoke(null, new object?[]{settings, defaults, null, null, Type.Missing});
_ = settingsType.GetProperty("ExecutionStatus")!.GetValue(settings);
_ = settingsType.GetProperty("WorkloadByService")!.GetValue(settings);
if (Captures() != 0) throw new Exception("Settings serialization consumed the first display snapshot.");
Expect("ExecutionStatus", "Inactive on this game version");
Expect("WorkloadByService", "No workloads recorded.");
if (Captures() != 1) throw new Exception("The first visible diagnostics widgets must share one snapshot.");
modType.GetField("_compatibilityInactiveReason", all)!.SetValue(owner, null);
diagnostic.GetMethod("MarkInstalled", all)!.Invoke(null, null);
diagnostic.GetMethod("StartLoadedSession", all)!.Invoke(null, new[] {owner});
// A workload-only search can render this widget before the Diagnostics tab opens.
Expect("WorkloadByService", unsampledRows);
if (Captures() != 1) throw new Exception("Rendering a workload-only search result acquired a snapshot.");
Expect("ExecutionStatus", "Waiting for a coverage update");
string emptyRows = "Latest workloads" + Environment.NewLine + "No workloads recorded.";
Expect("WorkloadByService", emptyRows);
if (Captures() != 2) throw new Exception("First display after a loaded session must take one new snapshot.");
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
simulation.GetType().GetField("ReleasedCompleted", all)!.SetValue(simulation, 1L);
for (int i = 0; i < 8; i++)
{
    Expect("ExecutionStatus", "Waiting for a coverage update");
    Expect("WorkloadByService", emptyRows);
}
if (Captures() != 2) throw new Exception("Ordinary updates sampled live diagnostics.");
Press();
Expect("ExecutionStatus", "Active");
Expect("WorkloadByService", "Latest workloads" + Environment.NewLine + "Park: 2 providers, 12 available, 7 processed");
diagnostic.GetMethod("RecordFallback", all)!.Invoke(null, new object[] {new Exception("preview-widget-test"), true});
Expect("ExecutionStatus", "Active");
diagnostic.GetMethod("RecordFallback", all)!.Invoke(null, new object[] {new Exception("simulation-widget-test"), false});
Expect("ExecutionStatus", "Active");
Press();
Expect("ExecutionStatus", "A coverage update used the game's calculation");
diagnostic.GetMethod("StartLoadedSession", all)!.Invoke(null, new[] {owner});
Expect("ExecutionStatus", "A coverage update used the game's calculation");
Expect("WorkloadByService", emptyRows);
if (Captures() != 5) throw new Exception("Incorrect capture count after repeated presses and a new load.");
Expect("BetaCompatibility", "These beta options may be incompatible with Big City Performance.");
foreach (var (key, label) in switches)
{
    var widget = Render(key);
    if (widget.Booleans["value"]) throw new Exception(key + " should default to off");
    string? Localized(string prefix) => widget.Strings
        .Where(text => text.StartsWith(prefix) && labels.ContainsKey(text)).Select(text => labels[text]).SingleOrDefault();
    if (Localized("Options.OPTION[") != label || string.IsNullOrWhiteSpace(Localized("Options.OPTION_DESCRIPTION[")))
        throw new Exception("Missing or incorrect widget text for " + key);
}
var names = switches.Keys.ToArray();
for (int mask = 0; mask < 1 << names.Length; mask++)
{
    for (int i = 0; i < names.Length; i++)
    {
        // Tick the same property proxy as the game. The live asset database saves it separately.
        var proxy = proxies[names[i]];
        proxy.GetType().GetMethod("SetValue")!.Invoke(proxy, new object[] {settings, (mask & (1 << i)) != 0});
    }
    for (int i = 0; i < names.Length; i++)
    {
        bool enabled = (mask & (1 << i)) != 0;
        if (!Equals(settingsType.GetProperty(names[i])!.GetValue(settings), enabled) ||
            Render(names[i]).Booleans["value"] != enabled)
            throw new Exception($"Widget value mismatch for {names[i]} at mask {mask}");
    }
}
settingsType.GetMethod("SetDefaults")!.Invoke(settings, null);
foreach (string key in names)
    if (Render(key).Booleans["value"]) throw new Exception(key + " stayed on after resetting defaults");
Console.WriteLine("PASS: seven game Options widgets, one capture per visible session and native Refresh button press, registration and real JSON diff skipped, frozen status and colon-separated service rows, three localized toggles in all eight combinations.");

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
