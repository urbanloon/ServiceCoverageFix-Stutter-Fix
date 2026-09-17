using System;
using System.Reflection;
using Colossal.Logging;
using Game;
using Game.Modding;
using HarmonyLib;

namespace ServiceCoverageFix
{
    public sealed class Mod : IMod
    {
        internal const string HarmonyId = "com.servicecoveragefix.applycoverage.byteradix";
        internal static readonly ILog Log = LogManager
            .GetLogger("ServiceCoverageFix")
            .SetShowsErrorsInUI(true);

        private Harmony? _harmony;

        public void OnLoad(UpdateSystem updateSystem)
        {
            try
            {
                CompatibilityGate.Validate();

                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());

                if (ServiceCoverageSystemPatch.ReplacementCount != 1)
                {
                    throw new InvalidOperationException(
                        $"Expected one ApplyCoverageJob schedule replacement, found {ServiceCoverageSystemPatch.ReplacementCount}.");
                }

                System.Version harmonyVersion = typeof(Harmony).Assembly.GetName().Version;
                System.Version modVersion = Assembly.GetExecutingAssembly().GetName().Version;
                // A different whole-file hash is not a compatibility failure.
                // Layout, code-contract and schedule checks must pass instead.
                // Keep one informational entry; only actual failures use the UI.
                Log.Info(
                    $"Enabled ServiceCoverageFix {modVersion} record-local byte-radix hardened build " +
                    $"with packaged Harmony {harmonyVersion}. Coverage compatibility checks passed. " +
                    $"Game.dll SHA-256: {CompatibilityGate.ActualGameSha256}");
            }
            catch (Exception exception)
            {
                try
                {
                    _harmony?.UnpatchAll(HarmonyId);
                }
                catch (Exception rollbackException)
                {
                    _harmony = null;
                    Log.Error(
                        $"ServiceCoverageFix could not restore its Harmony patch after a startup failure. " +
                        $"Restart the game with the mod disabled. Original failure: {exception}\n" +
                        $"Rollback failure: {rollbackException}");
                    return;
                }

                _harmony = null;
                Log.Error($"The stutter fix was disabled without changing simulation behavior: {exception}");
            }
        }

        public void OnDispose()
        {
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;
        }
    }
}
