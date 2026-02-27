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
                var entry = new JObject
                {
                    ["name"] = f["name"]?.ToString() ?? "",
                    ["type"] = f["type"]?.ToString() ?? "property",
                    ["class"] = f["class"]?.ToString() ?? name,
                };
                if (f["params"] is JArray seedParams && seedParams.Count > 0)
                    entry["params"] = seedParams;
                if (f["offset"] is JValue offsetVal)
                    entry["offset"] = offsetVal;
                fieldsArr.Add(entry);
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
                if (f.Params is { Count: > 0 })
                    entry["params"] = SerializeParams(f.Params);

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

    private static JArray SerializeParams(List<RepPropInfo> parms)
    {
        var arr = new JArray();
        foreach (var p in parms)
            arr.Add(SerializeProp(p));
        return arr;
    }

    private static JObject SerializeProp(RepPropInfo p)
    {
        var obj = new JObject
        {
            ["name"] = p.Name,
            ["type"] = p.TypeStr,
        };
        if (p.EnumName != null) obj["enum"] = p.EnumName;
        if (p.Bits.HasValue) obj["max"] = p.Bits.Value;
        if (p.EnumValues is { Count: > 0 })
            obj["values"] = JObject.FromObject(p.EnumValues);
        if (p.StructType != null) obj["struct_type"] = p.StructType;
        if (p.StructFields is { Count: > 0 })
        {
            var fields = new JArray();
            foreach (var f in p.StructFields)
                fields.Add(SerializeProp(f));
            obj["fields"] = fields;
        }
        if (p.MetaClass != null) obj["meta_class"] = p.MetaClass;
        if (p.ObjectClass != null) obj["object_class"] = p.ObjectClass;
        if (p.InnerProps is { Count: > 0 })
        {
            var inner = new JArray();
            foreach (var i in p.InnerProps)
                inner.Add(SerializeProp(i));
            obj["inner"] = inner;
        }
        return obj;
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
