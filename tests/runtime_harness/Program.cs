using System;
using System.Runtime.InteropServices;
using ServiceCoverageFix;
using Unity.Collections;
using Unity.Jobs;

internal static class Program
{
    // Same field names and element layouts as the actual private game job.
    [StructLayout(LayoutKind.Sequential, Pack=4, Size=24)]
    private struct BuildingData { public int a,b,c,d; public float e,f; }
    [StructLayout(LayoutKind.Explicit, Size=32)]
    private struct CoverageElement { [FieldOffset(0)] public long pointer; }
    private struct OriginalJob : IJob
    {
        public NativeList<BuildingData> m_BuildingData;
        public NativeList<CoverageElement> m_Elements;
        internal static int Executions;
        public void Execute() { Executions++; }
    }
    private struct WrongJob : IJob { public void Execute() {} }
    private static void Check(bool value, string message) { if(!value) throw new Exception(message); }
    public static void Main()
    {
        ApplyCoverageScheduleBridge.ValidateBridge(typeof(OriginalJob));
        Check(AllocationTracker.Live==0,"Probe leaks allocations");
        Check(OriginalJob.Executions==0,"Probe executed vanilla");
        bool rejected=false;
        try { ApplyCoverageScheduleBridge.ValidateBridge(typeof(WrongJob)); } catch { rejected=true; }
        Check(rejected,"Missing fields must fail before patch installation");
        Check(AllocationTracker.Live==0,"Failed probe leaks allocations");
        RuntimeDiagnostics.Initialize();
        Check(!RuntimeDiagnostics.Snapshot().Installed,"Initialize claims installed prematurely");
        RuntimeDiagnostics.MarkInstalled();
        using(var b=new NativeList<BuildingData>(1,Allocator.TempJob))
        using(var e=new NativeList<CoverageElement>(1,Allocator.TempJob))
        {
            var original=new OriginalJob{m_BuildingData=b,m_Elements=e};
            var h=ApplyCoverageScheduleBridge.ScheduleOptimized(original,default);
            var s=RuntimeDiagnostics.Snapshot();
            Check(s.Scheduled==1&&s.Completed==0,"Scheduling falsely claims execution");
            Check(s.State=="Awaiting execution","Pending state wrong");
            h.Complete();
            s=RuntimeDiagnostics.Snapshot();
            Check(s.Started==1&&s.Completed==1&&s.ManagedExecutions==1,"Worker counters wrong");
            Check(s.State=="Managed execution observed","Managed code falsely claims Burst");
            Check(OriginalJob.Executions==0,"Successful replacement also ran vanilla");
            int before=IJobExtensions.Schedules;
            IJobExtensions.FailNext=true;
            var fallback=ApplyCoverageScheduleBridge.ScheduleOptimized(original,default);
            fallback.Complete();
            s=RuntimeDiagnostics.Snapshot();
            Check(s.Fallbacks==1&&s.Scheduled==1&&OriginalJob.Executions==1,"Fallback counters wrong");
            Check(IJobExtensions.Schedules==before+1,"Fallback double-scheduled work");
            Check(s.State=="Fallback observed"&&s.LastFailure.Contains("Injected"),"Fallback not visible in status");
            IJobExtensions.FailNext=true;
            ApplyCoverageScheduleBridge.ScheduleOptimized(original,default).Complete();
            Check(RuntimeDiagnostics.Snapshot().Fallbacks==2,"Repeated fallback not counted");
            var pending=ApplyCoverageScheduleBridge.ScheduleOptimized(original,default);
            RuntimeDiagnostics.Dispose();
            Check(pending.IsCompleted,"Unload freed diagnostics before job completion");
            Check(!RuntimeDiagnostics.Snapshot().Installed,"Unload still claims installed");
        }
        Check(AllocationTracker.Live==0,"Lifecycle leaked native memory");
        RuntimeDiagnostics.Initialize(); RuntimeDiagnostics.MarkInstalled();
        var fresh=RuntimeDiagnostics.Snapshot();
        Check(fresh.Scheduled==0&&fresh.Fallbacks==0&&fresh.Completed==0,"Reload retained old state");
        RuntimeDiagnostics.Dispose();
        Check(AllocationTracker.Live==0,"Reload leaked native memory");
        Console.WriteLine("PASS: bridge probes (0/1/2 providers), bad fields, pending execution, managed detection, repeated fallback, single scheduling, unload completion, reload, allocation lifetime.");
    }
}
