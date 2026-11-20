using System;
using System.IO;
using System.Text;

internal static class RouteGenerator
{
    // The caller verifies the complete game assembly digest before requesting a projection.
    internal static void Build(AssemblyModel game, string outputDirectory)
    {
        string source = game.DecompileType("Game.Pathfind.PathfindJobs").Replace("\r\n", "\n");
        string owner = "public static class PathfindJobs";
        int ownerAt = Unique(source, owner);
        int jobAt = Unique(source, "public struct PathfindJob : IJob");
        int firstJobAttribute = source.LastIndexOf("[BurstCompile]", jobAt, StringComparison.Ordinal);
        int methodAt = Unique(source, "public static void Execute(NativePathfindData pathfindData,");
        int resultsAt = Unique(source, "public struct ResultItem");
        if (!(ownerAt < firstJobAttribute && firstJobAttribute < jobAt && jobAt < methodAt && methodAt < resultsAt))
            throw new InvalidDataException("Unexpected ordinary route type or entry-point ordering.");

        int methodStart = source.IndexOf('{', methodAt);
        int methodEnd = EndOfBody(source, methodStart);
        if (methodEnd >= resultsAt || !string.IsNullOrWhiteSpace(source[(methodEnd+1)..resultsAt].Trim().TrimEnd('}')))
            throw new InvalidDataException("Unexpected declaration following the ordinary route entry point.");

        string helpers = source[..firstJobAttribute].TrimEnd().Replace(owner, "internal static class ReconstructedRouteBody");
        const string extract = "heapData = m_Heap.Extract();";
        Unique(helpers, extract);
        Unique(helpers, "private UnsafeMinHeap<HeapData> m_Heap;");
        Unique(helpers, "return m_TotalCost < other.m_TotalCost;");
        Unique(helpers, "private bool HeapExtract(out HeapData heapData)");
        if (helpers.Contains("Unknown result type") || helpers.Contains("Invalid IL"))
            throw new InvalidDataException("Unresolved reference in the supported route executor.");
        helpers = helpers.Replace(extract, "heapData = global::ServiceCoverageFix.RouteHeap.Extract(ref m_Heap);");
        string entry = source[methodAt..(methodEnd+1)];
        bool blockNamespace = source[..ownerAt].Contains("namespace Game.Pathfind\n{");
        if (!blockNamespace && !source[..ownerAt].Contains("namespace Game.Pathfind;"))
            throw new InvalidDataException("Unexpected route namespace syntax.");
        string generated = "// Generated from the locally verified game assembly. Do not redistribute this intermediate.\n" +
            helpers + "\n\n" + entry + "\n}\n" + (blockNamespace ? "}\n" : "");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "RouteBody.g.cs"), generated, new UTF8Encoding(false));
    }

    internal static void Fallback(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        const string stub = """
        // Coverage-only build: no optimized route implementation was generated.
        namespace Game.Pathfind {
            internal static class ReconstructedRouteBody {
                internal static void Execute(NativePathfindData pathfindData, Unity.Collections.Allocator allocator,
                    Unity.Mathematics.Random random, PathfindHeuristicData heuristics, float passengerSpeed,
                    float cargoSpeed, ref PathfindActionData action) {
                    PathfindJobs.PathfindJob.Execute(pathfindData, allocator, random, heuristics,
                        passengerSpeed, cargoSpeed, ref action);
                }
            }
        }
        """;
        File.WriteAllText(Path.Combine(outputDirectory, "RouteBody.g.cs"), stub, new UTF8Encoding(false));
    }

    private static int Unique(string text, string marker)
    {
        int first = text.IndexOf(marker, StringComparison.Ordinal);
        if (first < 0 || text.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Ordinary route generation expected one occurrence of " + marker);
        return first;
    }

    private static int EndOfBody(string source, int start)
    {
        if (start < 0) throw new InvalidDataException("Missing route entry point.");
        int depth = 0;
        for (int i = start; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            if (source[i] == '}' && --depth == 0) return i;
        }
        throw new InvalidDataException("Unclosed route entry point.");
    }
}
