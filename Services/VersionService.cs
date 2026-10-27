using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinxLauncher.Models;

namespace LinxLauncher.Services;

public sealed class ResolvedVersion
{
    public VersionDetail Detail { get; set; } = new();
    /// <summary>最底层的原版版本号（决定 client.jar 用哪个）</summary>
    public string BaseId { get; set; } = "";
    public string? ClientJar => File.Exists(Paths.VersionJar(BaseId)) ? Paths.VersionJar(BaseId) : null;
    public bool IsLoader => !string.Equals(Detail.Id, BaseId, StringComparison.OrdinalIgnoreCase);
}

public sealed class VersionService
{
    private VersionManifest? _manifest;
    private DateTime _manifestAt = DateTime.MinValue;

    // ============ 版本清单 ============
    public async Task<VersionManifest> GetManifestAsync(bool force, CancellationToken ct)
    {
        if (!force && _manifest is not null && (DateTime.Now - _manifestAt).TotalMinutes < 20)
            return _manifest;

        var cache = Path.Combine(Paths.CacheDir, "version_manifest.json");
        Directory.CreateDirectory(Paths.CacheDir);

        if (!force && File.Exists(cache) && (DateTime.Now - File.GetLastWriteTime(cache)).TotalHours < 12)
        {
            try
            {
                var cached = Json.Parse<VersionManifest>(await File.ReadAllTextAsync(cache, ct));
                if (cached?.Versions.Count > 0)
                {
                    _manifest = cached;
                    _manifestAt = DateTime.Now;
                    return cached;
                }
            }
            catch { /* 缓存坏了就重新拉 */ }
        }

        var text = await Http.GetStringAsync(Endpoints.ManifestUrl, ct);
        var manifest = Json.Parse<VersionManifest>(text)
                       ?? throw new IOException("版本清单解析失败");

        if (manifest.Versions.Count == 0) throw new IOException("版本清单为空");

        await File.WriteAllTextAsync(cache, text, ct);
        _manifest = manifest;
        _manifestAt = DateTime.Now;
        return manifest;
    }

    // ============ 单版本 JSON ============
    public async Task<VersionDetail> GetVersionJsonAsync(string id, CancellationToken ct)
    {
        var local = Paths.VersionJson(id);
        if (File.Exists(local))
        {
            try
            {
                var cached = Json.Parse<VersionDetail>(await File.ReadAllTextAsync(local, ct));
                if (cached is not null && !string.IsNullOrEmpty(cached.Id)) return cached;
            }
            catch { }
        }

        string url;
        if (Endpoints.UseMirror)
        {
            url = $"{Endpoints.Bmcl}/version/{id}/json";
        }
        else
        {
            var manifest = await GetManifestAsync(false, ct);
            var entry = manifest.Versions.FirstOrDefault(v => v.Id == id)
                        ?? throw new IOException($"未找到版本 {id}");
            url = entry.Url;
        }

        var detail = await Http.GetJsonAsync<VersionDetail>(url, ct)
                     ?? throw new IOException($"版本 {id} 详情解析失败");
        if (string.IsNullOrEmpty(detail.Id)) detail.Id = id;
        return detail;
    }

    // ============ 继承链合并 ============
    public async Task<ResolvedVersion> ResolveAsync(string id, CancellationToken ct)
    {
        var chain = new List<VersionDetail>();
        var cur = await GetVersionJsonAsync(id, ct);
        chain.Add(cur);

        int guard = 0;
        while (!string.IsNullOrEmpty(cur.InheritsFrom) && guard++ < 6)
        {
            cur = await GetVersionJsonAsync(cur.InheritsFrom!, ct);
            chain.Add(cur);
        }

        var baseId = chain.Count > 0 ? chain[^1].Id : id;

        string? mainClass = null, assets = null, mcArgs = null;
        AssetIndexInfo? assetIndex = null;
        JavaVersionInfo? javaVersion = null;
        Dictionary<string, DownloadInfo>? downloads = null;
        var libs = new List<Library>();
        var gameArgs = new List<JsonElement>();
        var jvmArgs = new List<JsonElement>();
        var seenLib = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 从「子」到「父」：单值字段子优先，列表字段子在前
        foreach (var d in chain)
        {
            mainClass ??= d.MainClass;
            assets ??= d.Assets;
            mcArgs ??= d.MinecraftArguments;
            assetIndex ??= d.AssetIndex;
            javaVersion ??= d.JavaVersion;
            downloads ??= d.Downloads;

            if (d.Libraries is not null)
                foreach (var l in d.Libraries)
                    if (seenLib.Add(l.Name)) libs.Add(l);

            if (d.Arguments?.Game is not null) gameArgs.AddRange(d.Arguments.Game);
            if (d.Arguments?.Jvm is not null) jvmArgs.AddRange(d.Arguments.Jvm);
        }

        var merged = new VersionDetail
        {
            Id = id,
            Type = chain[0].Type,
            MainClass = mainClass,
            Assets = assets,
            AssetIndex = assetIndex,
            JavaVersion = javaVersion,
            Downloads = downloads,
            MinecraftArguments = mcArgs,
            Libraries = libs,
            Arguments = new VersionArguments { Game = gameArgs, Jvm = jvmArgs },
            InheritsFrom = chain.Count > 1 ? chain[1].Id : null
        };

        return new ResolvedVersion { Detail = merged, BaseId = baseId };
    }

    // ============ rules 过滤（Windows 平台） ============
    public static bool IsAllowed(List<Rule>? rules)
    {
        if (rules is null || rules.Count == 0) return true;

        bool allowed = false;
        foreach (var r in rules)
        {
            if (r.Os is not null)
            {
                var name = r.Os.Name;
                if (!string.IsNullOrEmpty(name) && !name.Equals("windows", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(r.Os.Arch) && !r.Os.Arch.Equals("x86", StringComparison.OrdinalIgnoreCase)
                    && Environment.Is64BitOperatingSystem)
                    continue;
            }

            if (r.Features is { Count: > 0 }) continue; // 启动器不启用任何可选特性

            allowed = r.Action.Equals("allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    // ============ 扫描已安装 ============
    public List<LocalVersion> ScanInstalled()
    {
        var list = new List<LocalVersion>();
        try
        {
            if (!Directory.Exists(Paths.VersionsDir)) return list;

            foreach (var dir in Directory.GetDirectories(Paths.VersionsDir))
            {
                var id = Path.GetFileName(dir);
                var json = Path.Combine(dir, id + ".json");
                if (!File.Exists(json)) continue;

                VersionDetail? d = null;
                try { d = Json.Parse<VersionDetail>(File.ReadAllText(json)); } catch { }

                var jar = Path.Combine(dir, id + ".jar");
                var hasJar = File.Exists(jar);

                var lv = new LocalVersion
                {
                    Id = id,
                    Directory = dir,
                    Detail = d,
                    HasJar = hasJar,
                    LastWrite = File.GetLastWriteTime(json),
                    Type = d?.Type ?? "release",
                    BaseVersion = d?.InheritsFrom,
                    SizeBytes = hasJar ? new FileInfo(jar).Length : 0
                };

                lv.DisplayKind = DetectKind(id, d);

                // 完整判定：有 jar，或继承版本本身完整
                lv.IsComplete = hasJar
                                || (!string.IsNullOrEmpty(lv.BaseVersion) && File.Exists(Paths.VersionJar(lv.BaseVersion!)));

                list.Add(lv);
            }
        }
        catch (Exception ex)
        {
            Log.Error("ScanInstalled", ex);
        }

        return list.OrderByDescending(v => v.LastWrite).ToList();
    }

    private static string DetectKind(string id, VersionDetail? d)
    {
        var lower = id.ToLowerInvariant();
        if (lower.StartsWith("fabric-loader")) return "Fabric";
        if (lower.StartsWith("quilt-loader")) return "Quilt";
        if (lower.StartsWith("neoforge")) return "NeoForge";
        if (lower.StartsWith("forge")) return "Forge";
        if (lower.StartsWith("optifine")) return "OptiFine";
        if (d?.MainClass?.Contains("fabric", StringComparison.OrdinalIgnoreCase) == true) return "Fabric";
        if (d?.MainClass?.Contains("forge", StringComparison.OrdinalIgnoreCase) == true) return "Forge";
        return "原版";
    }

    public LocalVersion? FindLocal(string id) => ScanInstalled().FirstOrDefault(v => v.Id == id);
}
