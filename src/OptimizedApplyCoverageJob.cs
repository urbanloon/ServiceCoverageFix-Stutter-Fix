using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace ServiceCoverageFix
{
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
    internal struct RawEntity
    {
        internal int Index;
        internal int Version;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 24)]
    internal struct RawBuildingData
    {
        internal RawEntity Entity;
        internal int ElementIndex;
        internal int ElementCount;
        internal float Total;
        internal float Remaining;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal unsafe struct RawCoverageElement
    {
        [FieldOffset(0)]
        [NativeDisableUnsafePtrRestriction]
        internal void* CoveragePtr;

        [FieldOffset(8)] internal float2 Coverage;
        [FieldOffset(16)] internal float AverageCoverage;
        [FieldOffset(20)] internal float DensityFactor;
        [FieldOffset(24)] internal float LengthFactor;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    internal struct ProviderState
    {
        internal int NextIndex;
        internal int EndIndex;
        internal float Total;
        internal float Remaining;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
    internal struct RadixNode
    {
        internal int Next;
        internal uint Priority;
    }

    // Keep every queue node and provider state inside its own original 24-byte
    // BuildingData record. This avoids the discarded cross-record compaction
    // prototype while requiring no allocator-backed or heap storage and no
    // provider-sized worker-stack array. The Entity field is disposable at this
    // stage, so its first eight bytes become the queue node; the remaining
    // sixteen bytes already have the exact shape needed by ProviderState after
    // ElementCount is converted to an absolute end index.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct ProviderSlot
    {
        [FieldOffset(0)] internal RadixNode Node;
        [FieldOffset(8)] internal ProviderState State;
    }

    [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
    internal unsafe struct OptimizedApplyCoverageJob : IJob
    {
        private const int ByteRadixBucketCount = 1025;
        private const int OccupancyWordCount = 17;
        private const int OrderedMergeLimit = 4;

        [NativeDisableContainerSafetyRestriction]
        internal NativeList<RawBuildingData> BuildingData;

        [ReadOnly]
        [NativeDisableContainerSafetyRestriction]
        internal NativeList<RawCoverageElement> Elements;

        [NativeDisableUnsafePtrRestriction]
        internal ExecutionCounters* Diagnostics;

        [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
        public void Execute()
        {
            if (Diagnostics != null)
            {
                Interlocked.Increment(ref Diagnostics->Started);
                bool managed = false;
                MarkManaged(ref managed);
                if (managed) Interlocked.Increment(ref Diagnostics->Managed);
            }
            WorkloadCounts workload = ExecuteCoverage();
            if (Diagnostics != null)
            {
                // A slot has one writer until its handle is reclaimed. Completed
                // publishes all four fields to readers with one release fence.
                Diagnostics->Workload = workload;
                Interlocked.Increment(ref Diagnostics->Completed);
            }
        }

        [BurstDiscard]
        private static void MarkManaged(ref bool managed) { managed = true; }

        private WorkloadCounts ExecuteCoverage()
        {
            FilterInactiveRecords();
            int recordCount = BuildingData.Length;
            var workload = new WorkloadCounts { Providers = recordCount, Reserved = Elements.Length };
            if (recordCount == 0)
            {
                return workload;
            }

            // Preserve the game's exact NativeSort and concrete initial tie order.
            BuildingData.Sort(new BuildingDataComparer { Elements = Elements });

            RawBuildingData* records =
                (RawBuildingData*)NativeListUnsafeUtility.GetUnsafePtr(BuildingData);
            RawCoverageElement* elements =
                (RawCoverageElement*)NativeListUnsafeUtility.GetUnsafeReadOnlyPtr(Elements);

            // Both temporary lists are disposed immediately after this job and no
            // downstream job reads these records. Reinterpret each independent
            // 24-byte record as one 8-byte queue node plus one 16-byte hot state.
            // Unlike the discarded compaction prototype, no write crosses a
            // record boundary. Queue nodes reuse the game's existing records
            // rather than allocator-backed or heap storage.
            ProviderSlot* slots = (ProviderSlot*)records;
            workload.Available = InitializeSlots(slots, recordCount);

            int skipped;
            if (recordCount == 1)
            {
                skipped = ExecuteSingleProvider(&slots[0].State, elements);
            }
            else if (recordCount <= OrderedMergeLimit)
            {
                skipped = ExecuteOrderedMerge(slots, elements, recordCount);
            }
            else
            {
                skipped = ExecuteByteRadixMerge(slots, elements, recordCount);
            }
            workload.Processed = workload.Available - skipped;
            return workload;
        }

        private static int InitializeSlots(
            ProviderSlot* slots,
            int recordCount)
        {
            int available = 0;
            for (int i = 0; i < recordCount; i++)
            {
                // State.NextIndex aliases RawBuildingData.ElementIndex and
                // State.EndIndex initially aliases ElementCount. Only the latter
                // needs conversion. Node bytes are initialized when enqueued.
                ProviderState* state = &slots[i].State;
                available += state->EndIndex;
                state->EndIndex += state->NextIndex;
            }
            return available;
        }

        private static int ExecuteSingleProvider(
            [NoAlias] ProviderState* state,
            [NoAlias] RawCoverageElement* elements)
        {
            while (state->NextIndex != state->EndIndex)
            {
                int elementIndex = state->NextIndex++;
                if (!ApplyElement(state, elements + elementIndex))
                {
                    break;
                }
            }
            return state->EndIndex - state->NextIndex;
        }

        private static int ExecuteOrderedMerge(
            [NoAlias] ProviderSlot* slots,
            [NoAlias] RawCoverageElement* elements,
            int recordCount)
        {
            // Follow the game's adjacent reinsertion comparisons for small
            // queues and for unordered initial heads. Links preserve the exact
            // order of the sorted records even when NaN separates two runs.
            for (int i = 0; i < recordCount - 1; i++)
            {
                slots[i].Node.Next = i + 1;
            }
            slots[recordCount - 1].Node.Next = -1;

            int skipped = 0;
            int winner = 0;
            while (true)
            {
                int runnerUp = slots[winner].Node.Next;
                ProviderState* state = &slots[winner].State;
                if (runnerUp == -1)
                {
                    return skipped + ExecuteSingleProvider(state, elements);
                }

                float runnerKey = elements[slots[runnerUp].State.NextIndex].AverageCoverage;
                while (true)
                {
                    int index = state->NextIndex++;
                    if (!ApplyElement(state, elements + index) || state->NextIndex == state->EndIndex)
                    {
                        skipped += state->EndIndex - state->NextIndex;
                        winner = runnerUp;
                        break;
                    }

                    float key = elements[state->NextIndex].AverageCoverage;
                    if (!(key < runnerKey))
                    {
                        continue;
                    }

                    int previous = runnerUp;
                    int next = slots[previous].Node.Next;
                    while (next != -1 && key < elements[slots[next].State.NextIndex].AverageCoverage)
                    {
                        previous = next;
                        next = slots[next].Node.Next;
                    }
                    slots[previous].Node.Next = winner;
                    slots[winner].Node.Next = next;
                    winner = runnerUp;
                    break;
                }
            }
        }

        [SkipLocalsInit]
        private static int ExecuteByteRadixMerge(
            [NoAlias] ProviderSlot* slots,
            [NoAlias] RawCoverageElement* elements,
            int recordCount)
        {
            // Fixed scratch storage, independent of provider count:
            // 3 * 1,025 * 4 bytes + 17 * 8 bytes = 12,436 bytes.
            int* heads = stackalloc int[ByteRadixBucketCount];
            int* tails = stackalloc int[ByteRadixBucketCount];
            uint* minimums = stackalloc uint[ByteRadixBucketCount];
            ulong* occupancy = stackalloc ulong[OccupancyWordCount];

            // Tails and minima are assigned when an empty bucket becomes live.
            for (int i = 0; i < ByteRadixBucketCount; i++)
            {
                heads[i] = -1;
            }

            for (int i = 0; i < OccupancyWordCount; i++)
            {
                occupancy[i] = 0;
            }

            uint occupiedWordMask = 0;
            uint lastPriority = 0;
            // The first sorted provider is already the winner. NaN can split
            // the initial list into ordered runs; until all heads are known to
            // be ordered, no provider state or coverage target is advanced.
            if (math.isnan(elements[slots[0].State.NextIndex].AverageCoverage))
            {
                return ExecuteOrderedMerge(slots, elements, recordCount);
            }
            // Reverse prepend retains the sorted order among the other providers.
            for (int i = recordCount - 1; i >= 1; i--)
            {
                float first = elements[slots[i].State.NextIndex].AverageCoverage;
                if (math.isnan(first))
                {
                    return ExecuteOrderedMerge(slots, elements, recordCount);
                }
                uint priority = GetPriority(first);
                BytePrepend(
                    slots, heads, tails, minimums, occupancy,
                    ref occupiedWordMask, i,
                    GetByteBucket(priority, lastPriority), priority);
            }

            int winner = 0;
            int skipped = 0;
            while (occupiedWordMask != 0)
            {
                // The strongest competitor stays fixed until this provider
                // loses or exhausts its budget. An equal or unordered next
                // key keeps the winner, matching vanilla's ordered '<' test.
                int runnerUp = ByteTakeWinner(slots, heads, tails, minimums, occupancy,
                    ref occupiedWordMask, ref lastPriority);
                float cutoff = elements[slots[runnerUp].State.NextIndex].AverageCoverage;
                ProviderState* state = &slots[winner].State;
                ProviderState local = *state;
                int cursor = local.NextIndex;
                int end = local.EndIndex;
                while (true)
                {
                    int index = cursor++;
                    bool hasNext = cursor != end;
                    float next = hasNext ? elements[cursor].AverageCoverage : 0f;
                    bool live = ApplyElement(&local, elements + index);
                    if (!live || !hasNext)
                    {
                        skipped += end - cursor;
                        break;
                    }
                    if (!(next < cutoff))
                    {
                        continue;
                    }

                    uint priority = GetPriority(next);
                    BytePrepend(slots, heads, tails, minimums, occupancy,
                        ref occupiedWordMask, winner, GetByteBucket(priority, lastPriority), priority);
                    break;
                }
                state->NextIndex = cursor;
                state->Remaining = local.Remaining;
                winner = runnerUp;
            }
            return skipped + ExecuteSingleProvider(&slots[winner].State, elements);
        }

        private static int ByteTakeWinner(
            ProviderSlot* slots,
            int* heads,
            int* tails,
            uint* minimums,
            ulong* occupancy,
            ref uint occupiedWordMask,
            ref uint lastPriority)
        {
            int current = heads[0];
            if (current != -1)
            {
                heads[0] = slots[current].Node.Next;
                if (heads[0] == -1)
                {
                    ClearByteBucket(heads, occupancy, ref occupiedWordMask, 0);
                }
                return current;
            }

            int sourceBucket = GetLowestOccupiedBucket(
                occupancy, occupiedWordMask);
            lastPriority = minimums[sourceBucket];
            current = heads[sourceBucket];
            ClearByteBucket(
                heads, occupancy, ref occupiedWordMask, sourceBucket);

            // A singleton can be returned without entering bucket zero.
            if (current == tails[sourceBucket])
            {
                return current;
            }

            // All four bytes of a lowest-octet bucket's priority are fixed.
            // Its existing linked list already has the sorted tie order.
            if (sourceBucket <= 256)
            {
                heads[0] = slots[current].Node.Next;
                tails[0] = tails[sourceBucket];
                minimums[0] = lastPriority;
                SetByteBucketOccupied(occupancy, ref occupiedWordMask, 0);
                return current;
            }

            // Append preserves concrete order; node.Priority is already cached.
            do
            {
                int next = slots[current].Node.Next;
                uint priority = slots[current].Node.Priority;
                ByteAppend(
                    slots, heads, tails, minimums, occupancy,
                    ref occupiedWordMask, current,
                    GetByteBucket(priority, lastPriority), priority);
                current = next;
            }
            while (current != -1);

            current = heads[0];
            heads[0] = slots[current].Node.Next;
            if (heads[0] == -1)
            {
                ClearByteBucket(heads, occupancy, ref occupiedWordMask, 0);
            }
            return current;
        }

        private static void BytePrepend(
            ProviderSlot* slots,
            int* heads,
            int* tails,
            uint* minimums,
            ulong* occupancy,
            ref uint occupiedWordMask,
            int providerIndex,
            int bucket,
            uint priority)
        {
            int oldHead = heads[bucket];
            slots[providerIndex].Node.Next = oldHead;
            slots[providerIndex].Node.Priority = priority;
            heads[bucket] = providerIndex;

            if (oldHead == -1)
            {
                tails[bucket] = providerIndex;
                minimums[bucket] = priority;
                SetByteBucketOccupied(
                    occupancy, ref occupiedWordMask, bucket);
            }
            else
            {
                minimums[bucket] = math.min(minimums[bucket], priority);
            }
        }

        private static void ByteAppend(
            ProviderSlot* slots,
            int* heads,
            int* tails,
            uint* minimums,
            ulong* occupancy,
            ref uint occupiedWordMask,
            int providerIndex,
            int bucket,
            uint priority)
        {
            slots[providerIndex].Node.Next = -1;
            if (heads[bucket] == -1)
            {
                heads[bucket] = providerIndex;
                tails[bucket] = providerIndex;
                minimums[bucket] = priority;
                SetByteBucketOccupied(
                    occupancy, ref occupiedWordMask, bucket);
            }
            else
            {
                slots[tails[bucket]].Node.Next = providerIndex;
                tails[bucket] = providerIndex;
                minimums[bucket] = math.min(minimums[bucket], priority);
            }
        }

        private static int GetByteBucket(uint priority, uint lastPriority)
        {
            uint difference = priority ^ lastPriority;
            if (difference == 0)
            {
                return 0;
            }

            int shift = (31 - math.lzcnt(difference)) & ~7;
            return 1 + (shift << 5) +
                (int)((priority >> shift) & 0xFFu);
        }

        private static int GetLowestOccupiedBucket(
            ulong* occupancy,
            uint occupiedWordMask)
        {
            int sourceWord = math.tzcnt(occupiedWordMask);
            int bit = math.tzcnt(occupancy[sourceWord]);
            return (sourceWord << 6) + bit;
        }

        private static void SetByteBucketOccupied(
            ulong* occupancy,
            ref uint occupiedWordMask,
            int bucket)
        {
            int word = bucket >> 6;
            occupancy[word] |= 1UL << (bucket & 63);
            occupiedWordMask |= 1u << word;
        }

        private static void ClearByteBucket(
            int* heads,
            ulong* occupancy,
            ref uint occupiedWordMask,
            int bucket)
        {
            heads[bucket] = -1;
            int word = bucket >> 6;
            occupancy[word] &= ~(1UL << (bucket & 63));
            if (occupancy[word] == 0)
            {
                occupiedWordMask &= ~(1u << word);
            }
        }

        private void FilterInactiveRecords()
        {
            int index = 0;
            while (index < BuildingData.Length)
            {
                RawBuildingData data = BuildingData[index];
                // Vanilla uses the ordered <= test. NaN therefore remains live.
                if (data.ElementCount == 0 || data.Remaining <= 0f)
                {
                    BuildingData.RemoveAtSwapBack(index);
                }
                else
                {
                    index++;
                }
            }
        }

        // The common occluded-element exit cannot mutate Remaining, which was
        // positive when the provider entered the queue. Only the arithmetic path
        // performs another budget comparison.
        private static bool ApplyElement(
            [NoAlias] ProviderState* building,
            [NoAlias] RawCoverageElement* element)
        {
            // ProcessCoverageJob gives every element a valid CoveragePtr.
            // Localize all temporary-list/provider inputs before following that
            // opaque pointer. The target can alias other CoveragePtr targets,
            // but it cannot alias either temporary allocation; explicit locals
            // prevent a conservative reload after the observable pointer store.
            void* coveragePtr = element->CoveragePtr;
            float2 coverage = element->Coverage;
            float densityFactor = element->DensityFactor;
            float lengthFactor = element->LengthFactor;
            float total = building->Total;
            float remaining = building->Remaining;

            float2 currentCoverage = *(float2*)coveragePtr;
            if (!math.any(coverage > currentCoverage))
            {
                return true;
            }

            // Preserve the game's strict floating-point operation order.
            float factor = 0.99f * (1f - remaining / total);
            factor *= factor;
            factor *= factor;
            factor *= factor;
            factor = 1f - factor;

            float2 targetCoverage = coverage * factor;
            float2 delta = math.clamp(
                targetCoverage - currentCoverage,
                0f,
                targetCoverage * densityFactor);

            *(float2*)coveragePtr = currentCoverage + delta;

            float2 ratio = math.saturate(delta / coverage);
            remaining -=
                math.lerp(ratio.x, ratio.y, 0.5f) *
                lengthFactor *
                densityFactor;
            building->Remaining = remaining;
            // Match vanilla's bgt.un liveness relation: NaN remains active.
            return !(remaining <= 0f);
        }

        private static uint GetPriority(float coverage)
        {
            uint bits = math.asuint(coverage);
            // In denormals-are-zero mode, vanilla's float comparer treats both
            // signs of subnormals as zero. Ask the same comparison only for a
            // zero exponent; otherwise preserve the normal/infinity bit order.
            if ((bits & 0x7F800000u) == 0 && coverage == 0f)
                return 0x7FFFFFFFu;
            if ((bits & 0x80000000u) == 0)
            {
                return bits ^ 0x7FFFFFFFu;
            }

            return bits;
        }

        private struct BuildingDataComparer : IComparer<RawBuildingData>
        {
            [ReadOnly]
            [NativeDisableContainerSafetyRestriction]
            internal NativeList<RawCoverageElement> Elements;

            public int Compare(RawBuildingData left, RawBuildingData right)
            {
                float leftCoverage = Elements[left.ElementIndex].AverageCoverage;
                float rightCoverage = Elements[right.ElementIndex].AverageCoverage;
                return leftCoverage == rightCoverage
                    ? 0
                    : (leftCoverage < rightCoverage ? 1 : -1);
            }
        }
    }
}

