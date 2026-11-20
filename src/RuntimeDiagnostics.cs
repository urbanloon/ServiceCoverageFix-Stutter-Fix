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
        internal const string FallbackState = "Fallback observed";
        internal const string ManagedState = "Managed execution observed";
        internal const string BurstState = "Burst execution observed";

        public string State => !Installed ? "Inactive" :
            Fallbacks != 0 ? FallbackState :
            ManagedExecutions != 0 ? ManagedState :
            Completed != 0 ? BurstState :
            Scheduled != 0 ? "Awaiting execution" : "Awaiting coverage update";

        private string? _text;
        public override string ToString() => _text ??=
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
            internal bool WorkloadObserved;
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
            internal CoverageRuntimeStatus? Status;
            internal long ReleasedCompleted;
            internal long ReleasedManaged;
            internal long ReleasedStarted;
            internal long Scheduled;
            internal long Fallbacks;
            internal string LastFailure = string.Empty;
        }

        private static CallerHistory _simulation = new CallerHistory();
        private static CallerHistory _preview = new CallerHistory();
        private static CoverageRuntimeStatus? _totalStatus;
        private static bool _previewInstalled;
        private static bool _installed;
        private static long _scheduled;
        private static long _workloadCutoff;
        private static long _fallbacks;
        private static string _lastFailure = string.Empty;
        internal sealed class OptionsSnapshot
        {
            internal readonly string Status;
            internal readonly string Workloads;
            internal readonly int Version;

            internal OptionsSnapshot(string status, string workloads, int version)
            {
                Status = status;
                Workloads = workloads;
                Version = version;
            }
        }

        // A completed city load replaces this holder. An in-flight formatter can
        // finish writing to its old holder without publishing into the next city.
        private sealed class OptionsSession
        {
            internal volatile OptionsSnapshot? Snapshot;
        }

        private static volatile OptionsSession _options = new OptionsSession();
        private static readonly OptionsSnapshot NotChecked = new OptionsSnapshot(
            "Not checked yet", "Open Diagnostics or press Refresh for a reading.", 0);
        private static int _nextVersion;
        internal static OptionsSnapshot CachedOptions => _options.Snapshot ?? NotChecked;

        internal static void Initialize(bool allocateWorkerSlot = true)
        {
            lock (Sync)
            {
                if (Slots.Count != 0)
                    throw new InvalidOperationException("Previous diagnostic jobs have not been disposed.");
                _simulation = new CallerHistory();
                _preview = new CallerHistory();
                if (allocateWorkerSlot) AllocateSlot(false);
                _scheduled = _fallbacks = _workloadCutoff = 0;
                _totalStatus = null;
                _installed = _previewInstalled = false;
                _lastFailure = string.Empty;
                _options = new OptionsSession();
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
                Array.Clear(_simulation.Latest, 0, _simulation.Latest.Length);
                Array.Clear(_preview.Latest, 0, _preview.Latest.Length);
                _options = new OptionsSession();
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

        private static void ArchiveWorkload(PassSlot slot, long completed, ExecutionCounters* counters)
        {
            if (slot.Submission == 0 || slot.WorkloadObserved || completed == slot.ObservedCompletions) return;
            CallerHistory history = slot.Preview ? _preview : _simulation;
            if (slot.Submission > _workloadCutoff && (uint)slot.Service < history.Latest.Length &&
                history.Latest[slot.Service].Submission < slot.Submission)
                history.Latest[slot.Service] = new Sample { Submission = slot.Submission, Workload = counters->Workload };
            slot.ObservedCompletions = completed;
            slot.WorkloadObserved = true;
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
                    if (slot.Preview != preview) continue;
                    if (slot.Submission != 0 && slot.Handle.IsCompleted)
                    {
                        // Reclaim a finished handle only. No active job is forced to run.
                        slot.Handle.Complete();
                        if (!slot.WorkloadObserved)
                        {
                            // Complete has joined this slot's writer. Preserve a published
                            // result before reuse, including when the panel is never opened.
                            var counters = (ExecutionCounters*)NativeArrayUnsafeUtility.GetUnsafePtr(slot.Counters);
                            ArchiveWorkload(slot, counters->Completed, counters);
                        }
                        slot.Submission = 0;
                        slot.Handle = default;
                    }
                    if (slot.Submission == 0)
                    {
                        free = slot;
                        break;
                    }
                }
                if (free == null) free = AllocateSlot(preview);
                job.Diagnostics = (ExecutionCounters*)NativeArrayUnsafeUtility.GetUnsafePtr(free.Counters);
                JobHandle handle = IJobExtensions.Schedule(job, dependency);
                // No allocating or throwing operations after successful submission.
                free.Handle = handle;
                free.WorkloadObserved = false;
                free.Service = service;
                free.Submission = ++_scheduled;
                (preview ? _preview : _simulation).Scheduled++;
                return handle;
            }
        }

        private static class ServiceRows
        {
            internal static readonly KeyValuePair<byte, string>[] Labels = BuildLabels();

            private static KeyValuePair<byte, string>[] BuildLabels()
            {
                var labels = new List<KeyValuePair<byte, string>>();
                foreach (CoverageService service in Enum.GetValues(typeof(CoverageService)))
                {
                    string name = service.ToString();
                    if (name == "Count") continue;
                    if (name == "FireRescue") name = "Fire and rescue";
                    if (name == "PostService") name = "Postal service";
                    if (name == "EmergencyShelter") name = "Emergency shelter";
                    labels.Add(new KeyValuePair<byte, string>((byte)service, name));
                }
                return labels.ToArray();
            }
        }

        internal static OptionsSnapshot ReadOptions(bool refresh = false)
        {
            OptionsSession session = _options;
            OptionsSnapshot? snapshot = session.Snapshot;
            if (!refresh && snapshot != null) return snapshot;
            // Serialize initial/manual captures independently of the scheduling lock.
            lock (session)
            {
                if (!refresh && session.Snapshot != null) return session.Snapshot;
                return session.Snapshot = CaptureOptions(false);
            }
        }

        internal static OptionsSnapshot CaptureOptions(bool preview)
        {
            Sample[] samples;
            KeyValuePair<byte, string>[] labels;
            string status;
            int version = Interlocked.Increment(ref _nextVersion);
            lock (Sync)
            {
                if (!_installed || (preview && !_previewInstalled))
                    return new OptionsSnapshot(Mod.CompatibilityInactiveReason == null ? "Inactive" :
                        "Inactive on this game version", "No workloads recorded.", version);
                CallerHistory history = preview ? _preview : _simulation;
                long completed = history.ReleasedCompleted;
                foreach (PassSlot slot in Slots)
                {
                    if (slot.Preview != preview) continue;
                    var counters = (ExecutionCounters*)NativeArrayUnsafeUtility.GetUnsafePtr(slot.Counters);
                    // One acquire read supplies both the status and the workload
                    // publication test. Never wait for a job while reading Options.
                    long published = Volatile.Read(ref counters->Completed);
                    completed += published;
                    ArchiveWorkload(slot, published, counters);
                }
                status = history.Fallbacks != 0 ? "A coverage update used the game's calculation" :
                    completed != 0 ? "Active" : "Waiting for a coverage update";
                if (!preview && ServiceCoverageSystemPatch.ServiceBindingAvailable == false)
                    return new OptionsSnapshot(status, "Service workloads are unavailable on this game version.", version);
                labels = ServiceRows.Labels;
                samples = new Sample[labels.Length];
                for (int i = 0; i < samples.Length; i++) samples[i] = history.Latest[labels[i].Key];
            }

            // Format the managed copy outside the scheduling lock.
            var rows = new StringBuilder("Latest workloads");
            bool hasWorkload = false;
            for (int i = 0; i < samples.Length; i++)
            {
                if (samples[i].Submission == 0) continue;
                WorkloadCounts counts = samples[i].Workload;
                rows.AppendLine().AppendFormat("{0}: {1:N0} {2}, {3:N0} available, {4:N0} processed",
                    labels[i].Value, counts.Providers, counts.Providers == 1 ? "provider" : "providers",
                    counts.Available, counts.Processed);
                hasWorkload = true;
            }
            if (!hasWorkload) rows.AppendLine().Append("No workloads recorded.");
            return new OptionsSnapshot(status, rows.ToString(), version);
        }

        internal static void RecordFallback(Exception error, bool preview = false)
        {
            lock (Sync)
            {
                CallerHistory history = preview ? _preview : _simulation;
                _fallbacks++;
                history.Fallbacks++;
                string failure;
                try { failure = error.ToString(); }
                catch { failure = error.GetType().Name; }
                history.LastFailure = _lastFailure = failure;
            }
        }

        internal static CoverageRuntimeStatus Snapshot() => SnapshotCaller(null);

        internal static CoverageRuntimeStatus SnapshotCaller(bool? preview)
        {
            lock (Sync)
            {
                CallerHistory history = preview == true ? _preview : _simulation;
                bool installed = _installed && (preview != true || _previewInstalled);
                long scheduled = preview.HasValue ? history.Scheduled : _scheduled;
                long fallbacks = preview.HasValue ? history.Fallbacks : _fallbacks;
                string failure = preview.HasValue ? history.LastFailure : _lastFailure;
                long completed = preview.HasValue ? history.ReleasedCompleted : _simulation.ReleasedCompleted + _preview.ReleasedCompleted;
                long started = preview.HasValue ? history.ReleasedStarted : _simulation.ReleasedStarted + _preview.ReleasedStarted;
                long managed = preview.HasValue ? history.ReleasedManaged : _simulation.ReleasedManaged + _preview.ReleasedManaged;
                foreach (PassSlot slot in Slots)
                {
                    if (preview.HasValue && slot.Preview != preview.Value) continue;
                    var p = (ExecutionCounters*)NativeArrayUnsafeUtility.GetUnsafePtr(slot.Counters);
                    // Read publication first and Started last. A just-starting
                    // worker may become visible during an observation.
                    completed += Volatile.Read(ref p->Completed);
                    managed += Volatile.Read(ref p->Managed);
                    started += Volatile.Read(ref p->Started);
                }
                CoverageRuntimeStatus? previous = preview.HasValue ? history.Status : _totalStatus;
                if (previous != null && previous.Installed == installed && previous.Scheduled == scheduled &&
                    previous.Started == started && previous.Completed == completed && previous.ManagedExecutions == managed &&
                    previous.Fallbacks == fallbacks && previous.LastFailure == failure)
                    return previous;
                var result = new CoverageRuntimeStatus
                {
                    Installed = installed, Scheduled = scheduled, Started = started, Completed = completed,
                    ManagedExecutions = managed, Fallbacks = fallbacks, LastFailure = failure
                };
                if (preview.HasValue) history.Status = result;
                else _totalStatus = result;
                return result;
            }
        }

        internal static void Dispose()
        {
            lock (Sync)
            {
                _installed = _previewInstalled = false;
                _options = new OptionsSession();
                // If completion throws, retain the allocation rather than free
                // memory that a worker might still reference.
                foreach (PassSlot slot in Slots)
                    if (slot.Submission != 0) slot.Handle.Complete();
                // Remove each successfully released allocation immediately. If a
                // later Dispose throws, status readers and retries see only live slots.
                for (int i = Slots.Count - 1; i >= 0; i--)
                {
                    PassSlot slot = Slots[i];
                    var counters = slot.Counters[0];
                    slot.Counters.Dispose();
                    // Publish the retired totals only after release succeeds.
                    // A failed partial cleanup can be inspected and retried.
                    CallerHistory history = slot.Preview ? _preview : _simulation;
                    history.ReleasedCompleted += counters.Completed;
                    history.ReleasedStarted += counters.Started;
                    history.ReleasedManaged += counters.Managed;
                    Slots.RemoveAt(i);
                }
                Array.Clear(_simulation.Latest, 0, _simulation.Latest.Length);
                Array.Clear(_preview.Latest, 0, _preview.Latest.Length);
            }
        }
    }
}
