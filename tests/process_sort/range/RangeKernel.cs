using System;
using System.Runtime.InteropServices;
using Colossal.Mathematics;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

[BurstCompile]
public static unsafe class RangeKernel
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Element : IComparable<Element>
    {
        public long Target;
        public float2 Coverage;
        public float Priority, Factor, Length, Padding;
        public int CompareTo(Element other) =>
            Priority == other.Priority ? 0 : Priority < other.Priority ? 1 : -1;
    }

    public struct Path
    {
        public int Edge;
        public float2 Cost;
    }

    public struct Geometry
    {
        public float Density, Length, LengthFactor;
    }

    static void PointerSort(Element* data, int count) => NativeSortExtension.Sort(data, count);

    static void GameSort(Element* data, int count) =>
        NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<Element>(data, count, Allocator.None).Sort();

    static void Finish(Element* destination, Path path, float magnitude, float efficiency, float length)
    {
        float2 coverage = math.max(0f, 1f - path.Cost * path.Cost) * magnitude * efficiency;
        *destination = new Element
        {
            Target = path.Edge + 1,
            Coverage = coverage,
            Priority = math.csum(coverage) * 0.5f,
            Factor = 1f,
            Length = length
        };
    }

    [BurstCompile(FloatMode = FloatMode.Strict)]
    public static void Execute(IntPtr destination, IntPtr paths, IntPtr geometry, IntPtr offsets,
        IntPtr magnitudes, IntPtr efficiencies, int providers, int mode, int sort)
    {
        var output = (Element*)destination;
        var input = (Path*)paths;
        var geometries = (Geometry*)geometry;
        var starts = (int*)offsets;
        var magnitude = (float*)magnitudes;
        var efficiency = (float*)efficiencies;
        if (mode == 4)
        {
            var curves = (Bezier4x3*)paths;
            for (int i = 0; i < providers; i++)
                geometries[i].Length = MathUtils.Length(curves[i]);
            return;
        }
        for (int provider = 0; provider < providers; provider++)
        {
            int begin = starts[provider];
            int end = starts[provider + 1];
            if (mode == 0)
            {
                for (int i = begin; i < end; i++)
                {
                    var path = input[i];
                    var edge = geometries[path.Edge];
                    float length = edge.Length * math.sqrt(math.max(0.01f, edge.Density));
                    Finish(output + i, path, magnitude[provider], efficiency[provider], length);
                }
            }
            else if (mode == 1)
            {
                for (int i = begin; i < end; i++)
                {
                    var path = input[i];
                    var edge = geometries[path.Edge];
                    float root = edge.Density <= 0.01f ? 0.1f : math.sqrt(math.max(0.01f, edge.Density));
                    Finish(output + i, path, magnitude[provider], efficiency[provider], edge.Length * root);
                }
            }
            else if (mode == 2)
            {
                for (int i = begin; i < end; i++)
                {
                    var path = input[i];
                    var edge = geometries[path.Edge];
                    Finish(output + i, path, magnitude[provider], efficiency[provider], edge.LengthFactor);
                }
            }
            else
            {
                // Setup uses the same compiled floating-point expression as the stock arithmetic.
                for (int i = begin; i < end; i++)
                {
                    var path = input[i];
                    var edge = geometries[path.Edge];
                    geometries[path.Edge].LengthFactor = edge.Length * math.sqrt(math.max(0.01f, edge.Density));
                }
            }
            if (end - begin > 1)
            {
                if (sort == 1) GameSort(output + begin, end - begin);
                if (sort == 2) PointerSort(output + begin, end - begin);
            }
        }
    }
}
