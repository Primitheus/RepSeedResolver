using System.Diagnostics;
using System.IO;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.Encryption.Aes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RepSeedResolver;

internal sealed class SeedResolver
{
    private const int ScanProgressInterval = 10_000;

    private readonly Action<string> _log;
    private readonly Action<double> _progress;

    public SeedResolver(Action<string> log, Action<double> progress)
    {
        _log = log;
        _progress = progress;
    }

    public async Task<ResolveResult> RunAsync(ResolveOptions options, CancellationToken ct)
    {
        return await Task.Run(() => Run(options, ct), ct);
    }

    private ResolveResult Run(ResolveOptions options, CancellationToken ct)
    {
        var seed = LoadSeedData(options.SeedPath);
        ct.ThrowIfCancellationRequested();

        InitOodle();
        ct.ThrowIfCancellationRequested();

        using var provider = CreateProvider(options.PaksDir, options.Game, options.UsmapPath, options.AesKey);
        ct.ThrowIfCancellationRequested();

        var bpClassIndex = FindBpClassIndex(provider);
        var scanResult = ScanBlueprintClasses(provider, bpClassIndex, ct);

        Directory.CreateDirectory(options.OutputDir);

        var repLayoutPath = Path.Combine(options.OutputDir, "rep_layout.json");
        var classNetCachePath = Path.Combine(options.OutputDir, "class_net_cache.json");

        _progress(80);
        var repLayout = RepLayoutBuilder.Build(seed, scanResult);
        File.WriteAllText(repLayoutPath, repLayout.ToString(Formatting.Indented));
        Log($"RepLayout -> {repLayoutPath}");

        _progress(90);
        var classNetCache = ClassNetCacheBuilder.Build(seed, scanResult);
        File.WriteAllText(classNetCachePath, classNetCache.ToString(Formatting.Indented));
        Log($"ClassNetCache -> {classNetCachePath}");

        _progress(100);
        Log("Done.");

        return new ResolveResult(repLayout, classNetCache, repLayoutPath, classNetCachePath);
    }

    private SeedData LoadSeedData(string seedPath)
    {
        Log($"Loading seed: {seedPath}");
        var root = JObject.Parse(File.ReadAllText(seedPath));
        var classesObj = root["classes"] as JObject
            ?? throw new InvalidDataException("seed JSON missing 'classes'");

        var classes = new Dictionary<string, SeedClass>(StringComparer.Ordinal);
        foreach (var prop in classesObj.Properties())
        {
            if (prop.Value is not JObject obj) continue;
            var handles = obj["handles"] as JArray ?? new JArray();
            var netFields = obj["net_fields"] as JArray ?? new JArray();
            classes[prop.Name] = new SeedClass(handles, netFields);
        }

        Log($"Loaded {classes.Count} C++ classes");
        return new SeedData(classes);
    }

    private DefaultFileProvider CreateProvider(string paksDir, EGame eGame, string usmapPath, string? aesKey)
    {
        var sw = Stopwatch.StartNew();
        var provider = new DefaultFileProvider(
            paksDir, SearchOption.TopDirectoryOnly,
            new VersionContainer(eGame), StringComparer.OrdinalIgnoreCase);

        provider.Initialize();
        if (!string.IsNullOrWhiteSpace(usmapPath) && File.Exists(usmapPath))
        {
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
            Log($"Loaded mappings: {usmapPath}");
        }

        if (!string.IsNullOrWhiteSpace(aesKey))
        {
            var key = new FAesKey(aesKey);
            provider.SubmitKey(new FGuid(0), key);
            Log($"Submitted AES key: {aesKey[..4]}...");
        }

        provider.Mount();
        Log($"Mounted {provider.MountedVfs.Count} containers, {provider.Files.Count} files ({sw.ElapsedMilliseconds}ms)");
        _progress(20);
        return provider;
    }

    private ScanResult ScanBlueprintClasses(DefaultFileProvider provider, FPackageObjectIndex bpClassIndex, CancellationToken ct)
    {
        var classes = new Dictionary<string, BpClassInfo>(StringComparer.Ordinal);
        int packageCount = 0, bpCount = 0, errorCount = 0, skippedCount = 0;

        var packageFiles = provider.Files
            .Where(static kvp => kvp.Value.IsUePackage)
            .ToList();

        Log($"Scanning {packageFiles.Count} packages...");
        var sw = Stopwatch.StartNew();

        foreach (var (_, gameFile) in packageFiles)
        {
            ct.ThrowIfCancellationRequested();
            packageCount++;

            if (packageCount % ScanProgressInterval == 0)
            {
                var pct = 20 + (int)(60.0 * packageCount / packageFiles.Count);
                _progress(pct);
                Log($"  {packageCount}/{packageFiles.Count} ({bpCount} BPs, {sw.ElapsedMilliseconds}ms)...");
            }

            try
            {
                var package = provider.LoadPackage(gameFile);

                if (package is IoPackage io && bpClassIndex != FPackageObjectIndex.InvalidObjectIndex)
                {
                    var found = false;
                    for (var i = 0; i < io.ExportMap.Length; i++)
                    {
                        if (io.ExportMap[i].ClassIndex != bpClassIndex)
                            continue;

                        found = true;
                        try
                        {
                            if (io.ExportsLazy[i].Value is UBlueprintGeneratedClass bp)
                                ProcessBpClass(bp, classes, ref bpCount);
                        }
                        catch (Exception ex)
                        {
                            if (errorCount++ == 0)
                                Log($"First export error: {ex.Message}");
                        }
                    }

                    if (!found) skippedCount++;
                }
                else
                {
                    foreach (var export in package.GetExports())
                    {
                        if (export is UBlueprintGeneratedClass bp)
                            ProcessBpClass(bp, classes, ref bpCount);
                    }
                }
            }
            catch (Exception ex)
            {
                if (errorCount++ == 0)
                    Log($"First error: {ex.Message}");
            }
        }

        sw.Stop();
        Log($"Scan complete: {bpCount} BPs in {packageCount} packages ({sw.ElapsedMilliseconds}ms)");
        Log($"Skipped {skippedCount}, errors {errorCount}");

        return new ScanResult(classes, new ScanStats(packageCount, bpCount, errorCount, skippedCount));
    }

    private static void ProcessBpClass(
        UBlueprintGeneratedClass bpClass,
        Dictionary<string, BpClassInfo> dict,
        ref int count)
    {
        count++;
        var parent = bpClass.SuperStruct is { IsNull: false }
            ? bpClass.SuperStruct.Name
            : "Unknown";

        var repProps = new List<RepPropInfo>();
        var netFields = new List<BpNetField>();

        if (bpClass.ChildProperties != null)
        {
            foreach (var field in bpClass.ChildProperties)
            {
                if (field is not FProperty prop || !prop.PropertyFlags.HasFlag(EPropertyFlags.Net))
                    continue;

                netFields.Add(new BpNetField(prop.Name.Text, "property", prop.ArrayDim));

                var info = PropertyMapper.Map(prop);
                if (info != null)
                    repProps.Add(info);
            }
        }

        if (bpClass.FuncMap != null)
        {
            /* Sort by name to match UE ClassNetCache ordering */
            foreach (var (funcName, funcIdx) in bpClass.FuncMap.OrderBy(kv => kv.Key.Text, StringComparer.Ordinal))
            {
                try
                {
                    if (funcIdx.TryLoad<UFunction>(out var f)
                        && f.FunctionFlags.HasFlag(EFunctionFlags.FUNC_Net))
                    {
                        List<RepPropInfo>? funcParams = null;
                        if (f.ChildProperties != null)
                        {
                            foreach (var child in f.ChildProperties)
                            {
                                if (child is not FProperty prop) continue;
                                if (!prop.PropertyFlags.HasFlag(EPropertyFlags.Parm)) continue;
                                if (prop.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm)) continue;

                                var mapped = PropertyMapper.Map(prop);
                                if (mapped != null)
                                {
                                    funcParams ??= new List<RepPropInfo>();
                                    funcParams.Add(mapped);
                                }
                            }
                        }
                        netFields.Add(new BpNetField(funcName.Text, "function", 0, funcParams));
                    }
                }
                catch { }
            }
        }

        dict[bpClass.Name] = new BpClassInfo(parent, repProps, netFields);
    }

    private void InitOodle()
    {
        string? path = Path.Combine(AppContext.BaseDirectory, OodleHelper.OodleFileName);
        if (!File.Exists(path))
            path = Path.Combine(Environment.CurrentDirectory, OodleHelper.OodleFileName);
        if (!File.Exists(path))
            path = null;

        OodleHelper.Initialize(path);
        if (OodleHelper.Instance != null) { Log("Oodle initialized"); return; }

        Log("Oodle not found, downloading...");
        string? dlPath = Path.Combine(AppContext.BaseDirectory, OodleHelper.OodleFileName);
        if (OodleHelper.DownloadOodleDll(ref dlPath))
        {
            OodleHelper.Initialize(dlPath);
            Log("Oodle downloaded and initialized");
            return;
        }

        throw new InvalidOperationException("Failed to initialize Oodle");
    }

    private FPackageObjectIndex FindBpClassIndex(DefaultFileProvider provider)
    {
        if (provider.GlobalData == null)
            return FPackageObjectIndex.InvalidObjectIndex;

        foreach (var (idx, entry) in provider.GlobalData.ScriptObjectEntriesMap)
        {
            if (!entry.ObjectName.IsGlobal) continue;
            var nameIdx = (int)entry.ObjectName.NameIndex;
            if (nameIdx >= provider.GlobalData.GlobalNameMap.Length) continue;
            if (provider.GlobalData.GlobalNameMap[nameIdx].Name != "BlueprintGeneratedClass") continue;

            Log($"BlueprintGeneratedClass index: 0x{idx.TypeAndId:X16}");
            return idx;
        }

        Log("BlueprintGeneratedClass not found in global data, using full scan");
        return FPackageObjectIndex.InvalidObjectIndex;
    }

    private void Log(string msg) => _log($"[*] {msg}");
}
