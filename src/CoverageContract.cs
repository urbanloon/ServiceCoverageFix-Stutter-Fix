using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Colossal.Mono.Cecil;
using Colossal.Mono.Cecil.Cil;

namespace ServiceCoverageFix
{
    // The original job's arithmetic, ordering, upstream input construction and
    // the scheduling/disposal chain are all part of the replacement's contract.
    // Resolve metadata operands to names so unrelated row renumbering is allowed.
    internal static class CoverageContract
    {
        internal const string ReviewedFingerprint = "162c6f1c9df6ba8e23edbc8b96c350f4c1ed074ac88830c8dee1355d40131711";
        internal const string ReviewedPreviewFingerprint = "ebfc761f13fd403c36065f33a7b1a9df84b0d803e9e32157c26ee952af3909b9";
        private static readonly string[] Methods =
        {
            "OnUpdate", "ClearCoverageJob.Execute", "PrepareCoverageJob.Execute",
            "ProcessCoverageJob.Execute", "CoverageElement.CompareTo",
            "BuildingDataComparer.Compare", "ApplyCoverageJob.Execute"
        };

        // The preview caller is optional and shares the same worker jobs as the simulation.
        internal static bool Validate(string assemblyPath)
        {
            using (var module = ModuleDefinition.ReadModule(assemblyPath))
            {
                string fingerprint = Fingerprint(module, false);
                if (fingerprint != ReviewedFingerprint)
                    throw new CoverageContractMismatch("Unrecognized simulation coverage contract " + fingerprint);

                try { return Fingerprint(module, true) == ReviewedPreviewFingerprint; }
                catch (CoverageContractMismatch) { return false; }
            }
        }

        internal static string Fingerprint(ModuleDefinition module, bool preview)
        {
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes))
            using (var sha = SHA256.Create())
            {
                string typeName = preview ? "Game.Tools.CoveragePreviewSystem" : "Game.Simulation.ServiceCoverageSystem";
                TypeDefinition system = module.GetType(typeName) ?? throw new CoverageContractMismatch(typeName + " is missing.");
                foreach (string member in preview ? new[] { "OnUpdate" } : Methods)
                {
                    string[] parts = member.Split('.');
                    TypeDefinition? type = parts.Length == 1 ? system :
                        system.NestedTypes.FirstOrDefault(t => t.Name == parts[0]);
                    var methods = type?.Methods.Where(m => m.Name == parts[parts.Length - 1]).ToArray();
                    if (methods == null || methods.Length != 1)
                        throw new CoverageContractMismatch("Unexpected coverage method signature for " + member);
                    WriteBody(writer, methods[0]);
                }
                writer.Flush();
                bytes.Position = 0;
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void WriteBody(BinaryWriter writer, MethodDefinition method)
        {
            writer.Write(method.FullName);
            WriteBurstAttributes(writer, method.DeclaringType);
            WriteBurstAttributes(writer, method);
            MethodBody body = method.Body;
            if (body == null || body.HasExceptionHandlers)
                throw new CoverageContractMismatch("Unexpected coverage method body in " + method.FullName);
            writer.Write(body.InitLocals);
            writer.Write(body.Variables.Count);
            foreach (VariableDefinition local in body.Variables)
                writer.Write(local.VariableType.FullName);

            writer.Write(body.Instructions.Count);
            foreach (Instruction instruction in body.Instructions)
            {
                writer.Write(instruction.OpCode.Value);
                switch (instruction.Operand)
                {
                    case null: break;
                    case MemberReference member:
                        writer.Write(member.FullName);
                        // Include the declaring assembly identity of resolved operands,
                        // without binding to its MVID or irrelevant metadata row numbers.
                        writer.Write((member is TypeReference type ? type : member.DeclaringType).Scope.Name);
                        break;
                    case Instruction target: writer.Write(target.Offset); break;
                    case Instruction[] targets:
                        writer.Write(targets.Length);
                        foreach (Instruction branch in targets) writer.Write(branch.Offset);
                        break;
                    case VariableDefinition variable: writer.Write(variable.Index); break;
                    case ParameterDefinition parameter: writer.Write(parameter.Index); break;
                    case sbyte int8: writer.Write(int8); break;
                    case byte uint8: writer.Write(uint8); break;
                    case int int32: writer.Write(int32); break;
                    case long int64: writer.Write(int64); break;
                    case float single: writer.Write(single); break;
                    case double real: writer.Write(real); break;
                    case string literal: writer.Write(literal); break;
                    default: throw new CoverageContractMismatch("Unsupported coverage IL operand in " + method.FullName);
                }
            }
        }

        private static void WriteBurstAttributes(BinaryWriter writer, ICustomAttributeProvider provider)
        {
            var attributes = provider.CustomAttributes
                .Where(attribute => attribute.AttributeType.FullName == "Unity.Burst.BurstCompileAttribute").ToArray();
            writer.Write(attributes.Length);
            foreach (CustomAttribute attribute in attributes)
            {
                writer.Write(attribute.Constructor.FullName);
                byte[] blob = attribute.GetBlob();
                writer.Write(blob.Length);
                writer.Write(blob);
            }
        }
    }
    internal sealed class CoverageContractMismatch : Exception
    {
        internal CoverageContractMismatch(string message) : base(message) { }
    }
}
