using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
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
        private DiagnosticSettings? _settings;
        private DiagnosticLocale? _diagnosticLocale;

        // Callable by diagnostic tools without parsing logs. Counters refer only
        // to live simulation jobs, not the isolated startup probes.
        public static CoverageRuntimeStatus GetRuntimeStatus() => RuntimeDiagnostics.Snapshot();

        public void OnLoad(UpdateSystem updateSystem)
        {
            try
            {
                CompatibilityGate.Validate();
                ApplyCoverageScheduleBridge.ValidateBridge(CompatibilityGate.ApplyCoverageJobType);
                RuntimeDiagnostics.Initialize();

                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());

                if (ServiceCoverageSystemPatch.ReplacementCount != 1)
                {
                    throw new InvalidOperationException(
                        $"Expected one ApplyCoverageJob schedule replacement, found {ServiceCoverageSystemPatch.ReplacementCount}.");
                }

                RuntimeDiagnostics.MarkInstalled();
                // The UI is optional: a presentation failure cannot unpatch the fix.
                try
                {
                    _settings = new DiagnosticSettings(this);
                    _diagnosticLocale = new DiagnosticLocale(_settings);
                    Game.SceneFlow.GameManager.instance.localizationManager.AddSource("en-US", _diagnosticLocale);
                    _settings.RegisterInOptionsUI();
                }
                catch (Exception uiError) { Log.Info($"Diagnostics panel unavailable: {uiError.Message}"); }

                System.Version harmonyVersion = typeof(Harmony).Assembly.GetName().Version;
                System.Version modVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (CompatibilityGate.IsUnverifiedGameBuild)
                {
                    // A new game hash is diagnostic information, not a load failure.
                    // Structural validation and the schedule replacement have succeeded.
                    Log.Info(
                        "Game build differs from the verified baseline; structural " +
                        "and scheduling checks passed. " +
                        $"Game.dll SHA-256: {CompatibilityGate.ActualGameSha256}");
                }

                Log.Info(
                    $"Installed ServiceCoverageFix {modVersion}; bridge preflight passed; awaiting live coverage execution " +
                    $"with packaged Harmony {harmonyVersion}.");
            }
            catch (Exception exception)
            {
                try
                {
                    _harmony?.UnpatchAll(HarmonyId);
                }
                catch
                {
                    // Preserve the original failure in the log below.
                }

                _harmony = null;
                try { RuntimeDiagnostics.Dispose(); }
                catch (Exception cleanup) { Log.Error($"Diagnostic cleanup could not complete: {cleanup}"); }
                Log.Error($"The stutter fix was disabled without changing simulation behavior: {exception}");
            }
        }

        public void OnDispose()
        {
            try
            {
                _settings?.UnregisterInOptionsUI();
                if (_diagnosticLocale != null)
                    Game.SceneFlow.GameManager.instance.localizationManager.RemoveSource("en-US", _diagnosticLocale);
            }
            finally
            {
                _settings = null;
                _diagnosticLocale = null;
                _harmony?.UnpatchAll(HarmonyId);
                _harmony = null;
                RuntimeDiagnostics.Dispose();
            }
        }
    }

    internal static class CompatibilityGate
    {
        internal const string ExpectedGameSha256 =
            "721e7e17bf74299aa2b988c1bd07e90874bb8bc72d263229500c4bf639e7e4ee";

        internal const string ServiceCoverageSystemName =
            "Game.Simulation.ServiceCoverageSystem";

        internal const string ApplyCoverageJobName =
            "Game.Simulation.ServiceCoverageSystem+ApplyCoverageJob";

        internal static Type ServiceCoverageSystemType { get; private set; } = null!;

        internal static Type ApplyCoverageJobType { get; private set; } = null!;

        internal static bool IsUnverifiedGameBuild { get; private set; }

        internal static string ActualGameSha256 { get; private set; } = string.Empty;

        internal static void Validate()
        {
            Type? systemType = AccessTools.TypeByName(ServiceCoverageSystemName);
            if (systemType == null)
            {
                throw new TypeLoadException(ServiceCoverageSystemName);
            }

            string assemblyPath = systemType.Assembly.Location;
            if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new FileNotFoundException("Could not locate the loaded Game.dll.", assemblyPath);
            }

            string actualHash;
            using (FileStream stream = File.OpenRead(assemblyPath))
            using (SHA256 sha256 = SHA256.Create())
            {
                actualHash = ToLowerHex(sha256.ComputeHash(stream));
            }

            ActualGameSha256 = actualHash;
            IsUnverifiedGameBuild = !string.Equals(
                actualHash,
                ExpectedGameSha256,
                StringComparison.Ordinal);

            Type? jobType = systemType.GetNestedType(
                "ApplyCoverageJob",
                BindingFlags.Public | BindingFlags.NonPublic);
            if (jobType == null || jobType.FullName != ApplyCoverageJobName)
            {
                throw new TypeLoadException(ApplyCoverageJobName);
            }

            ValidateNativeListField(jobType, "m_BuildingData", "BuildingData", 24);
            ValidateNativeListField(jobType, "m_Elements", "CoverageElement", 32);
            if (!typeof(Unity.Jobs.IJob).IsAssignableFrom(jobType) || !jobType.IsValueType)
                throw new InvalidOperationException("ApplyCoverageJob must remain an IJob value type.");
            Type coverageType = AccessTools.TypeByName("Game.Net.ServiceCoverage")
                ?? throw new TypeLoadException("Game.Net.ServiceCoverage");
            ValidateLayout(coverageType, new[] { "m_Coverage" },
                new[] { "Unity.Mathematics.float2" }, new[] { 0 });
            ApplyCoverageJobType = jobType;
            ServiceCoverageSystemType = systemType;
        }

        private static void ValidateNativeListField(
            Type jobType,
            string fieldName,
            string expectedElementName,
            int expectedElementSize)
        {
            FieldInfo? field = jobType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null || !field.FieldType.IsGenericType ||
                field.FieldType.GetGenericTypeDefinition().FullName != "Unity.Collections.NativeList`1")
            {
                throw new MissingFieldException(jobType.FullName, fieldName);
            }

            Type elementType = field.FieldType.GetGenericArguments()[0];
            if (elementType.Name != expectedElementName)
            {
                throw new InvalidOperationException(
                    $"{fieldName} has unexpected element type {elementType.FullName}.");
            }

            if (expectedElementName == "BuildingData")
                ValidateLayout(elementType,
                    new[] { "m_Entity", "m_ElementIndex", "m_ElementCount", "m_Total", "m_Remaining" },
                    new[] { "Unity.Entities.Entity", "System.Int32", "System.Int32", "System.Single", "System.Single" },
                    new[] { 0, 8, 12, 16, 20 });
            else
                ValidateLayout(elementType,
                    new[] { "m_CoveragePtr", "m_Coverage", "m_AverageCoverage", "m_DensityFactor", "m_LengthFactor" },
                    new[] { "System.Void*", "Unity.Mathematics.float2", "System.Single", "System.Single", "System.Single" },
                    new[] { 0, 8, 16, 20, 24 });

            int actualSize = System.Runtime.InteropServices.Marshal.SizeOf(elementType);
            if (actualSize != expectedElementSize)
            {
                throw new InvalidOperationException(
                    $"{elementType.FullName} is {actualSize} bytes; expected {expectedElementSize}.");
            }
        }

        private static void ValidateLayout(Type type, string[] names, string[] types, int[] offsets)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (!type.IsValueType || (!type.IsLayoutSequential && !type.IsExplicitLayout) ||
                type.GetFields(flags).Length != names.Length)
                throw new InvalidOperationException($"Unexpected layout for {type.FullName}.");
            for (int i = 0; i < names.Length; i++)
            {
                FieldInfo? field = type.GetField(names[i], flags);
                if (field == null || field.FieldType.FullName != types[i] ||
                    Marshal.OffsetOf(type, names[i]).ToInt64() != offsets[i])
                    throw new InvalidOperationException($"Unexpected type or offset for {type.FullName}.{names[i]}.");
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            char[] chars = new char[bytes.Length * 2];
            const string alphabet = "0123456789abcdef";
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = alphabet[bytes[i] >> 4];
                chars[i * 2 + 1] = alphabet[bytes[i] & 15];
            }

            return new string(chars);
        }
    }
}

