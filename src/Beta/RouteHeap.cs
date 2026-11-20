using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Colossal.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace ServiceCoverageFix
{
    internal static class RouteHeap
    {
        internal const int MinimumHeapSize = 4096;

        // UnsafeMinHeap has one instance field in the verified collection assembly.
        private struct LayoutProbe : ILessThan<LayoutProbe>
        {
            public bool LessThan(LayoutProbe other) => false;
        }

        internal static bool SupportsLayout()
        {
            Type heap = typeof(UnsafeMinHeap<LayoutProbe>);
            FieldInfo[] fields = heap.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return fields.Length == 1 && fields[0].Name == "m_Buffer" &&
                fields[0].FieldType == typeof(UnsafeList<LayoutProbe>) &&
                Marshal.OffsetOf(heap, "m_Buffer").ToInt64() == 0 &&
                UnsafeUtility.SizeOf<UnsafeMinHeap<LayoutProbe>>() == UnsafeUtility.SizeOf<UnsafeList<LayoutProbe>>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe T Extract<T>(ref UnsafeMinHeap<T> heap) where T : unmanaged, ILessThan<T>
        {
            if (heap.Length < MinimumHeapSize)
                return heap.Extract();

            ref UnsafeList<T> buffer = ref UnsafeUtility.As<UnsafeMinHeap<T>, UnsafeList<T>>(ref heap);
            T root = buffer.Ptr[0];
            buffer.RemoveAtSwapBack(0);
            T* data = buffer.Ptr;
            T tail = data[0];
            int count = buffer.Length;
            int parent = 0;
            // Compare pairs (0,1), (2,3), then winners. This also preserves stock NaN and tie behavior.
            while (parent <= (count - 2) / 4)
            {
                int a = 4 * parent + 1;
                int b, c, d;
                if (a <= count - 4)
                {
                    b = a + 1;
                    c = a + 2;
                    d = a + 3;
                }
                else
                {
                    int remaining = count - 1 - a;
                    b = a + math.min(1, remaining);
                    c = a + math.min(2, remaining);
                    d = count - 1;
                }
                int left = math.select(a, b, data[b].LessThan(data[a]));
                int right = math.select(c, d, data[d].LessThan(data[c]));
                int best = math.select(left, right, data[right].LessThan(data[left]));
                if (!data[best].LessThan(tail))
                    break;
                data[parent] = data[best];
                parent = best;
            }
            data[parent] = tail;
            return root;
        }
    }
}
