using Newtonsoft.Json.Linq;

namespace RepSeedResolver;

internal static class RepLayoutBuilder
{
    public static JObject Build(SeedData seed, ScanResult scan)
    {
        var seedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var classes = new JObject();

        foreach (var (name, sc) in seed.Classes)
        {
            seedCounts[name] = sc.Handles.Count;
            classes[name] = new JObject { ["handles"] = sc.Handles.DeepClone() };
        }

        int resolved = 0, unresolved = 0, withProps = 0;

        foreach (var bpName in scan.Classes.Keys)
        {
            var cppParent = FindCppAncestor(bpName, scan.Classes, seedCounts,
                new HashSet<string>(StringComparer.Ordinal));

            if (cppParent == null) { unresolved++; continue; }
            resolved++;

            var cppHandleCount = seedCounts[cppParent];
            var allProps = CollectBpChainRepProps(bpName, cppParent, scan.Classes);
            if (allProps.Count > 0) withProps++;

            var arr = new JArray();
            for (var i = 0; i < allProps.Count; i++)
            {
                var (prop, owner) = allProps[i];
                arr.Add(HandleEntryBuilder.Build(prop, owner, cppHandleCount + i + 1));
            }

            classes[bpName] = new JObject { ["parent"] = cppParent, ["handles"] = arr };
        }

        return new JObject
        {
            ["stats"] = new JObject
            {
                ["cpp_classes"] = seed.Classes.Count,
                ["bp_classes"] = scan.Classes.Count,
                ["resolved"] = resolved,
                ["unresolved"] = unresolved,
                ["with_rep_properties"] = withProps,
                ["packages"] = scan.Stats.PackageCount,
                ["skipped"] = scan.Stats.SkippedCount,
                ["errors"] = scan.Stats.ErrorCount,
            },
            ["classes"] = classes,
        };
    }

    private static string? FindCppAncestor(
        string name,
        Dictionary<string, BpClassInfo> bpClasses,
        Dictionary<string, int> cppCounts,
        HashSet<string> visited)
    {
        if (cppCounts.ContainsKey(name)) return name;
        if (!bpClasses.TryGetValue(name, out var info) || !visited.Add(name)) return null;
        return FindCppAncestor(info.Parent, bpClasses, cppCounts, visited);
    }

    private static List<(RepPropInfo Prop, string OwnerClass)> CollectBpChainRepProps(
        string bpName,
        string cppAncestor,
        Dictionary<string, BpClassInfo> bpClasses)
    {
        var chain = new List<string>();
        var current = bpName;
        while (current != cppAncestor && bpClasses.ContainsKey(current))
        {
            chain.Add(current);
            current = bpClasses[current].Parent;
        }
        chain.Reverse();

        var result = new List<(RepPropInfo, string)>();
        foreach (var className in chain)
        {
            if (!bpClasses.TryGetValue(className, out var classInfo)) continue;
            foreach (var prop in classInfo.RepProps)
                result.Add((prop, className));
        }
        return result;
    }
}
