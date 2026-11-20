using System.Collections.Generic;
using Colossal;
using Game.Modding;
using Game.Settings;
using Game.UI.Localization;

namespace ServiceCoverageFix
{
    // Plain read-only strings use the game's value accessor. Its multiline
    // widget instead renders the display name, so workload blocks bind that action.
    public class DiagnosticSettings : ModSetting
    {
        public DiagnosticSettings(IMod mod) : base(mod) { }
        [SettingsUISection("Diagnostics")]
        public string ExecutionStatus => Mod.CompatibilityInactiveReason ?? RuntimeDiagnostics.SnapshotCaller(false).State;

        [SettingsUISection("Diagnostics")]
        public string ExecutionCounts => RuntimeDiagnostics.SnapshotCaller(false).ToString();

        [SettingsUISection("Diagnostics")]
        public string LastFailure => RuntimeDiagnostics.LastFailure(false);

        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(DiagnosticSettings), nameof(SimulationWorkloadText))]
        public string WorkloadByService => Mod.CompatibilityInactiveReason == null
            ? RuntimeDiagnostics.FormatWorkloads() : "Simulation replacement inactive";

        [SettingsUISection("Diagnostics")]
        public string PreviewExecutionCounts => RuntimeDiagnostics.SnapshotCaller(true).ToString();

        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(DiagnosticSettings), nameof(PreviewWorkloadText))]
        public string PreviewWorkloadByService => RuntimeDiagnostics.FormatWorkloads(true);

        [SettingsUISection("Diagnostics")]
        public string PreviewLastFailure => RuntimeDiagnostics.LastFailure(true);

        public LocalizedString SimulationWorkloadText() => LocalizedString.Value(WorkloadByService);

        public LocalizedString PreviewWorkloadText() => LocalizedString.Value(PreviewWorkloadByService);

#if SCF_BETA
        private Mod? _betaOwner;
        private readonly bool[] _requested = new bool[4];
        internal void BindBeta(Mod owner)
        {
            _betaOwner = owner;
            foreach (BetaFeature feature in System.Enum.GetValues(typeof(BetaFeature)))
                if (_requested[(int)feature]) owner.ChangeBeta(feature, true);
        }
        private void Set(BetaFeature feature, bool value)
        {
            _requested[(int)feature] = value;
            _betaOwner?.ChangeBeta(feature, value);
        }
        [SettingsUISection("Beta")]
        public bool HousingSearch { get => _requested[(int)BetaFeature.Housing]; set => Set(BetaFeature.Housing, value); }
        [SettingsUISection("Beta")]
        public bool ResourceSellerSearch { get => _requested[(int)BetaFeature.Seller]; set => Set(BetaFeature.Seller, value); }
        [SettingsUISection("Beta")]
        public bool WaterFlow { get => _requested[(int)BetaFeature.Water]; set => Set(BetaFeature.Water, value); }
        [SettingsUISection("Beta")]
        public bool RouteSearch { get => _requested[(int)BetaFeature.Route]; set => Set(BetaFeature.Route, value); }
        [SettingsUISection("Beta")]
        public string BetaStatus => string.Join(" | ", System.Array.ConvertAll((BetaFeature[])System.Enum.GetValues(typeof(BetaFeature)), f => f + " " + (_betaOwner?.BetaStatus(f) ?? "Off")));
#endif
        public override void SetDefaults()
        {
#if SCF_BETA
            HousingSearch = ResourceSellerSearch = WaterFlow = RouteSearch = false;
#endif
        }
    }

#if SCF_BETA
    // SaveSpecificSetting identifies its target by the concrete short type name.
    [Colossal.IO.AssetDatabase.FileLocation("ServiceCoverageFix")]
    public sealed class ServiceCoverageStutterFixSettings : DiagnosticSettings
    {
        public ServiceCoverageStutterFixSettings(IMod mod) : base(mod) { }
    }
#endif

    internal sealed class DiagnosticLocale : IDictionarySource
    {
        private readonly DiagnosticSettings _settings;
        internal DiagnosticLocale(DiagnosticSettings settings) { _settings = settings; }
        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            const string workloadHelp = "Each row shows the latest completed replacement pass for that service. The scope above the rows changes when a game load completes. Execution counters span the mod session. Pass numbers follow submission order across both callers. Providers had a usable range and budget. Available counts elements in their ranges. Processed counts elements visited, including already-covered targets. Reserved counts all input element-list entries prepared before filtering.";
            var entries = new Dictionary<string, string>
            {
                { _settings.GetSettingsLocaleID(), "Service Coverage Stutter Fix" },
                { _settings.GetOptionTabLocaleID("Diagnostics"), "Diagnostics" },
#if SCF_BETA
                { _settings.GetOptionTabLocaleID("Beta"), "Beta options" },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.HousingSearch)), "Housing search" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.HousingSearch)), "Reuse household income and support checks when comparing homes." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.ResourceSellerSearch)), "Resource-seller search" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.ResourceSellerSearch)), "Reuse resource data when businesses search for a seller." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.WaterFlow)), "Water flow" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.WaterFlow)), "Speed up pressure and flow calculations in water and sewage pipes." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.RouteSearch)), "Route search" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.RouteSearch)), "Speed up large route searches. Yields to PathfindLocalityFix when loaded." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.BetaStatus)), "Beta worker status" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.BetaStatus)), "Shows whether each requested worker is available in this session." },
#endif
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.ExecutionStatus)), "Simulation execution state" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.ExecutionStatus)), "Reports observed live jobs since this mod loaded. Startup probes are excluded. Managed or fallback observations remain visible until restart." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.ExecutionCounts)), "Simulation counters" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.ExecutionCounts)), "Scheduled counts successful replacement submissions; started and completed are written by the worker. Managed counts jobs executed without Burst. Fallbacks counts submissions returned to vanilla. " + workloadHelp },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.WorkloadByService)), "Latest simulation workloads" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.WorkloadByService)), workloadHelp },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.PreviewExecutionCounts)), "Tool and infoview preview counters" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.PreviewExecutionCounts)), "Observed replacement submissions from the preview system. Inactive means this caller is using vanilla. Open a coverage infoview or use a relevant tool to request a preview. " + workloadHelp },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.PreviewWorkloadByService)), "Latest tool and infoview preview workloads" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.PreviewWorkloadByService)), workloadHelp },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.PreviewLastFailure)), "Last preview fallback error" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.PreviewLastFailure)), "The most recent preview scheduling failure, if any." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.LastFailure)), "Last simulation fallback error" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.LastFailure)), "The most recent replacement scheduling failure, if any." }
            };
#if SCF_BETA
            // The game's option IDs include the concrete settings class name. Keep
            // resolving the original public diagnostics IDs as well as the new UI IDs.
            const string active = "." + nameof(ServiceCoverageStutterFixSettings) + ".";
            foreach (var entry in new List<KeyValuePair<string, string>>(entries))
                if (entry.Key.Contains(active)) entries[entry.Key.Replace(active, "." + nameof(DiagnosticSettings) + ".")] = entry.Value;
#endif
            return entries;
        }
        public void Unload() { }
    }
}
