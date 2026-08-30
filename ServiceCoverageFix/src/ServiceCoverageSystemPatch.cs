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
        private const int DiagnosticSlotCount = 64;
        private static int _fallbackLogged;
        private static int _inputDiagnosticsEnabled;
        private static int _inputDiagnosticSession;
        private static int _inputDiagnosticPass;
        private static readonly NativeArray<int>[] DiagnosticValues =
            new NativeArray<int>[DiagnosticSlotCount];
        private static readonly JobHandle[] DiagnosticHandles =
            new JobHandle[DiagnosticSlotCount];
        private static readonly bool[] DiagnosticPending =
            new bool[DiagnosticSlotCount];
        private static readonly int[] DiagnosticPassNumbers =
            new int[DiagnosticSlotCount];
        private static readonly int[] DiagnosticSessionNumbers =
            new int[DiagnosticSlotCount];

        internal static bool InputDiagnosticsEnabled =>
            Volatile.Read(ref _inputDiagnosticsEnabled) != 0;

        internal static void InitializeInputDiagnostics()
        {
            Interlocked.Exchange(ref _inputDiagnosticsEnabled, 0);
            FlushCompletedInputDiagnostics(forceCompletion: true);
            Interlocked.Exchange(ref _inputDiagnosticPass, 0);
            Interlocked.Exchange(ref _inputDiagnosticSession, 0);
            Interlocked.Exchange(ref _fallbackLogged, 0);
        }

        internal static void ToggleInputDiagnostics()
        {
            if (InputDiagnosticsEnabled)
            {
                StopInputDiagnostics();
            }
            else
            {
                StartInputDiagnostics();
            }
        }

        internal static void StartInputDiagnostics()
        {
            Interlocked.Exchange(ref _inputDiagnosticsEnabled, 0);
            FlushCompletedInputDiagnostics(forceCompletion: true);
            Interlocked.Exchange(ref _inputDiagnosticPass, 0);
            int session = Interlocked.Increment(ref _inputDiagnosticSession);
            Interlocked.Exchange(ref _inputDiagnosticsEnabled, 1);
            Mod.Log.Info(
                $"Coverage input diagnostics started (session {session:D2}). " +
                "Press Ctrl+Shift+F9 again to stop.");
        }

        internal static void StopInputDiagnostics()
        {
            if (Interlocked.Exchange(ref _inputDiagnosticsEnabled, 0) == 0)
            {
                return;
            }

            FlushCompletedInputDiagnostics(forceCompletion: true);
            Mod.Log.Info(
                $"Coverage input diagnostics stopped after " +
                $"{Volatile.Read(ref _inputDiagnosticPass)} passes.");
        }

        internal static void ShutdownInputDiagnostics()
        {
            Interlocked.Exchange(ref _inputDiagnosticsEnabled, 0);
            FlushCompletedInputDiagnostics(forceCompletion: true);
        }

        private static void FlushCompletedInputDiagnostics(bool forceCompletion = false)
        {
            for (int slot = 0; slot < DiagnosticSlotCount; slot++)
            {
                if (!DiagnosticPending[slot])
                {
                    continue;
                }

                JobHandle handle = DiagnosticHandles[slot];
                if (!forceCompletion && !handle.IsCompleted)
                {
                    continue;
                }

                handle.Complete();
                NativeArray<int> values = DiagnosticValues[slot];
                Mod.Log.Info(
                    $"Coverage input pass {DiagnosticPassNumbers[slot]:D4} " +
                    $"(session {DiagnosticSessionNumbers[slot]:D2}): " +
                    $"providers={values[0]}, elements={values[1]}, processed={values[2]}");
                values.Dispose();
                DiagnosticValues[slot] = default;
                DiagnosticPending[slot] = false;
            }
        }

        private static int FindFreeDiagnosticSlot()
        {
            for (int slot = 0; slot < DiagnosticSlotCount; slot++)
            {
                if (!DiagnosticPending[slot])
                {
                    return slot;
                }
            }

            return -1;
        }

        public static JobHandle ScheduleOptimized<TApplyJob>(
            TApplyJob originalJob,
            JobHandle dependency)
            where TApplyJob : struct, IJob
        {
            NativeArray<int> inputDiagnostics = default;
            int diagnosticSlot = -1;
            int diagnosticPass = 0;
            int diagnosticSession = 0;
            try
            {
                FlushCompletedInputDiagnostics();

                IntPtr buildingAddress = ApplyJobFieldAccess<TApplyJob>.BuildingData(ref originalJob);
                IntPtr elementAddress = ApplyJobFieldAccess<TApplyJob>.Elements(ref originalJob);

                NativeList<RawBuildingData> buildingData =
                    CopyNativeList<RawBuildingData>(buildingAddress);
                NativeList<RawCoverageElement> elements =
                    CopyNativeList<RawCoverageElement>(elementAddress);

                if (InputDiagnosticsEnabled)
                {
                    diagnosticSlot = FindFreeDiagnosticSlot();
                    if (diagnosticSlot >= 0)
                    {
                        diagnosticPass = Interlocked.Increment(ref _inputDiagnosticPass);
                        diagnosticSession = Volatile.Read(ref _inputDiagnosticSession);
                        inputDiagnostics = new NativeArray<int>(
                            3,
                            Allocator.Persistent,
                            NativeArrayOptions.ClearMemory);
                    }
                    else
                    {
                        Mod.Log.Warn(
                            "Coverage diagnostics skipped one pass because all 64 result slots were still pending.");
                    }
                }

                var optimizedJob = new OptimizedApplyCoverageJob
                {
                    BuildingData = buildingData,
                    Elements = elements,
                    InputDiagnostics = inputDiagnostics
                };

                JobHandle handle = IJobExtensions.Schedule(optimizedJob, dependency);
                if (inputDiagnostics.IsCreated)
                {
                    DiagnosticValues[diagnosticSlot] = inputDiagnostics;
                    DiagnosticHandles[diagnosticSlot] = handle;
                    DiagnosticPassNumbers[diagnosticSlot] = diagnosticPass;
                    DiagnosticSessionNumbers[diagnosticSlot] = diagnosticSession;
                    DiagnosticPending[diagnosticSlot] = true;
                    inputDiagnostics = default;
                }

                return handle;
            }
            catch (Exception exception)
            {
                if (inputDiagnostics.IsCreated)
                {
                    inputDiagnostics.Dispose();
                }

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
