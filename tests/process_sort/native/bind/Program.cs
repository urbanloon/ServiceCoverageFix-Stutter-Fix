using Colossal.Mono.Cecil;
using Colossal.Mono.Cecil.Cil;

if (args[0] == "--inspect")
{
    using var inspected = AssemblyDefinition.ReadAssembly(args[1]);
    foreach (var type in inspected.MainModule.GetTypes().Where(t =>
        t.FullName == args[2] || t.FullName.StartsWith(args[2] + "/", StringComparison.Ordinal)))
    {
        Console.WriteLine("TYPE " + type.FullName);
        foreach (var field in type.Fields) Console.WriteLine("FIELD " + field);
        foreach (var method in type.Methods)
        {
            Console.WriteLine("METHOD " + method);
            if (method.HasBody)
                foreach (var instruction in method.Body.Instructions) Console.WriteLine(instruction);
        }
    }
    return;
}
using var game = AssemblyDefinition.ReadAssembly(args[0]);
using var bench = AssemblyDefinition.ReadAssembly(args[1]);
var element = game.MainModule.GetType("Game.Simulation.ServiceCoverageSystem").NestedTypes.Single(t => t.Name == "CoverageElement");
var root = bench.MainModule.Types.Single(t => t.Methods.Any(m => m.Name == "GameSort"));
var wrapper = root.Methods.Single(m => m.Name == "GameSort");
var calls = wrapper.Body.Instructions.Where(i => i.OpCode == OpCodes.Call)
    .Select(i => (GenericInstanceMethod)i.Operand).ToArray();
if (calls.Length != 2 || calls[0].Name != "ConvertExistingDataToNativeArray" ||
    calls[1].Name != "Sort" || calls[1].Parameters.Count != 1 ||
    calls[1].Parameters[0].ParameterType.GetElementType().FullName != "Unity.Collections.NativeArray`1" ||
    calls.Any(call => call.GenericArguments.Single().FullName != root.FullName + "/Element"))
    throw new Exception("Unexpected baseline wrapper");
foreach (var call in calls)
    call.GenericArguments[0] = bench.MainModule.ImportReference(element);
var control = root.Methods.SingleOrDefault(m => m.Name == "PointerSort");
if (control != null)
{
    var pointerSort = (GenericInstanceMethod)control.Body.Instructions.Single(i => i.OpCode == OpCodes.Call).Operand;
    if (pointerSort.Name != "Sort" || pointerSort.Parameters.Count != 2 ||
        !pointerSort.Parameters[0].ParameterType.IsPointer ||
        pointerSort.GenericArguments.Single().FullName != root.FullName + "/Element")
        throw new Exception("Unexpected pointer control");
    pointerSort.GenericArguments[0] = bench.MainModule.ImportReference(element);
}
bench.Write(args[2]);
Console.WriteLine("Bound baseline " + calls[1].FullName);
