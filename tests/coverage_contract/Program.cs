using System;
using System.IO;
using System.Linq;
using Colossal.Mono.Cecil;
using Colossal.Mono.Cecil.Cil;
using ServiceCoverageFix;

// Mutate only scratch copies of the supplied assembly. No game is launched.
internal static class Program
{
    private static void Main(string[] paths)
    {
        if (paths.Length == 0) throw new ArgumentException("Supply one or more reviewed Game.dll paths.");
        foreach (string path in paths)
        {
            if (!CoverageContract.Validate(path))
                throw new Exception("Unrecognized test fixture " + path);

            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var source = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver });
            string copy = Path.Combine(Path.GetTempPath(), "coverage-test-" + Guid.NewGuid() + ".dll");
            try
            {
                TypeDefinition system = source.GetType("Game.Simulation.ServiceCoverageSystem");
                MethodDefinition Job(string name) => system.NestedTypes.Single(t => t.Name == name)
                    .Methods.Single(m => m.Name == "Execute");
                void Check(bool unchanged)
                {
                    source.Write(copy);
                    bool accepted;
                    try { accepted = CoverageContract.Validate(copy); }
                    catch (CoverageContractMismatch) { accepted = false; }
                    if (accepted != unchanged) throw new Exception("Unexpected behavior fingerprint for " + path);
                }

                // Alter unrelated code and reassign metadata tokens of later methods.
                var method = new MethodDefinition("SyntheticUnrelatedMethod",
                    MethodAttributes.Private | MethodAttributes.Static, source.TypeSystem.Void);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                int before = Job("ApplyCoverageJob").MetadataToken.ToInt32();
                source.Types[0].Methods.Insert(0, method);
                Check(true);
                if (Job("ApplyCoverageJob").MetadataToken.ToInt32() == before)
                    throw new Exception("Fixture failed to renumber the coverage method token.");

                foreach (MethodDefinition tested in new[]
                {
                    system.Methods.Single(m => m.Name == "OnUpdate"), Job("PrepareCoverageJob"),
                    Job("ClearCoverageJob"), Job("ProcessCoverageJob"), Job("ApplyCoverageJob"),
                    system.NestedTypes.Single(t => t.Name == "BuildingDataComparer").Methods.Single(),
                    system.NestedTypes.Single(t => t.Name == "CoverageElement").Methods.Single()
                })
                {
                    tested.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
                    Check(false);
                    tested.Body.Instructions.RemoveAt(0);
                }
                var arithmetic = Job("ApplyCoverageJob").Body.Instructions.First(i => i.OpCode == OpCodes.Ldc_R4);
                float prior = (float)arithmetic.Operand;
                arithmetic.Operand = prior + 0.25f;
                Check(false);
                arithmetic.Operand = prior;

                TypeDefinition apply = Job("ApplyCoverageJob").DeclaringType;
                var burst = apply.CustomAttributes.Single(c => c.AttributeType.Name == "BurstCompileAttribute");
                apply.CustomAttributes.Remove(burst);
                Check(false);
                apply.CustomAttributes.Add(burst);
                Check(true);
                MethodDefinition preview = source.GetType("Game.Tools.CoveragePreviewSystem").Methods.Single(m => m.Name == "OnUpdate");
                preview.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
                source.Write(copy);
                if (CoverageContract.Validate(copy))
                    throw new Exception("Modified preview caller accepted.");
                Console.WriteLine("PASS: reviewed contract, unrelated edit and token renumbering, seven method mutations, arithmetic change, Burst annotation, independently disabled preview: " + path);
            }
            finally { if (File.Exists(copy)) File.Delete(copy); }
        }
    }
}
