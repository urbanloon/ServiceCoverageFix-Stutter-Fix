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
        private bool _optionsRegistered;

        // RegisterSetting builds and updates all widgets synchronously. Arm the
        // initial sample after that initial pass, when the live UI can display it.
        internal void SetOptionsRegistered(bool registered) => _optionsRegistered = registered;

        [Colossal.Json.Exclude]
        [SettingsUISection("Diagnostics")]
        [SettingsUIValueVersion(typeof(DiagnosticSettings), nameof(DiagnosticsVersion))]
        public string ExecutionStatus => RuntimeDiagnostics.CachedOptions.Status;

        public int DiagnosticsVersion() => _optionsRegistered ? RuntimeDiagnostics.ReadOptions().Version : 0;

        [Colossal.Json.Exclude]
        [SettingsUISection("Diagnostics")]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(DiagnosticSettings), nameof(SimulationWorkloadText))]
        public string WorkloadByService => RuntimeDiagnostics.CachedOptions.Workloads;

        public LocalizedString SimulationWorkloadText() => LocalizedString.Value(WorkloadByService);

        [Colossal.Json.Exclude]
        [SettingsUISection("Diagnostics")]
        [SettingsUIButton]
        public bool Refresh
        {
            set { if (value && _optionsRegistered) RuntimeDiagnostics.ReadOptions(refresh: true); }
        }

#if SCF_BETA
        private Mod? _betaOwner;
        private readonly bool[] _requested = new bool[BetaFeatures.Options.Length];
        internal void BindBeta(Mod owner)
        {
            _betaOwner = owner;
            foreach (BetaFeature feature in BetaFeatures.Options)
                if (_requested[(int)feature]) owner.ChangeBeta(feature, true);
        }
        private void Set(BetaFeature feature, bool value)
        {
            _requested[(int)feature] = value;
            _betaOwner?.ChangeBeta(feature, value);
        }
        [SettingsUISection("Beta")]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(DiagnosticSettings), nameof(BetaCompatibilityText))]
        public string BetaCompatibility => "These beta options may be incompatible with Big City Performance.";

        public LocalizedString BetaCompatibilityText() => LocalizedString.Value(BetaCompatibility);

        [SettingsUISection("Beta")]
        public bool WaterFlow { get => _requested[(int)BetaFeature.Water]; set => Set(BetaFeature.Water, value); }
        [SettingsUISection("Beta")]
        public bool RouteSearch { get => _requested[(int)BetaFeature.Route]; set => Set(BetaFeature.Route, value); }
        [SettingsUISection("Beta")]
        public bool CompanyPropertySearch { get => _requested[(int)BetaFeature.CompanyCommercial]; set => Set(BetaFeature.CompanyCommercial, value); }
#endif
        public override void SetDefaults()
        {
#if SCF_BETA
            WaterFlow = RouteSearch = CompanyPropertySearch = false;
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
            var entries = new Dictionary<string, string>
            {
                { _settings.GetSettingsLocaleID(), "Service Coverage Stutter Fix" },
                { _settings.GetOptionTabLocaleID("Diagnostics"), "Diagnostics" },
#if SCF_BETA
                { _settings.GetOptionTabLocaleID("Beta"), "Beta options" },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.BetaCompatibility)), "Beta compatibility" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.BetaCompatibility)), "Compatibility with other performance mods." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.WaterFlow)), "Water flow" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.WaterFlow)), "Optimize water and sewage flow calculations." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.CompanyPropertySearch)), "Company property search" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.CompanyPropertySearch)), "Reuse occupancy checks when companies compare commercial or industrial properties." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.RouteSearch)), "Route search" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.RouteSearch)), "Optimize searches for routes across the transport network." },
#endif
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.Refresh)), "Refresh" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.Refresh)), "Update the coverage status and latest completed workloads." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.ExecutionStatus)), "Coverage status" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.ExecutionStatus)), "Shows whether the fix has processed a coverage update since the mod loaded." },
                { _settings.GetOptionLabelLocaleID(nameof(DiagnosticSettings.WorkloadByService)), "Latest workloads" },
                { _settings.GetOptionDescLocaleID(nameof(DiagnosticSettings.WorkloadByService)), "Each service shows its latest update. Providers are participating buildings. Available and processed count coverage elements." }
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
