using CUE4Parse.UE4.Versions;
using Newtonsoft.Json.Linq;

namespace RepSeedResolver;

internal sealed record ResolveOptions(
    string PaksDir,
    string SeedPath,
    EGame Game,
    string UsmapPath,
    string OutputDir,
    string? AesKey = null);

internal sealed record ResolveResult(
    JObject RepLayout,
    JObject ClassNetCache,
    string RepLayoutPath,
    string ClassNetCachePath);

internal sealed record SeedClass(JArray Handles, JArray NetFields);
internal sealed record SeedData(Dictionary<string, SeedClass> Classes);

internal sealed record BpClassInfo(string Parent, List<RepPropInfo> RepProps, List<BpNetField> NetFields);
internal sealed record BpNetField(string Name, string Type, int ArrayDim, List<RepPropInfo>? Params = null);

internal sealed record ScanStats(int PackageCount, int BlueprintCount, int ErrorCount, int SkippedCount);
internal sealed record ScanResult(Dictionary<string, BpClassInfo> Classes, ScanStats Stats);

internal sealed class RepPropInfo
{
    public string Name { get; }
    public string TypeStr { get; }
    public int ArrayDim { get; }
    public string? EnumName { get; set; }
    public string? StructType { get; set; }
    public int? Bits { get; set; }
    public List<RepPropInfo>? InnerProps { get; set; }
    public Dictionary<string, long>? EnumValues { get; set; }
    public List<RepPropInfo>? StructFields { get; set; }
    public string? MetaClass { get; set; }
    public string? ObjectClass { get; set; }

    public RepPropInfo(string name, string typeStr, int arrayDim)
    {
        Name = name;
        TypeStr = typeStr;
        ArrayDim = arrayDim;
    }
}
