using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

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
            if (arguments.Length != 1 || arguments[0].FullName != CompatibilityGate.ApplyCoverageJobName)
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

        public static JobHandle ScheduleOptimized<TApplyJob>(
            TApplyJob originalJob,
            JobHandle dependency)
            where TApplyJob : struct, IJob
        {
            try
            {
                IntPtr buildingAddress = ApplyJobFieldAccess<TApplyJob>.BuildingData(ref originalJob);
                IntPtr elementAddress = ApplyJobFieldAccess<TApplyJob>.Elements(ref originalJob);

                NativeList<RawBuildingData> buildingData =
                    CopyNativeList<RawBuildingData>(buildingAddress);
                NativeList<RawCoverageElement> elements =
                    CopyNativeList<RawCoverageElement>(elementAddress);

                var optimizedJob = new OptimizedApplyCoverageJob
                {
                    BuildingData = buildingData,
                    Elements = elements
                };

                return IJobExtensions.Schedule(optimizedJob, dependency);
            }
            catch (Exception exception)
            {
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
                    field.FieldType.GetGenericTypeDefinition().FullName != "Unity.Collections.NativeList`1")
                {
                    throw new MissingFieldException(typeof(TApplyJob).FullName, fieldName);
                }

                Type elementType = field.FieldType.GetGenericArguments()[0];
                int elementSize = System.Runtime.InteropServices.Marshal.SizeOf(elementType);
                if (elementType.Name != expectedElementName || elementSize != expectedElementSize)
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
