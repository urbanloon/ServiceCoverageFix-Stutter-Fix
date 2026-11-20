using System;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Game.Net;
using HarmonyLib;
using System.Reflection;
using ServiceCoverageFix;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

internal static unsafe class WorkloadTests
{
    private static void RequireRow(string expected, bool preview = false)
    {
        string text = RuntimeDiagnostics.FormatWorkloads(preview);
        if (!text.Split('\n').Any(line => line.TrimEnd() == expected))
            throw new Exception($"Missing workload row '{expected}' in {text}");
    }

    internal static void Run()
    {
        RuntimeDiagnostics.Initialize();
        RuntimeDiagnostics.MarkInstalled();
        using var buildings = new NativeList<RawBuildingData>(3, Allocator.TempJob);
        using var elements = new NativeList<RawCoverageElement>(10, Allocator.TempJob);
        using var targets = new NativeArray<float2>(10, Allocator.TempJob);
        using var emptyBuildings = new NativeList<RawBuildingData>(1, Allocator.TempJob);
        using var emptyElements = new NativeList<RawCoverageElement>(1, Allocator.TempJob);
        try
        {
            if (RuntimeDiagnostics.FormatWorkloads().Split('\n').Length != 2 + (int)CoverageService.Count)
                throw new Exception("Unexpected number of coverage service rows.");
            var target = (float2*)targets.Ptr;
            for (int i = 0; i < 10; i++)
                elements.Add(new RawCoverageElement { CoveragePtr = target + i, Coverage = new float2(1f),
                    AverageCoverage = 1f, DensityFactor = 1f, LengthFactor = 1f });
            target[3] = new float2(1f); // visited but occluded
            buildings.Add(new RawBuildingData { ElementIndex = 0, ElementCount = 3, Total = .25f, Remaining = .25f });
            buildings.Add(new RawBuildingData { ElementIndex = 3, ElementCount = 2, Total = 10f, Remaining = 10f });
            buildings.Add(new RawBuildingData { ElementIndex = 5, ElementCount = 3, Total = 10f, Remaining = 0f });
            var empty = new OptimizedApplyCoverageJob { BuildingData = emptyBuildings, Elements = emptyElements };
            var stale = RuntimeDiagnostics.Schedule(empty, default, (int)CoverageService.FireRescue);
            var latest = RuntimeDiagnostics.Schedule(new OptimizedApplyCoverageJob {
                BuildingData = buildings, Elements = elements }, default, (int)CoverageService.FireRescue);
            RequireRow("Fire and rescue — Awaiting a completed pass");
            if (stale.IsCompleted || latest.IsCompleted) throw new Exception("Reading Options completed a pending job.");
            latest.Complete();
            const string fire = "Fire and rescue — pass 2, 2 providers, 5 available, 3 processed, 10 reserved";
            RequireRow(fire);
            stale.Complete();
            RequireRow(fire); // older overlapping pass must not overwrite the newer sample
            RuntimeDiagnostics.Schedule(empty, default, (int)CoverageService.Police).Complete();
            RequireRow("Police — pass 3, 0 providers, 0 available, 0 processed, 0 reserved");
            RuntimeDiagnostics.Schedule(empty, default, -1).Complete();
            RequireRow("Healthcare — Awaiting a completed pass");
            RequireRow(fire);

            // Distinguish unused reserved entries from an active range and
            // count an early-out on the single-provider fast path.
            using var single = new NativeList<RawBuildingData>(1, Allocator.TempJob);
            single.Add(new RawBuildingData { ElementIndex = 6, ElementCount = 4, Total = .25f, Remaining = .25f });
            RuntimeDiagnostics.Schedule(new OptimizedApplyCoverageJob { BuildingData = single, Elements = elements },
                default, (int)CoverageService.Healthcare).Complete();
            RequireRow("Healthcare — pass 5, 1 provider, 4 available, 1 processed, 10 reserved");
            if (RuntimeDiagnostics.Snapshot().Completed != 5) throw new Exception("Lost completions across slot reuse.");
        }
        finally { RuntimeDiagnostics.Dispose(); }
        foreach (int size in new[] { 2, 7, 32 })
        {
            TestUnorderedRetirement(size, 0);
            TestUnorderedRetirement(size, 2);
        }
        TestPreviewCaller();
        TestLoadBoundary();
        TestConcurrentFormatting();
        TestRetiredCounts();
        RuntimeDiagnostics.Initialize();
        try { RequireRow("Fire and rescue — Awaiting a completed pass"); }
        finally { RuntimeDiagnostics.Dispose(); }
        Console.WriteLine("PASS: eight service rows, unobserved/zero passes, active ranges, reserved input, budget tails, occluded visits, ordered overlapping passes, pooling, unattributed passes and reset.");
    }
    private static void TestUnorderedRetirement(int providers, int nanPosition)
    {
        const int length = 7;
        using var buildings = new NativeList<RawBuildingData>(providers, Allocator.TempJob);
        using var elements = new NativeList<RawCoverageElement>(providers * length, Allocator.TempJob);
        using var targets = new NativeArray<float2>(providers * length, Allocator.TempJob);
        using var counters = new NativeArray<ExecutionCounters>(1, Allocator.TempJob);
        int expectedProcessed = 0;
        for (int i = 0; i < providers; i++)
        {
            bool earlyExit = (i & 1) == 0;
            float budget = earlyExit ? 0.25f : 100f;
            expectedProcessed += earlyExit ? 1 : length;
            int start = elements.Length;
            buildings.Add(new RawBuildingData { ElementIndex = start, ElementCount = length, Total = budget, Remaining = budget });
            for (int j = 0; j < length; j++)
                elements.Add(new RawCoverageElement {
                    CoveragePtr = (float2*)targets.Ptr + start + j, Coverage = new float2(1f),
                    AverageCoverage = i == 1 && j == nanPosition ? float.NaN : 100f - j - i,
                    DensityFactor = 1f, LengthFactor = 1f
                });
        }

        new OptimizedApplyCoverageJob { BuildingData = buildings, Elements = elements,
            Diagnostics = (ExecutionCounters*)counters.Ptr }.Execute();
        var counts = counters[0].Workload;
        if (counts.Processed != expectedProcessed || counts.Available != providers * length ||
            counts.Providers != providers || counts.Reserved != elements.Length)
            throw new Exception("Incorrect workload totals for unordered heads and early provider retirement");
    }

    // Pause number formatting itself after it has captured a completed sample.
    // A schedule and completed load must proceed while that UI read is paused.
    private sealed class PausedCulture : CultureInfo
    {
        internal readonly ManualResetEventSlim Entered = new(false);
        internal readonly ManualResetEventSlim Resume = new(false);
        internal PausedCulture() : base("en-US") { }
        public override object? GetFormat(Type? formatType)
        {
            Entered.Set();
            if (!Resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Formatting was not released.");
            return base.GetFormat(formatType);
        }
    }

    private static void TestRetiredCounts()
    {
        RuntimeDiagnostics.Initialize();
        RuntimeDiagnostics.MarkInstalled();
        RuntimeDiagnostics.MarkPreviewInstalled();
        using var buildings = new NativeList<RawBuildingData>(1, Allocator.TempJob);
        using var elements = new NativeList<RawCoverageElement>(1, Allocator.TempJob);
        var empty = new OptimizedApplyCoverageJob { BuildingData = buildings, Elements = elements };
        RuntimeDiagnostics.Schedule(empty, default, 0);
        RuntimeDiagnostics.Schedule(empty, default, 0, true);
        NativeArray<ExecutionCounters>.DisposalsBeforeFailure = 1;
        try
        {
            try { RuntimeDiagnostics.Dispose(); throw new Exception("Expected partial disposal."); }
            catch (InvalidOperationException e) when (e.Message == "Injected disposal failure") { }
            if (RuntimeDiagnostics.Snapshot().Completed != 2 || RuntimeDiagnostics.SnapshotCaller(true).Completed != 1)
                throw new Exception("Partial disposal lost completed counters.");
        }
        finally { NativeArray<ExecutionCounters>.DisposalsBeforeFailure = -1; RuntimeDiagnostics.Dispose(); }
        var after = RuntimeDiagnostics.Snapshot();
        if (after.Installed || after.Completed != 2 || after.Started != 2 || after.ManagedExecutions != 2 ||
            RuntimeDiagnostics.SnapshotCaller(false).Completed != 1 || RuntimeDiagnostics.SnapshotCaller(true).Completed != 1)
            throw new Exception("Disposal retry lost or duplicated execution counters.");
    }

    private static void TestConcurrentFormatting()
    {
        RuntimeDiagnostics.Initialize();
        RuntimeDiagnostics.MarkInstalled();
        var owner = new Mod();
        typeof(Mod).GetField("_owner", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, owner);
        using var buildings = new NativeList<RawBuildingData>(1, Allocator.TempJob);
        using var elements = new NativeList<RawCoverageElement>(1, Allocator.TempJob);
        var empty = new OptimizedApplyCoverageJob { BuildingData = buildings, Elements = elements };
        var culture = new PausedCulture();
        try
        {
            RuntimeDiagnostics.Schedule(empty, default, (int)CoverageService.Park).Complete();
            CoverageRuntimeStatus first = RuntimeDiagnostics.SnapshotCaller(false);
            string counts = first.ToString();
            var panelRead = Task.Run(() =>
            {
                CultureInfo.CurrentCulture = culture;
                return RuntimeDiagnostics.FormatWorkloads();
            });
            try
            {
                if (!culture.Entered.Wait(TimeSpan.FromSeconds(5)))
                    throw new Exception("The formatting test did not capture a workload.");
                var change = Task.Run(() =>
                {
                    RuntimeDiagnostics.Schedule(empty, default, (int)CoverageService.Police).Complete();
                    RuntimeDiagnostics.StartLoadedSession(owner);
                });
                if (!change.Wait(TimeSpan.FromSeconds(3)))
                    throw new Exception("A UI formatter held the scheduling lock.");
            }
            finally { culture.Resume.Set(); }
            if (!panelRead.GetAwaiter().GetResult().Contains("Park — pass 1"))
                throw new Exception("UI did not render its captured sample.");
            RequireRow("Since the last completed game load");
            RequireRow("Park — Awaiting a completed pass");
            RequireRow("Police — Awaiting a completed pass");
            if (first.Scheduled != 1 || !ReferenceEquals(counts, first.ToString()) ||
                RuntimeDiagnostics.SnapshotCaller(false).Completed != 2)
                throw new Exception("An already-returned execution snapshot was mutated or stale.");
        }
        finally
        {
            culture.Resume.Set();
            owner.OnDispose();
        }
        Console.WriteLine("PASS: paused UI formatting allows new schedules and load reset, preserves immutable counters and cannot recache pre-load rows.");
    }

    private static void TestLoadBoundary()
    {
        RuntimeDiagnostics.Initialize();
        RuntimeDiagnostics.MarkInstalled();
        RuntimeDiagnostics.MarkPreviewInstalled();
        var mod = new Mod();
        typeof(Mod).GetField("_owner", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, mod);
        var manager = Game.SceneFlow.GameManager.instance;
        mod.TrackGameLoads(manager);
        using var buildings = new NativeList<RawBuildingData>(1, Allocator.TempJob);
        using var elements = new NativeList<RawCoverageElement>(1, Allocator.TempJob);
        var empty = new OptimizedApplyCoverageJob { BuildingData = buildings, Elements = elements };
        try
        {
            RequireRow("Since mod load");
            RuntimeDiagnostics.Schedule(empty, default, (int)CoverageService.Park).Complete();
            var oldSim = RuntimeDiagnostics.Schedule(empty, default, (int)CoverageService.Park);
            var oldPreview = RuntimeDiagnostics.Schedule(empty, default, (int)CoverageService.Park, true);
            RequireRow("Park — pass 1, 0 providers, 0 available, 0 processed, 0 reserved");
            manager.CompleteLoad();
            RequireRow("Since the last completed game load");
            RequireRow("Park — Awaiting a completed pass");
            if (oldSim.IsCompleted || oldPreview.IsCompleted) throw new Exception("Load reset forced pending work.");
            oldSim.Complete();
            oldPreview.Complete();
            RequireRow("Park — Awaiting a completed pass");
            RequireRow("Park — Awaiting a completed pass", true);
            RuntimeDiagnostics.Schedule(empty, default, (int)CoverageService.Park).Complete();
            RequireRow("Park — pass 4, 0 providers, 0 available, 0 processed, 0 reserved");
            if (RuntimeDiagnostics.Snapshot().Completed != 4) throw new Exception("A load reset lifetime execution totals.");
        }
        finally { mod.OnDispose(); }
        if (manager.LoadSubscribers != 0) throw new Exception("Unload retained the load callback.");
        Console.WriteLine("PASS: game load resets both workload histories, preserves pending jobs and totals, ignores earlier completions and unsubscribes on unload.");
    }

    private static void TestPreviewCaller()
    {
        RuntimeDiagnostics.Initialize();
        RuntimeDiagnostics.MarkInstalled();
        var harmony = new Harmony(Mod.HarmonyId);
        using var buildings = new NativeList<RawBuildingData>(1, Allocator.TempJob);
        using var elements = new NativeList<RawCoverageElement>(1, Allocator.TempJob);
        var empty = new OptimizedApplyCoverageJob { BuildingData = buildings, Elements = elements };
        try
        {
            int calls = 0;
            harmony.PatchClassAction = _ => { calls++; throw new Exception("Injected optional registration failure"); };
            Mod.TryInstallPreview(harmony, false);
            if (calls != 0) throw new Exception("Unreviewed preview was patched.");
            Mod.TryInstallPreview(harmony, true);
            if (calls != 1 || RuntimeDiagnostics.SnapshotCaller(true).Installed || !RuntimeDiagnostics.SnapshotCaller(false).Installed)
                throw new Exception("Preview registration failure changed caller state.");
            var replacementCount = typeof(ServiceCoveragePreviewPatch).GetProperty("ReplacementCount", BindingFlags.NonPublic | BindingFlags.Static)!;
            replacementCount.SetValue(null, 1);
            harmony.PatchClassAction = null;
            Mod.TryInstallPreview(harmony, true);
            if (RuntimeDiagnostics.SnapshotCaller(true).Installed) throw new Exception("A stale preview replacement count enabled a no-op patch.");
            harmony.PatchClassAction = type => {
                if (type != typeof(ServiceCoveragePreviewPatch)) throw new Exception("Wrong optional patch type.");
                replacementCount.SetValue(null, 1); // emulate verified transpiler result
            };
            Mod.TryInstallPreview(harmony, true);
            if (!RuntimeDiagnostics.SnapshotCaller(true).Installed) throw new Exception("Successful preview registration remained inactive.");
            var sim = RuntimeDiagnostics.Schedule(empty, default, (int)CoverageService.Park);
            RuntimeDiagnostics.Schedule(empty, default, (int)CoverageService.Park, true).Complete();
            RequireRow("Park — pass 2, 0 providers, 0 available, 0 processed, 0 reserved", true);
            RequireRow("Park — Awaiting a completed pass");
            sim.Complete();
            RequireRow("Park — pass 1, 0 providers, 0 available, 0 processed, 0 reserved");
            if (RuntimeDiagnostics.Snapshot().Completed != 2 || RuntimeDiagnostics.SnapshotCaller(false).Completed != 1 ||
                RuntimeDiagnostics.SnapshotCaller(true).Completed != 1) throw new Exception("Preview counters mixed with simulation.");
            RuntimeDiagnostics.RecordFallback(new Exception("preview"), true);
            if (RuntimeDiagnostics.SnapshotCaller(false).Fallbacks != 0 || RuntimeDiagnostics.SnapshotCaller(true).Fallbacks != 1)
                throw new Exception("Fallbacks mixed across callers.");
        }
        finally { RuntimeDiagnostics.Dispose(); }
        if (RuntimeDiagnostics.SnapshotCaller(true).Installed) throw new Exception("Unload kept optional preview active.");
        Console.WriteLine("PASS: optional preview contract, failure isolation, stale patch count, separate service samples, counters and fallbacks, unload.");
    }
}
