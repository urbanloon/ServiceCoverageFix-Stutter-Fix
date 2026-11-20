using Colossal.Mono.Cecil;
using Colossal.Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
static (int methods,string[] lines) Inspect(string path)
{
    using var assembly = AssemblyDefinition.ReadAssembly(path);
    var lines = new List<string>();
    int count = 0;
    foreach (var type in assembly.MainModule.GetTypes().OrderBy(t => t.FullName))
    {
        foreach (var method in type.Methods.OrderBy(m => m.FullName))
        {
            lines.Add($"method {method.FullName} {method.Attributes} {method.ImplAttributes}");
            count++;
            if (!method.HasBody) continue;
            var body = method.Body;
            lines.Add($"body {body.InitLocals} {body.MaxStackSize}");
            foreach (var variable in body.Variables) lines.Add("var " + variable.VariableType.FullName);
            foreach (var i in body.Instructions)
            {
                string operand = i.Operand switch {
                    Instruction target => target.Offset.ToString(),
                    Instruction[] targets => string.Join(",",targets.Select(t => t.Offset)),
                    float value => Convert.ToHexString(BitConverter.GetBytes(value)),
                    double value => Convert.ToHexString(BitConverter.GetBytes(value)),
                    MemberReference member => member.FullName,
                    VariableDefinition variable => variable.Index.ToString(),
                    ParameterDefinition param => param.Index.ToString(),
                    _ => i.Operand?.ToString() ?? ""
                };
                lines.Add($"{i.Offset} {i.OpCode.Name} {operand}");
            }
            foreach(var h in body.ExceptionHandlers)
                lines.Add($"EH {h.HandlerType} {h.CatchType} {h.TryStart?.Offset} {h.TryEnd?.Offset} {h.HandlerStart?.Offset} {h.HandlerEnd?.Offset} {h.FilterStart?.Offset}");
        }
    }
    return (count,lines.ToArray());
}
var a = Inspect(args[0]);
var b = Inspect(args[1]);
var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",b.lines)))).ToLowerInvariant();
if (!a.lines.SequenceEqual(b.lines)) throw new Exception("Normalized IL differs: "+string.Join(" / ",a.lines.Except(b.lines).Take(4))+" AGAINST "+string.Join(" / ",b.lines.Except(a.lines).Take(4)));
Console.WriteLine($"MATCH {b.methods} methods, resolved body instructions, locals, method flags and exception tables. Canonical SHA256 {digest}");
