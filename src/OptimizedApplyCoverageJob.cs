using System.Collections.Generic;
using System.Runtime.InteropServices;
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
        private const int WinnerProbeWindow = 4096;
        private const int WinnerProbePercent = 5;

        [NativeDisableContainerSafetyRestriction]
        internal NativeList<RawBuildingData> BuildingData;

        [ReadOnly]
        [NativeDisableContainerSafetyRestriction]
        internal NativeList<RawCoverageElement> Elements;

        [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
        public void Execute()
        {
            FilterInactiveRecords();
            int recordCount = BuildingData.Length;
            if (recordCount == 0)
            {
                return;
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
            InitializeSlots(slots, recordCount);

            if (recordCount == 1)
            {
                ExecuteSingleProvider(&slots[0].State, elements);
                return;
            }

            ExecuteByteRadixMerge(slots, elements, recordCount);
        }

        private static void InitializeSlots(
            ProviderSlot* slots,
            int recordCount)
        {
            for (int i = 0; i < recordCount; i++)
            {
                // State.NextIndex aliases RawBuildingData.ElementIndex and
                // State.EndIndex initially aliases ElementCount. Only the latter
                // needs conversion. Node bytes are initialized when enqueued.
                ProviderState* state = &slots[i].State;
                state->EndIndex += state->NextIndex;
            }
        }

        private static void ExecuteSingleProvider(
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
        }

        [SkipLocalsInit]
        private static void ExecuteByteRadixMerge(
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
            int winnerProbeRemaining = WinnerProbeWindow;
            int winnerProbeHits = 0;
            bool useFullWinnerProbe = true;

            // Reverse prepend retains NativeSort's concrete order, including ties.
            for (int i = recordCount - 1; i >= 0; i--)
            {
                uint priority = GetPriority(
                    elements[slots[i].State.NextIndex].AverageCoverage);
                BytePrepend(
                    slots, heads, tails, minimums, occupancy,
                    ref occupiedWordMask, i,
                    GetByteBucket(priority, lastPriority), priority);
            }

            while (occupiedWordMask != 0)
            {
                if (heads[0] == -1)
                {
                    ByteRefillZero(
                        slots, heads, tails, minimums, occupancy,
                        ref occupiedWordMask, ref lastPriority);
                }

                int providerIndex = heads[0];
                heads[0] = slots[providerIndex].Node.Next;
                if (heads[0] == -1)
                {
                    ClearByteBucket(
                        heads, occupancy, ref occupiedWordMask, 0);
                }

                ProviderState* state = &slots[providerIndex].State;
                while (true)
                {
                    int elementIndex = state->NextIndex++;
                    bool hasNextElement = state->NextIndex != state->EndIndex;

                    // Issue the next stream-key load before following the current
                    // element's random CoveragePtr. It is immutable and needed on
                    // the overwhelmingly common live-provider path, so this gives
                    // the CPU independent memory work without an unavailable
                    // experimental Burst prefetch intrinsic.
                    float nextAverageCoverage = hasNextElement
                        ? elements[state->NextIndex].AverageCoverage
                        : 0f;
                    bool hasBudget = ApplyElement(state, elements + elementIndex);

                    if (!hasNextElement || !hasBudget)
                    {
                        break;
                    }

                    // The selected provider is temporarily outside the queue. If
                    // its removal emptied the occupancy directory, no competitor
                    // remains and priorities are irrelevant for its serial tail.
                    if (occupiedWordMask == 0)
                    {
                        continue;
                    }

                    uint priority = GetPriority(nextAverageCoverage);

                    // Equality is free to prove: vanilla inserts the newly
                    // advanced provider before every existing equal provider.
                    if (priority == lastPriority)
                    {
                        continue;
                    }

                    // A broader competitor peek is exact but can cost more than
                    // it saves on extremely interleaved streams. Sample the first
                    // 4,096 non-equal advances and retain it only at a >=5% hit
                    // rate. The policy changes only whether an algebraically
                    // redundant reinsert/pop pair is executed, never selection.
                    bool remainsWinner = false;
                    if (useFullWinnerProbe)
                    {
                        remainsWinner = RemainsWinnerBeyondZero(
                            heads, minimums, occupancy,
                            occupiedWordMask, priority);

                        if (winnerProbeRemaining != 0)
                        {
                            winnerProbeRemaining--;
                            if (remainsWinner)
                            {
                                winnerProbeHits++;
                            }

                            if (winnerProbeRemaining == 0)
                            {
                                useFullWinnerProbe =
                                    winnerProbeHits * 100 >=
                                    WinnerProbePercent * WinnerProbeWindow;
                            }
                        }
                    }

                    if (remainsWinner)
                    {
                        continue;
                    }

                    BytePrepend(
                        slots, heads, tails, minimums, occupancy,
                        ref occupiedWordMask, providerIndex,
                        GetByteBucket(priority, lastPriority), priority);
                    break;
                }
            }
        }

        private static bool RemainsWinnerBeyondZero(
            int* heads,
            uint* minimums,
            ulong* occupancy,
            uint occupiedWordMask,
            uint priority)
        {
            // A live bucket zero contains a competitor at lastPriority. The
            // caller already excluded equality, so this provider cannot win.
            if (heads[0] != -1)
            {
                return false;
            }

            int sourceBucket = GetLowestOccupiedBucket(
                occupancy, occupiedWordMask);
            return priority <= minimums[sourceBucket];
        }

        private static void ByteRefillZero(
            ProviderSlot* slots,
            int* heads,
            int* tails,
            uint* minimums,
            ulong* occupancy,
            ref uint occupiedWordMask,
            ref uint lastPriority)
        {
            int sourceBucket = GetLowestOccupiedBucket(
                occupancy, occupiedWordMask);
            lastPriority = minimums[sourceBucket];
            int current = heads[sourceBucket];
            ClearByteBucket(
                heads, occupancy, ref occupiedWordMask, sourceBucket);

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
            ulong value = occupancy[sourceWord];
            uint low = (uint)value;
            int bit = low != 0
                ? math.tzcnt(low)
                : 32 + math.tzcnt((uint)(value >> 32));
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
            if ((bits & 0x80000000u) == 0)
            {
                return bits ^ 0x7FFFFFFFu;
            }

            return (bits & 0x7FFFFFFFu) == 0
                ? 0x7FFFFFFFu
                : bits;
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
