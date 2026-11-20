using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Game.Net;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace ServiceCoverageFix
{
    internal struct WorkloadCounts
    {
        internal int Providers;
        internal int Available;
        internal int Processed;
        internal int Reserved;
    }

    // One writer per leased slot. Started, Managed and Completed accumulate
    // across reuse. Workload is published by the final Completed increment.
    internal struct ExecutionCounters
    {
        internal long Started;
        internal long Completed;
        internal long Managed;
        internal WorkloadCounts Workload;
    }

    public sealed class CoverageRuntimeStatus
    {
        public bool Installed { get; internal set; }
        public long Scheduled { get; internal set; }
        public long Started { get; internal set; }
        public long Completed { get; internal set; }
        public long ManagedExecutions { get; internal set; }
        public long Fallbacks { get; internal set; }
        public string LastFailure { get; internal set; } = string.Empty;
        public string State => !Installed ? "Inactive" :
            Fallbacks != 0 ? "Fallback observed" :
            ManagedExecutions != 0 ? "Managed execution observed" :
            Completed != 0 ? "Burst execution observed" :
            Scheduled != 0 ? "Awaiting execution" : "Awaiting coverage update";
        public override string ToString() =>
            $"{State}; scheduled={Scheduled}, started={Started}, completed={Completed}, " +
            $"managed={ManagedExecutions}, fallbacks={Fallbacks}";
    }

    internal static unsafe class RuntimeDiagnostics
    {
        private static readonly object Sync = new object();
        private sealed class PassSlot
        {
            internal NativeArray<ExecutionCounters> Counters;
            internal JobHandle Handle;
            internal long Submission;
            internal long ObservedCompletions;
            internal int Service;
            internal bool Preview;
        }

        private struct Sample
        {
            internal long Submission;
            internal WorkloadCounts Workload;
        }

        private static readonly List<PassSlot> Slots = new List<PassSlot>();
        // CoverageService is byte-backed. Resolve display names from the loaded
        // game enum, so changing a numeric ID does not silently relabel a row.
        private sealed class CallerHistory
        {
            internal readonly Sample[] Latest = new Sample[256];
            internal string? Text;
            internal long Scheduled;
            internal long Fallbacks;
            internal string LastFailure = string.Empty;
        }

        private static CallerHistory _simulation = new CallerHistory();
        private static CallerHistory _preview = new CallerHistory();
        private static bool _previewInstalled;
        private static bool _installed;
        private static long _scheduled;
        private static long _workloadCutoff;
        private static bool _observedLoad;
        private static long _fallbacks;
        private static string _lastFailure = string.Empty;
        private static string _lastReportedState = string.Empty;

        internal static void Initialize()
        {
            lock (Sync)
            {
                if (Slots.Count != 0)
                    throw new InvalidOperationException("Previous diagnostic jobs have not been disposed.");
                _simulation = new CallerHistory();
                _preview = new CallerHistory();
                AllocateSlot(false);
                _scheduled = _fallbacks = _workloadCutoff = 0;
                _observedLoad = false;
                _installed = _previewInstalled = false;
                _lastFailure = _lastReportedState = string.Empty;
            }
        }

        internal static void StartLoadedSession(Mod source)
        {
            lock (Sync)
            {
                if (!_installed || !source.IsOwner) return;
                // Submissions through this boundary belong to the previous
                // display session, even when a worker publishes afterward.
                _workloadCutoff = _scheduled;
                _observedLoad = true;
                Array.Clear(_simulation.Latest, 0, _simulation.Latest.Length);
                Array.Clear(_preview.Latest, 0, _preview.Latest.Length);
                _simulation.Text = _preview.Text = null;
            }
        }

        internal static void MarkInstalled()
        {
            lock (Sync) _installed = true;
        }

        internal static void MarkPreviewInstalled()
        {
            lock (Sync) _previewInstalled = true;
        }

        private static PassSlot AllocateSlot(bool preview)
        {
            // Reserve space before allocating native memory. Slots are reused
            // and grow only if all previous jobs are still in flight.
            if (Slots.Count == Slots.Capacity)
                Slots.Capacity = Math.Max(4, checked(Slots.Count * 2));
            var slot = new PassSlot { Preview = preview };
            slot.Counters = new NativeArray<ExecutionCounters>(1, Allocator.Persistent);
            Slots.Add(slot);
            return slot;
        }

        private static void ObserveWorkload(PassSlot slot)
        {
            if (slot.Submission == 0) return;
            var counters = (ExecutionCounters*)NativeArrayUnsafeUtility.GetUnsafePtr(slot.Counters);
            long completed = Interlocked.Read(ref counters->Completed);
            if (completed == slot.ObservedCompletions) return;
            // The acquire read above proves the sole writer has finished its
            // payload. Sync excludes reuse while the UI copies that payload.
            CallerHistory history = slot.Preview ? _preview : _simulation;
            if (slot.Submission > _workloadCutoff && (uint)slot.Service < history.Latest.Length &&
                history.Latest[slot.Service].Submission < slot.Submission)
            {
                history.Latest[slot.Service] = new Sample { Submission = slot.Submission, Workload = counters->Workload };
                history.Text = null;
            }
            slot.ObservedCompletions = completed;
        }

        internal static JobHandle Schedule(OptimizedApplyCoverageJob job, JobHandle dependency, int service = -1, bool preview = false)
        {
            lock (Sync)
            {
                if (!_installed || (preview && !_previewInstalled) || Slots.Count == 0)
                    throw new InvalidOperationException("The replacement is not initialized.");
                PassSlot? free = null;
                foreach (PassSlot slot in Slots)
                {
                    if (slot.Submission != 0 && slot.Handle.IsCompleted)
                    {
                        // Reclaim a finished handle only. No active job is forced to run.
                        slot.Handle.Complete();
                        ObserveWorkload(slot);
                        slot.Submission = 0;
                        slot.Handle = default;
                    }
                    if (slot.Submission == 0 && slot.Preview == preview && free == null) free = slot;
                }
                if (free == null) free = AllocateSlot(preview);
                job.Diagnostics = (ExecutionCounters*)NativeArrayUnsafeUtility.GetUnsafePtr(free.Counters);
                JobHandle handle = IJobExtensions.Schedule(job, dependency);
                // No allocating or throwing operations after successful submission.
                free.Handle = handle;
                free.Service = service;
                free.Submission = ++_scheduled;
                (preview ? _preview : _simulation).Scheduled++;
                return handle;
            }
        }

        internal static string FormatWorkloads(bool preview = false)
        {
            lock (Sync)
            {
                if (preview && !_previewInstalled) return "Preview replacement inactive";
                if (!preview && ServiceCoverageSystemPatch.ServiceBindingAvailable == false)
                    return "Service categories are unavailable for this game or patch layout.";
                CallerHistory history = preview ? _preview : _simulation;
                foreach (PassSlot slot in Slots)
                    if (slot.Preview == preview) ObserveWorkload(slot);
                if (history.Text != null) return history.Text;
                var text = new StringBuilder(_observedLoad ? "Since the last completed game load" : "Since mod load");
                foreach (CoverageService service in Enum.GetValues(typeof(CoverageService)))
                {
                    string name = service.ToString();
                    if (name == "Count") continue;
                    text.AppendLine();
                    if (name == "FireRescue") name = "Fire and rescue";
                    if (name == "PostService") name = "Postal service";
                    if (name == "EmergencyShelter") name = "Emergency shelter";
                    Sample sample = history.Latest[(byte)service];
                    text.Append(name).Append(" — ");
                    if (sample.Submission == 0)
                    {
                        text.Append("Awaiting a completed pass");
                        continue;
                    }
                    WorkloadCounts counts = sample.Workload;
                    text.AppendFormat("pass {0:N0}, {1:N0} {2}, {3:N0} available, {4:N0} processed, {5:N0} reserved",
                        sample.Submission, counts.Providers, counts.Providers == 1 ? "provider" : "providers",
                        counts.Available, counts.Processed, counts.Reserved);
                }
                return history.Text = text.ToString();
            }
        }

        internal static void RecordFallback(Exception error, bool preview = false)
        {
            lock (Sync)
            {
                CallerHistory history = preview ? _preview : _simulation;
                _fallbacks++;
                history.Fallbacks++;
                history.LastFailure = _lastFailure = error.ToString();
            }
        }

        internal static CoverageRuntimeStatus Snapshot() => SnapshotCaller(null);

        internal static CoverageRuntimeStatus SnapshotCaller(bool? preview)
        {
            lock (Sync)
            {
                CallerHistory history = preview == true ? _preview : _simulation;
                var result = new CoverageRuntimeStatus
                {
                    Installed = _installed && (preview != true || _previewInstalled),
                    Scheduled = preview.HasValue ? history.Scheduled : _scheduled,
                    Fallbacks = preview.HasValue ? history.Fallbacks : _fallbacks,
                    LastFailure = preview.HasValue ? history.LastFailure : _lastFailure
                };
                foreach (PassSlot slot in Slots)
                {
                    if (preview.HasValue && slot.Preview != preview.Value) continue;
                    var p = (ExecutionCounters*)NativeArrayUnsafeUtility.GetUnsafePtr(slot.Counters);
                    // These are live observations, not a synchronized frame snapshot.
                    result.Completed += Interlocked.Read(ref p->Completed);
                    result.Started += Interlocked.Read(ref p->Started);
                    result.ManagedExecutions += Interlocked.Read(ref p->Managed);
                }
                return result;
            }
        }

        internal static void ReportTransition()
        {
            // Diagnostics must never trigger a gameplay fallback.
            try
            {
                CoverageRuntimeStatus status = Snapshot();
                if (status.State == _lastReportedState) return;
                _lastReportedState = status.State;
                Mod.Log.Info($"Coverage execution status: {status}");
            }
            catch { /* Status reporting cannot affect simulation scheduling. */ }
        }

        internal static void Dispose()
        {
            lock (Sync)
            {
                _installed = _previewInstalled = false;
                // If completion throws, retain the allocation rather than free
                // memory that a worker might still reference.
                foreach (PassSlot slot in Slots)
                    if (slot.Submission != 0) slot.Handle.Complete();
                // Remove each successfully released allocation immediately. If a
                // later Dispose throws, status readers and retries see only live slots.
                for (int i = Slots.Count - 1; i >= 0; i--)
                {
                    Slots[i].Counters.Dispose();
                    Slots.RemoveAt(i);
                }
                Array.Clear(_simulation.Latest, 0, _simulation.Latest.Length);
                Array.Clear(_preview.Latest, 0, _preview.Latest.Length);
                _simulation.Text = _preview.Text = null;
            }
        }
    }
}
