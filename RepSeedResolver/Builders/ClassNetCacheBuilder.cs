using Newtonsoft.Json.Linq;

namespace RepSeedResolver;

internal static class ClassNetCacheBuilder
{
    public static JObject Build(SeedData seed, ScanResult scan)
    {
        var seedMaxValues = new Dictionary<string, int>(StringComparer.Ordinal);
        var classes = new JObject();

        foreach (var (name, sc) in seed.Classes)
        {
            seedMaxValues[name] = sc.NetFields.Count;

            var fieldsArr = new JArray();
            foreach (var f in sc.NetFields)
            {
                fieldsArr.Add(new JObject
                {
                    ["name"] = f["name"]?.ToString() ?? "",
                    ["type"] = f["type"]?.ToString() ?? "property",
                    ["class"] = f["class"]?.ToString() ?? name,
                });
            }

            classes[name] = new JObject
            {
                ["max"] = sc.NetFields.Count,
                ["fields"] = fieldsArr,
            };
        }

        var maxCache = new Dictionary<string, int?>(StringComparer.Ordinal);
        int resolvedCount = 0, unresolvedCount = 0, bpFieldCount = 0;

        foreach (var (bpName, info) in scan.Classes)
        {
            var max = ResolveNetFieldMax(bpName,
                new HashSet<string>(StringComparer.Ordinal),
                seedMaxValues, scan.Classes, maxCache);

            if (!max.HasValue) { unresolvedCount++; continue; }
            resolvedCount++;

            var cppParent = FindAncestorInSeed(bpName, scan.Classes, seedMaxValues,
                new HashSet<string>(StringComparer.Ordinal));

            var fieldsArr = new JArray();
            foreach (var f in info.NetFields)
            {
                var entry = new JObject
                {
                    ["name"] = f.Name,
                    ["type"] = f.Type,
                    ["class"] = bpName,
                };
                if (f.ArrayDim > 1) entry["array_dim"] = f.ArrayDim;
                fieldsArr.Add(entry);
            }

            var classEntry = new JObject
            {
                ["max"] = max.Value,
                ["fields"] = fieldsArr,
            };
            if (cppParent != null) classEntry["parent"] = cppParent;

            classes[bpName] = classEntry;
            bpFieldCount += info.NetFields.Count;
        }

        return new JObject
        {
            ["stats"] = new JObject
            {
                ["cpp_classes"] = seed.Classes.Count,
                ["bp_classes"] = scan.Classes.Count,
                ["resolved"] = resolvedCount,
                ["unresolved"] = unresolvedCount,
                ["total_fields"] = seedMaxValues.Values.Sum() + bpFieldCount,
                ["packages"] = scan.Stats.PackageCount,
                ["skipped"] = scan.Stats.SkippedCount,
                ["errors"] = scan.Stats.ErrorCount,
            },
            ["classes"] = classes,
        };
    }

    private static string? FindAncestorInSeed(
        string name,
        Dictionary<string, BpClassInfo> bpClasses,
        Dictionary<string, int> seedMax,
        HashSet<string> visited)
    {
        if (seedMax.ContainsKey(name)) return name;
        if (!bpClasses.TryGetValue(name, out var info) || !visited.Add(name)) return null;
        return FindAncestorInSeed(info.Parent, bpClasses, seedMax, visited);
    }

    private static int? ResolveNetFieldMax(
        string name,
        HashSet<string> visited,
        Dictionary<string, int> seeds,
        Dictionary<string, BpClassInfo> classes,
        Dictionary<string, int?> cache)
    {
        if (cache.TryGetValue(name, out var cached)) return cached;
        if (seeds.TryGetValue(name, out var seed)) { cache[name] = seed; return seed; }
        if (!classes.TryGetValue(name, out var info) || !visited.Add(name)) return null;

        try
        {
            var parentMax = ResolveNetFieldMax(info.Parent, visited, seeds, classes, cache);
            if (parentMax == null) { cache[name] = null; return null; }
            var result = parentMax.Value + info.NetFields.Count;
            cache[name] = result;
            return result;
        }
        finally { visited.Remove(name); }
    }
}
