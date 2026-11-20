using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Unity.Jobs
{
    public interface IJob { }
    public sealed class Work { public bool Done, FailCompletion; }
    public struct JobHandle
    {
        public Work? Work;
        public bool IsCompleted => Work == null || Work.Done;
        public void Complete() { if (Work?.FailCompletion == true) throw new InvalidOperationException("completion"); if(Work != null) Work.Done = true; }
    }
    public static class IJobExtensions { public static JobHandle Schedule<T>(T job, JobHandle dependency) where T : struct, IJob => ServiceCoverageFix.Recorder.Record(job, 0, dependency); }
}
namespace Unity.Entities
{
    public interface IJobChunk { }
    public struct EntityQuery { public int Id; }
    public static class JobChunkExtensions
    { public static Unity.Jobs.JobHandle ScheduleParallel<T>(T job, EntityQuery query, Unity.Jobs.JobHandle dependency) where T:struct,IJobChunk => ServiceCoverageFix.Recorder.Record(job, query.Id, dependency); }
}
namespace Unity.Collections.LowLevel.Unsafe
{
    public static unsafe class UnsafeUtility
    {
        public static void* AddressOf<T>(ref T value) where T:struct => System.Runtime.CompilerServices.Unsafe.AsPointer(ref value);
        public static void CopyPtrToStructure<T>(void* ptr,out T value) where T:struct => value=System.Runtime.CompilerServices.Unsafe.ReadUnaligned<T>(ptr);
    }
}
namespace Game.Simulation { public struct WaterPipeFlowJob : Unity.Jobs.IJob { public int Id; } }
namespace Game.Pathfind { public class PathfindQueueSystem { public struct PathfindWorkerJob : Unity.Jobs.IJob { public int Id; } } }
namespace HarmonyLib
{
    public class PatchProcessor { internal static readonly object locker = new object(); }
    public class HarmonyMethod { public HarmonyMethod(MethodInfo m) {} }
    public sealed class Patches { public List<string> Owners = new List<string>(); }
    public sealed class Harmony
    {
        public readonly string Id;
        internal static readonly Dictionary<MethodInfo,Patches> Installed = new();
        internal static bool FailUnpatch;
        internal static int RegistryReads;
        internal static Action? BeforePublish, BeforeLookup;
        public Harmony(string id) { Id=id; }
        public static Patches? GetPatchInfo(MethodInfo method)
        {
            BeforeLookup?.Invoke();
            lock(PatchProcessor.locker) { RegistryReads++; return Installed.TryGetValue(method,out var p)?p:null; }
        }
        public void Patch(MethodInfo method,HarmonyMethod transpiler)
        {
            lock(PatchProcessor.locker) {
                ServiceCoverageFix.BetaPatches.Recompose(method); BeforePublish?.Invoke();
                Installed.TryAdd(method,new Patches()); Installed[method].Owners.Add(Id);
            }
        }
        public void UnpatchAll(string id)
        {
            lock(PatchProcessor.locker) {
                if(FailUnpatch)throw new Exception("unpatch");
                foreach(var item in Installed) if(item.Value.Owners.Contains(id)) {
                    ServiceCoverageFix.BetaPatches.Recompose(item.Key); BeforePublish?.Invoke(); item.Value.Owners.Remove(id);
                }
            }
        }
    }
}
namespace ServiceCoverageFix
{
    internal static class Mod { internal const string HarmonyId="coverage"; internal static readonly Logger Log=new(); internal sealed class Logger { public void Info(string s) {} } }
    internal static class BetaPatches
    {
        private static void Housing(){} private static void Seller(){} private static void Water(){} private static void Route(){}
        internal static MethodInfo Validate(BetaFeature f)=>Transpiler(f);
        internal static MethodInfo Transpiler(BetaFeature f)=>typeof(BetaPatches).GetMethod(f.ToString(),BindingFlags.NonPublic|BindingFlags.Static)!;
        private static readonly int[] Generations=new int[4];
        internal static int Generation(BetaFeature f)=>System.Threading.Volatile.Read(ref Generations[(int)f]);
        internal static void Recompose(MethodInfo target) { if(Enum.TryParse<BetaFeature>(target.Name,out var f))System.Threading.Interlocked.Increment(ref Generations[(int)f]); }
        internal static BetaFeature? OtherPublisher;
        internal static bool SharesPublisher(BetaFeature f) => f != OtherPublisher;
        internal static BetaFeature? Invalidated;
        internal static bool IsUsable(BetaFeature f)=>Invalidated!=f;
    }
    internal struct OriginalHousing : Unity.Entities.IJobChunk { public int Id; }
    internal struct OriginalSeller : Unity.Entities.IJobChunk { public int Id; }
    internal struct HousingJob : Unity.Entities.IJobChunk { public int Id; }
    internal struct SellerJob : Unity.Entities.IJobChunk { public int Id; }
    internal struct RouteJob : Unity.Jobs.IJob { public int Id; }
    internal struct WaterJob : Unity.Jobs.IJob { public int Id; }
    internal static class WaterFlowSchedule { internal static Unity.Jobs.JobHandle Schedule(Game.Simulation.WaterPipeFlowJob job,Unity.Jobs.JobHandle dependency)=>Recorder.Record(new WaterJob{Id=job.Id},0,dependency); }
    internal static class Recorder
    {
        internal static readonly List<(Type type,int id,int query,Unity.Jobs.JobHandle dependency,Unity.Jobs.Work work)> Calls=new();
        internal static bool Fail;
        internal static Unity.Jobs.JobHandle Record<T>(T job,int query,Unity.Jobs.JobHandle dep) where T:struct
        { var w=new Unity.Jobs.Work();Calls.Add((typeof(T),(int)typeof(T).GetField("Id")!.GetValue(job)!,query,dep,w));if(Fail)throw new Exception("scheduler");return new Unity.Jobs.JobHandle{Work=w}; }
    }
}
