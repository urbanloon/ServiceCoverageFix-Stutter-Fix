using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

internal static class Reader
{
    internal static List<CodeInstruction> Read(MethodInfo method, ILGenerator generator, bool inspectOnly = false)
    {
        var body=method.GetMethodBody()!;
        // Inspection can read the call operands without emitting the exception regions.
        if(!inspectOnly && body.ExceptionHandlingClauses.Count!=0)throw new NotSupportedException("New OnUpdate exception regions require review.");
        var bytes=body.GetILAsByteArray()!;
        var ops=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)
            .Select(f=>(OpCode)f.GetValue(null)!).ToDictionary(o=>unchecked((ushort)o.Value));
        var offsets=new Dictionary<int,CodeInstruction>();
        var jumps=new Dictionary<int,Label>();
        int p=0;
        int Int32() {var value=BitConverter.ToInt32(bytes,p);p+=4;return value;}
        Label Target(int destination) {
            if(!jumps.TryGetValue(destination,out var label))jumps[destination]=label=generator.DefineLabel();
            return label;
        }
        var module=method.Module;
        while(p<bytes.Length)
        {
            int start=p;
            ushort code=bytes[p++];
            if(code==0xfe)code=(ushort)(0xfe00|bytes[p++]);
            var op=ops[code];
            object? operand=null;
            switch(op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI: operand=(sbyte)bytes[p++];break;
                case OperandType.ShortInlineVar:operand=bytes[p++];break;
                case OperandType.ShortInlineBrTarget: {int delta=(sbyte)bytes[p++];operand=Target(p+delta);break;}
                case OperandType.InlineBrTarget: {int delta=Int32();operand=Target(p+delta);break;}
                case OperandType.InlineVar:operand=BitConverter.ToUInt16(bytes,p);p+=2;break;
                case OperandType.InlineI:operand=Int32();break;
                case OperandType.InlineI8:operand=BitConverter.ToInt64(bytes,p);p+=8;break;
                case OperandType.InlineR:operand=BitConverter.ToDouble(bytes,p);p+=8;break;
                case OperandType.ShortInlineR:operand=BitConverter.ToSingle(bytes,p);p+=4;break;
                case OperandType.InlineSwitch: {
                    int n=Int32(),end=p+4*n;
                    var labels=new Label[n];
                    for(int i=0;i<n;i++)labels[i]=Target(end+Int32());
                    operand=labels;break;}
                case OperandType.InlineMethod:operand=module.ResolveMethod(Int32());break;
                case OperandType.InlineField:operand=module.ResolveField(Int32());break;
                case OperandType.InlineString:operand=module.ResolveString(Int32());break;
                case OperandType.InlineType:operand=module.ResolveType(Int32());break;
                case OperandType.InlineTok:operand=module.ResolveMember(Int32());break;
                default:throw new NotSupportedException(op.OperandType.ToString());
            }
            offsets.Add(start,new CodeInstruction(op,operand));
        }
        foreach(var pair in jumps)offsets[pair.Key].labels.Add(pair.Value);
        return offsets.Values.ToList();
    }
}
