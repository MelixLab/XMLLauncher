using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>
/// 从别的启动器（PCL2 / HMCL / 官方启动器）把东西搬过来。
/// 它们用的都是同一个 .minecraft 布局：versions/ 存档 saves/ mods/ 资源 assets/。
/// 这里只做「复制缺失的东西」，不动源目录。
/// </summary>
public static class ImportService
{
    /// <summary>猜得到的常见位置（猜不到的让用户自己选）。</summary>
    public static List<string> DetectCandidates()
    {
        var list = new List<string>();

        void Add(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
                var full = Path.GetFullPath(path);
                if (string.Equals(full.TrimEnd('\\'), Path.GetFullPath(Paths.GameDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return;
                if (list.Exists(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase))) return;
                list.Add(full);
            }
            catch { }
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 官方 / PCL2 / HMCL 的默认位置
        Add(Path.Combine(roaming, ".minecraft"));
        Add(Path.Combine(user, ".minecraft"));
        Add(Path.Combine(local, ".minecraft"));

        // PCL2 一些常见自定义目录
        Add(Path.Combine(user, "Desktop", ".minecraft"));
        Add(Path.Combine(user, "Documents", ".minecraft"));
        Add(Path.Combine(user, "Downloads", ".minecraft"));

        return list;
    }

    /// <summary>这个目录像不像一个 Minecraft 游戏目录。</summary>
    public static bool LooksLikeGameDir(string dir)
    {
        try
        {
            return Directory.Exists(Path.Combine(dir, "versions")) ||
                   Directory.Exists(Path.Combine(dir, "saves")) ||
                   File.Exists(Path.Combine(dir, "options.txt"));
        }
        catch { return false; }
    }

    public sealed class ImportResult
    {
        public int Versions { get; set; }
        public int Saves { get; set; }
        public int Mods { get; set; }
        public List<string> Skipped { get; } = new();
        public string Summary => $"导入 {Versions} 个版本、{Saves} 个存档、{Mods} 个模组";
    }

    /// <summary>
    /// 把 source 里的版本 / 存档 / 模组复制到当前游戏目录。
    /// 版本目录整个搬（json + jar + natives 都在里面），同名跳过不覆盖。
    /// </summary>
    public static async Task<ImportResult> ImportAsync(
        string sourceDir, IProgress<string>? progress, CancellationToken ct,
        bool importVersions = true, bool importSaves = true, bool importMods = true)
    {
        var result = new ImportResult();
        if (!Directory.Exists(sourceDir)) throw new IOException("目录不存在：" + sourceDir);

        Paths.EnsureCore();

        if (importVersions)
        {
            var src = Path.Combine(sourceDir, "versions");
            if (Directory.Exists(src))
            {
                foreach (var dir in Directory.GetDirectories(src))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(dir);
                    var dest = Paths.VersionDir(name);

                    if (Directory.Exists(dest) && File.Exists(Path.Combine(dest, name + ".json")))
                    {
                        result.Skipped.Add(name);
                        continue;
                    }

                    progress?.Report($"复制版本 {name}…");
                    await CopyDirectoryAsync(dir, dest, ct);
                    result.Versions++;
                }
            }

            // 资源目录里缺的对象很多，按 hash 补齐比整体复制划算
            var srcAssets = Path.Combine(sourceDir, "assets", "objects");
            var destAssets = Paths.AssetsObjects;
            if (Directory.Exists(srcAssets))
            {
                progress?.Report("补齐资源文件…");
                await CopyDirectoryAsync(srcAssets, destAssets, ct, skipExisting: true);
            }

            var srcIndexes = Path.Combine(sourceDir, "assets", "indexes");
            if (Directory.Exists(srcIndexes))
                await CopyDirectoryAsync(srcIndexes, Paths.AssetsIndexes, ct, skipExisting: true);
        }

        if (importSaves)
        {
            var src = Path.Combine(sourceDir, "saves");
            if (Directory.Exists(src))
            {
                foreach (var dir in Directory.GetDirectories(src))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(dir);
                    if (Directory.Exists(Path.Combine(Paths.GameDir, "saves", name)))
                    {
                        result.Skipped.Add("存档 " + name);
                        continue;
                    }

                    progress?.Report($"复制存档 {name}…");
                    await CopyDirectoryAsync(dir, Path.Combine(Paths.GameDir, "saves", name), ct);
                    result.Saves++;
                }
            }
        }

        if (importMods)
        {
            var src = Path.Combine(sourceDir, "mods");
            if (Directory.Exists(src))
            {
                Directory.CreateDirectory(Paths.ModsDir);
                foreach (var file in Directory.GetFiles(src))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(file);
                    var dest = Path.Combine(Paths.ModsDir, name);
                    if (File.Exists(dest)) continue;
                    try
                    {
                        File.Copy(file, dest, false);
                        result.Mods++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"复制模组失败 {name}: {ex.Message}");
                    }
                }
            }
        }

        AppState.RefreshInstalled();
        Log.Info($"从 {sourceDir} 导入：{result.Summary}");
        return result;
    }

    private static async Task CopyDirectoryAsync(string source, string dest, CancellationToken ct, bool skipExisting = false)
    {
        Directory.CreateDirectory(dest);

        foreach (var file in Directory.GetFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(dest, Path.GetFileName(file));
            if (skipExisting && File.Exists(target)) continue;

            try
            {
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, 65536, ct);
            }
            catch (Exception ex)
            {
                Log.Warn($"复制失败 {file}: {ex.Message}");
            }
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            ct.ThrowIfCancellationRequested();
            await CopyDirectoryAsync(dir, Path.Combine(dest, Path.GetFileName(dir)), ct, skipExisting);
        }
    }
}
