using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using LinxLauncher.Controls;
using LinxLauncher.Views;

namespace LinxLauncher.Services;

/// <summary>
/// 一次「启动游戏」的完整生命周期：
/// 进度窗口 → 进程起来后启动器缩到托盘 → 盯着进程 → 非 0 退出时弹独立报错窗口。
/// 页面只管调 <see cref="StartAsync"/>，不用关心这些收尾动作。
/// </summary>
public static class GameSession
{
    private static LaunchJob? _job;

    /// <summary>当前是否有启动过程在跑。</summary>
    public static bool IsLaunching => _job is { IsRunning: true };

    public static LaunchJob? Current => _job;

    public static async Task StartAsync(string versionId, XamlRoot? root, string? quickJoin = null)
    {
        if (IsLaunching)
        {
            MainWindow.Instance?.Toast("正在启动中，请稍候…");
            return;
        }

        var job = new LaunchJob(versionId);
        _job = job;

        // 配置在 UI 线程读好再交给后台线程（正版账号会在这里顺手续期）
        var opt = await BuildOptionsAsync(versionId, job);
        opt.QuickJoinAddress = quickJoin;

        LaunchProgressDialog? dialog = null;
        if (root is not null)
        {
            try
            {
                dialog = new LaunchProgressDialog(job) { XamlRoot = root };
                _ = dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                // 已经有别的对话框开着时这里会抛，进度窗口只是锦上添花，继续启动。
                Log.Error("LaunchDialog", ex);
                dialog = null;
            }
        }

        // 没有满足要求的 Java 就先自动装一个（干净机器上这是唯一的硬门槛）
        try
        {
            var requiredJava = await AppState.Launcher.RequiredJavaAsync(versionId, default);

            if (JavaService.Pick(requiredJava, opt.JavaHome) is null)
            {
                Log.Info($"本机没有 Java {requiredJava}，开始自动下载");
                job.SetProgress($"本机没有 Java {requiredJava}，正在自动下载运行时…", 0.2);

                var java = await JavaInstaller.EnsureAsync(requiredJava,
                    (text, frac) => App.UI?.TryEnqueue(() =>
                        job.SetProgress(text, 0.2 + Math.Clamp(frac, 0, 1) * 0.55)),
                    default);

                if (java is not null)
                {
                    opt.JavaHome = java.Home;
                    job.SetProgress($"Java {java.Major} 已就绪，继续启动…", 0.8);
                }
                else
                {
                    throw new IOException($"本机没有 Java {requiredJava}，自动下载也失败了。请手动安装 Java {requiredJava} 后重试。");
                }
            }
        }
        catch (IOException ex)
        {
            job.Fail(ex.Message);
            HideDialog(dialog);
            ShowError(new LaunchErrorInfo
            {
                Title = "启动失败",
                Summary = ex.Message,
                Advice = "可以去 eclipse.org/adoptium 下载 Temurin 对应版本；装好后再启动一次即可。",
                VersionId = versionId
            });
            _job = null;
            return;
        }

        job.ClearOverrideFraction();

        LaunchResult result;
        try
        {
            var progress = new Progress<LaunchProgress>(p => App.UI?.TryEnqueue(() => job.Apply(p)));
            result = await Task.Run(() => AppState.Launcher.LaunchAsync(versionId, opt, default, progress));
        }
        catch (Exception ex)
        {
            Log.Error("Launch", ex);
            var message = FirstLine(ex.Message);
            job.Fail(message);

            await Task.Delay(900); // 让用户看清是卡在哪一步失败的
            HideDialog(dialog);

            ShowError(new LaunchErrorInfo
            {
                Title = "启动失败",
                Summary = message,
                Advice = Diagnose(ex.ToString()),
                VersionId = versionId,
                LogFile = job.LogFile
            });

            _job = null;
            return;
        }

        // 进程起来了不等于游戏起来了：还要等游戏窗口出现、资源加载完，这期间进度窗口留着
        job.WaitingForWindow();

        var windowUp = await WaitForGameWindowAsync(result.Process, job);
        if (!windowUp)
        {
            Log.Warn($"等待游戏窗口超时（{versionId}）");
            job.Fail("游戏进程已经起来，但等了 8 分钟也没看到游戏窗口。可能卡在加载、或者被其它窗口挡住了。");
            HideDialog(dialog);

            // 这里有两种情况，都必须弹报错窗口把日志摊开：
            //   1) 进程已经退出 —— 一启动就崩，窗口根本来不及出现（最常见）；
            //   2) 进程还活着但一直不显示窗口 —— 显卡/驱动/被挡住。
            // 以前这里只弹一个一闪而过的小通知，用户根本不知道崩在哪。
            var exited = true;
            var exitCode = -1;
            try
            {
                exited = result.Process?.HasExited ?? true;
                if (exited && result.Process is not null) exitCode = result.Process.ExitCode;
            }
            catch { exited = true; exitCode = -1; }

            var tail = ReadTail(result.LogFile, 40);
            var crash = AnalyzeCrash(tail, result.GameDir);

            ShowError(new LaunchErrorInfo
            {
                Title = exited ? "游戏异常退出" : "游戏没有出现窗口",
                Summary = exited
                    ? $"《{versionId}》的进程以退出码 {exitCode} 结束，游戏窗口没来得及出现。"
                    : $"《{versionId}》的进程还在运行，但等了 8 分钟也没看到游戏窗口。",
                CrashDetail = crash,
                Advice = Diagnose((crash ?? "") + "\n" + tail),
                VersionId = versionId,
                ExitCode = exited ? exitCode : null,
                LogFile = result.LogFile,
                LogTail = tail
            });

            WatchExit(result, versionId);
            return;
        }

        // 窗口出现后再等资源加载完（Sound engine started 之类），最多 45 秒
        await WaitForGameLoadedAsync(result.LogFile, job, TimeSpan.FromMinutes(5));

        job.Complete(result.LogFile);
        await Task.Delay(700);   // 五步全绿再收窗口，别一闪而过
        HideDialog(dialog);
        await Task.Delay(150);

        // 游戏起来了：启动器让位，只留托盘图标
        TrayService.Show($"XML Minecraft Launcher · {versionId}");
        TrayService.Notify("游戏已启动",
            $"《{versionId}》已经起来了，启动器缩到托盘（点托盘图标可以打开）。");
        TrayService.HideMainWindow();

        WatchExit(result, versionId);
    }

    /// <summary>轮询游戏进程的主窗口，直到窗口真正出现（标题稳定约 1 秒）或超时。</summary>
    private static async Task<bool> WaitForGameWindowAsync(Process? proc, LaunchJob job)
    {
        if (proc is null) return true;

        // 新版游戏 + 整合包在机械硬盘上能有一两分钟的黑屏期，给足 8 分钟
        var deadline = DateTime.UtcNow.AddMinutes(8);
        string? lastTitle = null;
        int stable = 0;
        int tick = 0;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (proc.HasExited) return false;
                proc.Refresh();
            }
            catch (Exception ex)
            {
                Log.Error("WaitWindow", ex);
                return false;
            }

            var hwnd = proc.MainWindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                var title = proc.MainWindowTitle;
                if (title == lastTitle) stable++;
                else { stable = 0; lastTitle = title; }

                if (stable >= 3) return true;
            }

            if (++tick % 4 == 0)
            {
                job?.UpdateWaitDetail($"正在等待游戏窗口出现…（已等待 {tick * 0.3:0} 秒）");
            }

            await Task.Delay(300);
        }

        return false;
    }

    /// <summary>等日志里出现「加载完成」的标记，超时就按已经加载好处理。</summary>
    private static async Task WaitForGameLoadedAsync(string? logFile, LaunchJob job, TimeSpan timeout)
    {
        // Minecraft 1.13+ 在资源加载完后会打印这几行之一；老版本也有 OpenAL 那条
        // 出现任意一条就认为资源已经加载完（不同版本打印的进度不一样）
        string[] markers =
        {
            "Sound engine started",
            "OpenAL initialized",
            "Reloading ResourceManager",
            "Backend library:",
            "Created: 1024x512",
            "Started integrated server"
        };

        var start = DateTime.UtcNow;
        var deadline = start + timeout;
        int tick = 0;

        while (DateTime.UtcNow < deadline)
        {
            var tail = ReadTail(logFile, 120);
            if (tail is not null && markers.Any(m => tail.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return;

            if (++tick % 3 == 0)
            {
                var elapsed = (int)(DateTime.UtcNow - start).TotalSeconds;
                job?.UpdateWaitDetail($"游戏窗口已出现，正在等待资源加载完成…（已等待 {elapsed} 秒）");
            }

            await Task.Delay(400);
        }
    }

    private static async Task<LaunchOptions> BuildOptionsAsync(string versionId, LaunchJob job)
    {
        var s = AppState.Settings;

        // 优先用该实例自己的配置（版本详情里可以单独改内存 / 分辨率 / 全屏）
        var inst = s.Instances.TryGetValue(versionId, out var i) ? i : null;

        // 正版还是离线：正版会在这里检查令牌，快过期就自动续期
        var (name, uuid, token) = await AccountService.ResolveForLaunchAsync(default);
        job?.UpdateWaitDetail(AccountService.IsMicrosoft
            ? $"使用正版账号 {name} 启动"
            : $"使用离线账号 {name} 启动");

        return new LaunchOptions
        {
            PlayerName = name,
            Uuid = uuid,
            AccessToken = token,
            MemoryMb = inst?.MemoryMb ?? s.MemoryMb,
            JavaHome = s.JavaHome,
            ExtraJvmArgs = s.ExtraJvmArgs,
            ExtraGameArgs = s.ExtraGameArgs,
            Fullscreen = inst?.Fullscreen ?? s.Fullscreen,
            WindowWidth = inst?.Width ?? s.GameWidth,
            WindowHeight = inst?.Height ?? s.GameHeight,
            Isolated = inst?.Isolated == true
        };
    }

    /// <summary>盯住游戏进程：正常退出就把启动器叫回来，非 0 退出再弹报错窗口。</summary>
    private static void WatchExit(LaunchResult result, string versionId)
    {
        var proc = result.Process;
        if (proc is null)
        {
            _job = null;
            return;
        }

        proc.EnableRaisingEvents = true;

        // 进程可能在我们订阅之前就已经退出（一启动就崩的情况），
        // 这时 Exited 永远不会再触发 —— 之前就是这样，报错窗口一直没机会出现。
        var alreadyExited = false;
        try { alreadyExited = proc.HasExited; } catch { alreadyExited = true; }

        if (alreadyExited)
        {
            ReportExit(proc, result, versionId);
            return;
        }

        proc.Exited += (_, _) => ReportExit(proc, result, versionId);

        Log.Info($"开始监视游戏进程（{versionId}）");
    }

    /// <summary>游戏进程退出后的统一处理：非 0 弹报错窗口，0 就把启动器叫回来。</summary>
    private static void ReportExit(Process proc, LaunchResult result, string versionId)
    {
        int code = 0;
        try { code = proc.ExitCode; }
        catch { code = -1; }

        // 日志是流式写的，进程刚退时最后几行可能还没落盘，等一小会儿再读
        var tail = ReadTail(result.LogFile, 40);

        App.UI?.TryEnqueue(() =>
        {
            TrayService.Hide();

            if (code != 0)
            {
                TrayService.ShowMainWindow();

                var crash = AnalyzeCrash(tail, result.GameDir);

                ShowError(new LaunchErrorInfo
                {
                    Title = "游戏异常退出",
                    Summary = $"《{versionId}》的进程以退出码 {code} 结束，游戏没有正常关闭。",
                    CrashDetail = crash,
                    Advice = Diagnose((crash ?? "") + "\n" + tail),
                    VersionId = versionId,
                    ExitCode = code,
                    LogFile = result.LogFile,
                    LogTail = tail
                });
            }
            else
            {
                // 正常退出：把启动器从托盘叫回来
                TrayService.ShowMainWindow();
                MainWindow.Instance?.Toast($"《{versionId}》已退出");
            }

            _job = null;
        });
    }

    private static void HideDialog(Controls.LaunchProgressDialog? dialog)
    {
        if (dialog is null) return;

        try { dialog.Hide(); }
        catch (Exception ex) { Log.Error("LaunchDialog", ex); }
    }

    private static void ShowError(LaunchErrorInfo info)
    {
        if (App.UI is null)
        {
            LaunchErrorWindow.ShowError(info);
            return;
        }

        App.UI.TryEnqueue(() => LaunchErrorWindow.ShowError(info));
    }

    /// <summary>读日志最后 N 行（游戏还在写这个文件，所以必须带 FileShare.ReadWrite）。</summary>
    private static string? ReadTail(string? file, int lines)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;

        try
        {
            if (!File.Exists(file)) return null;

            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);

            var all = reader.ReadToEnd()
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (all.Count == 0) return null;
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Count - lines)));
        }
        catch (Exception ex)
        {
            Log.Error("LaunchLog", ex);
            return null;
        }
    }

    /// <summary>
    /// 解析具体崩溃信息：优先看 Minecraft 自己写的 crash-reports/*.txt（有 Description 和 Caused by），
    /// 没有再退回日志尾部里第一处异常行。解析不出来返回 null。
    /// </summary>
    private static string? AnalyzeCrash(string? logTail, string? gameDir = null)
    {
        var parts = new List<string>();

        var report = ReadLatestCrashReport(string.IsNullOrWhiteSpace(gameDir) ? Paths.GameDir : gameDir!);
        if (!string.IsNullOrWhiteSpace(report)) parts.Add(report!);

        var fromLog = FirstExceptionLine(logTail);
        if (!string.IsNullOrWhiteSpace(fromLog) &&
            !parts.Any(p => p.Contains(fromLog!, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("日志异常：" + fromLog);
        }

        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
    }

    /// <summary>最近 10 分钟内生成的那份崩溃报告（游戏刚崩，多半就是它）。</summary>
    private static string? ReadLatestCrashReport(string gameDir)
    {
        try
        {
            var dir = Path.Combine(gameDir, "crash-reports");
            if (!Directory.Exists(dir)) return null;

            var file = new DirectoryInfo(dir).GetFiles("*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (file is null) return null;
            if ((DateTime.UtcNow - file.LastWriteTimeUtc).TotalMinutes > 10) return null;

            string[] lines;
            using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                lines = reader.ReadToEnd().Split('\n');
            }

            string? desc = null;
            string? ex = null;
            string? caused = null;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (desc is null && line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                {
                    desc = Clip(line["Description:".Length..].Trim(), 160);
                    continue;
                }

                if (ex is null &&
                    line.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase))
                {
                    ex = Clip(line, 200);
                    continue;
                }

                if (ex is null &&
                    !line.StartsWith("at ", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("#", StringComparison.Ordinal) &&
                    (line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Error", StringComparison.OrdinalIgnoreCase)))
                {
                    ex = Clip(line, 200);
                    continue;
                }

                if (ex is not null && caused is null &&
                    line.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase))
                {
                    caused = Clip(line, 200);
                }

                if (desc is not null && ex is not null && caused is not null) break;
            }

            if (desc is null && ex is null) return null;

            var sb = new StringBuilder("崩溃报告：");
            sb.Append(file.Name);
            if (desc is not null) sb.Append($"{Environment.NewLine}描述：{desc}");
            if (ex is not null) sb.Append($"{Environment.NewLine}异常：{ex}");
            if (caused is not null) sb.Append($"{Environment.NewLine}根因：{caused}");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log.Error("CrashReport", ex);
            return null;
        }
    }

    /// <summary>日志里第一处真正的异常行（跳过堆栈的 at / ... N more）。</summary>
    private static string? FirstExceptionLine(string? tail)
    {
        if (string.IsNullOrWhiteSpace(tail)) return null;

        foreach (var raw in tail.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 8) continue;
            if (line.StartsWith("at ", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("...", StringComparison.Ordinal)) continue;

            if (line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Caused by:", StringComparison.OrdinalIgnoreCase))
            {
                return Clip(line, 220);
            }
        }

        return null;
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>把常见的崩法翻译成人话，认不出来就返回 null。</summary>
    private static string? Diagnose(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text;

        if (t.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase))
            return "Java 版本太低了。这个版本需要更高的 Java，去「设置 → Java 运行时」里换一个再启动。";

        // JVM 连堆都申请不到（多个游戏实例/内存占用过高时很常见），
        // 日志里是 "insufficient memory for the Java Runtime Environment"，不是普通 OOM
        if (t.Contains("insufficient memory for the Java Runtime Environment", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Native memory allocation (mmap) failed", StringComparison.OrdinalIgnoreCase))
            return "内存不够：Java 连启动用的内存都申请不到。先把其他 Minecraft / 占内存的程序关掉，再到「设置 → 内存分配」降一点（例如 2048 MB）后重试。";

        if (t.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase))
            return "内存不够。去「设置 → 内存分配」调大一些，或关掉其它占内存的程序。";

        if (t.Contains("Could not find or load main class", StringComparison.OrdinalIgnoreCase))
            return "找不到主类，版本文件大概率不完整。建议在版本库里重装这个版本。";

        if (t.Contains("NoClassDefFoundError", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("ClassNotFoundException", StringComparison.OrdinalIgnoreCase))
            return "缺少依赖库，通常是下载没下全。建议在版本库里重装这个版本。";

        if (t.Contains("GLFW error", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Pixel format not accelerated", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Failed to create window", StringComparison.OrdinalIgnoreCase))
            return "显卡驱动或 OpenGL 初始化失败。更新显卡驱动后再试，笔记本记得用独显运行。";

        if (t.Contains("Missing or unsupported mandatory dependencies", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Incompatible mod set", StringComparison.OrdinalIgnoreCase))
            return "模组之间不兼容或缺少前置。先把最近装的模组禁用掉再试。";

        if (t.Contains("需要 Java") && t.Contains("没有满足要求的运行时"))
            return "本机没有这个版本需要的 Java。先在「设置 → Java 运行时」里指定，或去装一个。";

        return null;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "未知错误";
        var line = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? text.Trim();
        if (line.Length > 240) line = line[..240] + "…";
        return line;
    }
}
