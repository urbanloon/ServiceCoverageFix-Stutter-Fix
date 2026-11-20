using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Colossal.Logging;
using Game.SceneFlow;
using HarmonyLib;
using ServiceCoverageFix;
using Unity.Collections;
using Unity.Jobs;

// Exercises production Mod.OnDispose and the production scheduling bridge.
// Only their host dependencies are replaced by fault-injectable adapters.
internal static class FailurePathTests
{
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
    private struct DependencyJob : IJob
    {
        internal static int Executions;
        public void Execute() { Executions++; }
    }
    private sealed class UnprintableException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("Injected exception formatting failure");
    }
    private static void Check(bool condition,string message)
    { if(!condition)throw new Exception(message); }
    private static void SetField(Mod mod,string name,object value) =>
        typeof(Mod).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(mod,value);

    internal static void Run()
    {
        var failures=new List<Exception>();
        for(int scenario=0;scenario<4;scenario++)
        {
            try { ReportingFailure(scenario); }
            catch(Exception error) { failures.Add(new Exception($"Reporting scenario {scenario}",error)); }
        }
        try { VanillaFailure(); }
        catch(Exception error) { failures.Add(new Exception("Vanilla scheduling failure",error)); }
        for(int mask=0;mask<16;mask++)
        {
            try { UnloadFailures(mask); }
            catch(Exception error) { failures.Add(new Exception($"Unload failure mask {mask}",error)); }
        }
        if(failures.Count!=0)throw new AggregateException("Failure-path regressions",failures);
        Console.WriteLine("PASS: reporting/formatting failures preserve one vanilla job and its dependency; vanilla scheduling errors propagate; all 16 unload-failure combinations preserve errors, counter lifetime, and retryable cleanup.");
    }

    private static void ReportingFailure(int scenario)
    {
        ApplyCoverageScheduleBridge.ValidateBridge(typeof(OriginalJob));
        RuntimeDiagnostics.Initialize();RuntimeDiagnostics.MarkInstalled();
        using var buildings=new NativeList<BuildingData>(1,Allocator.TempJob);
        using var elements=new NativeList<CoverageElement>(1,Allocator.TempJob);
        try
        {
            OriginalJob.Executions=DependencyJob.Executions=0;
            var original=new OriginalJob{m_BuildingData=buildings,m_Elements=elements};
            var dependency=IJobExtensions.Schedule(new DependencyJob(),default);
            int before=IJobExtensions.Schedules;
            IJobExtensions.ScheduleFailures.Enqueue(scenario==1||scenario==3
                ? new UnprintableException() : new InvalidOperationException("Injected optimized scheduling failure"));
            LogManager.Logger.ErrorFailure=new InvalidOperationException("Injected error logger failure");
            if(scenario>=2)LogManager.Logger.InfoFailure=new InvalidOperationException("Injected status logger failure");
            var fallback=ApplyCoverageScheduleBridge.ScheduleOptimized(original,dependency);
            Check(OriginalJob.Executions==0,"Fallback executed early");
            fallback.Complete();
            Check(OriginalJob.Executions==1&&DependencyJob.Executions==1,"Fallback lost or duplicated work/dependency");
            Check(IJobExtensions.Schedules==before+1,"Fallback did not schedule exactly once");
            Check(RuntimeDiagnostics.Snapshot().Fallbacks==1,"Reporting failure lost fallback count");
        }
        finally
        {
            LogManager.Logger.ErrorFailure=LogManager.Logger.InfoFailure=null;
            IJobExtensions.ScheduleFailures.Clear();RuntimeDiagnostics.Dispose();
        }
    }

    private static void VanillaFailure()
    {
        ApplyCoverageScheduleBridge.ValidateBridge(typeof(OriginalJob));
        RuntimeDiagnostics.Initialize();RuntimeDiagnostics.MarkInstalled();
        using var buildings=new NativeList<BuildingData>(1,Allocator.TempJob);
        using var elements=new NativeList<CoverageElement>(1,Allocator.TempJob);
        var expected=new InvalidOperationException("Injected vanilla scheduling failure");
        try
        {
            IJobExtensions.ScheduleFailures.Enqueue(new InvalidOperationException("Injected optimized scheduling failure"));
            IJobExtensions.ScheduleFailures.Enqueue(expected);
            Exception? actual=null;
            try { ApplyCoverageScheduleBridge.ScheduleOptimized(new OriginalJob{m_BuildingData=buildings,m_Elements=elements},default); }
            catch(Exception error) { actual=error; }
            Check(ReferenceEquals(actual,expected),"Vanilla scheduling exception was swallowed or replaced");
            Check(IJobExtensions.ScheduleFailures.Count==0,"Vanilla was not attempted");
        }
        finally { IJobExtensions.ScheduleFailures.Clear();RuntimeDiagnostics.Dispose(); }
    }

    private static void UnloadFailures(int mask)
    {
        RuntimeDiagnostics.Initialize();RuntimeDiagnostics.MarkInstalled();
        var mod=new Mod();
        var settings=new DiagnosticSettings(mod);
        var locale=new DiagnosticLocale(settings);
        var harmony=new Harmony(Mod.HarmonyId);
        var localization=GameManager.instance.localizationManager;
        localization.AddSource("en-US",locale);
        int previousRemoveCalls=localization.RemoveCalls;
        SetField(mod,"_settings",settings);SetField(mod,"_diagnosticLocale",locale);SetField(mod,"_harmony",harmony);
        using var buildings=new NativeList<RawBuildingData>(1,Allocator.TempJob);
        using var elements=new NativeList<RawCoverageElement>(1,Allocator.TempJob);
        var pending=RuntimeDiagnostics.Schedule(new OptimizedApplyCoverageJob{BuildingData=buildings,Elements=elements},default);
        var expected=new List<Exception>();
        if((mask&1)!=0){settings.UnregisterFailure=new InvalidOperationException("Injected settings failure");expected.Add(settings.UnregisterFailure);}
        if((mask&2)!=0){localization.RemoveFailure=new InvalidOperationException("Injected locale failure");expected.Add(localization.RemoveFailure);}
        if((mask&4)!=0){harmony.UnpatchFailure=new InvalidOperationException("Injected unpatch failure");expected.Add(harmony.UnpatchFailure);}
        if((mask&8)!=0){pending.Work!.CompletionFailure=new InvalidOperationException("Injected completion failure");expected.Add(pending.Work.CompletionFailure);}
        try
        {
            Exception? actual=null;
            try { mod.OnDispose(); } catch(Exception error) { actual=error; }
            if(expected.Count==0)Check(actual==null,"Clean unload threw");
            else
            {
                Check(actual is AggregateException,"Cleanup failures were not collected");
                var errors=((AggregateException)actual!).InnerExceptions;
                Check(errors.Count==expected.Count,"Cleanup lost an error or skipped a stage");
                for(int i=0;i<expected.Count;i++)Check(ReferenceEquals(errors[i],expected[i]),"Cleanup replaced an original exception");
            }
            Check(settings.UnregisterCalls==1&&localization.RemoveCalls==previousRemoveCalls+1&&harmony.UnpatchCalls==1,"A cleanup stage was skipped");
            Check(!RuntimeDiagnostics.Snapshot().Installed,"Failed unpatch left optimized scheduling active");
            Check(pending.IsCompleted==((mask&8)==0),"Unexpected pending-job completion state");
            Check(AllocationTracker.Live==((mask&8)!=0?3:2),"Counter memory freed early or leaked after completed work");
            if((mask&4)!=0)
            {
                // A surviving patch must route subsequent submissions to vanilla
                // while optimized scheduling is disabled by diagnostic disposal.
                using var fallbackBuildings=new NativeList<BuildingData>(1,Allocator.TempJob);
                using var fallbackElements=new NativeList<CoverageElement>(1,Allocator.TempJob);
                int before=OriginalJob.Executions;
                ApplyCoverageScheduleBridge.ScheduleOptimized(new OriginalJob
                {
                    m_BuildingData=fallbackBuildings,m_Elements=fallbackElements
                },default).Complete();
                Check(OriginalJob.Executions==before+1,"Surviving patch did not fall back after failed unpatch");
                Check(RuntimeDiagnostics.Snapshot().Scheduled==1,"Failed unpatch allowed another optimized job");
            }
        }
        finally
        {
            settings.UnregisterFailure=localization.RemoveFailure=harmony.UnpatchFailure=null;
            mod.OnDispose();
        }
        Check(pending.IsCompleted&&AllocationTracker.Live==2,"Retry did not finish work and release counters");
        Check(!harmony.Patched&&!localization.Sources.Contains(locale),"Failed cleanup could not be retried");
        Check(settings.UnregisterCalls==((mask&1)!=0?2:1),"Settings cleanup retry lost its handle or repeated success");
        Check(localization.RemoveCalls==previousRemoveCalls+((mask&2)!=0?2:1),"Locale cleanup retry lost its handle or repeated success");
        Check(harmony.UnpatchCalls==((mask&4)!=0?2:1),"Unpatch retry lost its handle or repeated success");
        mod.OnDispose();
        Check(AllocationTracker.Live==2,"Repeated disposal changed allocation lifetime");
    }
}
