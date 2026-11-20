using System.Reflection;
using System.Runtime.InteropServices;

internal sealed unsafe class NativeWorker
{
    private readonly nint library;
    private readonly long* counters;
    private readonly delegate* unmanaged[Cdecl]<nint*, nint, nint, void*, int, void> producer;
    private bool checkExecution;

    internal NativeWorker(string binary, string symbol, bool mod)
    {
        library = NativeLibrary.Load(binary);
        // The harness can initialize a self-contained job. Check both callbacks
        // emitted by this exact x64 Burst compiler before calling burst.initialize.
        foreach (string prefix in new[] { "burst.initialize.externals.", "burst.initialize.statics." })
        {
            var code = (byte*)NativeLibrary.GetExport(library, prefix + symbol);
            ReadOnlySpan<byte> emptyFunction = [0x55, 0x48, 0x89, 0xe5, 0x5d, 0xc3];
            if (code[0] != 0xc3 && !new ReadOnlySpan<byte>(code, 6).SequenceEqual(emptyFunction))
                throw new NotSupportedException("Worker requires a Unity external or static initializer.");
        }
        var initialize = (delegate* unmanaged[Cdecl]<nint, nint, void>)NativeLibrary.GetExport(library, "burst.initialize");
        initialize(0, 0);
        producer = (delegate* unmanaged[Cdecl]<nint*, nint, nint, void*, int, void>)NativeLibrary.GetExport(library, symbol);
        if (mod) counters = (long*)NativeMemory.AllocZeroed(256);
        checkExecution = mod;
    }

    internal (int Providers, int Available, int Processed, int Reserved) LastWorkload()
    {
        int* values = (int*)(counters + 3);
        return (values[0], values[1], values[2], values[3]);
    }

    ~NativeWorker()
    {
        NativeMemory.Free(counters);
        if (library != 0) NativeLibrary.Free(library);
    }

    internal void Execute(nint providers, nint elements)
    {
        nint* job = stackalloc nint[] { providers, elements, (nint)counters };
        byte* ranges = stackalloc byte[64];
        new Span<byte>(ranges, 64).Clear();
        producer(job, 0, 0, ranges, 0);
        if (checkExecution)
        {
            if (counters[0] != 1 || counters[1] != 1 || counters[2] != 0)
                throw new InvalidDataException("Expected one started and completed Burst job and zero managed executions.");
            checkExecution = false;
        }
        GC.KeepAlive(this);
    }

    internal static void ValidateLayout(Type job, bool mod)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string providers = mod ? "BuildingData" : "m_BuildingData";
        string elements = mod ? "Elements" : "m_Elements";
        if (Marshal.OffsetOf(job, providers) != 0 || Marshal.OffsetOf(job, elements) != 8 ||
            (mod && Marshal.OffsetOf(job, "Diagnostics") != 16) || Marshal.SizeOf(job) != (mod ? 24 : 16))
            throw new InvalidDataException("Unexpected native job layout.");
        foreach (var (field, stride) in new[] { (providers, 24), (elements, 32) })
        {
            var list = job.GetField(field, fields)!.FieldType;
            var header = list.GetField("m_ListData", fields)!.FieldType.GetElementType()!;
            if (Marshal.OffsetOf(list, "m_ListData") != 0 || Marshal.SizeOf(list.GetGenericArguments()[0]) != stride ||
                Marshal.OffsetOf(header, "Ptr") != 0 || Marshal.OffsetOf(header, "m_length") != 8 ||
                Marshal.OffsetOf(header, "m_capacity") != 12)
                throw new InvalidDataException("Unexpected UnsafeList layout.");
        }
        if (mod)
        {
            Type counter = job.GetField("Diagnostics", fields)!.FieldType.GetElementType()!;
            if (Marshal.SizeOf(counter) > 256 || Marshal.OffsetOf(counter, "Started") != 0 ||
                Marshal.OffsetOf(counter, "Completed") != 8 || Marshal.OffsetOf(counter, "Managed") != 16)
                throw new InvalidDataException("Unexpected execution counter layout.");
            var workload = counter.GetField("Workload", fields);
            if (workload != null)
            {
                var type = workload.FieldType;
                if (Marshal.OffsetOf(counter, "Workload") != 24 || Marshal.SizeOf(type) != 16 ||
                    Marshal.OffsetOf(type, "Providers") != 0 || Marshal.OffsetOf(type, "Available") != 4 ||
                    Marshal.OffsetOf(type, "Processed") != 8 || Marshal.OffsetOf(type, "Reserved") != 12)
                    throw new InvalidDataException("Unexpected workload counter layout.");
            }
        }
    }
}
