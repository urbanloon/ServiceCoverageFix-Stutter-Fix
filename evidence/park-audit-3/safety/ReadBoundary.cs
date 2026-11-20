using System.Runtime.InteropServices;

internal static unsafe class ReadBoundary
{
    // Each header and array ends on a page inaccessible to both reads and writes.
    private sealed class Pages : IDisposable
    {
        private static readonly int Page = Environment.SystemPageSize;
        private readonly nint baseAddress;
        private readonly int dataBytes;
        private readonly int reserved;
        internal nint Data => baseAddress + dataBytes - reserved;
        internal nint Inaccessible => baseAddress + dataBytes;

        internal Pages(int bytes)
        {
            reserved = bytes;
            dataBytes = ((bytes + Page - 1) / Page) * Page;
            baseAddress = mmap(0, (nuint)(dataBytes + Page), 3, 0x22, -1, 0);
            if (baseAddress == -1 || mprotect(Inaccessible,(nuint)Page,0) != 0)
                throw new InvalidOperationException("Could not set guard page");
        }
        public void Dispose() => munmap(baseAddress, (nuint)(dataBytes + Page));
    }

    [DllImport("libc")] private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, nint off);
    [DllImport("libc")] private static extern int mprotect(nint addr, nuint length, int prot);
    [DllImport("libc")] private static extern int munmap(nint addr, nuint length);

    internal static void Run(Action<nint,nint> worker)
    {
        int cases = 0;
        foreach (var count in new[] {1, 2, 16, 17, 33, 526, 1154, 1536, 1537, 1538})
        foreach (var shortBudget in new[] {false,true})
        foreach (var unordered in new[] {false,true})
        {
            int per = shortBudget ? 2 : 1;
            using var ps = new Pages(sizeof(Provider) * count);
            using var es = new Pages(sizeof(Element) * per * count);
            using var ts = new Pages(8 * count);
            using var pheader = new Pages(32);
            using var eheader = new Pages(32);
            SetupHeader(pheader.Data,ps.Data,count);
            SetupHeader(eheader.Data,es.Data, per * count);
            var providers=(Provider*)ps.Data;
            var elements=(Element*)es.Data;
            for (int i = 0; i < count; i++)
            {
                providers[i] = new Provider { Start=i*per, Count=per, Total=.5f, Remaining=.5f };
                elements[i*per] = new Element {
                    Target=ts.Data+i*8, X=1f,Y=1f,Density=1f,Length=1f,
                    Priority=unordered && i==count/2 ? float.NaN : i%7
                };
                if (shortBudget)
                    elements[i*per+1]=new Element {
                        Target=ts.Inaccessible, X=1f, Y=1f,Density=1f,Length=1f,Priority=99f
                    };
            }
            worker(pheader.Data,eheader.Data);
            foreach (float actual in new ReadOnlySpan<float>((void*)ts.Data, 2*count))
                if(actual!=1f) throw new InvalidDataException("Missing store to reachable target");
            cases++;
        }
        Console.WriteLine($"PASS {cases} protected-page fixtures; tables/headers end at inaccessible pages, exhausted tails' target addresses point at an inaccessible page.");
    }

    private static void SetupHeader(nint header,nint body,int count)
    {
        *(nint*)header=body;
        ((int*)(header+8))[0]=count;
        ((int*)(header+8))[1]=count;
    }
}
