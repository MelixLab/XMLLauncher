using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>一个可以下载的 Java 运行时包。</summary>
public sealed class JavaPackage
{
    public int Major { get; init; }
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Sha256 { get; init; }
    public long Size { get; init; }
}

/// <summary>
/// Java 运行时自动获取：本机找不到版本要求的 Java 时，从 Adoptium（Temurin）下 zip 解到
/// 启动器自己的 runtime 目录，JavaService 下次扫描就能看到。
/// 这是「干净机器上装完启动器就能直接开游戏」的关键一环。
/// </summary>
public static class JavaInstaller
{
    private const string Api = "https://api.adoptium.net/v3/assets/latest/{0}/hotspot" +
                               "?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";

    /// <summary>查一下这个主版本能下哪个包（不下载）。</summary>
    public static async Task<JavaPackage?> FindAsync(int major, CancellationToken ct)
    {
        try
        {
            var text = await Http.Client.GetStringAsync(string.Format(Api, major), ct);

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;

            var first = doc.RootElement[0];
            if (!first.TryGetProperty("binary", out var binary) ||
                !binary.TryGetProperty("package", out var package))
                return null;

            var link = package.TryGetProperty("link", out var l) ? l.GetString() ?? "" : "";
            var name = package.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var size = package.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            var sha = package.TryGetProperty("checksum", out var c) ? c.GetString() : null;

            if (string.IsNullOrEmpty(link)) return null;

            // 只要 zip；msi 需要静默安装，不适合启动器代劳
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var zipLink = link.Replace(".msi", ".zip", StringComparison.OrdinalIgnoreCase);
                if (!link.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
            }

            return new JavaPackage { Major = major, Name = name, Url = link, Size = size, Sha256 = sha };
        }
        catch (Exception ex)
        {
            Log.Warn($"查询 Java {major} 下载地址失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 确保本机有满足要求的 Java：有就直接返回，没有就下载 + 解压 + 重新扫描。
    /// progress(说明, 0~1)。
    /// </summary>
    public static async Task<JavaInfo?> EnsureAsync(int major, Action<string, double>? progress, CancellationToken ct)
    {
        var existing = JavaService.Pick(major);
        if (existing is not null) return existing;

        var package = await FindAsync(major, ct);
        if (package is null)
        {
            Log.Warn($"Java {major}：没有可用的下载源");
            return null;
        }

        Directory.CreateDirectory(Paths.RuntimeDir);
        Directory.CreateDirectory(Paths.CacheDir);

        var zipPath = Path.Combine(Paths.CacheDir, package.Name);
        var targetDir = Path.Combine(Paths.RuntimeDir, "java" + major);

        long total = package.Size > 0 ? package.Size : 1;
        long done = 0;

        progress?.Invoke($"下载 Java {major}（{GameInstaller.FormatSize(package.Size)}）…", 0);

        await Http.DownloadToFileAsync(package.Url, zipPath, n =>
        {
            done += n;
            var frac = Math.Clamp((double)done / total, 0, 1);
            progress?.Invoke($"下载 Java {major}：{GameInstaller.FormatSize(done)} / {GameInstaller.FormatSize(package.Size)}", frac * 0.9);
        }, ct);

        progress?.Invoke("解压 Java 运行时…", 0.92);

        await Task.Run(() =>
        {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            Directory.CreateDirectory(targetDir);
            ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);
        }, ct);

        progress?.Invoke("检查 Java 运行时…", 0.98);

        var home = FindJavaHome(targetDir);
        if (home is null)
        {
            Log.Warn("Java 解压完成但没找到 bin/javaw.exe");
            return null;
        }

        try { File.Delete(zipPath); } catch { }

        JavaService.Detect(true);

        var info = JavaService.Detect().FirstOrDefault(j =>
            j.Home.Equals(home, StringComparison.OrdinalIgnoreCase)) ?? JavaService.Pick(major);

        Log.Info($"Java {major} 已就绪：{info?.Home ?? home}");
        return info;
    }

    /// <summary>在解压出来的目录里找 bin/javaw.exe 所在的那一层。</summary>
    private static string? FindJavaHome(string root)
    {
        bool IsHome(string dir) =>
            File.Exists(Path.Combine(dir, "bin", "javaw.exe")) ||
            File.Exists(Path.Combine(dir, "bin", "java.exe"));

        if (IsHome(root)) return root;

        try
        {
            foreach (var d in Directory.GetDirectories(root))
            {
                if (IsHome(d)) return d;
                foreach (var d2 in Directory.GetDirectories(d))
                {
                    if (IsHome(d2)) return d2;
                    foreach (var d3 in Directory.GetDirectories(d2))
                        if (IsHome(d3)) return d3;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("查找 Java 目录失败：" + ex.Message);
        }

        return null;
    }
}
