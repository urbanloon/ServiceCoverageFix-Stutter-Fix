using System.Text.RegularExpressions;

internal static class CompanyGenerator
{
    // The caller pins Game.dll before generating this file in the intermediate build directory.
    internal static void Build(AssemblyModel game, string outputDirectory)
    {
        string source = game.DecompileType("Game.Buildings.PropertyUtils").Replace("\r\n", "\n");
        int preambleEnd = source.IndexOf("namespace Game.Buildings", StringComparison.Ordinal);
        string comparer = Body(source, "private struct PropertyPriorityComparer : IComparer<int>", true);
        string job = Body(source, "public struct CompanyFindPropertyJob : IJob", false);
        int methodsAt = job.IndexOf("private void Evaluate", StringComparison.Ordinal);
        if (preambleEnd < 0 || methodsAt < 0 || job.Contains("Unknown result type"))
            throw new InvalidDataException("Unresolved company property job.");

        // Use the stock job's public fields directly. Keep nested component fields and calls to the
        // original public commercial/industrial evaluators qualified exactly as the game specifies.
        string[] fields = Regex.Matches(job[..methodsAt], @"\b(m_\w+);").Select(m => m.Groups[1].Value).ToArray();
        if (fields.Length != 30) throw new InvalidDataException("Unexpected company property job fields.");
        string methods = job[methodsAt..];
        foreach (string field in fields)
            methods = Regex.Replace(methods, @"(?<![\w.])" + field + @"\b", "stock." + field);
        foreach (var (result, name) in new[] { ("void", "Evaluate"), ("bool", "PropertyAllowsResource"), ("int", "LookupResourceDemand") })
        {
            methods = Regex.Replace(methods, @"(?<![\w.])" + name + @"\(", name + "(ref stock, ");
            methods = Replace(methods, $"private {result} {name}(ref stock, ",
                $"private static {result} {name}(ref PropertyUtils.CompanyFindPropertyJob stock, ");
        }

        methods = Replace(methods, "public void Execute()", "internal static unsafe void Execute(ref PropertyUtils.CompanyFindPropertyJob stock)");
        // Retain the original empty-batch return before touching any other input.
        const string priorities = "NativeArray<int> priorities = new NativeArray<int>(length, Allocator.Temp);";
        methods = Replace(methods, priorities, Gate + "\n" + priorities);
        const string companies = "for (int j = 0; j < length; j++)";
        methods = Replace(methods, companies, "byte* occupancy = null;\nint searches = 0;\n" + companies);
        const string company = "Entity entity = stock.m_PropertylessCompanies[array[j]];";
        methods = Replace(methods, company, "if (array[j] < housedPrefix) continue;\n" + company);
        const string candidates = "for (int k = 0; k < stock.m_FreePropertyEntities.Length; k++)";
        methods = Replace(methods, candidates, StartSearch + "\n" + candidates);
        methods = Replace(methods, "bool flag2 = true;", ReadOccupancy);
        const string allows = "if (flag2 && PropertyAllowsResource(";
        methods = Replace(methods, allows, StoreOccupancy + "\n" + allows);

        string generated = source[..preambleEnd] + "namespace Game.Buildings { internal static class CompanySearchBody {\n" +
            comparer + "\n" + PrefixLength + "\n" + SafePriorityRange + "\n" + methods + "\n} }\n";
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "CompanyBody.g.cs"), generated.Replace("\r\n", "\n"));
    }

    private const string Gate = """
        int housedPrefix = length >= 16 ? HousedPrefix(ref stock, length) : 0;
        if (housedPrefix == length)
        {
            // An extreme custom demand range can overflow the stock subtraction comparer.
            // Keep the original sorting behavior if its order cannot be established.
            if (!SafePriorityRange(ref stock)) stock.Execute();
            return;
        }
        int listingCount = stock.m_FreePropertyEntities.Length;
        if (length < 3 || listingCount < 8 || listingCount > 2048)
        {
            stock.Execute();
            return;
        }
        """;

    private const string PrefixLength = """
        private static int HousedPrefix(ref PropertyUtils.CompanyFindPropertyJob stock, int length)
        {
            for (int i = 0; i < length; i++)
            {
                Entity company = stock.m_PropertylessCompanies[i];
                if (!stock.m_PropertyRenters.TryGetComponent(company, out var renter) ||
                    renter.m_Property == Entity.Null || !stock.m_PropertySeekers.HasComponent(company))
                    return i;
                if (stock.m_Commercial && (!stock.m_PrefabFromEntity.TryGetComponent(company, out var prefab) ||
                    !stock.m_ServiceCompanies.HasComponent(prefab.m_Prefab)))
                    return i;
            }
            return length;
        }
        """;

    private const string SafePriorityRange = """
        private static bool SafePriorityRange(ref PropertyUtils.CompanyFindPropertyJob stock)
        {
            int min = 0, max = 0;
            for (int i = 0; i < stock.m_ResourceDemands.Length; i++)
            {
                int value = stock.m_ResourceDemands[i];
                min = math.min(min, value);
                max = math.max(max, value);
            }
            return (long)max - min <= int.MaxValue;
        }
        """;

    // The second actual search records each reached index. Every previously claimed entity remains
    // claimed throughout this job, so an index skipped during population cannot later be consumed.
    private const string StartSearch = """
        bool reuseOccupancy = occupancy != null;
        if (searches++ == 1 && j + 1 < length)
        {
            byte* scratch = stackalloc byte[listingCount];
            Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(scratch, listingCount);
            occupancy = scratch;
        }
        """;

    private const string ReadOccupancy = """
        bool flag2;
        if (reuseOccupancy)
        {
            flag2 = occupancy[k] != 0;
        }
        else
        {
            flag2 = true;
        """;

    private const string StoreOccupancy = """
            if (occupancy != null) occupancy[k] = (byte)(flag2 ? 1 : 0);
        }
        """;

    private static string Body(string source, string head, bool keepDeclaration)
    {
        int at = source.IndexOf(head, StringComparison.Ordinal);
        if (at < 0 || source.IndexOf(head, at + head.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Company projection expected one declaration: " + head);
        int open = source.IndexOf('{', at), end = open + 1, depth = 1;
        for (; end < source.Length && depth != 0; end++)
        {
            if (source[end] == '{') depth++;
            if (source[end] == '}') depth--;
        }
        if (depth != 0) throw new InvalidDataException("Unclosed company method: " + head);
        return keepDeclaration ? source[at..end] : source[(open + 1)..(end - 1)];
    }

    private static string Replace(string source, string marker, string replacement)
    {
        int at = source.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || source.IndexOf(marker, at + marker.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Company projection expected one occurrence: " + marker);
        return source.Replace(marker, replacement);
    }
}
