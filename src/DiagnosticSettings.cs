using System.Collections.Generic;
using Colossal;
using Game.Modding;
using Game.Settings;

namespace ServiceCoverageFix
{
    // Read-only Options panel. No persistent settings, gameplay switches, or
    // compatibility popups; reopen the panel to refresh on UI versions that cache.
    public sealed class DiagnosticSettings : ModSetting
    {
        public DiagnosticSettings(IMod mod) : base(mod) { }
        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        public string ExecutionStatus => RuntimeDiagnostics.SnapshotCaller(false).State;

        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        public string ExecutionCounts => RuntimeDiagnostics.SnapshotCaller(false).ToString();

        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        public string LastFailure => RuntimeDiagnostics.SnapshotCaller(false).LastFailure;

        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        public string WorkloadByService => RuntimeDiagnostics.FormatWorkloads();

        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        public string PreviewExecutionCounts => RuntimeDiagnostics.SnapshotCaller(true).ToString();

        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        public string PreviewWorkloadByService => RuntimeDiagnostics.FormatWorkloads(true);

        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        public string PreviewLastFailure => RuntimeDiagnostics.SnapshotCaller(true).LastFailure;

        public override void SetDefaults() { }
    }

    internal sealed class DiagnosticLocale : IDictionarySource
    {
        private readonly DiagnosticSettings _settings;
        internal DiagnosticLocale(DiagnosticSettings settings) { _settings = settings; }
        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            const string workloadHelp = "Each row shows the latest completed replacement pass for that service. The scope above the rows changes when a game load completes. Execution counters span the mod session. Pass numbers follow submission order across both callers. Providers had a usable range and budget. Available counts elements in their ranges. Processed counts elements visited, including already-covered targets. Reserved counts all input element-list entries prepared before filtering. Reopen to refresh.";
            return new Dictionary<string, string>
            {
                { _settings.GetSettingsLocaleID(), "Service Coverage Stutter Fix" },
                { _settings.GetOptionTabLocaleID("Diagnostics"), "Diagnostics" },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.ExecutionStatus)), "Simulation execution state" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.ExecutionStatus)), "Reports observed live jobs since this mod loaded. Startup probes are excluded. Managed or fallback observations remain visible until restart." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.ExecutionCounts)), "Simulation counters" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.ExecutionCounts)), "Scheduled counts successful replacement submissions; started and completed are written by the worker. Managed counts jobs executed without Burst. Fallbacks counts submissions returned to vanilla. Reopen this panel to refresh." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.WorkloadByService)), "Latest simulation workloads" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.WorkloadByService)), workloadHelp },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.PreviewExecutionCounts)), "Tool and infoview preview counters" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.PreviewExecutionCounts)), "Observed replacement submissions from the preview system. Inactive means this caller is using vanilla. Open a coverage infoview or use a relevant tool to request a preview." },
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
