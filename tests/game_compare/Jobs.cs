using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

// Invoke the supplied compiled game/mod jobs directly, with guarded buffers owned by
// this process. The game is never started. Unity's managed list, sort, and math methods
// run from the supplied assemblies. This measures a .NET JIT worker, not a Burst frame.
internal sealed class Jobs
{
    internal readonly Action<IntPtr, IntPtr> Vanilla;
    internal readonly Action<IntPtr, IntPtr> Candidate;
    internal readonly Action<IntPtr, IntPtr>? Baseline;

    internal Jobs(string managedDirectory, string candidateFile, string? baselineFile)
    {
        var managed = Path.GetFullPath(managedDirectory);
        var shared = AssemblyLoadContext.Default;
        shared.Resolving += (_, name) => {
            var path = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(path) ? shared.LoadFromAssemblyPath(path) : null;
        };
        var game = shared.LoadFromAssemblyPath(Path.Combine(managed, "Game.dll"));
        Vanilla = Create(game.GetType("Game.Simulation.ServiceCoverageSystem+ApplyCoverageJob", true)!, "m_BuildingData", "m_Elements");
        Candidate = LoadMod(candidateFile, "candidate");
        if (baselineFile != null) Baseline = LoadMod(baselineFile, "baseline");
    }

    private static Action<IntPtr, IntPtr> LoadMod(string file, string name)
    {
        var context = new AssemblyLoadContext(name);
        context.Resolving += (_, requested) => AssemblyLoadContext.Default.LoadFromAssemblyName(requested);
        var mod = context.LoadFromAssemblyPath(Path.GetFullPath(file));
        return Create(mod.GetType("ServiceCoverageFix.OptimizedApplyCoverageJob", true)!, "BuildingData", "Elements");
    }

    private static int SizeOf(Type type) => (int)typeof(System.Runtime.CompilerServices.Unsafe)
        .GetMethod("SizeOf")!.MakeGenericMethod(type).Invoke(null, null)!;

    private static Action<IntPtr, IntPtr> Create(Type job, string providers, string elements)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var call = new DynamicMethod("RunIsolatedCoverage", typeof(void),
            new[] { typeof(IntPtr), typeof(IntPtr) }, job.Module, true);
        var il = call.GetILGenerator();
        var local = il.DeclareLocal(job);
        foreach (var (field, size, index) in new[] { (providers, 24, 0), (elements, 32, 1) })
        {
            var list = job.GetField(field, fields) ?? throw new MissingFieldException(job.FullName, field);
            var listData = list.FieldType.GetField("m_ListData", fields)!;
            var header = listData.FieldType.GetElementType()!;
            if (!listData.FieldType.IsPointer || Marshal.SizeOf(list.FieldType.GetGenericArguments()[0]) != size ||
                Marshal.OffsetOf(header, "Ptr").ToInt32() != 0 ||
                Marshal.OffsetOf(header, "m_length").ToInt32() != IntPtr.Size ||
                Marshal.OffsetOf(header, "m_capacity").ToInt32() != IntPtr.Size + 4 ||
                SizeOf(header) > 32 || IntPtr.Size != 8)
                throw new InvalidOperationException("Incompatible game list header or element layout.");
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Ldflda, list);
            il.Emit(index == 0 ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, listData);
        }
        il.Emit(OpCodes.Ldloca, local);
        il.Emit(OpCodes.Call, job.GetMethod("Execute", fields)!);
        il.Emit(OpCodes.Ret);
        return call.CreateDelegate<Action<IntPtr, IntPtr>>();
    }
}
