using Game.Pathfind;
using Game.Simulation;
using Unity.Collections.LowLevel.Unsafe;
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

        private struct CompanySubmission : IBetaSubmission
        {
            internal Game.Buildings.PropertyUtils.CompanyFindPropertyJob Job;
            internal JobHandle Dependency;
            public JobHandle Schedule() => IJobExtensions.Schedule(new CompanyPropertyJob { Source = Job }, Dependency);
        }

        internal static JobHandle CompanyCommercial(Game.Buildings.PropertyUtils.CompanyFindPropertyJob job, JobHandle dependency)
            => ScheduleCompany(BetaFeature.CompanyCommercial, job, dependency);
        internal static JobHandle CompanyIndustrial(Game.Buildings.PropertyUtils.CompanyFindPropertyJob job, JobHandle dependency)
            => ScheduleCompany(BetaFeature.CompanyIndustrial, job, dependency);

        private static JobHandle ScheduleCompany(BetaFeature site, Game.Buildings.PropertyUtils.CompanyFindPropertyJob job, JobHandle dependency)
        {
            var work = new CompanySubmission { Job = job, Dependency = dependency };
            return BetaRuntime.TrySchedule(site, work, out var handle) ? handle : IJobExtensions.Schedule(job, dependency);
        }
    }
}
