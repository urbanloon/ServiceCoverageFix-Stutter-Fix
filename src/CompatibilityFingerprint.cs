using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace ServiceCoverageFix
{
    // A scoped code contract, not a whole-assembly version lock. Metadata tokens
    // are replaced with resolved type/member identities; their numeric values
    // can move when unrelated game code is rebuilt. Instructions, literals,
    // branches, signatures, locals and exception regions must still match.
    internal static class CompatibilityFingerprint
    {
        private static readonly Dictionary<ushort, OpCode> Opcodes = BuildOpcodes();

        internal static string Compute(MethodBase method)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(Canonicalize(method))));
            }
        }

        internal static string Canonicalize(MethodBase method)
        {
            MethodBody body = method.GetMethodBody()
                ?? throw new NotSupportedException($"No IL body for {method}.");
            byte[] code = body.GetILAsByteArray()
                ?? throw new NotSupportedException($"No IL bytes for {method}.");
            byte[] normalized = (byte[])code.Clone();
            var operands = new List<string>();
            Type[]? typeArguments = method.DeclaringType?.GetGenericArguments();
            Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            int position = 0;
            while (position < code.Length)
            {
                int start = position;
                ushort value = code[position++];
                if (value == 0xFE)
                {
                    RequireBytes(code, position, 1);
                    value = (ushort)(0xFE00 | code[position++]);
                }
                if (!Opcodes.TryGetValue(value, out OpCode opcode))
                    throw new NotSupportedException($"Unknown IL opcode 0x{value:X4} in {method}.");

                int width;
                bool token = false;
                switch (opcode.OperandType)
                {
                    case OperandType.InlineNone: width = 0; break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: width = 1; break;
                    case OperandType.InlineVar: width = 2; break;
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineI:
                    case OperandType.ShortInlineR: width = 4; break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: width = 8; break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                    case OperandType.InlineString: width = 4; token = true; break;
                    case OperandType.InlineSwitch:
                        RequireBytes(code, position, 4);
                        int count = BitConverter.ToInt32(code, position);
                        if (count < 0 || count > (code.Length - position - 4) / 4)
                            throw new NotSupportedException($"Malformed IL switch in {method}.");
                        width = checked(4 + count * 4);
                        break;
                    default:
                        // In particular, do not normalize an unreviewed calli
                        // signature or a newly introduced operand kind away.
                        throw new NotSupportedException($"Unsupported IL operand {opcode.OperandType} in {method}.");
                }
                RequireBytes(code, position, width);
                if (token)
                {
                    int metadataToken = BitConverter.ToInt32(code, position);
                    string identity;
                    if (opcode.OperandType == OperandType.InlineString)
                    {
                        identity = "S|" + Hex(Encoding.Unicode.GetBytes(method.Module.ResolveString(metadataToken)));
                    }
                    else
                    {
                        MemberInfo member = method.Module.ResolveMember(metadataToken, typeArguments, methodArguments)
                            ?? throw new NotSupportedException($"Unresolved IL token in {method}.");
                        identity = MemberKey(member);
                    }
                    operands.Add(Number(start) + ":" + identity);
                    Array.Clear(normalized, position, 4);
                }
                position += width;
            }

            var locals = new List<string>();
            foreach (LocalVariableInfo local in body.LocalVariables)
                locals.Add(TypeKey(local.LocalType) + (local.IsPinned ? " pinned" : string.Empty));
            var clauses = new List<string>();
            foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
            {
                string extra = "-";
                if (clause.Flags == ExceptionHandlingClauseOptions.Clause)
                    extra = "T|" + TypeKey(clause.CatchType
                        ?? throw new NotSupportedException("Missing catch type."));
                else if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
                    extra = Number(clause.FilterOffset);
                clauses.Add(Number((int)clause.Flags) + "," + Number(clause.TryOffset) + "," +
                    Number(clause.TryLength) + "," + Number(clause.HandlerOffset) + "," +
                    Number(clause.HandlerLength) + "," + extra);
            }
            return "SCF-IL-1\n" + MemberKey(method) + "\n" +
                "maxstack:" + Number(body.MaxStackSize) + "\n" +
                "initlocals:" + (body.InitLocals ? "1" : "0") + "\n" +
                "locals:" + string.Join(";", locals) + "\n" +
                "exceptions:" + string.Join(";", clauses) + "\n" +
                "code:" + Hex(normalized) + "\noperands:\n" +
                (operands.Count == 0 ? string.Empty : string.Join("\n", operands) + "\n");
        }

        internal static string TypeKey(Type type)
        {
            if (type.IsByRef) return TypeKey(type.GetElementType()!) + "&";
            if (type.IsPointer) return TypeKey(type.GetElementType()!) + "*";
            if (type.IsArray)
            {
                // Rank-one non-vector arrays have a different type contract.
                string suffix = type.GetArrayRank() == 1 && type.Name.EndsWith("[*]", StringComparison.Ordinal)
                    ? "[*]" : "[" + new string(',', type.GetArrayRank() - 1) + "]";
                return TypeKey(type.GetElementType()!) + suffix;
            }
            if (type.IsGenericParameter)
                return (type.DeclaringMethod == null ? "!" : "!!") + Number(type.GenericParameterPosition);
            if (type.IsGenericType)
            {
                Type[] arguments = type.GetGenericArguments();
                string[] names = Array.ConvertAll(arguments, TypeKey);
                return type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", names) + ">";
            }
            return type.FullName ?? throw new NotSupportedException("Unnamed type in compatibility contract.");
        }

        private static string MemberKey(MemberInfo member)
        {
            if (member is Type type) return "T|" + TypeKey(type);
            if (member.DeclaringType == null)
                throw new NotSupportedException("Global members are outside the compatibility contract.");
            string owner = TypeKey(member.DeclaringType);
            if (member is FieldInfo field)
                return "F|" + owner + "|" + field.Name + "|" + TypeKey(field.FieldType);
            if (member is MethodBase method)
            {
                string[] parameters = Array.ConvertAll(method.GetParameters(), p => TypeKey(p.ParameterType));
                string[] arguments = method.IsGenericMethod
                    ? Array.ConvertAll(method.GetGenericArguments(), TypeKey) : Array.Empty<string>();
                string result = method is MethodInfo info ? TypeKey(info.ReturnType) : "System.Void";
                return "M|" + owner + "|" + method.Name + "|" + result + "|" +
                    string.Join(",", parameters) + "|" + string.Join(",", arguments) + "|" +
                    Number((int)method.CallingConvention);
            }
            throw new NotSupportedException($"Unsupported member kind: {member.MemberType}.");
        }

        private static Dictionary<ushort, OpCode> BuildOpcodes()
        {
            var result = new Dictionary<ushort, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is OpCode opcode)
                    result[unchecked((ushort)opcode.Value)] = opcode;
            }
            return result;
        }

        private static void RequireBytes(byte[] code, int position, int count)
        {
            if (position < 0 || count < 0 || position > code.Length - count)
                throw new NotSupportedException("Truncated IL in compatibility contract.");
        }

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }
}
