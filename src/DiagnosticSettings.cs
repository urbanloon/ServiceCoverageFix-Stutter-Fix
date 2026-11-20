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
        public string ExecutionStatus => Mod.GetRuntimeStatus().State;

        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        public string ExecutionCounts => Mod.GetRuntimeStatus().ToString();

        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        public string LastFailure => Mod.GetRuntimeStatus().LastFailure;

        public override void SetDefaults() { }
    }

    internal sealed class DiagnosticLocale : IDictionarySource
    {
        private readonly DiagnosticSettings _settings;
        internal DiagnosticLocale(DiagnosticSettings settings) { _settings = settings; }
        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { _settings.GetSettingsLocaleID(), "Service Coverage Stutter Fix" },
                { _settings.GetOptionTabLocaleID("Diagnostics"), "Diagnostics" },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.ExecutionStatus)), "Execution state" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.ExecutionStatus)), "Reports observed live jobs since this mod loaded. Startup probes are excluded. Managed or fallback observations remain visible until restart." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.ExecutionCounts)), "Live counters" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.ExecutionCounts)), "Scheduled counts successful replacement submissions; started and completed are written by the worker. Managed counts jobs executed without Burst. Fallbacks counts submissions returned to vanilla. Reopen this panel to refresh." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.LastFailure)), "Last fallback error" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.LastFailure)), "The most recent replacement scheduling failure, if any." }
            };
        }
        public void Unload() { }
    }
}
