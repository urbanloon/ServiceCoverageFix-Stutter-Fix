using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Game.Simulation;
using Game.Pathfind;
using HarmonyLib;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace ServiceCoverageFix
{
    internal static class BetaPatches
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly int[] Sites = new int[BetaFeatures.SiteCount];
        private static readonly int[] Generations = new int[BetaFeatures.SiteCount];
        private static readonly int[] OtherPublisher = new int[BetaFeatures.SiteCount];
        internal static bool SharesPublisher(BetaFeature feature) => Volatile.Read(ref OtherPublisher[(int)feature]) == 0;
        internal static int Generation(BetaFeature feature) => Volatile.Read(ref Generations[(int)feature]);
        private static readonly Type?[] Originals = new Type?[BetaFeatures.SiteCount];
        internal static bool IsUsable(BetaFeature feature) => Volatile.Read(ref Sites[(int)feature]) == 1;
        internal static MethodInfo Transpiler(BetaFeature feature) => typeof(BetaPatches).GetMethod(feature.ToString(), All)!;

        internal static MethodInfo Validate(BetaFeature feature)
        {
            BetaManifest.Validate(feature, CompatibilityGate.ActualGameSha256);
            string systemName, jobName, methodName;
            Type worker;
            switch (feature)
            {
                case BetaFeature.Housing:
                    systemName = "Game.Simulation.CitizenPathfindSetup"; jobName = systemName + "+SetupFindHomeJob";
                    methodName = "SetupFindHome"; worker = typeof(HousingJob); break;
                case BetaFeature.Seller:
                    systemName = "Game.Simulation.ResourcePathfindSetup"; jobName = systemName + "+SetupResourceSellerJob";
                    methodName = "SetupResourceSeller"; worker = typeof(SellerJob); break;
                case BetaFeature.Water:
                    systemName = "Game.Simulation.WaterPipeFlowSystem"; jobName = typeof(WaterPipeFlowJob).FullName;
                    methodName = "ScheduleFlowJob"; worker = typeof(OptimizedWaterFlowJob); break;
                case BetaFeature.Route:
                    systemName = "Game.Pathfind.PathfindQueueSystem"; jobName = systemName + "+PathfindWorkerJob";
                    methodName = "ScheduleWorkerJobs"; worker = typeof(RouteJob); break;
                case BetaFeature.CompanyCommercial:
                case BetaFeature.CompanyIndustrial:
                    systemName = feature == BetaFeature.CompanyCommercial ? "Game.Simulation.CommercialFindPropertySystem" : "Game.Simulation.IndustrialFindPropertySystem";
                    jobName = typeof(Game.Buildings.PropertyUtils.CompanyFindPropertyJob).FullName;
                    methodName = "OnUpdate"; worker = typeof(CompanyPropertyJob); break;
                default: throw new ArgumentOutOfRangeException(nameof(feature));
            }
            var game = typeof(Game.UpdateSystem).Assembly;
            Type source = game.GetType(jobName, true);
            var target = game.GetType(systemName, true).GetMethods(All).Single(m => m.Name == methodName);
            var contract = IsChunk(feature) ? typeof(IJobChunk) : typeof(IJob);
            if (!contract.IsAssignableFrom(source) || !contract.IsAssignableFrom(worker) ||
                worker.GetCustomAttribute<BurstCompileAttribute>() == null ||
                !contract.CustomAttributes.Any(a => a.AttributeType.Name == "JobProducerTypeAttribute"))
                throw new InvalidOperationException("Unsupported Unity job producer contract for " + worker.Name);
            if (feature == BetaFeature.Route)
            {
                var collections = typeof(Colossal.Collections.UnsafeMinHeap<>).Assembly;
                using (var stream = File.OpenRead(collections.Location))
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() !=
                        "22b6e2693e4eb533ec086f3c7f5ccc93b75f071e6bb6132a01574bcf5c5f4460" || !RouteHeap.SupportsLayout())
                        throw new InvalidOperationException("Unsupported route heap layout or collection build.");
            }
            if (feature == BetaFeature.Water) WaterFlowSchedule.Validate();
            else if (BetaFeatures.IsCompany(feature))
            {
                var fields = worker.GetFields(All).Where(f => !f.IsStatic).ToArray();
                if (IntPtr.Size != 8 || fields.Length != 1 || fields[0].Name != nameof(CompanyPropertyJob.Source) ||
                    fields[0].FieldType != source || UnsafeUtility.GetFieldOffset(fields[0]) != 0 ||
                    UnsafeUtility.SizeOf<CompanyPropertyJob>() != UnsafeUtility.SizeOf<Game.Buildings.PropertyUtils.CompanyFindPropertyJob>())
                    throw new InvalidOperationException("Unsupported company property job layout.");
            }
            else typeof(BetaPatches).GetMethod(nameof(ValidateMirror), All)!
                .MakeGenericMethod(source, worker).Invoke(null, null);
            // Register the concrete producer before an enabled bridge can submit anything.
            // The postprocessor also sees closed worker schedule calls in BetaSchedule.
            switch (feature)
            {
                case BetaFeature.Housing: JobChunkExtensions.EarlyJobInit<HousingJob>(); break;
                case BetaFeature.Seller: JobChunkExtensions.EarlyJobInit<SellerJob>(); break;
                case BetaFeature.Water: IJobExtensions.EarlyJobInit<OptimizedWaterFlowJob>(); break;
                case BetaFeature.Route: IJobExtensions.EarlyJobInit<RouteJob>(); break;
                case BetaFeature.CompanyCommercial:
                case BetaFeature.CompanyIndustrial: IJobExtensions.EarlyJobInit<CompanyPropertyJob>(); break;
            }
            Originals[(int)feature] = source;
            Volatile.Write(ref Sites[(int)feature], 0);
            return target;
        }

        private static void ValidateMirror<TSource, TWorker>() where TSource : struct where TWorker : struct
        {
            var original = typeof(TSource); var worker = typeof(TWorker);
            var sourceFields = original.GetFields(All).Where(f => !f.IsStatic).ToArray();
            var workerFields = worker.GetFields(All).Where(f => !f.IsStatic).ToDictionary(f => f.Name);
            if (IntPtr.Size != 8 || UnsafeUtility.SizeOf<TSource>() != UnsafeUtility.SizeOf<TWorker>() ||
                sourceFields.Length != workerFields.Count)
                throw new InvalidOperationException("Incompatible job layout for " + worker.Name);
            foreach (var field in sourceFields)
            {
                if (!workerFields.TryGetValue(field.Name, out var mirror) || mirror.FieldType != field.FieldType ||
                    UnsafeUtility.GetFieldOffset(field) != UnsafeUtility.GetFieldOffset(mirror) ||
                    !SafetyAttributes(field).SequenceEqual(SafetyAttributes(mirror)))
                    throw new InvalidOperationException("Incompatible job field: " + worker.Name + "." + field.Name);
            }
        }

        private static IEnumerable<string> SafetyAttributes(FieldInfo field) => field.CustomAttributes
            .Where(a => a.AttributeType.Namespace?.StartsWith("Unity.Collections", StringComparison.Ordinal) == true ||
                        a.AttributeType.FullName == "Unity.Jobs.LowLevel.Unsafe.NativeSetThreadIndexAttribute")
            .Select(a => a.ToString()).OrderBy(a => a, StringComparer.Ordinal);

        private static bool IsChunk(BetaFeature f) => f == BetaFeature.Housing || f == BetaFeature.Seller;
        private static IEnumerable<CodeInstruction> Housing(IEnumerable<CodeInstruction> instructions) => Replace(BetaFeature.Housing, instructions);
        private static IEnumerable<CodeInstruction> Seller(IEnumerable<CodeInstruction> instructions) => Replace(BetaFeature.Seller, instructions);
        private static IEnumerable<CodeInstruction> Water(IEnumerable<CodeInstruction> instructions) => Replace(BetaFeature.Water, instructions);
        private static IEnumerable<CodeInstruction> Route(IEnumerable<CodeInstruction> instructions) => Replace(BetaFeature.Route, instructions);

        private static IEnumerable<CodeInstruction> CompanyCommercial(IEnumerable<CodeInstruction> instructions) => Replace(BetaFeature.CompanyCommercial, instructions);
        private static IEnumerable<CodeInstruction> CompanyIndustrial(IEnumerable<CodeInstruction> instructions) => Replace(BetaFeature.CompanyIndustrial, instructions);

        private static IEnumerable<CodeInstruction> Replace(BetaFeature f, IEnumerable<CodeInstruction> instructions)
        {
            // Harmony 2.2.2 publishes its owner list after running transpilers.
            // Invalidate the previous snapshot now; the dispatcher checks the published list.
            // Separate 0Harmony assemblies can share the patch registry but own
            // different publication monitors. Do not authorize their wrappers from
            // a possibly stale registry image. This stack inspection runs only on rebuild.
            bool otherPublisher = false;
            foreach (var frame in new StackTrace(false).GetFrames() ?? Array.Empty<StackFrame>())
            {
                var type = frame.GetMethod()?.DeclaringType;
                if (type != null && type.Namespace == "HarmonyLib" && type.Assembly != typeof(Harmony).Assembly &&
                    type.Assembly.GetName().Name == typeof(Harmony).Assembly.GetName().Name)
                { otherPublisher = true; break; }
            }
            Volatile.Write(ref OtherPublisher[(int)f], otherPublisher ? 1 : 0);
            Interlocked.Increment(ref Generations[(int)f]);
            var code = new List<CodeInstruction>(instructions);
            bool chunk = IsChunk(f);
            Type? source = Originals[(int)f];
            var matches = code.Where(i => i.opcode == OpCodes.Call && i.operand is MethodInfo method && method.IsStatic &&
                method.DeclaringType == (chunk ? typeof(JobChunkExtensions) : typeof(IJobExtensions)) &&
                method.Name == (chunk ? "ScheduleParallel" : "Schedule") && method.IsGenericMethod &&
                method.GetGenericArguments().Length == 1 && method.GetGenericArguments()[0] == source &&
                method.ReturnType == typeof(JobHandle) && method.GetParameters().Select(p => p.ParameterType)
                    .SequenceEqual(chunk ? new[] {source, typeof(EntityQuery), typeof(JobHandle)} : new[] {source, typeof(JobHandle)})).ToArray();
            Volatile.Write(ref Sites[(int)f], matches.Length);
            if (matches.Length != 1) return code;
            var adapter = typeof(BetaSchedule).GetMethod(f.ToString(), All)!;
            matches[0].operand = chunk ? adapter.MakeGenericMethod(source!) : adapter;
            return code;
        }
    }

}
