using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Game;
using Unity.Collections;
using Unity.Jobs;

namespace ServiceCoverageFix
{
    internal static class CompatibilityGate
    {
        // Provenance only. A full-file hash is neither an override nor a reason
        // to show an error: all supported builds must pass the same checks.
        internal const string ReviewedGameSha256 =
            "aaee15c4fa41c130abaa1183840667e4faae531d67e8a9fa7536618effa2f86a";
        internal const string ServiceCoverageSystemName = "Game.Simulation.ServiceCoverageSystem";
        internal const string ApplyCoverageJobName = ServiceCoverageSystemName + "+ApplyCoverageJob";
        internal static Type ServiceCoverageSystemType { get; private set; } = null!;
        internal static string ActualGameSha256 { get; private set; } = string.Empty;
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        internal static void Validate()
        {
            ServiceCoverageSystemType = null!;
            ActualGameSha256 = string.Empty;
            if (IntPtr.Size != 8)
                throw new NotSupportedException("ServiceCoverageFix requires the reviewed 64-bit layout.");

            // Resolve from the game assembly, not an unrelated type with the
            // same full name in another mod.
            Type system = typeof(UpdateSystem).Assembly.GetType(ServiceCoverageSystemName, true)!;
            string path = system.Assembly.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException("Could not locate the loaded Game.dll.", path);
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 hash = SHA256.Create())
                ActualGameSha256 = CompatibilityFingerprint.Hex(hash.ComputeHash(stream));

            Type job = Nested(system, "ApplyCoverageJob");
            Type building = Nested(system, "BuildingData");
            Type element = Nested(system, "CoverageElement");
            Type comparer = Nested(system, "BuildingDataComparer");
            if (job.FullName != ApplyCoverageJobName || !job.IsValueType ||
                !typeof(IJob).IsAssignableFrom(job))
                throw new NotSupportedException("ApplyCoverageJob no longer implements the expected IJob contract.");

            RequireFieldCount(job, 2);
            ValidateListField(job, "m_BuildingData", building);
            ValidateListField(job, "m_Elements", element);
            RequireFieldCount(comparer, 1);
            ValidateListField(comparer, "m_Elements", element);

            ValidateLayout(building, 24,
                new FieldContract("m_Entity", "Unity.Entities.Entity", 0),
                new FieldContract("m_ElementIndex", "System.Int32", 8),
                new FieldContract("m_ElementCount", "System.Int32", 12),
                new FieldContract("m_Total", "System.Single", 16),
                new FieldContract("m_Remaining", "System.Single", 20));
            ValidateLayout(element, 32,
                new FieldContract("m_CoveragePtr", "System.Void*", 0),
                new FieldContract("m_Coverage", "Unity.Mathematics.float2", 8),
                new FieldContract("m_AverageCoverage", "System.Single", 16),
                new FieldContract("m_DensityFactor", "System.Single", 20),
                new FieldContract("m_LengthFactor", "System.Single", 24));
            Type entity = Field(building, "m_Entity").FieldType;
            ValidateLayout(entity, 8,
                new FieldContract("Index", "System.Int32", 0),
                new FieldContract("Version", "System.Int32", 4));
            Type float2 = Field(element, "m_Coverage").FieldType;
            ValidateLayout(float2, 8,
                new FieldContract("x", "System.Single", 0),
                new FieldContract("y", "System.Single", 4));
            Type coverage = system.Assembly.GetType("Game.Net.ServiceCoverage", true)!;
            ValidateLayout(coverage, 8,
                new FieldContract("m_Coverage", "Unity.Mathematics.float2", 0));

            // Check the destination mirrors too: equal byte counts alone are
            // insufficient if fields move, change type, or fill former padding.
            ValidateLayout(typeof(RawEntity), 8,
                new FieldContract("Index", "System.Int32", 0),
                new FieldContract("Version", "System.Int32", 4));
            ValidateLayout(typeof(RawBuildingData), 24,
                new FieldContract("Entity", typeof(RawEntity).FullName!, 0),
                new FieldContract("ElementIndex", "System.Int32", 8),
                new FieldContract("ElementCount", "System.Int32", 12),
                new FieldContract("Total", "System.Single", 16),
                new FieldContract("Remaining", "System.Single", 20));
            ValidateLayout(typeof(RawCoverageElement), 32,
                new FieldContract("CoveragePtr", "System.Void*", 0),
                new FieldContract("Coverage", "Unity.Mathematics.float2", 8),
                new FieldContract("AverageCoverage", "System.Single", 16),
                new FieldContract("DensityFactor", "System.Single", 20),
                new FieldContract("LengthFactor", "System.Single", 24));

            // Layout compatibility does not imply equivalent behavior. These
            // fingerprints include resolved references instead of numeric tokens.
            ValidateMethod(job, "Execute", Type.EmptyTypes,
                "a046fc6daf4122e72c12d810efc33e42e2c37acb96b80942682d6a0caddaba0b");
            ValidateMethod(comparer, "Compare", new[] { building, building },
                "ceeff10e772404a2dcfab56cdca491b221723133bcb8ee23d693f5722c73571e");
            ValidateMethod(element, "CompareTo", new[] { element },
                "7b3aa84c28f523f29160f1058e0ac0c02e899351a345f9bd21fa9b5261f7b1d9");
            // Preserve the allocation, dependencies and disposal of the temporary
            // lists whose records the optimized job uses as scratch storage.
            ValidateMethod(system, "OnUpdate", Type.EmptyTypes,
                "51182339f28b61ba18d5b826d530d31527ed62c9a067024c80f3491b0f539e3f");
            ServiceCoverageSystemType = system;
        }

        private static Type Nested(Type system, string name) =>
            system.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new TypeLoadException(system.FullName + "+" + name);

        private static FieldInfo Field(Type owner, string name) =>
            owner.GetField(name, InstanceFields) ?? throw new MissingFieldException(owner.FullName, name);

        private static void RequireFieldCount(Type type, int count)
        {
            if (type.GetFields(InstanceFields).Length != count)
                throw new NotSupportedException($"{type.FullName} has an unexpected instance-field set.");
        }

        private static void ValidateListField(Type owner, string name, Type element)
        {
            if (Field(owner, name).FieldType != typeof(NativeList<>).MakeGenericType(element))
                throw new NotSupportedException($"{owner.FullName}.{name} has an unexpected NativeList type.");
        }

        private static void ValidateLayout(Type type, int size, params FieldContract[] fields)
        {
            if (!type.IsValueType || type.IsEnum ||
                (!type.IsLayoutSequential && !type.IsExplicitLayout) || Marshal.SizeOf(type) != size)
                throw new NotSupportedException($"{type.FullName} no longer has the expected {size}-byte layout.");
            RequireFieldCount(type, fields.Length);
            foreach (FieldContract expected in fields)
            {
                FieldInfo field = Field(type, expected.Name);
                if (CompatibilityFingerprint.TypeKey(field.FieldType) != expected.TypeName ||
                    Marshal.OffsetOf(type, expected.Name).ToInt64() != expected.Offset)
                    throw new NotSupportedException(
                        $"{type.FullName}.{expected.Name} changed type or offset; the optimized job was not installed.");
            }
        }

        private static void ValidateMethod(Type type, string name, Type[] arguments, string expected)
        {
            MethodInfo method = type.GetMethod(name, InstanceFields, null, arguments, null)
                ?? throw new MissingMethodException(type.FullName, name);
            if (method.IsGenericMethod || method.IsStatic)
                throw new NotSupportedException($"Unexpected method signature: {type.FullName}.{name}.");
            string actual = CompatibilityFingerprint.Compute(method);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new NotSupportedException(
                    $"The reviewed coverage code contract no longer matches {type.FullName}.{name}. " +
                    $"ServiceCoverageFix needs a compatibility review. Expected {expected}; got {actual}. " +
                    $"Game.dll SHA-256: {ActualGameSha256}");
        }

        private readonly struct FieldContract
        {
            internal FieldContract(string name, string typeName, int offset)
            { Name = name; TypeName = typeName; Offset = offset; }
            internal string Name { get; }
            internal string TypeName { get; }
            internal int Offset { get; }
        }
    }
}
