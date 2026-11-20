using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Size=24)]
internal struct Provider
{
    internal long Entity;
    internal int Start, Count;
    internal float Total, Remaining;
}

[StructLayout(LayoutKind.Explicit, Size=32)]
internal struct Element
{
    [FieldOffset(0)] internal nint Target;
    [FieldOffset(8)] internal float X;
    [FieldOffset(12)] internal float Y;
    [FieldOffset(16)] internal float Priority;
    [FieldOffset(20)] internal float Density;
    [FieldOffset(24)] internal float Length;
}

internal sealed record Inputs(Provider[] Providers, Element[] Elements, float[] InitialTargets)
{
    internal static Inputs Make(int seed, int providerCount, int maxLength, int mode, bool benchmark = false)
    {
        var rng = new Random(seed);
        var providers = new Provider[providerCount];
        var elements = new List<Element>();
        var targets = new float[2 * (mode == 2 ? 1 : Math.Max(3, benchmark ? providerCount * 32 : providerCount))];
        if (mode == 4) for (int i=0; i<targets.Length; i++) targets[i] = rng.NextSingle();
        for (int i=0; i<providerCount; i++)
        {
            int length = benchmark ? maxLength : rng.Next(maxLength + 1);
            var budget = benchmark ? 1000000f : 1f + 32f * rng.NextSingle();
            providers[i] = new Provider { Entity=i, Start=elements.Count, Count=length, Total=budget,
                Remaining=benchmark ? budget : (i % 13 == 0 ? 0f : budget * rng.NextSingle()) };
            int start = elements.Count;
            for (int j=0; j<length; j++)
            {
                float prior = mode == 1 ? rng.Next(8) / 8f : rng.NextSingle();
                float dx = rng.NextSingle() * 0.4f;
                elements.Add(new Element {Target=rng.Next(targets.Length/2), X=prior + dx, Y=prior - dx,
                    Priority=prior, Density=j % 2 == 0 ? 0.5f : 1f, Length=1f + rng.NextSingle()*3f});
            }
            elements.Sort(start, length, Comparer<Element>.Create((a,b) => b.Priority.CompareTo(a.Priority)));
            if (length > 0 && mode >= 5)
            {
                var index = start + (mode == 5 ? 0 : rng.Next(length));
                var element = elements[index];
                element.X = float.NaN;
                element.Priority = (i%2 == 0) ? float.NaN : BitConverter.Int32BitsToSingle(0x7fc00005);
                elements[index] = element;
                if (mode == 7 && length > 2) {
                    int other = start + rng.Next(length);
                    (elements[start],elements[other])=(elements[other],elements[start]);
                }
            }
        }
        return new Inputs(providers, elements.ToArray(), targets);
    }
}

internal unsafe sealed class Block : IDisposable
{
    private byte* allocation;
    internal readonly int Length;
    internal byte* Data => allocation + 64;
    internal Block(int bytes)
    {
        Length = bytes;
        allocation = (byte*)NativeMemory.AllocZeroed(checked((nuint)bytes + 128));
        new Span<byte>(allocation,64).Fill(0x5a);
        new Span<byte>(Data+bytes,64).Fill(0xc3);
    }
    internal void Check()
    {
        for(int i=0;i<64;i++) if(allocation[i]!=0x5a || Data[Length+i]!=0xc3)
            throw new InvalidOperationException("A native buffer guard was overwritten.");
    }
    public void Dispose() { NativeMemory.Free(allocation); allocation=null; }
}

internal unsafe sealed class Pass : IDisposable
{
    private readonly Inputs source;
    private readonly Block providers, elements, targets, headers;
    internal Pass(Inputs input)
    {
        source = input;
        providers = new Block(checked(input.Providers.Length * sizeof(Provider)));
        elements = new Block(checked(input.Elements.Length * sizeof(Element)));
        targets = new Block(checked(input.InitialTargets.Length * sizeof(float)));
        headers = new Block(64);
        input.Elements.AsSpan().CopyTo(new Span<Element>(elements.Data,input.Elements.Length));
        var buffer = (Element*)elements.Data;
        for (int i=0;i<input.Elements.Length;i++) buffer[i].Target = (nint)(targets.Data + 8*buffer[i].Target);
        Reset();
    }
    internal void Reset()
    {
        source.Providers.AsSpan().CopyTo(new Span<Provider>(providers.Data,source.Providers.Length));
        source.InitialTargets.AsSpan().CopyTo(new Span<float>(targets.Data,source.InitialTargets.Length));
        Header(headers.Data, providers.Data, source.Providers.Length);
        Header(headers.Data+32, elements.Data, source.Elements.Length);
    }
    private static void Header(byte* address, byte* data,int length)
    {
        *(byte**)address=data;
        ((int*)(address+IntPtr.Size))[0]=length;
        ((int*)(address+IntPtr.Size))[1]=length;
    }
    internal void Execute(Action<nint,nint> job) => job((nint)headers.Data,(nint)(headers.Data+32));
    internal byte[] Outputs() => new ReadOnlySpan<byte>(targets.Data,targets.Length).ToArray();
    internal void Check()
    {
        providers.Check(); elements.Check(); targets.Check(); headers.Check();
        var buffer = (Element*)elements.Data;
        for(int i=0;i<source.Elements.Length;i++)
        {
            var expected = source.Elements[i];
            expected.Target=(nint)(targets.Data+8*expected.Target);
            if(!new ReadOnlySpan<byte>(&expected,sizeof(Element)).SequenceEqual(new ReadOnlySpan<byte>(buffer+i,sizeof(Element))))
                throw new InvalidOperationException("Coverage element mutated.");
        }
    }
    public void Dispose() { headers.Dispose(); targets.Dispose(); elements.Dispose(); providers.Dispose(); }
}
