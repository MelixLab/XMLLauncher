using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinxLauncher.Models;

namespace LinxLauncher.Services;

public sealed class InstallProgress
{
    public string Stage { get; set; } = "准备";
    public string CurrentFile { get; set; } = "";
    public long DoneBytes { get; set; }
    public long TotalBytes { get; set; }
    public int DoneFiles { get; set; }
    public int TotalFiles { get; set; }
    public bool Indeterminate { get; set; }
    public string? Message { get; set; }

    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)DoneBytes / TotalBytes, 0, 1) : 0;
}

/// <summary>把版本装进磁盘：版本 JSON、client.jar、依赖库、资源文件、natives。</summary>
public sealed class GameInstaller
{
    private readonly VersionService _versions = new();

    public VersionService Versions => _versions;

    /// <summary>下载并安装一个原版版本，返回合并后的版本详情</summary>
    public async Task<VersionDetail> InstallVanillaAsync(
        string id,
        IProgress<InstallProgress>? progress,
        CancellationToken ct,
        bool verify = false)
    {
        Paths.EnsureCore();

        progress?.Report(new InstallProgress { Stage = "准备", Indeterminate = true, Message = "获取版本信息…" });

        var detail = await _versions.GetVersionJsonAsync(id, ct);

        // 1) 落地版本 JSON
        var dir = Paths.VersionDir(id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Paths.VersionJson(id), Json.Write(detail), ct);

        // 2) 资源索引（小文件，必须先拿到才能枚举对象）
        var index = await EnsureAssetIndexAsync(detail, progress, ct);

        // 3) 组装下载清单
        var items = new List<DownloadItem>();

        var client = detail.Downloads is not null && detail.Downloads.TryGetValue("client", out var c) ? c : null;
        if (client?.Url is not null)
        {
            items.Add(new DownloadItem
            {
                Url = client.Url,
                Dest = Paths.VersionJar(id),
                Size = client.Size,
                Sha1 = client.Sha1,
                Label = id + ".jar",
                Stage = "客户端"
            });
        }

        items.AddRange(BuildLibraryItems(detail, id));

        if (index is not null)
        {
            foreach (var (name, obj) in index.Objects)
            {
                if (string.IsNullOrEmpty(obj.Hash) || obj.Hash.Length < 2) continue;
                items.Add(new DownloadItem
                {
                    Url = Endpoints.AssetUrl(obj.Hash),
                    Dest = Paths.AssetObjectPath(obj.Hash),
                    Size = obj.Size,
                    Sha1 = obj.Hash,
                    Label = name,
                    Stage = "资源文件"
                });
            }
        }

        long totalBytes = items.Sum(i => Math.Max(0, i.Size));
        int totalFiles = items.Count;

        progress?.Report(new InstallProgress
        {
            Stage = "准备完成",
            TotalBytes = totalBytes,
            TotalFiles = totalFiles,
            Message = $"共 {totalFiles:N0} 个文件 / {FormatSize(totalBytes)}"
        });

        // 4) 并发下载
        // 资源文件都是几十 KB 的小文件，并发低了会被单文件延迟拖死
        var downloader = new BatchDownloader { Parallelism = Math.Clamp(AppState.Settings.DownloadThreads * 3, 12, 32), VerifyExisting = verify };

        var tickProgress = new Progress<DownloadTick>(t => progress?.Report(new InstallProgress
        {
            Stage = string.IsNullOrEmpty(t.Stage) ? "下载中" : t.Stage,
            CurrentFile = t.CurrentFile,
            DoneBytes = t.DoneBytes,
            TotalBytes = Math.Max(t.TotalBytes, 1),
            DoneFiles = t.DoneFiles,
            TotalFiles = t.TotalFiles
        }));

        await downloader.RunAsync(items, tickProgress, ct);

        progress?.Report(new InstallProgress
        {
            Stage = "完成",
            DoneBytes = totalBytes,
            TotalBytes = Math.Max(totalBytes, 1),
            DoneFiles = totalFiles,
            TotalFiles = totalFiles,
            Message = "安装完成"
        });

        return detail;
    }

    /// <summary>
    /// 校验并修复：按版本 JSON 重新枚举客户端 / 依赖库 / 资源索引里的所有文件，
    /// 缺什么补什么（已存在且大小对得上的直接跳过）。返回补下的文件数。
    ///
    /// 之前主菜单是灰底、界面是英文，就是因为 1.20.1 的资源对象缺了 400 多个
    /// （全景图、语言文件、音效都在里面）—— 这个方法就是干这个的。
    /// </summary>
    public async Task<int> RepairAsync(string versionId, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        Paths.EnsureCore();

        progress?.Report(new InstallProgress { Stage = "校验", Indeterminate = true, Message = "读取版本信息…" });

        var resolved = await _versions.ResolveAsync(versionId, ct);
        var detail = resolved.Detail;

        var items = new List<DownloadItem>();

        // 1) client.jar
        var client = detail.Downloads is not null && detail.Downloads.TryGetValue("client", out var c) ? c : null;
        if (client?.Url is not null && !string.IsNullOrEmpty(resolved.BaseId))
        {
            items.Add(new DownloadItem
            {
                Url = client.Url,
                Dest = Paths.VersionJar(resolved.BaseId!),
                Size = client.Size,
                Sha1 = client.Sha1,
                Label = resolved.BaseId + ".jar",
                Stage = "客户端"
            });
        }

        // 2) 依赖库与 natives
        items.AddRange(BuildLibraryItems(detail, versionId));

        // 3) 资源对象（语言文件、全景图、音效全在这）
        var index = await EnsureAssetIndexAsync(detail, progress, ct);
        if (index is not null)
        {
            foreach (var (name, obj) in index.Objects)
            {
                if (string.IsNullOrEmpty(obj.Hash) || obj.Hash.Length < 2) continue;
                items.Add(new DownloadItem
                {
                    Url = Endpoints.AssetUrl(obj.Hash),
                    Dest = Paths.AssetObjectPath(obj.Hash),
                    Size = obj.Size,
                    Sha1 = obj.Hash,
                    Label = name,
                    Stage = "资源文件"
                });
            }
        }

        // 先数一下到底缺多少，缺 0 个就直接返回，不用白跑一趟
        var missing = items.Where(it =>
        {
            try
            {
                var fi = new FileInfo(it.Dest);
                return !fi.Exists || (it.Size > 0 && fi.Length != it.Size);
            }
            catch { return true; }
        }).ToList();

        if (missing.Count == 0)
        {
            progress?.Report(new InstallProgress { Stage = "完成", Message = "文件完整", TotalFiles = 0, DoneFiles = 0 });
            return 0;
        }

        var totalBytes = missing.Sum(i => Math.Max(0, i.Size));
        progress?.Report(new InstallProgress
        {
            Stage = "缺少文件",
            TotalBytes = totalBytes,
            TotalFiles = missing.Count,
            Message = $"缺少 {missing.Count:N0} 个文件 / {FormatSize(totalBytes)}，开始补全…"
        });

        var downloader = new BatchDownloader { Parallelism = Math.Clamp(AppState.Settings.DownloadThreads * 3, 12, 32) };
        var tick = new Progress<DownloadTick>(t => progress?.Report(new InstallProgress
        {
            Stage = string.IsNullOrEmpty(t.Stage) ? "修复中" : t.Stage,
            CurrentFile = t.CurrentFile,
            DoneBytes = t.DoneBytes,
            TotalBytes = Math.Max(t.TotalBytes, 1),
            DoneFiles = t.DoneFiles,
            TotalFiles = t.TotalFiles
        }));

        await downloader.RunAsync(missing, tick, ct);

        progress?.Report(new InstallProgress
        {
            Stage = "完成",
            DoneBytes = totalBytes,
            TotalBytes = Math.Max(totalBytes, 1),
            DoneFiles = missing.Count,
            TotalFiles = missing.Count,
            Message = "修复完成"
        });

        Log.Info($"修复 {versionId}：补下 {missing.Count} 个文件");
        return missing.Count;
    }

    private async Task<AssetIndexFile?> EnsureAssetIndexAsync(VersionDetail detail, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (detail.AssetIndex?.Url is null) return null;

        var indexId = detail.AssetIndex.Id ?? detail.Assets ?? "legacy";
        var path = Path.Combine(Paths.AssetsIndexes, indexId + ".json");
        Directory.CreateDirectory(Paths.AssetsIndexes);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            progress?.Report(new InstallProgress { Stage = "资源索引", Indeterminate = true, Message = "下载资源索引…" });
            await Http.DownloadToFileAsync(detail.AssetIndex.Url, path, null, ct);
        }

        try
        {
            return Json.Parse<AssetIndexFile>(await File.ReadAllTextAsync(path, ct));
        }
        catch (Exception ex)
        {
            Log.Warn($"资源索引解析失败 {path}: {ex.Message}");
            return null;
        }
    }

    public static List<DownloadItem> BuildLibraryItems(VersionDetail detail, string versionId)
    {
        var list = new List<DownloadItem>();
        var nativesDir = Paths.VersionNatives(versionId);

        foreach (var lib in detail.Libraries ?? new List<Library>())
        {
            if (!VersionService.IsAllowed(lib.Rules)) continue;

            // 老式 natives：downloads.classifiers["natives-windows"]
            if (lib.Natives is { Count: > 0 } && lib.Natives.TryGetValue("windows", out var classifier))
            {
                var key = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                if (lib.Downloads?.Classifiers is not null &&
                    lib.Downloads.Classifiers.TryGetValue(key, out var nd) && nd.Url is not null)
                {
                    list.Add(new DownloadItem
                    {
                        Url = nd.Url,
                        Dest = Path.Combine(Paths.LibrariesDir, (nd.Path ?? "").Replace('/', Path.DirectorySeparatorChar)),
                        Size = nd.Size,
                        Sha1 = nd.Sha1,
                        Label = Path.GetFileName(nd.Path ?? key),
                        Stage = "本地库",
                        ExtractTo = nativesDir,
                        ExcludeFromExtract = lib.Extract?.Exclude
                    });
                }
                continue;
            }

            var artifact = lib.Downloads?.Artifact;
            var mavenPath = artifact?.Path ?? MavenToPath(lib.Name);
            if (string.IsNullOrEmpty(mavenPath)) continue;

            var url = artifact?.Url;
            if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(lib.Url))
                url = lib.Url!.TrimEnd('/') + "/" + mavenPath;
            if (string.IsNullOrEmpty(url)) continue;

            bool isNative = lib.Name.Contains("natives-windows", StringComparison.OrdinalIgnoreCase);

            // 只取当前架构的 natives：x64 机器上装 arm64 / x86 的 dll 会把正确的覆盖掉
            if (isNative)
            {
                var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
                bool wrongArch =
                    (arch == System.Runtime.InteropServices.Architecture.X64 &&
                     (lib.Name.EndsWith("natives-windows-arm64", StringComparison.OrdinalIgnoreCase) ||
                      lib.Name.EndsWith("natives-windows-x86", StringComparison.OrdinalIgnoreCase))) ||
                    (arch == System.Runtime.InteropServices.Architecture.Arm64 &&
                     lib.Name.EndsWith("natives-windows-x86", StringComparison.OrdinalIgnoreCase));

                if (wrongArch) continue;
            }

            list.Add(new DownloadItem
            {
                Url = url,
                Dest = Path.Combine(Paths.LibrariesDir, mavenPath.Replace('/', Path.DirectorySeparatorChar)),
                Size = artifact?.Size ?? 0,
                Sha1 = artifact?.Sha1,
                Label = Path.GetFileName(mavenPath),
                Stage = isNative ? "本地库" : "依赖库",
                ExtractTo = isNative ? nativesDir : null,
                ExcludeFromExtract = isNative ? lib.Extract?.Exclude : null
            });
        }

        return list;
    }

    /// <summary>group:artifact:version[:classifier] → group/artifact/version/artifact-version[-classifier].jar</summary>
    public static string MavenToPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var parts = name.Split(':');
        if (parts.Length < 3) return "";

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} {units[u]}" : $"{v:0.0} {units[u]}";
    }
}
