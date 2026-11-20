using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

internal unsafe sealed class CounterProbe : IDisposable
{
    private readonly nint library;
    private readonly delegate* unmanaged[Cdecl]<nint*, nint, nint, void*, int, void> producer;
    private readonly long* counters = (long*)NativeMemory.AllocZeroed(256);
    private nint argument;

    internal CounterProbe(string directory)
    {
        // VerifiedWorkers has already verified the precise inputs, ISA, binary,
        // layout and producer and initialized this library above.
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"worker.json")));
        library = NativeLibrary.Load(Path.GetFullPath(Path.Combine(directory,"coverage.so")));
        string export = manifest.RootElement.GetProperty("symbol").GetString()!;
        producer = (delegate* unmanaged[Cdecl]<nint*,nint,nint,void*,int,void>)NativeLibrary.GetExport(library,export);
    }
    internal void SetEnabled(bool enabled) => argument = enabled ? (nint)counters : 0;
    internal void Execute(nint providers, nint elements)
    {
        nint* job = stackalloc nint[] {providers,elements,argument};
        byte* ranges = stackalloc byte[64];
        new Span<byte>(ranges,64).Clear();
        producer(job,0,0,ranges,0);
    }
    internal long Started => counters[0];
    internal long Completed => counters[1];
    public void Dispose() { NativeMemory.Free(counters); NativeLibrary.Free(library); }
}

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct Time { internal long Seconds, Nanoseconds; }
    [DllImport("libc", EntryPoint="clock_gettime")] private static extern int Clock(int clock, out Time time);
    private static long Cpu()
    {
        if (Clock(3,out var t) != 0) throw new InvalidOperationException("thread CPU clock");
        return t.Seconds*1_000_000_000L + t.Nanoseconds;
    }
    private static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var loader = new VerifiedWorkers(args[0]);
        var stock = loader.Load(args[1], "vanilla");
        var candidate = loader.Load(args[2], "mod");
        using var probe = new CounterProbe(args[2]);
        var execute = probe.Execute;
        Console.WriteLine("shape,providers,elements,batch,round,enabled,wall_ns_per_job,cpu_ns_per_job");
        foreach (var (shape,p,length,mode,batch) in new[] {
            ("empty",0,0,0,512), ("one",1,32,0,256), ("small",8,32,1,128),
            ("medium",49,128,0,8), ("park-interleaved",526,970,0,1), ("park-ties",526,970,1,1)})
        {
            var input=Inputs.Make(78491,p,length,mode,true);
            using var oracle=new Pass(input);
            oracle.Execute(stock.Execute);
            oracle.Check();
            var expected=oracle.Outputs();
            oracle.Reset();
            oracle.Execute(candidate.Execute);
            oracle.Check();
            if (!expected.AsSpan().SequenceEqual(oracle.Outputs())) throw new Exception("stock parity");
            var buffers=Enumerable.Range(0,batch).Select(_=>new Pass(input)).ToArray();
            try
            {
                for(int round=-4; round<32; ++round)
                {
                    for(int order=0; order<2; ++order)
                    {
                        bool enabled=((round+order)&1)!=0;
                        probe.SetEnabled(enabled);
                        long before=probe.Started;
                        foreach (var pass in buffers) pass.Reset();
                        long cpu=Cpu();
                        long wall=Stopwatch.GetTimestamp();
                        foreach(var pass in buffers) pass.Execute(execute);
                        double wallNs=Stopwatch.GetElapsedTime(wall).TotalNanoseconds/batch;
                        double cpuNs=(Cpu()-cpu)/(double)batch;
                        long expectedCount=before+(enabled?batch:0);
                        if(probe.Started!=expectedCount || probe.Completed!=expectedCount) throw new Exception("counter increment");
                        foreach (var pass in buffers)
                        {
                            pass.Check();
                            if (!expected.AsSpan().SequenceEqual(pass.Outputs())) throw new Exception("output parity");
                        }
                        if (round>=0) Console.WriteLine($"{shape},{p},{p*length},{batch},{round},{(enabled?1:0)},{wallNs:F3},{cpuNs:F3}");
                    }
                }
            }
            finally { foreach(var pass in buffers) pass.Dispose(); }
        }
        GC.KeepAlive(stock); GC.KeepAlive(candidate);
    }
}
