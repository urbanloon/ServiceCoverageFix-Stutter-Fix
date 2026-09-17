// Game-independent fixtures. These compile the ACTUAL new validators but do
// not claim to build the CS2 mod or execute a real coverage/scheduling job.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using ServiceCoverageFix;

internal static class Program
{
    private static int _checks;
    private static int Main()
    {
        try
        {
            var first = Fixture("A", 0, 7, false, false);
            var shifted = Fixture("B", 21, 7, false, false);
            Require(first.Method.MetadataToken != shifted.Method.MetadataToken, "fixture tokens must move");
            Require(CompatibilityFingerprint.Compute(first.Method) == CompatibilityFingerprint.Compute(shifted.Method),
                "unrelated token movement must not invalidate equivalent code");
            Require(CompatibilityFingerprint.Compute(first.Method) != CompatibilityFingerprint.Compute(
                Fixture("C", 0, 8, false, false).Method), "changed constant must fail");
            Require(CompatibilityFingerprint.Compute(first.Method) != CompatibilityFingerprint.Compute(
                Fixture("D", 0, 7, true, false).Method), "changed call identity must fail");
            Require(CompatibilityFingerprint.Compute(first.Method) != CompatibilityFingerprint.Compute(
                Fixture("E", 0, 7, false, true).Method), "changed same-size field identity must fail");
            var originalCulture = CultureInfo.CurrentCulture;
            string digest = CompatibilityFingerprint.Compute(first.Method);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            try { Require(digest == CompatibilityFingerprint.Compute(first.Method), "culture-independent contract"); }
            finally { CultureInfo.CurrentCulture = originalCulture; }

            MethodInfo generic = typeof(ContractFixtures.GenericCases).GetMethod("Read")!;
            string canonical = CompatibilityFingerprint.Canonicalize(generic);
            Require(canonical.Contains("ContractFixtures.Box`1<System.Int32>|Read|System.Int32|System.Int32||33"),
                "resolve concrete method signatures on generic declaring types");
            Require(canonical.Contains("|Echo|System.Int32|System.Int32|System.Int32|1"),
                "resolve generic method instantiations");
            Require(CompatibilityFingerprint.TypeKey(typeof(int).MakeArrayType(1)) == "System.Int32[*]",
                "non-vector rank-one array remains distinct");
            Require(CompatibilityFingerprint.TypeKey(typeof(int[])) == "System.Int32[]", "vector array");
            Require(CompatibilityFingerprint.TypeKey(typeof(int[,])) == "System.Int32[,]", "rank-two array");
            Require(CompatibilityFingerprint.TypeKey(typeof(int).MakeByRefType()) == "System.Int32&", "byref type");
            Require(CompatibilityFingerprint.TypeKey(typeof(int).MakePointerType()) == "System.Int32*", "pointer type");
            Require(CompatibilityFingerprint.Canonicalize(typeof(ContractFixtures.GenericCases).GetMethod("Catch")!)
                .Contains("T|System.InvalidOperationException"), "exception clause identity is included");
            Require(CompatibilityFingerprint.Canonicalize(typeof(ContractFixtures.GenericCases).GetMethod("Text")!)
                .Contains("S|"), "string contents are included");
            string simple = CompatibilityFingerprint.Canonicalize(first.ConstantMethod);
            string expected = "SCF-IL-1\nM|ContractFixtures.Input|Constant|System.Int32|||1\n" +
                "maxstack:" + first.ConstantMethod.GetMethodBody()!.MaxStackSize.ToString(CultureInfo.InvariantCulture) +
                "\ninitlocals:0\nlocals:\nexceptions:\ncode:20070000002a\noperands:\n";
            Require(simple == expected, "independent canonical text for a simple method");

            ValidateLayout(typeof(GoodLayout), 8, new[] { "A", "B" }, new[] { "System.Int32", "System.Int32" }, new[] { 0, 4 });
            _checks++;
            Reject(() => ValidateLayout(typeof(SwappedLayout), 8, new[] { "A", "B" }, new[] { "System.Int32", "System.Int32" }, new[] { 0, 4 }), "same-size swapped fields");
            Reject(() => ValidateLayout(typeof(ChangedType), 8, new[] { "A", "B" }, new[] { "System.Int32", "System.Int32" }, new[] { 0, 4 }), "same-size changed field type");
            Reject(() => ValidateLayout(typeof(ExtraField), 8, new[] { "A", "B" }, new[] { "System.Int32", "System.Int32" }, new[] { 0, 4 }), "extra field in existing bytes");
            Reject(() => ValidateLayout(typeof(GoodLayout), 12, new[] { "A", "B" }, new[] { "System.Int32", "System.Int32" }, new[] { 0, 4 }), "changed structure size");
            Reject(() => ValidateLayout(typeof(GoodLayout), 8, new[] { "A", "Missing" }, new[] { "System.Int32", "System.Int32" }, new[] { 0, 4 }), "missing field");
            Reject(() => CompatibilityGate.Validate(), "non-game fixture assembly must never be accepted");
            Console.WriteLine("PASS: " + _checks + " production-validator fixture checks. No game runtime test performed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private sealed class Built
    {
        internal MethodInfo Method = null!;
        internal MethodInfo ConstantMethod = null!;
    }

    private static Built Fixture(string suffix, int padding, int constant, bool alternateCall, bool alternateField)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Fixture" + suffix), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Fixture");
        for (int i = 0; i < padding; i++)
        {
            var dummy = module.DefineType("Padding" + i, TypeAttributes.Public);
            dummy.DefineField("Unused", typeof(int), FieldAttributes.Public);
            var empty = dummy.DefineMethod("Unused", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
            empty.GetILGenerator().Emit(OpCodes.Ret);
            dummy.CreateType();
        }
        var type = module.DefineType("ContractFixtures.Input", TypeAttributes.Public);
        var a = type.DefineField("A", typeof(int), FieldAttributes.Public);
        var b = type.DefineField("B", typeof(int), FieldAttributes.Public);
        var one = type.DefineMethod("One", MethodAttributes.Public | MethodAttributes.Static, typeof(int), new[] { typeof(int) });
        one.GetILGenerator().Emit(OpCodes.Ldarg_0); one.GetILGenerator().Emit(OpCodes.Ret);
        var two = type.DefineMethod("Two", MethodAttributes.Public | MethodAttributes.Static, typeof(int), new[] { typeof(int) });
        two.GetILGenerator().Emit(OpCodes.Ldarg_0); two.GetILGenerator().Emit(OpCodes.Ret);
        var method = type.DefineMethod("Execute", MethodAttributes.Public | MethodAttributes.Static, typeof(int), new Type[] { type });
        method.InitLocals = false;
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, alternateField ? b : a);
        il.Emit(OpCodes.Ldc_I4, constant); il.Emit(OpCodes.Add); il.Emit(OpCodes.Call, alternateCall ? two : one); il.Emit(OpCodes.Ret);
        var value = type.DefineMethod("Constant", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        value.InitLocals = false;
        value.GetILGenerator().Emit(OpCodes.Ldc_I4, 7); value.GetILGenerator().Emit(OpCodes.Ret);
        Type created = type.CreateType()!;
        return new Built { Method = created.GetMethod("Execute")!, ConstantMethod = created.GetMethod("Constant")! };
    }

    private static void ValidateLayout(Type type, int size, string[] names, string[] types, int[] offsets)
    {
        Type item = typeof(CompatibilityGate).GetNestedType("FieldContract", BindingFlags.NonPublic)!;
        Array specs = Array.CreateInstance(item, names.Length);
        for (int i = 0; i < names.Length; i++)
            specs.SetValue(Activator.CreateInstance(item, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { names[i], types[i], offsets[i] }, CultureInfo.InvariantCulture), i);
        try { typeof(CompatibilityGate).GetMethod("ValidateLayout", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { type, size, specs }); }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }

    private static void Require(bool condition, string name)
    { if (!condition) throw new Exception("FAIL: " + name); _checks++; }
    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (NotSupportedException) { _checks++; return; }
        catch (MissingFieldException) { _checks++; return; }
        catch (TypeLoadException) { _checks++; return; }
        throw new Exception("FAIL: accepted " + name);
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct GoodLayout { public int A, B; }
    [StructLayout(LayoutKind.Explicit, Size = 8)] private struct SwappedLayout { [FieldOffset(4)] public int A; [FieldOffset(0)] public int B; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct ChangedType { public int A; public float B; }
    [StructLayout(LayoutKind.Explicit, Size = 8)] private struct ExtraField { [FieldOffset(0)] public int A; [FieldOffset(4)] public int B; [FieldOffset(0)] public int C; }
}

namespace ContractFixtures
{
    public class Box<T> { public T Value = default!; public T Read(int unused) => Value; }
    public static class GenericCases
    {
        public static T Echo<T>(T value) => value;
        public static int Read(Box<int> box) => Echo(box.Read(0));
        public static int Catch() { try { return int.Parse("invalid"); } catch (InvalidOperationException) { return 1; } }
        public static string Text() => "coverage";
    }
}

// Compile-only stand-ins for the gate's existing external types. The actual
// game, Unity jobs, Harmony, scheduler, and optimizer are NOT emulated here.
namespace Game { public sealed class UpdateSystem { } }
namespace Unity.Jobs { public interface IJob { void Execute(); } }
namespace Unity.Collections { public struct NativeList<T> where T : struct { } }
namespace Unity.Mathematics { public struct float2 { public float x, y; } }
namespace ServiceCoverageFix
{
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)] internal struct RawEntity { internal int Index, Version; }
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 24)] internal struct RawBuildingData { internal RawEntity Entity; internal int ElementIndex, ElementCount; internal float Total, Remaining; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] internal unsafe struct RawCoverageElement
    {
        [FieldOffset(0)] internal void* CoveragePtr;
        [FieldOffset(8)] internal Unity.Mathematics.float2 Coverage;
        [FieldOffset(16)] internal float AverageCoverage;
        [FieldOffset(20)] internal float DensityFactor;
        [FieldOffset(24)] internal float LengthFactor;
    }
}
