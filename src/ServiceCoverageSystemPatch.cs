using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace ServiceCoverageFix
{
    [HarmonyPatch]
    internal static class ServiceCoverageSystemPatch
    {
        internal static int ReplacementCount { get; private set; }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(CompatibilityGate.ServiceCoverageSystemType, "OnUpdate")
                ?? throw new MissingMethodException(
                    CompatibilityGate.ServiceCoverageSystemName,
                    "OnUpdate");
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo bridgeDefinition = AccessTools.Method(
                typeof(ApplyCoverageScheduleBridge),
                nameof(ApplyCoverageScheduleBridge.ScheduleOptimized))
                ?? throw new MissingMethodException(
                    typeof(ApplyCoverageScheduleBridge).FullName,
                    nameof(ApplyCoverageScheduleBridge.ScheduleOptimized));

            int replacements = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call &&
                    instruction.operand is MethodInfo calledMethod &&
                    IsApplyCoverageSchedule(calledMethod, out Type? applyJobType))
                {
                    instruction.operand = bridgeDefinition.MakeGenericMethod(applyJobType!);
                    replacements++;
                }

                yield return instruction;
            }

            if (replacements != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one ApplyCoverageJob Schedule call, found {replacements}.");
            }

            ReplacementCount = replacements;
        }

        private static bool IsApplyCoverageSchedule(MethodInfo method, out Type? applyJobType)
        {
            applyJobType = null;
            if (method.Name != nameof(IJobExtensions.Schedule) ||
                method.DeclaringType != typeof(IJobExtensions) ||
                !method.IsGenericMethod)
            {
                return false;
            }

            Type[] arguments = method.GetGenericArguments();
            if (arguments.Length != 1 ||
                arguments[0].FullName != CompatibilityGate.ApplyCoverageJobName)
            {
                return false;
            }

            applyJobType = arguments[0];
            return true;
        }
    }

    internal static class ApplyCoverageScheduleBridge
    {
        private static int _fallbackLogged;

        internal static void ValidateBridge(Type applyJobType)
        {
            _fallbackLogged = 0;
            MethodInfo method = typeof(ApplyCoverageScheduleBridge).GetMethod(
                nameof(ValidateBridgeTyped), BindingFlags.Static | BindingFlags.NonPublic)!;
            try { method.MakeGenericMethod(applyJobType).Invoke(null, null); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            { throw new InvalidOperationException("Coverage bridge preflight failed.", e.InnerException); }
        }

        private static unsafe void ValidateBridgeTyped<TApplyJob>() where TApplyJob : struct, IJob
        {
            // Exercise field delegates, container copying, Unity scheduling, and
            // actual worker execution using isolated data, never city buffers.
            foreach (int count in new[] { 0, 1, 2 })
            {
                using (var buildings = new NativeList<RawBuildingData>(Math.Max(1, count), Allocator.TempJob))
                using (var elements = new NativeList<RawCoverageElement>(Math.Max(1, count), Allocator.TempJob))
                using (var targets = new NativeArray<float2>(Math.Max(1, count), Allocator.TempJob))
                using (var counters = new NativeArray<ExecutionCounters>(1, Allocator.TempJob))
                {
                    var target = (float2*)NativeArrayUnsafeUtility.GetUnsafePtr(targets);
                    for (int i = 0; i < count; i++)
                    {
                        buildings.Add(new RawBuildingData { ElementIndex = i, ElementCount = 1,
                            Total = 10f, Remaining = 10f });
                        elements.Add(new RawCoverageElement { CoveragePtr = target + i,
                            Coverage = new float2(1f), AverageCoverage = 1f,
                            DensityFactor = 1f, LengthFactor = 1f });
                    }
                    TApplyJob original = default;
                    var buildingCopy = buildings;
                    var elementCopy = elements;
                    UnsafeUtility.MemCpy(ApplyJobFieldAccess<TApplyJob>.BuildingData(ref original).ToPointer(),
                        UnsafeUtility.AddressOf(ref buildingCopy), UnsafeUtility.SizeOf<NativeList<RawBuildingData>>());
                    UnsafeUtility.MemCpy(ApplyJobFieldAccess<TApplyJob>.Elements(ref original).ToPointer(),
                        UnsafeUtility.AddressOf(ref elementCopy), UnsafeUtility.SizeOf<NativeList<RawCoverageElement>>());
                    OptimizedApplyCoverageJob job = CreateOptimized(ref original);
                    job.Diagnostics = (ExecutionCounters*)NativeArrayUnsafeUtility.GetUnsafePtr(counters);
                    IJobExtensions.Schedule(job, default).Complete();
                    if (counters[0].Started != 1 || counters[0].Completed != 1)
                        throw new InvalidOperationException("Replacement execution was not observed during preflight.");
                    for (int i = 0; i < count; i++)
                        if (targets[i].x != 1f || targets[i].y != 1f)
                            throw new InvalidOperationException("Coverage bridge preflight returned incorrect coverage.");
                    if (counters[0].Managed != 0)
                        Mod.Log.Info("Coverage preflight executed in managed mode; live execution mode will be tracked.");
                }
            }
        }

        private static OptimizedApplyCoverageJob CreateOptimized<TApplyJob>(ref TApplyJob originalJob)
            where TApplyJob : struct, IJob
        {
            IntPtr buildingAddress = ApplyJobFieldAccess<TApplyJob>.BuildingData(ref originalJob);
            IntPtr elementAddress = ApplyJobFieldAccess<TApplyJob>.Elements(ref originalJob);
            return new OptimizedApplyCoverageJob
            {
                BuildingData = CopyNativeList<RawBuildingData>(buildingAddress),
                Elements = CopyNativeList<RawCoverageElement>(elementAddress)
            };
        }

        public static JobHandle ScheduleOptimized<TApplyJob>(
            TApplyJob originalJob,
            JobHandle dependency)
            where TApplyJob : struct, IJob
        {
            RuntimeDiagnostics.ReportTransition();
            try
            {
                return RuntimeDiagnostics.Schedule(CreateOptimized(ref originalJob), dependency);
            }
            catch (Exception exception)
            {
                RuntimeDiagnostics.RecordFallback(exception);
                if (Interlocked.Exchange(ref _fallbackLogged, 1) == 0)
                {
                    Mod.Log.Error(
                        $"Could not construct the optimized job; retaining the game's original job: {exception}");
                }

                return IJobExtensions.Schedule(originalJob, dependency);
            }
        }

        private static unsafe NativeList<T> CopyNativeList<T>(IntPtr sourceAddress)
            where T : unmanaged
        {
            if (sourceAddress == IntPtr.Zero)
            {
                throw new InvalidOperationException("A NativeList field address was null.");
            }

            NativeList<T> destination = default;
            UnsafeUtility.MemCpy(
                UnsafeUtility.AddressOf(ref destination),
                sourceAddress.ToPointer(),
                UnsafeUtility.SizeOf<NativeList<T>>());
            return destination;
        }

        private delegate IntPtr FieldAddressGetter<T>(ref T value) where T : struct;

        private static class ApplyJobFieldAccess<TApplyJob>
            where TApplyJob : struct
        {
            internal static readonly FieldAddressGetter<TApplyJob> BuildingData =
                CreateFieldAddressGetter("m_BuildingData", "BuildingData", 24);

            internal static readonly FieldAddressGetter<TApplyJob> Elements =
                CreateFieldAddressGetter("m_Elements", "CoverageElement", 32);

            private static FieldAddressGetter<TApplyJob> CreateFieldAddressGetter(
                string fieldName,
                string expectedElementName,
                int expectedElementSize)
            {
                FieldInfo? field = typeof(TApplyJob).GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (field == null || !field.FieldType.IsGenericType ||
                    field.FieldType.GetGenericTypeDefinition().FullName !=
                        "Unity.Collections.NativeList`1")
                {
                    throw new MissingFieldException(typeof(TApplyJob).FullName, fieldName);
                }

                Type elementType = field.FieldType.GetGenericArguments()[0];
                int elementSize = System.Runtime.InteropServices.Marshal.SizeOf(elementType);
                if (elementType.Name != expectedElementName ||
                    elementSize != expectedElementSize)
                {
                    throw new InvalidOperationException(
                        $"Unexpected {fieldName} layout: {elementType.FullName}, {elementSize} bytes.");
                }

                var dynamicMethod = new DynamicMethod(
                    $"AddressOf_{typeof(TApplyJob).Name}_{fieldName}",
                    typeof(IntPtr),
                    new[] { typeof(TApplyJob).MakeByRefType() },
                    typeof(ApplyCoverageScheduleBridge).Module,
                    true);

                ILGenerator il = dynamicMethod.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldflda, field);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Ret);

                return (FieldAddressGetter<TApplyJob>)dynamicMethod.CreateDelegate(
                    typeof(FieldAddressGetter<TApplyJob>));
            }
        }
    }
}

