using System;
using System.Collections.Generic;
using System.Threading;
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
        private static class Logger
        {
            internal static readonly ILog Instance = LogManager.GetLogger("ServiceCoverageFix").SetShowsErrorsInUI(true);
        }
        internal static ILog Log => Logger.Instance;

        private static Mod? _owner;
        private string? _compatibilityInactiveReason;
        internal static string? CompatibilityInactiveReason =>
            Volatile.Read(ref _owner)?._compatibilityInactiveReason;
        private Harmony? _harmony;
        private DiagnosticSettings? _settings;
        private DiagnosticLocale? _diagnosticLocale;
        private Action? _stopTrackingGameLoads;
        internal bool IsOwner => ReferenceEquals(Volatile.Read(ref _owner), this);

        // Callable by diagnostic tools without parsing logs. Counters refer only
        // to live simulation and preview jobs. The isolated startup probes are excluded.
        public static CoverageRuntimeStatus GetRuntimeStatus() => RuntimeDiagnostics.Snapshot();

        public void OnLoad(UpdateSystem updateSystem)
        {
            // A second instance must never remove the first instance's patch or
            // free diagnostics still in use, including during retryable teardown.
            if (Interlocked.CompareExchange(ref _owner, this, null) != null) return;
            _compatibilityInactiveReason = null;
            try
            {
                try { CompatibilityGate.Validate(); }
                catch (CoverageContractMismatch mismatch)
                {
                    // The gate runs before any patch or worker allocation. Keep
                    // the Options panel available to explain this checked state.
                    _compatibilityInactiveReason = "Inactive. " + mismatch.Message;
                    RuntimeDiagnostics.Initialize(allocateWorkerSlot: false);
                    RegisterDiagnosticsPanel();
                    try { Log.Info("ServiceCoverageFix " + _compatibilityInactiveReason); } catch { }
                    return;
                }
                ApplyCoverageScheduleBridge.ValidateBridge(CompatibilityGate.ApplyCoverageJobType);
                RuntimeDiagnostics.Initialize();

                _harmony = new Harmony(HarmonyId);
                ServiceCoverageSystemPatch.Reset();
                _harmony.CreateClassProcessor(typeof(ServiceCoverageSystemPatch)).Patch();

                if (ServiceCoverageSystemPatch.ReplacementCount != 1)
                {
                    throw new InvalidOperationException(
                        $"Expected one ApplyCoverageJob schedule replacement, found {ServiceCoverageSystemPatch.ReplacementCount}.");
                }

                RuntimeDiagnostics.MarkInstalled();
                TryInstallPreview(_harmony, CompatibilityGate.PreviewSupported);
            }
            catch (Exception exception)
            {
                AbortLoad(exception);
                return;
            }

            try { TrackGameLoads(Game.SceneFlow.GameManager.instance); }
            catch (Exception error) { try { Log.Info($"Workload history follows mod lifetime: {error.Message}"); } catch { } }

            RegisterDiagnosticsPanel();

            try
            {
                System.Version? version = Assembly.GetExecutingAssembly().GetName().Version;
                Log.Info($"Installed ServiceCoverageFix {version}. Coverage contract and bridge preflight passed.");
            }
            catch { /* Logging cannot change an installed patch. */ }
        }

        private void RegisterDiagnosticsPanel()
        {
            try
            {
                _settings = new DiagnosticSettings(this);
                _diagnosticLocale = new DiagnosticLocale(_settings);
                Game.SceneFlow.GameManager.instance.localizationManager.AddSource("en-US", _diagnosticLocale);
                _settings.RegisterInOptionsUI();
            }
            catch (Exception uiError)
            {
                try { Log.Info($"Diagnostics panel unavailable: {uiError.Message}"); } catch { }
            }
        }

        internal void TrackGameLoads(Game.SceneFlow.GameManager source)
        {
            // Keep the optional event API out of the unload method. A JIT can
            // resolve a missing accessor before reaching a null guard.
            Action unsubscribe = () => source.onGameLoadingComplete -= OnGameLoadingComplete;
            source.onGameLoadingComplete += OnGameLoadingComplete;
            _stopTrackingGameLoads = unsubscribe;
        }

        private void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode) =>
            RuntimeDiagnostics.StartLoadedSession(this);

        internal static void TryInstallPreview(Harmony harmony, bool supported)
        {
            ServiceCoveragePreviewPatch.Reset();
            if (!supported) return;
            // A preview transpiler or Harmony failure leaves simulation active.
            try
            {
                harmony.CreateClassProcessor(typeof(ServiceCoveragePreviewPatch)).Patch();
                if (ServiceCoveragePreviewPatch.ReplacementCount != 1)
                    throw new InvalidOperationException("Expected one preview ApplyCoverageJob schedule replacement.");
                RuntimeDiagnostics.MarkPreviewInstalled();
            }
            catch (Exception error)
            {
                try { Log.Info($"Coverage preview remains on vanilla: {error.Message}"); } catch { }
            }
        }

        private void AbortLoad(Exception exception)
        {
            // Preserve all handles until their own cleanup succeeds. A surviving
            // Harmony patch routes new submissions to vanilla after disposal.
            try { OnDispose(); }
            catch (Exception cleanup) { try { Log.Error(cleanup); } catch { } }
            try
            {
                Log.Error($"ServiceCoverageFix could not initialize. {exception}");
            }
            catch { /* Reporting cannot interrupt cleanup. */ }
        }

        public void OnDispose()
        {
            if (!IsOwner) return;
            List<Exception>? failures = null;
            AttemptCleanup(() =>
            {
                _stopTrackingGameLoads?.Invoke();
                _stopTrackingGameLoads = null;
            }, ref failures);
            AttemptCleanup(() =>
            {
                _settings?.UnregisterInOptionsUI();
                _settings = null;
            }, ref failures);
            AttemptCleanup(() =>
            {
                if (_diagnosticLocale != null)
                    Game.SceneFlow.GameManager.instance.localizationManager.RemoveSource("en-US", _diagnosticLocale);
                _diagnosticLocale = null;
            }, ref failures);
            AttemptCleanup(() =>
            {
                _harmony?.UnpatchAll(HarmonyId);
                _harmony = null;
            }, ref failures);

            // Always disable new optimized submissions and finish tracked work,
            // even if UI cleanup or unpatching failed. Dispose retains counter
            // memory when completion fails, so workers never lose their buffer.
            AttemptCleanup(RuntimeDiagnostics.Dispose, ref failures);

            if (failures != null)
                throw new AggregateException("ServiceCoverageFix cleanup did not fully complete.", failures);
            Interlocked.CompareExchange(ref _owner, null, this);
        }

        private static void AttemptCleanup(Action cleanup, ref List<Exception>? failures)
        {
            try { cleanup(); }
            catch (Exception exception)
            {
                // Preserve every failure and retain the corresponding resource
                // reference, allowing a later OnDispose call to retry cleanup.
                if (failures == null) failures = new List<Exception>();
                failures.Add(exception);
            }
        }
    }

    internal static class CompatibilityGate
    {
        internal const string ExpectedGameSha256 =
            "721e7e17bf74299aa2b988c1bd07e90874bb8bc72d263229500c4bf639e7e4ee";

        private const string SecondReviewedGameSha256 =
            "f076df4b70aa0f21a80345e03a052ca2273bc6754f41dab7e63d837837a43745";

        internal const string ServiceCoverageSystemName =
            "Game.Simulation.ServiceCoverageSystem";

        internal const string ApplyCoverageJobName =
            "Game.Simulation.ServiceCoverageSystem+ApplyCoverageJob";

        internal static Type ServiceCoverageSystemType { get; private set; } = null!;

        internal static Type ApplyCoverageJobType { get; private set; } = null!;
        internal static bool PreviewSupported { get; private set; }
        internal static Type? CoveragePreviewSystemType { get; private set; }

        internal static string ActualGameSha256 { get; private set; } = string.Empty;
        internal static bool IsUnverifiedGameBuild => ActualGameSha256 != ExpectedGameSha256 &&
            ActualGameSha256 != SecondReviewedGameSha256;

        internal static void Validate()
        {
            Type? systemType = typeof(UpdateSystem).Assembly.GetType(ServiceCoverageSystemName);
            if (systemType == null)
            {
                throw new CoverageContractMismatch(ServiceCoverageSystemName + " is missing.");
            }

            string assemblyPath = systemType.Assembly.Location;
            if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new FileNotFoundException("Could not locate the loaded Game.dll.", assemblyPath);
            }

            // Both released fixtures have the same reviewed simulation and preview
            // contracts. Stream their hash first to avoid the much larger metadata
            // allocation on these builds. Other hashes still use the behavior gate.
            using (FileStream stream = File.OpenRead(assemblyPath))
            using (SHA256 sha256 = SHA256.Create())
                ActualGameSha256 = ToLowerHex(sha256.ComputeHash(stream));

            bool compatiblePreview = !IsUnverifiedGameBuild || CoverageContract.Validate(assemblyPath);
            CoveragePreviewSystemType = systemType.Assembly.GetType("Game.Tools.CoveragePreviewSystem");
            PreviewSupported = compatiblePreview && CoveragePreviewSystemType != null;

            Type? jobType = systemType.GetNestedType(
                "ApplyCoverageJob",
                BindingFlags.Public | BindingFlags.NonPublic);
            if (jobType == null || jobType.FullName != ApplyCoverageJobName)
            {
                throw new CoverageContractMismatch(ApplyCoverageJobName);
            }

            ValidateNativeListField(jobType, "m_BuildingData", "BuildingData", 24);
            ValidateNativeListField(jobType, "m_Elements", "CoverageElement", 32);
            if (jobType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length != 2)
                throw new CoverageContractMismatch("Unexpected fields in the game's ApplyCoverageJob.");
            if (!typeof(Unity.Jobs.IJob).IsAssignableFrom(jobType) || !jobType.IsValueType)
                throw new CoverageContractMismatch("ApplyCoverageJob must remain an IJob value type.");
            Type coverageType = systemType.Assembly.GetType("Game.Net.ServiceCoverage")
                ?? throw new CoverageContractMismatch("Game.Net.ServiceCoverage");
            ValidateLayout(coverageType, new[] { "m_Coverage" },
                new[] { "Unity.Mathematics.float2" }, new[] { 0 });
            if (Marshal.SizeOf(coverageType) != 8)
                throw new CoverageContractMismatch("Unexpected size for ServiceCoverage.");
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
                field.FieldType.GetGenericTypeDefinition() != typeof(Unity.Collections.NativeList<>))
            {
                throw new CoverageContractMismatch("Unexpected field " + jobType.FullName + "." + fieldName);
            }

            Type elementType = field.FieldType.GetGenericArguments()[0];
            if (elementType.Name != expectedElementName)
            {
                throw new CoverageContractMismatch(
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
                throw new CoverageContractMismatch(
                    $"{elementType.FullName} is {actualSize} bytes; expected {expectedElementSize}.");
            }
        }

        private static void ValidateLayout(Type type, string[] names, string[] types, int[] offsets)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (!type.IsValueType || (!type.IsLayoutSequential && !type.IsExplicitLayout) ||
                type.GetFields(flags).Length != names.Length)
                throw new CoverageContractMismatch($"Unexpected layout for {type.FullName}.");
            for (int i = 0; i < names.Length; i++)
            {
                FieldInfo? field = type.GetField(names[i], flags);
                if (field == null || field.FieldType.FullName != types[i] ||
                    Marshal.OffsetOf(type, names[i]).ToInt64() != offsets[i])
                    throw new CoverageContractMismatch($"Unexpected type or offset for {type.FullName}.{names[i]}.");
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

