using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
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

    [BurstCompile]
    internal unsafe struct OptimizedApplyCoverageJob : IJob
    {
        [NativeDisableContainerSafetyRestriction]
        internal NativeList<RawBuildingData> BuildingData;

        [ReadOnly]
        [NativeDisableContainerSafetyRestriction]
        internal NativeList<RawCoverageElement> Elements;

        [BurstCompile]
        public void Execute()
        {
            FilterInactiveRecords();
            if (BuildingData.Length == 0)
            {
                return;
            }

            // This is the same NativeSort operation used by the original job. It
            // preserves the game's comparison semantics and concrete initial tie
            // order for both optimized queue implementations.
            BuildingData.Sort(new BuildingDataComparer { Elements = Elements });

            RawBuildingData* records = (RawBuildingData*)NativeListUnsafeUtility.GetUnsafePtr(BuildingData);
            RawCoverageElement* elements =
                (RawCoverageElement*)NativeListUnsafeUtility.GetUnsafeReadOnlyPtr(Elements);

            // ProcessCoverageJob NativeSorts every provider's element range in
            // descending AverageCoverage order. A radix queue can therefore merge
            // the ranges without the log(B) comparisons of the first fix. Validate
            // that invariant before performing any coverage writes; unexpected or
            // NaN data takes the exact binary-heap fallback.
            if (CanUseMonotoneRadix(
                records,
                elements,
                BuildingData.Length,
                Elements.Length))
            {
                ExecuteRadixMerge(records, elements, BuildingData.Length);
            }
            else
            {
                ExecuteBinaryHeap(records, elements, BuildingData.Length);
            }
        }

        private static bool CanUseMonotoneRadix(
            RawBuildingData* records,
            RawCoverageElement* elements,
            int recordCount,
            int elementCount)
        {
            for (int i = 0; i < recordCount; i++)
            {
                int start = records[i].ElementIndex;
                int count = records[i].ElementCount;
                if (start < 0 || count <= 0 || start > elementCount - count)
                {
                    return false;
                }

                float previous = elements[start].AverageCoverage;
                if (math.isnan(previous))
                {
                    return false;
                }

                for (int j = 1; j < count; j++)
                {
                    float current = elements[start + j].AverageCoverage;
                    if (math.isnan(current) || previous < current)
                    {
                        return false;
                    }

                    previous = current;
                }
            }

            return true;
        }

        private static void ExecuteRadixMerge(
            RawBuildingData* records,
            RawCoverageElement* elements,
            int recordCount)
        {
            int* heads = stackalloc int[33];
            int* tails = stackalloc int[33];
            for (int i = 0; i < 33; i++)
            {
                heads[i] = -1;
                tails[i] = -1;
            }

            // Descending float priority is converted into an ascending integer
            // priority. Every provider range is monotone, satisfying the radix
            // queue invariant. Reverse insertion preserves the concrete tie order
            // produced by the game's NativeSort.
            uint lastPriority = 0;
            for (int i = recordCount - 1; i >= 0; i--)
            {
                uint priority = GetPriority(elements[records[i].ElementIndex].AverageCoverage);
                records[i].Entity.Version = (int)priority;
                Prepend(records, heads, tails, i, GetBucket(priority, lastPriority));
            }

            int activeCount = recordCount;
            while (activeCount > 0)
            {
                if (heads[0] == -1)
                {
                    RefillBucketZero(records, heads, tails, ref lastPriority);
                }

                int recordIndex = heads[0];
                heads[0] = records[recordIndex].Entity.Index;
                if (heads[0] == -1)
                {
                    tails[0] = -1;
                }

                RawBuildingData current = records[recordIndex];
                int elementIndex = current.ElementIndex;
                current.ElementIndex = elementIndex + 1;
                RawCoverageElement element = elements[elementIndex];
                ApplyElement(ref current, in element);

                current.ElementCount--;
                if (current.ElementCount != 0 && current.Remaining > 0f)
                {
                    uint priority = GetPriority(elements[current.ElementIndex].AverageCoverage);
                    current.Entity.Version = (int)priority;
                    records[recordIndex] = current;
                    Prepend(
                        records,
                        heads,
                        tails,
                        recordIndex,
                        GetBucket(priority, lastPriority));
                }
                else
                {
                    records[recordIndex] = current;
                    activeCount--;
                }
            }
        }

        private static void RefillBucketZero(
            RawBuildingData* records,
            int* heads,
            int* tails,
            ref uint lastPriority)
        {
            int sourceBucket = 1;
            while (heads[sourceBucket] == -1)
            {
                sourceBucket++;
            }

            uint nextPriority = uint.MaxValue;
            for (int recordIndex = heads[sourceBucket];
                 recordIndex != -1;
                 recordIndex = records[recordIndex].Entity.Index)
            {
                uint priority = (uint)records[recordIndex].Entity.Version;
                nextPriority = math.min(nextPriority, priority);
            }

            lastPriority = nextPriority;
            int current = heads[sourceBucket];
            heads[sourceBucket] = -1;
            tails[sourceBucket] = -1;

            // Append while redistributing so equal priorities retain newest-first
            // insertion order. Reinsertion itself prepends, exactly matching the
            // original "insert before existing equals" loop.
            while (current != -1)
            {
                int next = records[current].Entity.Index;
                uint priority = (uint)records[current].Entity.Version;
                Append(
                    records,
                    heads,
                    tails,
                    current,
                    GetBucket(priority, lastPriority));
                current = next;
            }
        }

        private static void Prepend(
            RawBuildingData* records,
            int* heads,
            int* tails,
            int recordIndex,
            int bucket)
        {
            records[recordIndex].Entity.Index = heads[bucket];
            heads[bucket] = recordIndex;
            if (tails[bucket] == -1)
            {
                tails[bucket] = recordIndex;
            }
        }

        private static void Append(
            RawBuildingData* records,
            int* heads,
            int* tails,
            int recordIndex,
            int bucket)
        {
            records[recordIndex].Entity.Index = -1;
            if (tails[bucket] == -1)
            {
                heads[bucket] = recordIndex;
            }
            else
            {
                records[tails[bucket]].Entity.Index = recordIndex;
            }

            tails[bucket] = recordIndex;
        }

        private static int GetBucket(uint priority, uint lastPriority)
        {
            uint difference = priority ^ lastPriority;
            return difference == 0 ? 0 : 32 - math.lzcnt(difference);
        }

        private static uint GetPriority(float coverage)
        {
            // CompareTo considers -0 and +0 equal, so normalize both forms.
            if (coverage == 0f)
            {
                coverage = 0f;
            }

            uint bits = math.asuint(coverage);
            uint ascending = (bits & 0x80000000u) != 0
                ? ~bits
                : bits ^ 0x80000000u;
            return ~ascending;
        }

        private static void ExecuteBinaryHeap(
            RawBuildingData* heap,
            RawCoverageElement* elements,
            int recordCount)
        {
            for (int i = 0; i < recordCount; i++)
            {
                // Entity is never read by ApplyCoverageJob and this TempJob list is
                // disposed immediately after the returned JobHandle. Reuse Index as
                // the heap's tie epoch so no additional allocation is required.
                heap[i].Entity.Index = recordCount - i;
            }

            int activeCount = recordCount;
            int nextTieRank = activeCount;
            while (activeCount > 0)
            {
                RawBuildingData current = heap[0];
                int elementIndex = current.ElementIndex;

                current.ElementIndex = elementIndex + 1;
                RawCoverageElement element = elements[elementIndex];
                ApplyElement(ref current, in element);

                current.ElementCount--;
                if (current.ElementCount != 0 && current.Remaining > 0f)
                {
                    current.Entity.Index = ++nextTieRank;
                    heap[0] = current;
                    SiftRootDown(heap, elements, activeCount);
                }
                else
                {
                    RemoveHeapRoot(heap, elements, ref activeCount);
                }
            }
        }

        private void FilterInactiveRecords()
        {
            int index = 0;
            while (index < BuildingData.Length)
            {
                RawBuildingData data = BuildingData[index];
                if (data.ElementCount == 0 || !(data.Remaining > 0f))
                {
                    BuildingData.RemoveAtSwapBack(index);
                }
                else
                {
                    index++;
                }
            }
        }

        private static void ApplyElement(
            ref RawBuildingData building,
            in RawCoverageElement element)
        {
            if (element.CoveragePtr == null)
            {
                return;
            }

            float2 currentCoverage = *(float2*)element.CoveragePtr;
            if (!math.any(element.Coverage > currentCoverage))
            {
                return;
            }

            float factor = 0.99f * (1f - building.Remaining / building.Total);
            factor *= factor;
            factor *= factor;
            factor *= factor;
            factor = 1f - factor;

            float2 targetCoverage = element.Coverage * factor;
            float2 delta = math.clamp(
                targetCoverage - currentCoverage,
                0f,
                targetCoverage * element.DensityFactor);

            *(float2*)element.CoveragePtr = currentCoverage + delta;

            float2 ratio = math.saturate(delta / element.Coverage);
            building.Remaining -=
                math.lerp(ratio.x, ratio.y, 0.5f) *
                element.LengthFactor *
                element.DensityFactor;
        }

        private static void RemoveHeapRoot(
            RawBuildingData* heap,
            RawCoverageElement* elements,
            ref int activeCount)
        {
            activeCount--;
            if (activeCount == 0)
            {
                return;
            }

            heap[0] = heap[activeCount];
            SiftRootDown(heap, elements, activeCount);
        }

        private static void SiftRootDown(
            RawBuildingData* heap,
            RawCoverageElement* elements,
            int activeCount)
        {
            int parent = 0;
            RawBuildingData value = heap[parent];

            while (true)
            {
                int left = parent * 2 + 1;
                if (left >= activeCount)
                {
                    break;
                }

                int right = left + 1;
                int child = left;
                if (right < activeCount)
                {
                    RawBuildingData rightValue = heap[right];
                    RawBuildingData leftValue = heap[left];
                    if (IsHigherPriority(elements, in rightValue, in leftValue))
                    {
                        child = right;
                    }
                }

                RawBuildingData childValue = heap[child];
                if (!IsHigherPriority(elements, in childValue, in value))
                {
                    break;
                }

                heap[parent] = childValue;
                parent = child;
            }

            heap[parent] = value;
        }

        private static bool IsHigherPriority(
            RawCoverageElement* elements,
            in RawBuildingData left,
            in RawBuildingData right)
        {
            float leftCoverage = elements[left.ElementIndex].AverageCoverage;
            float rightCoverage = elements[right.ElementIndex].AverageCoverage;

            // CoverageElement.CompareTo sorts larger finite values first. On a
            // tie the newest reinsertion wins, reproducing the
            // original loop's "insert before existing equals" behavior.
            if (leftCoverage == rightCoverage)
            {
                return left.Entity.Index > right.Entity.Index;
            }

            // This is the exact non-equal relation used by the game's
            // CoverageElement.CompareTo: left sorts first unless left < right.
            return !(leftCoverage < rightCoverage);
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

                // Exact managed CoverageElement.CompareTo implementation.
                return leftCoverage == rightCoverage
                    ? 0
                    : (leftCoverage < rightCoverage ? 1 : -1);
            }
        }
    }
}
