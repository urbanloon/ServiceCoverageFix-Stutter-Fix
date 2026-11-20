using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using HarmonyLib;

// Exercise the same reader, transpiler dispatcher and Cecil emitter used by Harmony
// 2.2.2, without installing a process detour or running Unity's native ECS methods.
internal static class HarmonyEmission
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Public |
                                         BindingFlags.Instance | BindingFlags.Static;

    internal static void Check(MethodInfo original, MethodInfo transpiler, string bridge)
    {
        Assembly harmony = typeof(Harmony).Assembly;
        Type patcher = harmony.GetType("HarmonyLib.MethodPatcher", true)!;
        using var method = (IDisposable)patcher.GetMethod("CreateDynamicMethod", Members)!
            .Invoke(null, new object[] { original, "_offline", false })!;
        Type methodType = method.GetType();
        var generator = (ILGenerator)methodType.GetMethod("GetILGenerator", Type.EmptyTypes)!.Invoke(method, null)!;
        Type copierType = harmony.GetType("HarmonyLib.MethodCopier", true)!;
        object copier = Activator.CreateInstance(copierType, Members, null, new object?[] { original, generator, null }, null)!;
        copierType.GetMethod("AddTranspiler", Members)!.Invoke(copier, new object[] { transpiler });

        Type emitterType = harmony.GetType("HarmonyLib.Emitter", true)!;
        object emitter = Activator.CreateInstance(emitterType, Members, null, new object[] { generator, false }, null)!;
        var endLabels = new List<Label>();
        MethodInfo finalize = copierType.GetMethod("Finalize", Members, null,
            new[] { emitterType, endLabels.GetType(), typeof(bool).MakeByRefType() }, null)!;
        finalize.Invoke(copier, new object[] { emitter, endLabels, false });
        foreach (Label label in endLabels) generator.MarkLabel(label);
        generator.Emit(OpCodes.Ret);

        var emitted = (Dictionary<int, CodeInstruction>)emitterType.GetMethod("GetInstructions", Members)!.Invoke(emitter, null)!;
        int offset = emitted.Single(pair => pair.Value.operand is MethodInfo methodInfo &&
            methodInfo.DeclaringType == transpiler.DeclaringType!.Assembly.GetType("ServiceCoverageFix.ApplyCoverageScheduleBridge") &&
            methodInfo.Name == bridge).Key;

        // Re-read the actual call token in the emitted method body using the .NET
        // metadata reader, independently of Harmony's in-memory instruction log.
        object definition = methodType.GetProperty("Definition", Members)!.GetValue(method)!;
        object module = definition.GetType().GetProperty("Module")!.GetValue(definition)!;
        using var bytes = new MemoryStream();
        module.GetType().GetMethod("Write", new[] { typeof(Stream) })!.Invoke(module, new object[] { bytes });
        bytes.Position = 0;
        using var pe = new PEReader(bytes);
        MetadataReader reader = pe.GetMetadataReader();
        MethodDefinition body = reader.GetMethodDefinition(reader.MethodDefinitions.Single());
        byte[] il = pe.GetMethodBody(body.RelativeVirtualAddress).GetILBytes()!;
        if (il[offset] != OpCodes.Call.Value || il[^1] != OpCodes.Ret.Value)
            throw new InvalidDataException("Invalid emitted call or return.");
        var handle = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, offset + 1));
        var genericCall = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
        var member = reader.GetMemberReference((MemberReferenceHandle)genericCall.Method);
        var declaring = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
        if (reader.GetString(member.Name) != bridge || reader.GetString(declaring.Name) != "ApplyCoverageScheduleBridge" ||
            reader.GetString(declaring.Namespace) != "ServiceCoverageFix")
            throw new InvalidDataException("Unexpected bridge in serialized method.");
    }
}
