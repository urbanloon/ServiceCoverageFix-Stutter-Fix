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

        internal static bool? ServiceBindingAvailable { get; private set; }
        internal static void Reset() { ReplacementCount = 0; ServiceBindingAvailable = null; }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(CompatibilityGate.ServiceCoverageSystemType, "OnUpdate")
                ?? throw new MissingMethodException(
                    CompatibilityGate.ServiceCoverageSystemName,
                    "OnUpdate");
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = ReplaceSchedule(instructions, generator, false, out bool bound);
            ServiceBindingAvailable = bound;
            ReplacementCount = 1;
            return code;
        }

        internal static IEnumerable<CodeInstruction> ReplaceSchedule(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator, bool preview, out bool bound)
        {
            var code = new List<CodeInstruction>(instructions);
            Type applyJobType = CompatibilityGate.ApplyCoverageJobType;
            var applySites = code.FindAll(i => IsSchedule(i, applyJobType));
            if (applySites.Count != 1)
                throw new InvalidOperationException($"Expected exactly one ApplyCoverageJob Schedule call, found {applySites.Count}.");
            var apply = applySites[0];
            string entry = preview ? nameof(ApplyCoverageScheduleBridge.SchedulePreview) : nameof(ApplyCoverageScheduleBridge.ScheduleOptimized);
            var bridge = AccessTools.Method(typeof(ApplyCoverageScheduleBridge), entry)!
                .MakeGenericMethod(applyJobType);

            var prepareJobType = CompatibilityGate.ServiceCoverageSystemType.GetNestedType(
                "PrepareCoverageJob", BindingFlags.Public | BindingFlags.NonPublic);
            var prepareSites = code.FindAll(i => IsSchedule(i, prepareJobType));
            FieldInfo? serviceField = null;
            if (prepareSites.Count == 1)
            {
                int start = code.IndexOf(prepareSites[0]);
                int end = code.IndexOf(apply);
                // Both calls must lie on one straight-line path. Alternate
                // control flow could otherwise reach Apply with a stale local.
                bool singlePath = start < end && code[start].blocks.Count == 0;
                for (int i = start + 1; singlePath && i <= end; i++)
                    singlePath = code[i].labels.Count == 0 && code[i].blocks.Count == 0 &&
                        code[i].opcode.FlowControl != FlowControl.Branch &&
                        code[i].opcode.FlowControl != FlowControl.Cond_Branch;
                if (singlePath)
                {
                    var field = prepareJobType!.GetField("m_Service", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field?.FieldType == typeof(Game.Net.CoverageService) &&
                        Enum.GetUnderlyingType(field.FieldType) == typeof(byte)) serviceField = field;
                }
            }

            if (preview && serviceField == null)
                throw new InvalidOperationException("Could not bind the service on the scheduled preview PrepareCoverageJob.");
            LocalBuilder? service = null;
            if (serviceField != null)
            {
                var dependency = generator.DeclareLocal(typeof(JobHandle));
                service = generator.DeclareLocal(typeof(int));
                CodeInstruction prepare = prepareSites[0];
                var originalCall = new CodeInstruction(prepare.opcode, prepare.operand);
                // Preserve entry labels and exception metadata on the first
                // operation. Read m_Service from the exact value being scheduled.
                prepare.opcode = OpCodes.Stloc;
                prepare.operand = dependency;
                code.InsertRange(code.IndexOf(prepare) + 1, new[] {
                    new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Ldfld, serviceField),
                    new CodeInstruction(OpCodes.Stloc, service), new CodeInstruction(OpCodes.Ldloc, dependency), originalCall
                });
            }

            // A changed Prepare site disables attribution only. A value of -1
            // still schedules the fix and leaves all service rows unattributed.
            apply.opcode = service == null ? OpCodes.Ldc_I4_M1 : OpCodes.Ldloc;
            apply.operand = service;
            code.Insert(code.IndexOf(apply) + 1, new CodeInstruction(OpCodes.Call, bridge));
            bound = service != null;
            return code;
        }

        private static bool IsSchedule(CodeInstruction instruction, Type? jobType)
        {
            if (instruction.opcode != OpCodes.Call || !(instruction.operand is MethodInfo method) ||
                method.Name != nameof(IJobExtensions.Schedule) || method.DeclaringType != typeof(IJobExtensions) ||
                !method.IsGenericMethod || method.ReturnType != typeof(JobHandle)) return false;
            Type[] arguments = method.GetGenericArguments();
            ParameterInfo[] parameters = method.GetParameters();
            return arguments.Length == 1 && arguments[0] == jobType &&
                parameters.Length == 2 && parameters[0].ParameterType == arguments[0] &&
                parameters[1].ParameterType == typeof(JobHandle);
        }
    }

    [HarmonyPatch]
    internal static class ServiceCoveragePreviewPatch
    {
        internal static int ReplacementCount { get; private set; }

        internal static void Reset() => ReplacementCount = 0;

        private static MethodBase TargetMethod() =>
            AccessTools.Method(CompatibilityGate.CoveragePreviewSystemType!, "OnUpdate")
            ?? throw new MissingMethodException("Game.Tools.CoveragePreviewSystem", "OnUpdate");

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = ServiceCoverageSystemPatch.ReplaceSchedule(instructions, generator, true, out _);
            ReplacementCount = 1;
            return code;
        }
    }

    internal static class ApplyCoverageScheduleBridge
    {
        private static int _fallbackLogged;
        private static int _previewFallbackLogged;

        internal static void ValidateBridge(Type applyJobType)
        {
            _fallbackLogged = _previewFallbackLogged = 0;
            MethodInfo method = typeof(ApplyCoverageScheduleBridge).GetMethod(
                nameof(ValidateBridgeTyped), BindingFlags.Static | BindingFlags.NonPublic)!;
            try { method.MakeGenericMethod(applyJobType).Invoke(null, null); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            { throw new InvalidOperationException("Coverage bridge preflight failed.", e.InnerException); }
        }

        private static unsafe void ValidateBridgeTyped<TApplyJob>() where TApplyJob : struct, IJob
        {
            // Exercise field delegates, container copying and synchronous Unity job
            // execution using isolated data. Run returns after execution, so even
            // a failed probe cannot outlive its temporary buffers.
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
                    IJobExtensions.Run(job);
                    if (counters[0].Started != 1 || counters[0].Completed != 1)
                        throw new InvalidOperationException("Replacement execution was not observed during preflight.");
                    for (int i = 0; i < count; i++)
                        if (targets[i].x != 1f || targets[i].y != 1f)
                            throw new InvalidOperationException("Coverage bridge preflight returned incorrect coverage.");
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

        public static JobHandle ScheduleOptimized<TApplyJob>(TApplyJob originalJob, JobHandle dependency, int service = -1)
            where TApplyJob : struct, IJob => Schedule(originalJob, dependency, service, false);

        public static JobHandle SchedulePreview<TApplyJob>(TApplyJob originalJob, JobHandle dependency, int service)
            where TApplyJob : struct, IJob => Schedule(originalJob, dependency, service, true);

        private static JobHandle Schedule<TApplyJob>(TApplyJob originalJob, JobHandle dependency, int service, bool preview)
            where TApplyJob : struct, IJob
        {
            try
            {
                return RuntimeDiagnostics.Schedule(CreateOptimized(ref originalJob), dependency, service, preview);
            }
            catch (Exception exception)
            {
                // Reporting is best-effort. Even exception formatting or the
                // logger itself may throw; neither may prevent vanilla work.
                try { RuntimeDiagnostics.RecordFallback(exception, preview); }
                catch { /* Keep the original scheduling path available. */ }
                try
                {
                    ref int reported = ref (preview ? ref _previewFallbackLogged : ref _fallbackLogged);
                    if (Interlocked.Exchange(ref reported, 1) == 0)
                    {
                        Mod.Log.Error(
                            $"Could not schedule the optimized {(preview ? "preview" : "simulation")} job; retaining the game's original job: {exception}");
                    }
                }
                catch { /* Logging cannot suppress the fallback. */ }

                // Deliberately outside the reporting guards: a failure in the
                // original scheduler must still propagate to the game.
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
                    field.FieldType.GetGenericTypeDefinition() != typeof(NativeList<>))
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

