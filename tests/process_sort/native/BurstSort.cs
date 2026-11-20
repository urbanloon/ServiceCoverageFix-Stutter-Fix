using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

[BurstCompile]
public static unsafe class BurstSort
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Element : IComparable<Element>
    {
        public long Id;
        public float X, Y, Priority, Density, Length;
        public int Padding;
        public int CompareTo(Element other) =>
            Priority == other.Priority ? 0 : Priority < other.Priority ? 1 : -1;
    }

    public struct Descriptor : IComparable<Descriptor>
    {
        public float Priority;
        public int Source;
        public int CompareTo(Descriptor other) =>
            Priority == other.Priority ? 0 : Priority < other.Priority ? 1 : -1;
    }

    // The offline binder changes only the generic argument of this Sort<T> call
    // to the actual private Game.Simulation.ServiceCoverageSystem+CoverageElement.
    static void GameSort(Element* data, int count) => NativeSortExtension.Sort(data, count);

    struct RadixKey
    {
        public uint Order;
        public int Source;
    }

    static bool UniqueRadix(Element* first, int n, RadixKey* keys, Element* rowScratch)
    {
        int* counts = stackalloc int[4 * 256];
        uint* seen = stackalloc uint[256];
        UnsafeUtility.MemClear(counts, sizeof(int) * 4 * 256);
        // 0xffffffff is impossible here: NaNs fall back and zeros normalize.
        UnsafeUtility.MemSet(seen, 255, sizeof(uint) * 256);
        for (int i = 0; i < n; i++)
        {
            float priority = first[i].Priority;
            if (math.isnan(priority)) return false;
            uint bits = priority == 0 ? 0u : math.asuint(priority);
            uint order = (bits & 0x80000000u) != 0 ? bits : ~bits ^ 0x80000000u;
            uint hash = ((order ^ (order >> 16)) * 2654435761u) >> 24;
            if (seen[hash] == order) return false;
            seen[hash] = order;
            keys[i] = new RadixKey { Order = order, Source = i };
            counts[order & 255]++;
            counts[256 + ((order >> 8) & 255)]++;
            counts[512 + ((order >> 16) & 255)]++;
            counts[768 + (order >> 24)]++;
        }
        RadixKey* input = keys;
        RadixKey* output = (RadixKey*)rowScratch;
        for (int pass = 0; pass < 4; pass++)
        {
            int* offsets = counts + 256 * pass;
            int sum = 0;
            for (int bin = 0; bin < 256; bin++)
            {
                int size = offsets[bin];
                offsets[bin] = sum;
                sum += size;
            }
            int shift = 8 * pass;
            for (int i = 0; i < n; i++)
            {
                RadixKey item = input[i];
                output[offsets[(item.Order >> shift) & 255]++] = item;
            }
            RadixKey* swap = input;
            input = output;
            output = swap;
        }
        // The slot cache only catches some duplicates. This pass catches every
        // remaining duplicate before the original element array is modified.
        for (int i = 1; i < n; i++)
            if (keys[i - 1].Order == keys[i].Order) return false;
        for (int i = 0; i < n; i++) rowScratch[i] = first[keys[i].Source];
        UnsafeUtility.MemCpy(first, rowScratch, (long)n * sizeof(Element));
        return true;
    }

    [BurstCompile(FloatMode = FloatMode.Strict)]
    public static void SortPass(IntPtr data, IntPtr offsets, int providers,
        IntPtr keyScratch, IntPtr rowScratch, int method)
    {
        var rows = (Element*)data;
        var starts = (int*)offsets;
        var keys = (Descriptor*)keyScratch;
        var ordered = (Element*)rowScratch;
        for (int p = 0; p < providers; ++p)
        {
            Element* first = rows + starts[p];
            int count = starts[p + 1] - starts[p];
            if (count < 2) continue;
            if (method == 0) { GameSort(first, count); continue; }
            if (method == 1)
            {
                for (int i = 1; i < count; i++)
                    if (!(first[i-1].Priority > first[i].Priority))
                    {
                        GameSort(first, count);
                        break;
                    }
                continue;
            }
            if (method == 4)
            {
                if (!UniqueRadix(first, count, (RadixKey*)keys, ordered))
                    GameSort(first, count);
                continue;
            }
            for (int i = 0; i < count; i++)
                keys[i] = new Descriptor { Priority = first[i].Priority, Source = i };
            NativeSortExtension.Sort(keys, count);
            if (method == 2)
            {
                for (int i = 0; i < count; i++) ordered[i] = first[keys[i].Source];
                UnsafeUtility.MemCpy(first, ordered, (long)count * sizeof(Element));
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    if (keys[i].Source == i) continue;
                    Element original = first[i];
                    int dest = i;
                    int source;
                    while ((source = keys[dest].Source) != i)
                    {
                        first[dest] = first[source];
                        keys[dest].Source = dest;
                        dest = source;
                    }
                    first[dest] = original;
                    keys[dest].Source = dest;
                }
            }
        }
    }
}
