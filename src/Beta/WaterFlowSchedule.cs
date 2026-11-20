using System;
using System.Reflection;
using Game.Simulation;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace ServiceCoverageFix
{
    internal static class WaterFlowSchedule
    {
        internal static JobHandle Schedule(WaterPipeFlowJob job, JobHandle dependency)
        {
            return new OptimizedWaterFlowJob { Original = job }.Schedule(dependency);
        }

        internal static unsafe void Validate()
        {
            var wrapper = typeof(OptimizedWaterFlowJob);
            var fields = wrapper.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (IntPtr.Size != 8 || fields.Length != 1 || fields[0].FieldType != typeof(WaterPipeFlowJob)
                || UnsafeUtility.GetFieldOffset(fields[0]) != 0 || sizeof(OptimizedWaterFlowJob) != sizeof(WaterPipeFlowJob))
                throw new InvalidOperationException("Unsupported water flow job layout.");
            if (typeof(WaterPipeFlowJob).Assembly.ManifestModule.ModuleVersionId !=
                new Guid("46198354-d4f9-4d23-bf32-7e3c085d8b92"))
                throw new InvalidOperationException("Unrecognized water solver game module.");
        }
    }
}
