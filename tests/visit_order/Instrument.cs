using Colossal.Mono.Cecil;
using Colossal.Mono.Cecil.Cil;

internal static class Instrument
{
    internal static void Copy(string original, string output, bool vanilla, string managed)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        using var asm = AssemblyDefinition.ReadAssembly(original, new ReaderParameters { AssemblyResolver = resolver });
        var module = asm.MainModule;
        var owner = vanilla ? "Game.Simulation.ServiceCoverageSystem/ApplyCoverageJob" : "ServiceCoverageFix.OptimizedApplyCoverageJob";
        var type = module.GetTypes().Single(t => t.FullName == owner);
        var method = type.Methods.Single(m => m.Name == (vanilla ? "Execute" : "ApplyElement"));
        var instructions = method.Body.Instructions;
        var il = method.Body.GetILProcessor();
        MethodReference Hook(string name) => module.ImportReference(typeof(Probe).GetMethod(name)!);
        void Insert(Instruction before, params Instruction[] batch)
        {
            if (instructions.Any(i => ReferenceEquals(i.Operand, before) || (i.Operand is Instruction[] targets && targets.Contains(before))))
                throw new Exception("Instrumented instruction is a branch target");
            foreach (var i in batch) il.InsertBefore(before, i);
        }
        if (vanilla)
        {
            // The consumed record is saved in a local, reloaded, then its coverage
            // pointer dereferenced. Lookahead get_Item calls do not fit this shape.
            var read = instructions.Single(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r &&
                r.Name == "get_Item" && r.DeclaringType.FullName.Contains("ServiceCoverageSystem/CoverageElement") &&
                i.Next.Next.Next.Operand is FieldReference f && f.Name == "m_CoveragePtr");
            Console.WriteLine($"{owner}: consumed index at IL_{read.Offset:x4}");
            Insert(read, il.Create(OpCodes.Dup), il.Create(OpCodes.Call, Hook(nameof(Probe.VisitIndex))));
        }
        else
        {
            if (method.Parameters.Count != 2 || !method.Parameters[1].ParameterType.FullName.EndsWith("RawCoverageElement*"))
                throw new Exception("ApplyElement signature changed");
            var first = instructions[0];
            Insert(first, il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Conv_I), il.Create(OpCodes.Call, Hook(nameof(Probe.VisitAddress))));
            Console.WriteLine($"{owner}: ApplyElement entry logs element pointer");
        }

        var store = instructions.Single(i => i.OpCode == OpCodes.Stobj && ((TypeReference)i.Operand).FullName == "Unity.Mathematics.float2");
        Console.WriteLine($"{owner}: destination pointer and both float bit patterns at IL_{store.Offset:x4}");
        var value = new VariableDefinition((TypeReference)store.Operand);
        method.Body.Variables.Add(value);
        Insert(store, il.Create(OpCodes.Stloc, value), il.Create(OpCodes.Dup), il.Create(OpCodes.Conv_I),
            il.Create(OpCodes.Ldloca, value), il.Create(OpCodes.Conv_I), il.Create(OpCodes.Ldind_I8),
            il.Create(OpCodes.Call, Hook(nameof(Probe.Store))), il.Create(OpCodes.Ldloc, value));
        // Hooks can grow short branches beyond 127 bytes. Keep every branch semantically identical.
        var longs = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode))
            .ToDictionary(f=>f.Name,f=>(OpCode)f.GetValue(null)!);
        foreach (var i in instructions)
            if (i.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                i.OpCode = longs[i.OpCode.Code.ToString()[..^2]];
        asm.Write(output);
    }
}
