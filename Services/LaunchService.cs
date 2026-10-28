using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinxLauncher.Models;

namespace LinxLauncher.Services;

public sealed class LaunchOptions
{
    public string PlayerName { get; set; } = "Steve";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "0";
    public int MemoryMb { get; set; } = 4096;
    public string? JavaHome { get; set; }
    public string? ExtraJvmArgs { get; set; }
    public string? ExtraGameArgs { get; set; }
    public int WindowWidth { get; set; } = 925;
    public int WindowHeight { get; set; } = 520;
    public bool Fullscreen { get; set; }

    /// <summary>版本隔离：游戏目录用 versions/&lt;id&gt;，存档 / mods / 配置都跟着版本走。</summary>
    public bool Isolated { get; set; }

    /// <summary>
    /// 直连地址（如 127.0.0.1:25565）。非空时用 1.20+ 的快速加入参数，
    /// 进游戏直接连到这个服务器，不用玩家自己去「添加服务器」。
    /// </summary>
    public string? QuickJoinAddress { get; set; }
}

/// <summary>启动过程中的一个阶段。Step 对应 LaunchJob.Captions 的下标（0–4）。</summary>
public readonly record struct LaunchProgress(int Step, string Detail);

public sealed class LaunchResult
{
    public Process? Process { get; set; }
    public string CommandLine { get; set; } = "";
    public string? LogFile { get; set; }

    /// <summary>这次实际用的游戏目录（版本隔离时是 versions/&lt;id&gt;）。</summary>
    public string GameDir { get; set; } = "";
}

public sealed class LaunchService
{
    private readonly VersionService _versions = new();

    /// <summary>这个版本需要哪个大版本的 Java（用于提前准备运行时）。</summary>
    public async Task<int> RequiredJavaAsync(string versionId, CancellationToken ct)
    {
        var resolved = await _versions.ResolveAsync(versionId, ct);
        return resolved.Detail.JavaVersion?.MajorVersion ?? 8;
    }

    public async Task<LaunchResult> LaunchAsync(string versionId, LaunchOptions opt, CancellationToken ct,
        IProgress<LaunchProgress>? progress = null)
    {
        Paths.EnsureCore();

        void Step(int index, string detail) => progress?.Report(new LaunchProgress(index, detail));

        Step(0, $"读取版本 {versionId} 的信息…");

        var resolved = await _versions.ResolveAsync(versionId, ct);
        var detail = resolved.Detail;

        Step(0, $"检查 {resolved.BaseId}.jar 与版本文件…");

        var clientJar = resolved.ClientJar
                        ?? throw new IOException($"缺少 {resolved.BaseId}.jar，请先安装原版 {resolved.BaseId}");

        var requiredJava = detail.JavaVersion?.MajorVersion ?? 8;
        Step(1, $"这个版本需要 Java {requiredJava}，正在本机查找…");

        var java = JavaService.Pick(requiredJava, opt.JavaHome)
                   ?? throw new IOException(
                       $"这个版本需要 Java {requiredJava}，本机没有满足要求的运行时。请先安装 Java {requiredJava}，或在「设置」里指定它的路径。");

        Step(1, $"使用 Java {java.Major} · {java.Home}");

        // 版本隔离：整个 .minecraft 等价目录换成 versions/<id>，
        // 存档 / mods / options.txt 就都跟着这个版本走了，多版本互不干扰。
        var gameDir = opt.Isolated ? Paths.VersionDir(versionId) : Paths.GameDir;
        Directory.CreateDirectory(gameDir);
        var assetsIndexName = detail.AssetIndex?.Id ?? detail.Assets ?? "legacy";
        var nativesDir = Paths.VersionNatives(versionId);
        Directory.CreateDirectory(nativesDir);

        // ---- classpath ----
        Step(2, "整理依赖库与启动参数…");
        var cp = new List<string>();
        foreach (var lib in detail.Libraries ?? new List<Library>())
        {
            if (!VersionService.IsAllowed(lib.Rules)) continue;

            // 只有「旧式 natives」（version json 里有 natives 映射、要解压到指定目录那种）才跳过；
            // 1.19+ 的 natives 是普通 jar（名字里带 :natives-windows），LWJGL 需要它们留在 classpath 上
            // 自己提取 dll —— 跳过就会报 Failed to locate library: lwjgl.dll。
            if (lib.Natives is { Count: > 0 }) continue;

            var artifact = lib.Downloads?.Artifact;
            var path = artifact?.Path;
            if (string.IsNullOrEmpty(path))
            {
                // 只有 URL 的库（极少）：按 maven 命名推路径
                var guessed = GameInstaller.MavenToPath(lib.Name);
                if (string.IsNullOrEmpty(guessed)) continue;
                var full = Path.Combine(Paths.LibrariesDir, guessed.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) cp.Add(full);
                continue;
            }

            var local = Path.Combine(Paths.LibrariesDir, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(local)) cp.Add(local);
        }
        cp.Add(clientJar);

        var classpath = string.Join(";", cp);

        // ---- 参数替换表 ----
        var uuid = string.IsNullOrEmpty(opt.Uuid) ? OfflineUuid(opt.PlayerName) : opt.Uuid;

        string Subst(string s) => s
            .Replace("${auth_player_name}", opt.PlayerName)
            .Replace("${auth_uuid}", uuid)
            .Replace("${auth_access_token}", opt.AccessToken)
            .Replace("${auth_session}", opt.AccessToken)
            .Replace("${clientid}", "linx")
            .Replace("${auth_xuid}", "0")
            .Replace("${user_type}", "legacy")
            .Replace("${user_properties}", "{}")
            .Replace("${version_name}", versionId)
            .Replace("${version_type}", string.IsNullOrEmpty(detail.Type) ? "release" : detail.Type)
            .Replace("${game_directory}", gameDir)
            .Replace("${game_assets}", Paths.AssetsVirtual)
            .Replace("${assets_root}", Paths.AssetsDir)
            .Replace("${assets_index_name}", assetsIndexName)
            .Replace("${natives_directory}", nativesDir)
            .Replace("${launcher_name}", "XMLLauncher")
            .Replace("${launcher_version}", "1.0.0")
            .Replace("${classpath}", classpath)
            .Replace("${classpath_separator}", ";")
            .Replace("${library_directory}", Paths.LibrariesDir)
            .Replace("${primary_jar}", clientJar)
            .Replace("${version_json}", Paths.VersionJson(versionId));

        // ---- JVM 参数 ----
        var jvm = new List<string>();
        int mem = Math.Clamp(opt.MemoryMb, 512, 65536);
        jvm.Add($"-Xmx{mem}M");
        jvm.Add($"-Xms{Math.Max(512, mem / 4)}M");
        jvm.Add("-XX:+UseG1GC");
        jvm.Add("-XX:+ParallelRefProcEnabled");
        jvm.Add("-XX:MaxGCPauseMillis=200");
        jvm.Add("-XX:+UnlockExperimentalVMOptions");
        jvm.Add("-XX:G1NewSizePercent=20");
        jvm.Add("-XX:G1MaxNewSizePercent=40");

        if (detail.Arguments?.Jvm is { Count: > 0 })
        {
            foreach (var el in detail.Arguments.Jvm)
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s)) jvm.Add(Subst(s));
                }
                else if (el.ValueKind == JsonValueKind.Object && RulesPass(el))
                {
                    if (el.TryGetProperty("value", out var v))
                        foreach (var one in ReadStringOrArray(v)) jvm.Add(Subst(one));
                }
            }
        }
        else
        {
            jvm.Add($"-Djava.library.path={nativesDir}");
            jvm.Add("-Dminecraft.launcher.brand=XMLLauncher");
            jvm.Add("-Dminecraft.launcher.version=1.0.0");
            jvm.Add("-cp");
            jvm.Add(classpath);
        }

        if (!string.IsNullOrWhiteSpace(opt.ExtraJvmArgs))
            jvm.AddRange(SplitArgs(opt.ExtraJvmArgs!));

        // ---- 游戏参数 ----
        var game = new List<string>();
        if (detail.Arguments?.Game is { Count: > 0 })
        {
            foreach (var el in detail.Arguments.Game)
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrEmpty(s)) game.Add(Subst(s));
                }
                else if (el.ValueKind == JsonValueKind.Object && RulesPass(el))
                {
                    if (el.TryGetProperty("value", out var v))
                        foreach (var one in ReadStringOrArray(v)) game.Add(Subst(one));
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(detail.MinecraftArguments))
        {
            foreach (var a in SplitArgs(detail.MinecraftArguments!))
                game.Add(Subst(a));
        }
        else
        {
            game.AddRange(new[] { "--username", opt.PlayerName, "--version", versionId, "--gameDir", gameDir,
                "--assetsDir", Paths.AssetsDir, "--assetIndex", assetsIndexName, "--uuid", uuid,
                "--accessToken", opt.AccessToken, "--userType", "legacy", "--versionType", "release" });
        }

        if (opt.Fullscreen) game.Add("--fullscreen");

        // --quickPlayMultiplayer 是 1.20 才有的。低版本传了会直接被忽略，
        // 用户以为「点了就自动进服」，结果停在主菜单，所以这里先判断版本。
        if (!string.IsNullOrWhiteSpace(opt.QuickJoinAddress) && SupportsQuickPlay(versionId))
        {
            game.Add("--quickPlayMultiplayer");
            game.Add(opt.QuickJoinAddress!);
        }
        if (!string.IsNullOrWhiteSpace(opt.ExtraGameArgs))
            game.AddRange(SplitArgs(opt.ExtraGameArgs!));

        // ---- 组装 ----
        var mainClass = detail.MainClass ?? "net.minecraft.client.main.Main";
        var allArgs = new List<string>(jvm) { mainClass };
        allArgs.AddRange(game);

        // ---- 启动 ----
        Step(3, "正在启动 java 进程…");
        var consoleJava = Path.Combine(java.Home, "bin", "java.exe");
        var exe = File.Exists(consoleJava) ? consoleJava : java.Exe;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = gameDir,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in allArgs) psi.ArgumentList.Add(a);

        var logDir = Path.Combine(Paths.AppData, "logs");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"game-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var logWriter = new StreamWriter(logFile, false, Encoding.UTF8) { AutoFlush = true };

        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { try { logWriter.WriteLine(e.Data); } catch { } } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { try { logWriter.WriteLine(e.Data); } catch { } } };
        proc.Exited += (_, _) => { try { logWriter.Dispose(); } catch { } };

        if (!proc.Start())
            throw new IOException("游戏进程启动失败");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        Step(4, "游戏进程已运行，等待主窗口出现…");

        Log.Info($"启动 {versionId} · Java {java.Major} · 内存 {mem}M");

        return new LaunchResult
        {
            Process = proc,
            CommandLine = BuildCommandPreview(exe, allArgs),
            LogFile = logFile,
            GameDir = gameDir
        };
    }

    public static string OfflineUuid(string name)
    {
        // 离线模式 UUID：和原版一致的 name-based 生成方式
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash).ToString("N");
    }

    private static bool RulesPass(JsonElement obj)
    {
        if (!obj.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return true;

        bool allowed = false;
        foreach (var r in rules.EnumerateArray())
        {
            if (r.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.Object)
            {
                if (os.TryGetProperty("name", out var n) && n.GetString() is { } osName &&
                    !osName.Equals("windows", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            if (r.TryGetProperty("features", out _)) continue;

            var action = r.TryGetProperty("action", out var a) ? a.GetString() : "allow";
            allowed = string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static IEnumerable<string> ReadStringOrArray(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrEmpty(s)) yield return s;
        }
        else if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in v.EnumerateArray())
            {
                var s = e.GetString();
                if (!string.IsNullOrEmpty(s)) yield return s;
            }
        }
    }

    /// <summary>按空格切分参数，支持双引号包裹</summary>
    public static List<string> SplitArgs(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    private static string BuildCommandPreview(string exe, List<string> args)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(exe).Append('"');
        foreach (var a in args)
        {
            if (a.Length > 160) { sb.Append(" …"); continue; }
            sb.Append(' ').Append(a.Contains(' ') ? $"\"{a}\"" : a);
        }
        return sb.ToString();
    }

    /// <summary>1.20 起客户端才认 --quickPlayMultiplayer。</summary>
    public static bool SupportsQuickPlay(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return false;

        var m = System.Text.RegularExpressions.Regex.Match(versionId, @"(\d+)\.(\d+)");
        if (!m.Success) return false;

        var major = int.Parse(m.Groups[1].Value);
        var minor = int.Parse(m.Groups[2].Value);
        return major > 1 || (major == 1 && minor >= 20);
    }
}
