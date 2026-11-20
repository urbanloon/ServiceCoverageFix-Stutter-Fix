using Game.Pathfind;
using Game.Simulation;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace ServiceCoverageFix
{
    internal static class BetaSchedule
    {
        private static unsafe TWorker Copy<TSource,TWorker>(ref TSource source) where TSource : struct where TWorker : struct
        {
            UnsafeUtility.CopyPtrToStructure(UnsafeUtility.AddressOf(ref source), out TWorker job);
            return job;
        }

        private struct HousingChunk<TSource> : IBetaSubmission where TSource : struct, IJobChunk
        {
            internal TSource Source;
            internal EntityQuery Query;
            internal JobHandle Dependency;
            public JobHandle Schedule() => JobChunkExtensions.ScheduleParallel(Copy<TSource,HousingJob>(ref Source), Query, Dependency);
        }

        private struct SellerChunk<TSource> : IBetaSubmission where TSource : struct, IJobChunk
        {
            internal TSource Source;
            internal EntityQuery Query;
            internal JobHandle Dependency;
            public JobHandle Schedule() => JobChunkExtensions.ScheduleParallel(Copy<TSource,SellerJob>(ref Source), Query, Dependency);
        }

        internal static JobHandle Housing<T>(T job, EntityQuery query, JobHandle dependency) where T : struct, IJobChunk
        {
            var work = new HousingChunk<T> {Source=job, Query=query, Dependency=dependency};
            return BetaRuntime.TrySchedule(BetaFeature.Housing, work, out var handle) ? handle : JobChunkExtensions.ScheduleParallel(job, query, dependency);
        }
        internal static JobHandle Seller<T>(T job, EntityQuery query, JobHandle dependency) where T : struct, IJobChunk
        {
            var work = new SellerChunk<T> {Source=job, Query=query, Dependency=dependency};
            return BetaRuntime.TrySchedule(BetaFeature.Seller, work, out var handle) ? handle : JobChunkExtensions.ScheduleParallel(job, query, dependency);
        }
        private struct WaterSubmission : IBetaSubmission
        {
            internal WaterPipeFlowJob Job; internal JobHandle Dependency;
            public JobHandle Schedule() => WaterFlowSchedule.Schedule(Job, Dependency);
        }
        internal static JobHandle Water(WaterPipeFlowJob job, JobHandle dependency)
        {
            var work = new WaterSubmission {Job=job, Dependency=dependency};
            return BetaRuntime.TrySchedule(BetaFeature.Water, work, out var handle) ? handle : IJobExtensions.Schedule(job, dependency);
        }
        private struct RouteSubmission : IBetaSubmission
        {
            internal PathfindQueueSystem.PathfindWorkerJob Job; internal JobHandle Dependency;
            public JobHandle Schedule() => IJobExtensions.Schedule(Copy<PathfindQueueSystem.PathfindWorkerJob,RouteJob>(ref Job), Dependency);
        }
        internal static JobHandle Route(PathfindQueueSystem.PathfindWorkerJob job, JobHandle dependency)
        {
            var work = new RouteSubmission {Job=job, Dependency=dependency};
            return BetaRuntime.TrySchedule(BetaFeature.Route, work, out var handle) ? handle : IJobExtensions.Schedule(job, dependency);
        }
    }
}
