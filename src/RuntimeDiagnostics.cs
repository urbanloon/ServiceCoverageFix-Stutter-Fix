using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace ServiceCoverageFix
{
    // Separate from gameplay buffers. Three aligned atomics per invocation,
    // never per coverage element. Read through Interlocked while workers run.
    internal struct ExecutionCounters
    {
        internal long Started;
        internal long Completed;
        internal long Managed;
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
        private static readonly List<JobHandle> Pending = new List<JobHandle>();
        private static NativeArray<ExecutionCounters> _counters;
        private static bool _installed;
        private static long _scheduled;
        private static long _fallbacks;
        private static string _lastFailure = string.Empty;
        private static string _lastReportedState = string.Empty;

        internal static void Initialize()
        {
            lock (Sync)
            {
                if (_counters.IsCreated)
                    throw new InvalidOperationException("Previous diagnostic jobs have not been disposed.");
                _counters = new NativeArray<ExecutionCounters>(1, Allocator.Persistent);
                _scheduled = _fallbacks = 0;
                _installed = false;
                _lastFailure = _lastReportedState = string.Empty;
                Pending.Clear();
            }
        }

        internal static void MarkInstalled()
        {
            lock (Sync) _installed = true;
        }

        internal static JobHandle Schedule(OptimizedApplyCoverageJob job, JobHandle dependency)
        {
            lock (Sync)
            {
                if (!_installed || !_counters.IsCreated)
                    throw new InvalidOperationException("The replacement is not initialized.");
                // Reclaim only already-finished handles. Never force the live
                // coverage dependency to complete on the scheduling thread.
                for (int i = Pending.Count - 1; i >= 0; i--)
                {
                    if (!Pending[i].IsCompleted) continue;
                    Pending[i].Complete();
                    Pending.RemoveAt(i);
                }
                // Allocate tracking capacity BEFORE scheduling: nothing that can
                // fail after Schedule may cause the bridge to schedule vanilla too.
                if (Pending.Count == Pending.Capacity)
                    Pending.Capacity = Math.Max(4, checked(Pending.Count * 2));
                job.Diagnostics = (ExecutionCounters*)NativeArrayUnsafeUtility.GetUnsafePtr(_counters);
                JobHandle handle = IJobExtensions.Schedule(job, dependency);
                Pending.Add(handle);
                _scheduled++;
                return handle;
            }
        }

        internal static void RecordFallback(Exception error)
        {
            lock (Sync)
            {
                _fallbacks++;
                _lastFailure = error.ToString();
            }
        }

        internal static CoverageRuntimeStatus Snapshot()
        {
            lock (Sync)
            {
                var result = new CoverageRuntimeStatus
                {
                    Installed = _installed, Scheduled = _scheduled,
                    Fallbacks = _fallbacks, LastFailure = _lastFailure
                };
                if (_counters.IsCreated)
                {
                    var p = (ExecutionCounters*)NativeArrayUnsafeUtility.GetUnsafePtr(_counters);
                    // These are live observations, not a synchronized frame snapshot.
                    result.Completed = Interlocked.Read(ref p->Completed);
                    result.Started = Interlocked.Read(ref p->Started);
                    result.ManagedExecutions = Interlocked.Read(ref p->Managed);
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
                _installed = false;
                // If completion throws, retain the allocation rather than free
                // memory that a worker might still reference.
                foreach (JobHandle handle in Pending) handle.Complete();
                Pending.Clear();
                if (_counters.IsCreated) _counters.Dispose();
            }
        }
    }
}
