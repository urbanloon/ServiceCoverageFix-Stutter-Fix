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
var wrapper = bench.MainModule.GetType("BurstSort").Methods.Single(m => m.Name == "GameSort");
var call = (GenericInstanceMethod)wrapper.Body.Instructions.Single(i => i.OpCode == OpCodes.Call).Operand;
if (call.ElementMethod.Name != "Sort" || call.GenericArguments.Single().FullName != "BurstSort/Element")
    throw new Exception("Unexpected baseline wrapper");
call.GenericArguments[0] = bench.MainModule.ImportReference(element);
bench.Write(args[2]);
Console.WriteLine("Bound baseline " + call.FullName);
