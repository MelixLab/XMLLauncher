using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>更新下载进度：字节数、总量、瞬时速度、当前阶段。</summary>
public readonly record struct UpdateProgress(long Done, long Total, double BytesPerSecond, string Phase);

/// <summary>
/// 客户端自更新。
///
/// 发版方式是「只发一个独立 exe」——单文件包里已经把整套运行时打进去了，
/// 所以更新就是「下新的那个 exe → 替换掉旧的 → 重新打开」。
/// 替换不能在自己进程里做（Windows 不允许覆盖正在运行的 exe），
/// 因此下载完写一个批处理：等本进程退出 → 覆盖 → 启动新 exe → 删掉自己。
/// </summary>
public static class UpdateService
{
    public static string UpdateDir => Path.Combine(Paths.AppData, "update");

    /// <summary>
    /// 要替换的那个 exe。
    /// 单文件版里当前进程是 %LOCALAPPDATA%\LinxLauncher\runtime\...\LinxLauncher.WinUI.exe，
    /// 用户手里真正该被替换的是外面的壳，外壳启动我们的时候会通过环境变量告诉我们。
    /// </summary>
    public static string SelfExePath
    {
        get
        {
            var shell = Environment.GetEnvironmentVariable("XML_LAUNCHER_SELF");
            if (!string.IsNullOrWhiteSpace(shell) && File.Exists(shell)) return shell;

            return Environment.ProcessPath ?? "";
        }
    }

    /// <summary>把一个 /download/xxx 这样的站内地址补成完整 URL。</summary>
    public static string ResolveUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new IOException("服务器没有给下载地址，请联系管理员。");

        return url.StartsWith('/') ? RemoteService.BaseUrl + url : url;
    }

    /// <summary>下载新版本到 %APPDATA%\LinxLauncher\update，返回本地路径。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var url = ResolveUrl(info.DownloadUrl);

        var ext = ".exe";
        try
        {
            var candidate = Path.GetExtension(new Uri(url).AbsolutePath);
            if (!string.IsNullOrEmpty(candidate)) ext = candidate;
        }
        catch { /* 用默认扩展名 */ }

        Directory.CreateDirectory(UpdateDir);
        var dest = Path.Combine(UpdateDir, $"XMLLauncher-{info.Latest}{ext}");
        var tmp = dest + ".part";
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

        using var resp = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        long done = 0;
        var sw = Stopwatch.StartNew();
        long lastReport = -1000;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
        {
            var buffer = new byte[65536];
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;

                var ms = sw.ElapsedMilliseconds;
                if (ms - lastReport >= 120)
                {
                    lastReport = ms;
                    var speed = ms > 0 ? done * 1000.0 / ms : 0;
                    progress?.Report(new UpdateProgress(done, total, speed, "正在下载新版本"));
                }
            }
        }

        // 有的服务器不给 Content-Length，下载完再按实际大小补一次
        progress?.Report(new UpdateProgress(done, total > 0 ? total : done, 0, "下载完成"));

        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
        return dest;
    }

    /// <summary>校验大小与 sha256；没问题返回 null，有问题返回给用户看的说明。</summary>
    public static string? Verify(string file, UpdateInfo info)
    {
        if (!File.Exists(file)) return "下载的文件不见了，请重试。";

        var length = new FileInfo(file).Length;
        if (length < 1024 * 1024) return "下载到的文件不完整（只有 " + length + " 字节），请重试。";

        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            using var fs = File.OpenRead(file);
            using var sha = SHA256.Create();
            var actual = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
            if (!string.Equals(actual, info.Sha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                return "新版本校验失败（文件可能被改动或没下完整），请重试。";
        }

        return null;
    }

    /// <summary>
    /// 直接把新版本换上去，然后让调用方退出。
    ///
    /// 不用批处理/cmd 中转：之前那版在无控制台的进程里调 timeout / tasklist，
    /// 实测会挂住，更新就永远卡在「重启中」。Windows 允许重命名正在运行的 exe，
    /// 所以「旧文件改名 → 新文件就位 → 启动新版本」三步在进程内就能做完，稳得多。
    /// </summary>
    public static bool ApplyUpdateNow(string newFile, out string message)
    {
        message = "";

        try
        {
            var target = SelfExePath;
            if (string.IsNullOrEmpty(target) || !File.Exists(target))
            {
                message = "找不到启动器本体，无法自动替换。";
                return false;
            }
            if (!File.Exists(newFile))
            {
                message = "下载的新版本不见了，请重试。";
                return false;
            }

            var dir = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                message = "启动器所在目录不可用。";
                return false;
            }

            // 先探写权限：放到 Program Files 之类的地方会失败，早点说清楚
            try
            {
                var probe = Path.Combine(dir, ".xmlprobe-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            catch
            {
                message = "启动器所在目录没有写入权限，请把它放到桌面或普通文件夹再更新。";
                return false;
            }

            var backup = target + ".old";
            if (File.Exists(backup))
            {
                try { File.Delete(backup); } catch { }
            }

            // 1) 旧文件改名：正在运行的 exe 也允许改名
            try
            {
                File.Move(target, backup);
            }
            catch (Exception ex)
            {
                message = "替换失败（旧文件改不了名）：" + ex.Message;
                return false;
            }

            // 2) 新文件就位
            try
            {
                File.Move(newFile, target);
            }
            catch (Exception ex)
            {
                try { File.Move(backup, target); } catch { }
                message = "替换失败（新文件放不进去）：" + ex.Message;
                return false;
            }

            // 3) 拉起新版本
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    WorkingDirectory = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                message = "新版本已就位，但没能自动启动：" + ex.Message + "，请手动打开 " + target;
                return false;
            }

            Log.Info($"更新完成：{target}（旧文件保留为 .old，下次启动自动清理）");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Update", ex);
            message = ex.Message;
            return false;
        }
    }

    /// <summary>启动时清理上次更新留下的 .old 文件。</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            var target = SelfExePath;
            if (string.IsNullOrEmpty(target)) return;
            var backup = target + ".old";
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch { /* 还占着就下次再说 */ }
    }
}
