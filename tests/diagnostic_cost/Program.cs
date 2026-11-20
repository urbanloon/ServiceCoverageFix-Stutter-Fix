using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using ServiceCoverageFix;
using Unity.Collections;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
foreach (int slots in new[] {1, 16})
{
    RuntimeDiagnostics.Initialize();
    RuntimeDiagnostics.MarkInstalled();
    RuntimeDiagnostics.MarkPreviewInstalled();
    using var buildings = new NativeList<RawBuildingData>(1, Allocator.Persistent);
    using var elements = new NativeList<RawCoverageElement>(1, Allocator.Persistent);
    var job = new OptimizedApplyCoverageJob {BuildingData = buildings, Elements = elements};
    var queued = Enumerable.Range(0, slots).Select(i => RuntimeDiagnostics.Schedule(job, default, i % 8)).ToArray();
    foreach (var handle in queued) handle.Complete();
    var settings = new DiagnosticSettings(new Mod());
    int service = 0;
    void Pass()
    {
        RuntimeDiagnostics.ReportTransition();
        RuntimeDiagnostics.Schedule(job, default, service++ % 8).Complete();
    }
    void Menu()
    {
        // Exercise the seven getters, independent of the host's widget binding.
        _ = settings.ExecutionStatus;
        _ = settings.ExecutionCounts;
        _ = settings.LastFailure;
        _ = settings.WorkloadByService;
        _ = settings.PreviewExecutionCounts;
        _ = settings.PreviewWorkloadByService;
        _ = settings.PreviewLastFailure;
    }
    Measure("pass-managed", slots, 100_000, Pass);
    Measure("menu-unchanged", slots, 30_000, Menu);
    Measure("pass-and-menu-changed", slots, 3_000, () => { Pass(); Menu(); });
    // The managed adapter cannot execute Burst. Seed the equivalent already-
    // completed native status once, outside the timed logging-only measurement.
    var pool = (IEnumerable)typeof(RuntimeDiagnostics).GetField("Slots", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    foreach (object slot in pool)
    {
        var array = (NativeArray<ExecutionCounters>)slot.GetType().GetField("Counters", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(slot)!;
        var counters = array[0];
        counters.Managed = 0;
        array[0] = counters;
    }
    typeof(RuntimeDiagnostics).GetField("_lastReportedState", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, "");
    RuntimeDiagnostics.ReportTransition();
    Measure("log-Burst-state", slots, 150_000, RuntimeDiagnostics.ReportTransition);
    RuntimeDiagnostics.Dispose();
}

static void Measure(string name, int slots, int iterations, Action action)
{
    for (int i = 0; i < Math.Min(10_000, iterations); i++) action();
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    long start = Stopwatch.GetTimestamp();
    for (int i = 0; i < iterations; i++) action();
    double elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations;
    double bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)iterations;
    Console.WriteLine($"{name},{slots},{iterations},{elapsed:F2},{bytes:F2}");
}
