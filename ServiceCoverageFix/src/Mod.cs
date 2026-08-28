using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Colossal.Logging;
using Game;
using Game.Modding;
using HarmonyLib;

namespace ServiceCoverageFix
{
    public sealed class Mod : IMod
    {
        internal const string HarmonyId = "com.servicecoveragefix.applycoverageheap";
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
                Log.Info(
                    $"Enabled ServiceCoverageFix {modVersion} monotone-radix scheduling " +
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
                Log.Error($"The stutter fix was disabled without changing simulation behavior: {exception}");
            }
        }

        public void OnDispose()
        {
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;
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

            if (!string.Equals(actualHash, ExpectedGameSha256, StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"Game.dll does not match the analyzed build. Expected SHA-256 {ExpectedGameSha256}, got {actualHash}.");
            }

            Type? jobType = systemType.GetNestedType(
                "ApplyCoverageJob",
                BindingFlags.Public | BindingFlags.NonPublic);
            if (jobType == null || jobType.FullName != ApplyCoverageJobName)
            {
                throw new TypeLoadException(ApplyCoverageJobName);
            }

            ValidateNativeListField(jobType, "m_BuildingData", "BuildingData", 24);
            ValidateNativeListField(jobType, "m_Elements", "CoverageElement", 32);
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

            int actualSize = System.Runtime.InteropServices.Marshal.SizeOf(elementType);
            if (actualSize != expectedElementSize)
            {
                throw new InvalidOperationException(
                    $"{elementType.FullName} is {actualSize} bytes; expected {expectedElementSize}.");
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
