#define UNITY_ASSERTIONS
using Colossal.Collections;
using Game.Simulation;
using Game.Simulation.Flow;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace ServiceCoverageFix
{
    // The sole field retains the stock job's deferred arrays, container annotations,
    // and the source system's existing ownership of persistent solver state.
    [StructLayout(LayoutKind.Sequential)]
    [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
    internal unsafe struct OptimizedWaterFlowJob : IJob
    {
        internal WaterPipeFlowJob Original;

        public void Execute()
        {
            ref var state = ref CollectionUtils.ValueAsRef(Original.m_State);
            if (state.m_Error || state.m_Phase != WaterPipeFlowJob.Phase.FluidFlow)
            {
                Original.Execute();
                return;
            }

            state.m_Error = true;
            int budget = unchecked(state.m_StepCounter + Original.m_FrameCount * math.max(100, state.m_LastTotalSteps / 124));
            if (Original.m_FinalFrame || state.m_StepCounter < budget)
                RunFluidPhase(ref state, budget);
            state.m_Error = false;

            if (Original.m_FinalFrame)
            {
                state.m_Phase = WaterPipeFlowJob.Phase.Initial;
                state.m_LastTotalSteps = state.m_StepCounter;
                state.m_StepCounter = 0;
            }
        }

        private void RunFluidPhase(ref WaterPipeFlowJob.State state, int budget)
        {
            var indices = Original.m_NodeIndices.Value;
            var labels = new WaterLabelHeap(256);
            var pushes = new NativeMinHeap<PushHeapData>(2048, Allocator.Temp);
            var progress = Original.m_FluidFlowState.Value;
            if (progress.m_CurrentVersion == 0)
                progress = default;
            int steps = state.m_StepCounter;
            var nodes = (Node*)Original.m_Nodes.GetUnsafePtr();
            var edges = (Edge*)Original.m_Edges.GetUnsafePtr();
            var connections = (Connection*)Original.m_Connections.GetUnsafeReadOnlyPtr();
            while (!progress.m_Complete && (Original.m_FinalFrame || steps < budget))
            {
                progress.m_CurrentVersion = unchecked(progress.m_CurrentVersion + 1);
                Label(nodes, edges, connections, indices.m_SourceNode, indices.m_SinkNode, progress.m_CurrentVersion, ref labels, ref pushes, ref steps);
                if (pushes.Length == 0)
                    progress.m_Complete = true;
                else
                {
                    Assert.AreEqual(0, labels.Length);
                    Push(nodes, edges, connections, ref pushes, ref steps);
                }
            }

            Original.m_FluidFlowState.Value = new FluidFlowSolverState
            {
                m_Complete = progress.m_Complete,
                m_CurrentVersion = progress.m_CurrentVersion
            };
            labels.Dispose();
            pushes.Dispose();
            state.m_StepCounter = steps;
            if (progress.m_Complete)
                state.m_Phase = WaterPipeFlowJob.Phase.Complete;
        }

        private static void Label(Node* nodes, Edge* edges, Connection* connections, int source, int sink, int version,
            ref WaterLabelHeap labels, ref NativeMinHeap<PushHeapData> pushes, ref int steps)
        {
            Assert.AreEqual(0, labels.Length);
            Assert.AreEqual(0, pushes.Length);
            ref Node root = ref nodes[source];
            root.m_Distance = 0;
            root.m_Height = 0;
            root.m_Version = version;
            root.m_Enqueued = true;
            labels.Insert(new LabelHeapData(source, 0));
            while (labels.Length != 0)
            {
                var item = labels.Extract();
                ref Node node = ref nodes[item.m_NodeIndex];
                if (node.m_Distance < item.m_Distance)
                    continue;
                steps = unchecked(steps + 1);
                Assert.IsTrue(node.m_Distance == item.m_Distance);
                int height = unchecked(node.m_Height + 1);
                for (int i = node.m_FirstConnection; i < node.m_LastConnection; i++)
                {
                    var connection = connections[i];
                    ref Edge edge = ref edges[connection.m_Edge];
                    if (edge.GetResidualCapacity(connection.m_Backwards) <= 0)
                        continue;
                    Assert.IsTrue(connection.m_EndNode != sink);
                    int flow = edge.GetFinalFlow(connection.m_Backwards);
                    int distance = unchecked(node.m_Distance + EdgeLength(flow));
                    ref Node destination = ref nodes[connection.m_EndNode];
                    if (destination.m_Version != version)
                    {
                        destination.m_Enqueued = false;
                        destination.m_Height = height;
                        destination.m_Version = version;
                    }
                    else
                    {
                        if (destination.m_Distance <= distance)
                            continue;
                        destination.m_Enqueued &= destination.m_Height == height;
                        destination.m_Height = height;
                    }
                    destination.m_Distance = distance;
                    destination.m_Predecessor = i;
                    labels.Insert(new LabelHeapData(connection.m_EndNode, distance));
                }

                if (!node.m_Enqueued && node.m_Excess > 0)
                {
                    node.m_Enqueued = true;
                    pushes.Insert(new PushHeapData(item.m_NodeIndex, node.m_Height));
                }
            }
        }

        private static void Push(Node* nodes, Edge* edges, Connection* connections,
            ref NativeMinHeap<PushHeapData> pushes, ref int steps)
        {
            Assert.AreNotEqual(0, pushes.Length);
            while (pushes.Length != 0)
            {
                var item = pushes.Extract();
                ref Node node = ref nodes[item.m_NodeIndex];
                if (!node.m_Enqueued || item.m_Height != node.m_Height)
                    continue;
                steps = unchecked(steps + 1);
                Assert.IsTrue(node.m_Excess > 0);
                Assert.AreNotEqual(0, node.m_Predecessor);
                node.m_Enqueued = false;
                var connection = connections[node.m_Predecessor];
                ref Edge edge = ref edges[connection.m_Edge];
                int flow = edge.GetFinalFlow(connection.m_Backwards);
                int residual = edge.GetResidualCapacity(connection.m_Backwards);
                Assert.IsTrue(residual > 0);
                Assert.IsTrue(connection.m_EndNode == item.m_NodeIndex);
                int amount = math.min(node.m_Excess, math.min(unchecked(NextFlow(flow) - flow), residual));
                Assert.IsTrue(amount > 0);
                ref Node start = ref nodes[connection.m_StartNode];
                if (!start.m_Enqueued)
                {
                    start.m_Enqueued = true;
                    pushes.Insert(new PushHeapData(connection.m_StartNode, start.m_Height));
                }

                Augment(nodes, ref edge, connection, amount);
            }
        }

        // The connection is authoritative, even for inconsistent predecessor data.
        // Do not update cached copies of either endpoint, which can alias.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Augment(Node* nodes, ref Edge edge, Connection connection, int amount)
        {
            Assert.IsTrue(amount >= 0);
            nodes[connection.m_StartNode].m_Excess = unchecked(nodes[connection.m_StartNode].m_Excess + amount);
            edge.m_FinalFlow = unchecked(edge.m_FinalFlow + (connection.m_Backwards ? -amount : amount));
            nodes[connection.m_EndNode].m_Excess = unchecked(nodes[connection.m_EndNode].m_Excess - amount);
            Assert.IsFalse(edge.m_FinalFlow < -edge.GetCapacity(true));
            Assert.IsFalse(edge.m_FinalFlow > edge.GetCapacity(false));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int EdgeLength(int flow)
        {
            if (flow > 1) return 1 + math.ceillog2(flow);
            if (flow < -1) return 1 - math.ceillog2(unchecked(-flow));
            return 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int NextFlow(int flow)
        {
            if (flow > 1) return Mathf.NextPowerOfTwo(flow) << 1;
            if (flow < -2) return -(Mathf.NextPowerOfTwo(unchecked(-flow)) >> 2) - 1;
            return flow == -2 ? 1 : 2;
        }
    }

    internal unsafe struct WaterLabelHeap
    {
        private UnsafeList<LabelHeapData> entries;
        internal WaterLabelHeap(int capacity) => entries = new UnsafeList<LabelHeapData>(capacity, Allocator.Temp);
        internal int Length => entries.Length;
        internal void Dispose() => entries.Dispose();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Insert(LabelHeapData item)
        {
            int index = entries.Length;
            entries.Add(item);
            var data = entries.Ptr;
            while (index > 0)
            {
                int parent = (index - 1) >> 2;
                if (item.m_Distance >= data[parent].m_Distance) break;
                data[index] = data[parent];
                index = parent;
            }
            data[index] = item;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal LabelHeapData Extract()
        {
            var data = entries.Ptr;
            var result = data[0];
            int count = entries.Length - 1;
            var tail = data[count];
            entries.Length = count;
            int hole = 0;
            while (hole <= ((count - 2) >> 2))
            {
                int first = 4 * hole + 1;
                // Four-way heap with leftmost-child preference on equal distances.
                int b = math.min(first + 1, count - 1);
                int c = math.min(first + 2, count - 1);
                int d = math.min(first + 3, count - 1);
                int left = data[b].m_Distance < data[first].m_Distance ? b : first;
                int right = data[d].m_Distance < data[c].m_Distance ? d : c;
                int best = data[right].m_Distance < data[left].m_Distance ? right : left;
                if (data[best].m_Distance >= tail.m_Distance) break;
                data[hole] = data[best];
                hole = best;
            }
            data[hole] = tail;
            return result;
        }
    }
}
