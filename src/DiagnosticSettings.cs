using System.Collections.Generic;
using Colossal;
using Game.Modding;
using Game.Settings;
using Game.UI.Localization;

namespace ServiceCoverageFix
{
    // Plain read-only strings use the game's value accessor. Its multiline
    // widget instead renders the display name, so workload blocks bind that action.
    public sealed class DiagnosticSettings : ModSetting
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

        public override void SetDefaults() { }
    }

    internal sealed class DiagnosticLocale : IDictionarySource
    {
        private readonly DiagnosticSettings _settings;
        internal DiagnosticLocale(DiagnosticSettings settings) { _settings = settings; }
        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            const string workloadHelp = "Each row shows the latest completed replacement pass for that service. The scope above the rows changes when a game load completes. Execution counters span the mod session. Pass numbers follow submission order across both callers. Providers had a usable range and budget. Available counts elements in their ranges. Processed counts elements visited, including already-covered targets. Reserved counts all input element-list entries prepared before filtering.";
            return new Dictionary<string, string>
            {
                { _settings.GetSettingsLocaleID(), "Service Coverage Stutter Fix" },
                { _settings.GetOptionTabLocaleID("Diagnostics"), "Diagnostics" },
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
        }
        public void Unload() { }
    }
}
