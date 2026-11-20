using System.Reflection;
using System.Runtime.Loader;
using Burst.Compiler.IL.Helpers;
using Mono.Cecil;
using static System.Reflection.BindingFlags;

// Compare Burst's encoding of each managed job producer with its named PE export.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Expected historical Managed directory, Burst .Runtime directory and job-roots.csv");
            return 2;
        }
        string burst = Path.GetFullPath(args[1]);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(burst, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        Inspect(Path.GetFullPath(args[0]), burst, args[2]);
        return 0;
    }

    private static void Inspect(string managed, string burst, string roots)
    {
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);
        resolver.AddSearchDirectory(burst);
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(managed, "Game.dll"),
            new ReaderParameters { AssemblyResolver = resolver });
        var compiler = typeof(Hash128).Assembly;
        var resolve = compiler.GetType("Burst.Compiler.IL.Server.EntryPointMethodFinder")!
            .GetMethod("ResolveMethod", Static | NonPublic)!;
        var encode = compiler.GetType("Burst.Compiler.IL.Helpers.CecilInterop")!
            .GetMethods(Static | Public | NonPublic)
            .Single(m => m.Name == "EncodeMethodToString" && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.Name == "MethodReference");
        var jobs = game.MainModule.GetTypes().ToDictionary(t => t.FullName);
        foreach (string row in File.ReadLines(roots).Skip(1))
        {
            var values = row.Split(',');
            var job = jobs[values[1]];
            var producer = job.Interfaces.Select(i => i.InterfaceType.Resolve())
                .SelectMany(i => i.CustomAttributes).Single(a => a.AttributeType.Name == "JobProducerTypeAttribute");
            var type = ((TypeReference)producer.ConstructorArguments[0].Value).Resolve();
            var closed = new GenericInstanceType(type);
            closed.GenericArguments.Add(job);
            var entry = type.Methods.Single(m => m.IsStatic && m.Name == "Execute");
            var reference = (MethodReference)resolve.Invoke(null, new object[] { closed, entry })!;
            string encoded = (string)encode.Invoke(null, new object[] { reference.FullyResolve() })!;
            string actual = Hash128.FromString(encoded).ToString();
            if (actual != values[2]) throw new InvalidDataException(job.FullName + " export mismatch");
            Console.WriteLine($"{job.FullName} {actual}");
        }
    }
}
