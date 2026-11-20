using System.Threading;
using Colossal.Collections;
using Game.Common;
using Game.Pathfind;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace ServiceCoverageFix
{
    [BurstCompile]
    public struct RouteJob : IJob
    {
        [ReadOnly] public RandomSeed m_RandomSeed;
        [ReadOnly] public NativePathfindData m_PathfindData;
        [ReadOnly] public PathfindHeuristicData m_PathfindHeuristicData;
        [ReadOnly] public float m_MaxPassengerTransportSpeed;
        [ReadOnly] public float m_MaxCargoTransportSpeed;
        [ReadOnly] public NativeArray<PathfindQueueSystem.WorkerAction> m_Actions;
        [NativeDisableContainerSafetyRestriction] public NativeReference<int> m_ActionIndex;
        [NativeDisableUnsafePtrRestriction] public AllocatorHelper<UnsafeLinearAllocator> m_Allocator;

        internal const int MinimumGraphSize = 8192;

        public unsafe void Execute()
        {
            ref int nextAction = ref CollectionUtils.ValueAsRef(m_ActionIndex);
            ref UnsafeLinearAllocator scratch = ref m_Allocator.Allocator;
            Allocator allocator = scratch.Handle.ToAllocator;
            while (true)
            {
                int index = Interlocked.Increment(ref nextAction) - 1;
                if (index >= m_Actions.Length)
                    break;
                PathfindQueueSystem.WorkerAction action = m_Actions[index];
                switch (action.m_Type)
                {
                    case PathfindQueueSystem.ActionType.Pathfind:
                        ExecuteRoute(ref UnsafeUtility.AsRef<PathfindActionData>(action.m_ActionData), index, allocator);
                        break;
                    case PathfindQueueSystem.ActionType.Coverage:
                        ref CoverageActionData coverage = ref UnsafeUtility.AsRef<CoverageActionData>(action.m_ActionData);
                        CoverageJobs.CoverageJob.Execute(m_PathfindData, allocator, ref coverage);
                        Interlocked.MemoryBarrier();
                        coverage.m_State = PathfindActionState.Completed;
                        break;
                    case PathfindQueueSystem.ActionType.Availability:
                        ref AvailabilityActionData availability = ref UnsafeUtility.AsRef<AvailabilityActionData>(action.m_ActionData);
                        AvailabilityJobs.AvailabilityJob.Execute(m_PathfindData, allocator, ref availability);
                        Interlocked.MemoryBarrier();
                        availability.m_State = PathfindActionState.Completed;
                        break;
                }
                scratch.Rewind(false);
            }
            scratch.Rewind(true);
        }

        private void ExecuteRoute(ref PathfindActionData action, int index, Allocator allocator)
        {
            var random = m_RandomSeed.GetRandom(index);
            if (!m_PathfindData.IsCreated || m_PathfindData.Size < MinimumGraphSize)
            {
                PathfindJobs.PathfindJob.Execute(m_PathfindData, allocator, random, m_PathfindHeuristicData,
                    m_MaxPassengerTransportSpeed, m_MaxCargoTransportSpeed, ref action);
            }
            else
            {
                ReconstructedRouteBody.Execute(m_PathfindData, allocator, random, m_PathfindHeuristicData,
                    m_MaxPassengerTransportSpeed, m_MaxCargoTransportSpeed, ref action);
            }
            Interlocked.MemoryBarrier();
            action.m_State = PathfindActionState.Completed;
        }
    }
}
