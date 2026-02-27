using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json.Linq;

namespace RepSeedResolver;

internal static class PropertyMapper
{
    public static RepPropInfo? Map(FProperty prop)
    {
        var name = prop.Name.Text;
        var arrayDim = prop.ArrayDim;

        return prop switch
        {
            FBoolProperty => new RepPropInfo(name, "bool", arrayDim),
            FByteProperty bp => MapByte(name, bp, arrayDim),
            FEnumProperty ep => MapEnum(name, ep, arrayDim),
            FInt8Property => new RepPropInfo(name, "int8", arrayDim),
            FInt16Property => new RepPropInfo(name, "int16", arrayDim),
            FIntProperty => new RepPropInfo(name, "int32", arrayDim),
            FInt64Property => new RepPropInfo(name, "int64", arrayDim),
            FUInt16Property => new RepPropInfo(name, "uint16", arrayDim),
            FUInt32Property => new RepPropInfo(name, "uint32", arrayDim),
            FUInt64Property => new RepPropInfo(name, "uint64", arrayDim),
            FFloatProperty => new RepPropInfo(name, "float", arrayDim),
            FDoubleProperty => new RepPropInfo(name, "double", arrayDim),
            FStrProperty => new RepPropInfo(name, "string", arrayDim),
            FNameProperty => new RepPropInfo(name, "name", arrayDim),
            FTextProperty => new RepPropInfo(name, "text", arrayDim),
            FClassProperty => new RepPropInfo(name, "class_ref", arrayDim),
            FSoftObjectProperty => new RepPropInfo(name, "soft_obj", arrayDim),
            FWeakObjectProperty => new RepPropInfo(name, "weak_obj", arrayDim),
            FObjectProperty => new RepPropInfo(name, "object", arrayDim),
            FInterfaceProperty => new RepPropInfo(name, "interface", arrayDim),
            FStructProperty sp => MapStruct(name, sp, arrayDim),
            FArrayProperty ap => MapArray(name, ap, arrayDim),
            FSetProperty sp => MapSet(name, sp, arrayDim),
            FMapProperty mp => MapMap(name, mp, arrayDim),
            _ => null,
        };
    }

    private static RepPropInfo MapByte(string name, FByteProperty bp, int arrayDim)
    {
        if (bp.Enum is { IsNull: false })
        {
            var info = new RepPropInfo(name, "byte", arrayDim) { EnumName = bp.Enum.Name };
            info.Bits = EnumHelper.ComputeMax(bp.Enum);
            return info;
        }
        return new RepPropInfo(name, "byte", arrayDim);
    }

    private static RepPropInfo MapEnum(string name, FEnumProperty ep, int arrayDim)
    {
        var enumName = !ep.Enum.IsNull ? ep.Enum.Name : null;
        var info = new RepPropInfo(name, "byte", arrayDim) { EnumName = enumName };
        if (!ep.Enum.IsNull) info.Bits = EnumHelper.ComputeMax(ep.Enum);
        return info;
    }

    private static RepPropInfo MapStruct(string name, FStructProperty sp, int arrayDim)
    {
        var sn = !sp.Struct.IsNull ? sp.Struct.Name : "Unknown";
        return new RepPropInfo(name, $"struct:{sn}", arrayDim) { StructType = sn };
    }

    private static RepPropInfo MapArray(string name, FArrayProperty ap, int arrayDim)
    {
        var info = new RepPropInfo(name, "array", arrayDim);
        if (ap.Inner is FProperty inner) { var i = Map(inner); if (i != null) info.InnerProps = [i]; }
        return info;
    }

    private static RepPropInfo MapSet(string name, FSetProperty sp, int arrayDim)
    {
        var info = new RepPropInfo(name, "set", arrayDim);
        if (sp.ElementProp is FProperty el) { var i = Map(el); if (i != null) info.InnerProps = [i]; }
        return info;
    }

    private static RepPropInfo MapMap(string name, FMapProperty mp, int arrayDim)
    {
        var info = new RepPropInfo(name, "map", arrayDim);
        var inners = new List<RepPropInfo>();
        if (mp.KeyProp is FProperty k) { var i = Map(k); if (i != null) inners.Add(i); }
        if (mp.ValueProp is FProperty v) { var i = Map(v); if (i != null) inners.Add(i); }
        if (inners.Count > 0) info.InnerProps = inners;
        return info;
    }
}

internal static class EnumHelper
{
    public static int? ComputeMax(FPackageIndex enumRef)
    {
        try
        {
            var ueEnum = enumRef.Load<UEnum>();
            if (ueEnum == null || ueEnum.Names.Length == 0) return null;

            var maxValue = ueEnum.Names.Max(e => e.Item2);
            var baseMax = Math.Max((int)(maxValue + 1), ueEnum.Names.Length);
            var hasExplicitMax = ueEnum.Names.Any(e => IsMaxName(e.Item1.ToString(), ueEnum.Name));
            return hasExplicitMax ? baseMax : baseMax + 1;
        }
        catch { return null; }
    }

    private static bool IsMaxName(string entryName, string? enumName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return false;
        if (entryName.EndsWith("_MAX", StringComparison.OrdinalIgnoreCase)
            || entryName.EndsWith("::MAX", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(enumName)) return false;
        return entryName.Equals($"{enumName}_MAX", StringComparison.OrdinalIgnoreCase)
            || entryName.Equals($"{enumName}::MAX", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class HandleEntryBuilder
{
    public static JObject Build(RepPropInfo prop, string ownerClass, int handle)
    {
        var entry = new JObject
        {
            ["h"] = handle,
            ["name"] = prop.Name,
            ["type"] = prop.TypeStr,
            ["class"] = ownerClass,
        };

        if (prop.EnumName != null) entry["enum"] = prop.EnumName;
        if (prop.Bits.HasValue) entry["max"] = prop.Bits.Value;
        if (prop.ArrayDim > 1) entry["array_dim"] = prop.ArrayDim;
        if (prop.StructType != null) entry["struct_type"] = prop.StructType;
        if (prop.InnerProps is { Count: > 0 }) entry["inner"] = BuildInner(prop);

        return entry;
    }

    private static JArray BuildInner(RepPropInfo prop)
    {
        var arr = new JArray();
        for (var i = 0; i < prop.InnerProps!.Count; i++)
        {
            var inner = prop.InnerProps[i];
            var entry = new JObject
            {
                ["h"] = i + 1,
                ["name"] = prop.Name,
                ["type"] = inner.TypeStr,
            };
            if (inner.StructType != null) entry["struct_type"] = inner.StructType;
            if (inner.InnerProps is { Count: > 0 }) entry["inner"] = BuildInner(inner);
            arr.Add(entry);
        }
        return arr;
    }
}
