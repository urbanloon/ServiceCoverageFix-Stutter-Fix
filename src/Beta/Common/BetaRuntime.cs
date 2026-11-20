using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Unity.Jobs;

namespace ServiceCoverageFix
{
    internal enum BetaFeature { Housing, Seller, Water, Route }

    internal interface IBetaSubmission { JobHandle Schedule(); }

    internal sealed class BetaRuntime
    {
        internal const string GameHash = "f076df4b70aa0f21a80345e03a052ca2273bc6754f41dab7e63d837837a43745";
        private static BetaRuntime? s_current;
        private readonly object _gate = new object();
        private readonly JobHandle[] _handles = new JobHandle[64];
        private readonly bool[] _occupied = new bool[64];
        private readonly bool[] _enabled = new bool[4];
        private readonly string[] _state = { "Off", "Off", "Off", "Off" };
        private readonly Harmony?[] _patches = new Harmony?[4];
        private readonly MethodInfo?[] _targets = new MethodInfo?[4];
        private readonly bool[] _installed = new bool[4];
        private readonly Ownership?[] _ownership = new Ownership?[4];
        private sealed class Ownership
        {
            internal readonly int Generation;
            internal readonly bool Exclusive;
            internal readonly int CheckedAt;
            internal Ownership(int generation, bool exclusive)
            { Generation = generation; Exclusive = exclusive; CheckedAt = Environment.TickCount; }
        }
        // GetPatchInfo uses this same monitor. If a foreign patch calls our bridge
        // reentrantly during recomposition, the registry still describes the old wrapper.
        private static readonly object? HarmonyPatchLock = typeof(PatchProcessor)
            .GetField("locker", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        private readonly bool _compatible;
        private volatile bool _stopping;
        private volatile bool _routeSuperseded;
        private int _next;

        internal BetaRuntime(string hash)
        {
            _compatible = hash == GameHash;
            AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) NoticeAssembly(assembly);
            Volatile.Write(ref s_current, this);
        }

        internal static void RejectExistingCoverageOwner(IEnumerable<string>? owners)
        {
            if (owners?.Contains(Mod.HarmonyId) == true)
                throw new InvalidOperationException("Another instance already owns the service coverage patch.");
        }

        private void AssemblyLoaded(object? sender, AssemblyLoadEventArgs e) => NoticeAssembly(e.LoadedAssembly);
        private void NoticeAssembly(Assembly assembly)
        {
            if (string.Equals(assembly.GetName().Name, "PathfindLocalityFix", StringComparison.OrdinalIgnoreCase))
                _routeSuperseded = true;
        }

        private bool CheckOwnership(BetaFeature feature, bool displaying = false, bool force = false)
        {
            int i = (int)feature;
            if (!BetaPatches.SharesPublisher(feature)) return false;
            int generation = BetaPatches.Generation(feature);
            var prior = Volatile.Read(ref _ownership[i]);
            // The UI can detect external removal of our transpiler, which cannot
            // invalidate its own generation. Ordinary submissions never poll on a timer.
            if (!force && prior != null && prior.Generation == generation &&
                (!displaying || unchecked((uint)(Environment.TickCount - prior.CheckedAt)) < 1000)) return prior.Exclusive;
            // A forced read is made only by Set, after its own completed patch
            // transaction while it still holds Harmony's publication monitor.
            if (HarmonyPatchLock == null || (!force && Monitor.IsEntered(HarmonyPatchLock))) return false;
            var target = Volatile.Read(ref _targets[i]);
            var patch = Volatile.Read(ref _patches[i]);
            if (target == null || patch == null) return false;
            bool exclusive;
            try
            {
                var owners = Harmony.GetPatchInfo(target)?.Owners;
                exclusive = owners != null && owners.Count == 1 && owners[0] == patch.Id;
            }
            catch { exclusive = false; }
            if (BetaPatches.Generation(feature) != generation) return false;
            Volatile.Write(ref _ownership[i], new Ownership(generation, exclusive));
            return exclusive;
        }

        private bool CurrentOwner(BetaFeature feature)
        {
            var snapshot = Volatile.Read(ref _ownership[(int)feature]);
            return snapshot != null && snapshot.Exclusive && BetaPatches.SharesPublisher(feature) && snapshot.Generation == BetaPatches.Generation(feature);
        }

        internal string Status(BetaFeature feature)
        {
            int i = (int)feature;
            if (_stopping) return "Off";
            if (Volatile.Read(ref _enabled[i]))
            {
                CheckOwnership(feature, displaying: true);
                if (feature == BetaFeature.Route && _routeSuperseded) return "Inactive while PathfindLocalityFix is loaded";
                if (!BetaPatches.IsUsable(feature)) return "Inactive. The game schedule call changed.";
                if (!BetaPatches.SharesPublisher(feature)) return "Inactive. Another Harmony assembly rebuilt this method.";
                if (!CurrentOwner(feature)) return "Inactive. The scheduling patch is missing or shared.";
            }
            return Volatile.Read(ref _state[i]);
        }

        internal void Set(BetaFeature feature, bool enabled)
        {
            int i = (int)feature;
            if (HarmonyPatchLock == null)
            {
                Volatile.Write(ref _state[i], enabled ? "Unsupported Harmony publication contract" : "Off");
                return;
            }
            // Do not start a nested patch transaction from another mod's transpiler.
            if (Monitor.IsEntered(HarmonyPatchLock))
            {
                Volatile.Write(ref _enabled[i], false);
                Volatile.Write(ref _state[i], enabled ? "Inactive. Retry after patch publication." : "Off");
                return;
            }
            // Settings and unpatching share Harmony's existing publication monitor.
            // Never acquire or hold the scheduling gate while calling Harmony APIs.
            lock (HarmonyPatchLock)
            {
                if (_stopping) return;
                Volatile.Write(ref _enabled[i], false);
                if (!enabled) { Volatile.Write(ref _state[i], "Off"); return; }
                if (!_compatible) { Volatile.Write(ref _state[i], "Unsupported game build"); return; }
                if (feature == BetaFeature.Route && _routeSuperseded) { Volatile.Write(ref _state[i], "Inactive while PathfindLocalityFix is loaded"); return; }
                try
                {
                    if (!_installed[i])
                    {
                        if (_patches[i] != null) { _patches[i]!.UnpatchAll(_patches[i]!.Id); _patches[i] = null; }
                        Install(feature);
                    }
                    if (!CheckOwnership(feature, force: true))
                        throw new InvalidOperationException("The target has a conflicting patch owner.");
                    if (!BetaPatches.IsUsable(feature)) throw new InvalidOperationException("The game schedule call was changed by another patch.");
                    Volatile.Write(ref _state[i], "On");
                    Volatile.Write(ref _enabled[i], true);
                }
                catch (Exception error)
                {
                    Volatile.Write(ref _state[i], "Unavailable. " + error.GetBaseException().Message);
                    try { Mod.Log.Info(feature + " beta: " + _state[i]); } catch { }
                }
            }
        }

        private void Install(BetaFeature feature)
        {
            var target = BetaPatches.Validate(feature);
            var info = Harmony.GetPatchInfo(target);
            if (info != null && info.Owners.Count != 0)
                throw new InvalidOperationException("Another patch already owns the target method.");
            int i = (int)feature;
            var patch = new Harmony(Mod.HarmonyId + ".beta." + feature.ToString().ToLowerInvariant());
            var transpiler = BetaPatches.Transpiler(feature);
            // Retain the owner even when Harmony reports a partially installed patch.
            _targets[i] = target;
            _patches[i] = patch;
            try
            {
                patch.Patch(target, transpiler: new HarmonyMethod(transpiler));
                if (!BetaPatches.IsUsable(feature)) throw new InvalidOperationException("Expected exactly one stock scheduling call.");
                _installed[i] = true;
            }
            catch
            {
                patch.UnpatchAll(patch.Id);
                _patches[i] = null;
                throw;
            }
        }

        internal static bool TrySchedule<T>(BetaFeature feature, T submission, out JobHandle handle)
            where T : struct, IBetaSubmission
        {
            handle = default;
            var current = Volatile.Read(ref s_current);
            int i = (int)feature;
            if (current == null || !Volatile.Read(ref current._enabled[i]) || !current.CheckOwnership(feature)) return false;
            lock (current._gate)
            {
                if (current._stopping || !Volatile.Read(ref current._enabled[i]) || !current.CurrentOwner(feature) || !BetaPatches.IsUsable(feature) ||
                    (feature == BetaFeature.Route && current._routeSuperseded)) return false;
                int slot = current._next;
                current._next = (slot + 1) % current._handles.Length;
                if (current._occupied[slot])
                {
                    try
                    {
                        if (!current._handles[slot].IsCompleted) return false;
                        current._handles[slot].Complete();
                        current._occupied[slot] = false;
                    }
                    catch (Exception error)
                    {
                        Volatile.Write(ref current._state[i], "On. Last handle check failed. " + error.GetBaseException().Message);
                        return false;
                    }
                }
                // Once the scheduler is called, an exception must propagate. Retrying vanilla
                // after an ambiguous native submission could execute the same work twice.
                handle = submission.Schedule();
                current._handles[slot] = handle;
                current._occupied[slot] = true;
                return true;
            }
        }

        internal void Dispose()
        {
            var publication = HarmonyPatchLock;
            if (publication != null)
            {
                // Do not alter another in-progress transaction on the same thread.
                bool reentrant = Monitor.IsEntered(publication);
                lock (publication)
                {
                    _stopping = true;
                    Array.Clear(_enabled, 0, _enabled.Length);
                }
                if (reentrant) throw new InvalidOperationException("Retry beta teardown after Harmony patch publication.");
            }
            else _stopping = true;

            // The stop flag blocks new admissions. Finish earlier submissions
            // without holding Harmony's lock, then remove only our own patches.
            lock (_gate)
            {
                for (int i = 0; i < _handles.Length; i++)
                {
                    if (!_occupied[i]) continue;
                    _handles[i].Complete();
                    _occupied[i] = false;
                }
            }
            if (publication != null)
            {
                lock (publication)
                {
                    for (int i = 0; i < _patches.Length; i++)
                    {
                        var patch = _patches[i];
                        if (patch == null) continue;
                        patch.UnpatchAll(patch.Id);
                        _patches[i] = null;
                        _installed[i] = false;
                    }
                }
            }
            AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
            Interlocked.CompareExchange(ref s_current, null, this);
        }
    }
}
