using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace LinxLauncher.Services;

/// <summary>
/// 基于 EasyTier 的联机通道。
///
/// 和之前自研打洞的区别：EasyTier 有成熟的中继回退，打不通也不会干等着超时。
/// 双方各自起一个 easytier-core 进程，用我们自己的节点撮合：
///
///   房主：固定虚拟 IP 10.114.114.114，把本机游戏端口加进白名单
///   访客：把 127.0.0.1:{本地端口} 转发到 10.114.114.114:{房主游戏端口}
///
/// 访客进游戏直接连 localhost 就行，不用记 IP。
/// </summary>
public sealed class EasyTierLink : IDisposable
{
    /// <summary>我们自己部署的节点（香港那台，和启动器服务端是同一台机器）。</summary>
    public const string Node = "tcp://103.118.245.82:11010";

    public const string HostIp = "10.114.114.114";
    public const string GuestIp = "10.114.114.115";

    /// <summary>
    /// 联机组件的下载源，按顺序试。
    ///
    /// Gitee 最快（实测 2 MB/s，15 秒下完），但要防它哪天改版或限速；
    /// 自家服务器是兜底（跨境只有 39 KB/s，慢，但一定在）。
    /// </summary>
    static readonly string[] Sources =
    {
        "https://gitee.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-x86_64-v2.6.4.zip",
        "https://xml.xiguastudio.top/dl/easytier-win.zip",
    };

    static readonly string Root = Path.Combine(Paths.AppData, "EasyTier");

    static string CoreExe => Path.Combine(Root, "easytier-core.exe");
    static string CliExe => Path.Combine(Root, "easytier-cli.exe");

    Process? _core;
    int _rpcPort;

    public string Code { get; private init; } = "";
    public bool IsHost { get; private init; }

    /// <summary>访客：本地转发端口；房主：游戏端口。</summary>
    public int LocalPort { get; private init; }

    public int GamePort { get; private init; }

    /// <summary>到中转节点的延迟（毫秒），-1 表示还没测出来。大厅列表用这个画信号条。</summary>
    public int NodePing { get; private set; } = -1;

    // ---------- 组件准备 ----------

    public static bool IsInstalled => File.Exists(CoreExe) && File.Exists(CliExe);

    /// <summary>
    /// 首次联机时下载 EasyTier（约 32 MB，走 Gitee，国内 2 MB/s 左右）。
    /// 从官方 GitHub Release 下载只有几 KB/s，所以固定用 Gitee 镜像。
    /// </summary>
    public static async Task<string> EnsureInstalledAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (IsInstalled) return Root;

        Directory.CreateDirectory(Root);

        var zip = Path.Combine(Paths.CacheDir, "easytier.zip");

        progress?.Report("正在下载联机组件（约 32 MB）…");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            Exception? last = null;

            foreach (var url in Sources)
            {
                try
                {
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(zip);
                    await src.CopyToAsync(dst, ct);

                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                    progress?.Report($"这个源不通（{new Uri(url).Host}），换下一个…");
                }
            }

            if (last is not null) throw new IOException("联机组件下载失败：" + last.Message);
        }

        progress?.Report("正在解压联机组件…");

        var tmp = Path.Combine(Paths.CacheDir, "easytier_unzip");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        ZipFile.ExtractToDirectory(zip, tmp);

        // 压缩包里套了一层 easytier-windows-x86_64，把它里面的文件搬上来
        var inner = Directory.GetDirectories(tmp).FirstOrDefault() ?? tmp;
        foreach (var file in Directory.GetFiles(inner))
        {
            File.Copy(file, Path.Combine(Root, Path.GetFileName(file)), true);
        }

        try { File.Delete(zip); } catch { }
        try { Directory.Delete(tmp, true); } catch { }

        if (!IsInstalled)
            throw new IOException("联机组件解压后不完整，可能是下载被中断了，重试一次。");

        progress?.Report("联机组件就绪");
        return Root;
    }

    // ---------- 启动 ----------

    public static async Task<EasyTierLink> StartHostAsync(string code, string secret, int gamePort, CancellationToken ct)
    {
        var link = new EasyTierLink
        {
            Code = code,
            IsHost = true,
            LocalPort = gamePort,
            GamePort = gamePort,
        };

        await link.LaunchAsync(
            $"-i {HostIp} --tcp-whitelist={gamePort} --udp-whitelist={gamePort}",
            code, secret, ct);

        return link;
    }

    public static async Task<EasyTierLink> StartGuestAsync(string code, string secret, int hostGamePort,
        int localPort, CancellationToken ct)
    {
        var link = new EasyTierLink
        {
            Code = code,
            IsHost = false,
            LocalPort = localPort,
            GamePort = hostGamePort,
        };

        var forward =
            $" --port-forward tcp://127.0.0.1:{localPort}/{HostIp}:{hostGamePort}" +
            $" --port-forward udp://127.0.0.1:{localPort}/{HostIp}:{hostGamePort}";

        await link.LaunchAsync($"-i {GuestIp}{forward}", code, secret, ct);

        return link;
    }

    async Task LaunchAsync(string roleArgs, string code, string secret, CancellationToken ct)
    {
        if (!IsInstalled)
            throw new IOException("联机组件还没准备好。");

        // 注意：这里以前会 KillAll()，结果「开了房再点加入」会把房主进程杀掉。
        // 现在端口都随机分配，各实例互不干扰，不需要清场。
        _rpcPort = PickFreePort();
        var listen = PickFreePort();

        var log = Path.Combine(Paths.AppData, "easytier.log");

        // network-name 就是房间号，network-secret 就是房间密码 —— 两边对不上就握不上手
        var args =
            $"--network-name={code} --network-secret={secret}" +
            $" --listeners {listen} --rpc-portal {_rpcPort} --private-mode true" +
            $" --hostname={(_rpcPort % 2 == 0 ? "S" : "C")}-{Environment.MachineName.ToLowerInvariant()}" +
            $" {roleArgs} -p {Node}";

        // EasyTier 要创建 TUN 网卡，必须管理员权限；没有的话提权启动（会弹一次 UAC）
        _core = Launch(Path.Combine(Root, "easytier-core.exe"), args, log);

        await Task.Delay(1200, ct);

        if (_core is null || _core.HasExited)
        {
            // 退出时没清干净的话，上一个实例还占着 10.114.114.x 这块虚拟地址，
            // 新实例会直接退出 —— 表现就是「上次还好好的，这次启动联机失败」。
            // 这里清一次再试，别把残留问题丢给用户。
            Log.Warn("EasyTier 首次启动失败，清理残留实例后重试");
            CleanupStale(0);
            await Task.Delay(800, ct);

            _core = Launch(Path.Combine(Root, "easytier-core.exe"), args, log);
            await Task.Delay(1200, ct);

            if (_core is null || _core.HasExited)
            {
                var tail = ReadTail(log, 6);
                throw new IOException("联机组件启动失败。\n" + tail);
            }

            Log.Info("清理残留后 EasyTier 启动成功");
        }
    }

    /// <summary>
    /// 杀掉残留的 easytier-core（保留 keepPid 指定的那个）。
    /// 虚拟网卡是全局资源，旧实例不死新的就起不来。
    /// </summary>
    static void CleanupStale(int keepPid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Get-Process easytier-core -ErrorAction SilentlyContinue | "
                            + $"Where-Object {{ $_.Id -ne {keepPid} }} | Stop-Process -Force\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var p = Process.Start(psi);
            p?.WaitForExit(4000);
        }
        catch { }
    }

    static Process? Launch(string exe, string args, string logFile)
    {
        var cmd = $"cd /d \"{Root}\" && \"{Path.GetFileName(exe)}\" {args} > \"{logFile}\" 2>&1";

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + cmd,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (!IsElevated)
        {
            psi.Verb = "runas";
        }

        try
        {
            return Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户点了「否」
            throw new OperationCanceledException("联机需要管理员权限（要创建虚拟网卡），你在 UAC 弹窗里点了取消。");
        }
    }

    // ---------- 状态 ----------

    /// <summary>等对端连上。返回 (是否连上, 对端延迟毫秒)。</summary>
    public async Task<(bool Connected, int Ping)> WaitPeerAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, ct);

            if (_core is { HasExited: true })
                throw new IOException("联机组件意外退出了。");

            var (peers, ping) = await ReadPeersAsync(ct);
            if (peers > 0) return (true, ping);
        }

        return (false, -1);
    }

    /// <summary>读当前对端数量和延迟（不含节点自己）。</summary>
    public async Task<(int Peers, int Ping)> ReadPeersAsync(CancellationToken ct = default)
    {
        if (!File.Exists(CliExe)) return (0, -1);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = CliExe,
                Arguments = $"-p 127.0.0.1:{_rpcPort} -o json peer",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return (0, -1);

            var json = await p.StandardOutput.ReadToEndAsync(ct);
            var errText = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            if (string.IsNullOrWhiteSpace(json))
            {
                // 以前这里静默返回 0，界面就一直显示「延迟未知」，无从查起
                Log.Warn($"EasyTier CLI 没有输出（rpc {_rpcPort}）：{errText.Trim()}");
                return (0, -1);
            }

            // 输出可能是数组，也可能被包成 {"peers":[...]}
            var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty("peers", out var inner) ? inner : default;

            if (arr.ValueKind != JsonValueKind.Array) return (0, -1);

            int peers = 0;
            double best = double.MaxValue;

            foreach (var item in arr.EnumerateArray())
            {
                var host = item.TryGetProperty("hostname", out var h) ? h.GetString() ?? "" : "";

                if (host.StartsWith("PublicServer", StringComparison.OrdinalIgnoreCase))
                {
                    // 节点自己的延迟单独记下来：房主报给大厅，别人看得到这个房间快不快
                    var nv = ReadPing(item);
                    if (nv > 0) NodePing = (int)Math.Round(nv);
                    continue;
                }

                peers++;

                var v = ReadPing(item);
                if (v > 0 && v < best) best = v;
            }

            return (peers, best == double.MaxValue ? -1 : (int)Math.Round(best));
        }
        catch
        {
            return (0, -1);
        }
    }

    // ---------- 停止 ----------

    public void Stop()
    {
        try
        {
            // 只杀自己这个实例：同一台机器可能同时有别人的（或上一个）会话在跑
            if (_core is { HasExited: false })
            {
                KillProcess(_core.Id);
            }
        }
        catch { }

        _core = null;
    }

    /// <summary>精确杀掉一个进程。EasyTier 是提权起的，所以这里也得提权执行。</summary>
    public static void KillProcess(int pid)
    {
        if (pid <= 0) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c taskkill /F /PID {pid} > nul 2>&1",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };

            if (!IsElevated) psi.Verb = "runas";

            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { }
    }

    /// <summary>
    /// 杀掉所有 easytier-core。进程是提权起的，普通权限杀不掉，
    /// 所以这里也要提权执行——同一个 cmd 通常不会再弹 UAC。
    /// </summary>
    public static void KillAll()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c taskkill /F /IM easytier-core.exe > nul 2>&1",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };

            if (!IsElevated) psi.Verb = "runas";

            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { }
    }

    public void Dispose() => Stop();

    // ---------- 杂项 ----------

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>以管理员身份重启自己，用于「联机需要管理员权限」时一键提权。</summary>
    public static bool RelaunchAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
            };

            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从 peer 的 JSON 里取延迟。EasyTier 不同版本用过 lat_ms / lat / latency 几个键名，
    /// 全试一遍，取到就行。
    /// </summary>
    static double ReadPing(JsonElement item)
    {
        foreach (var key in new[] { "lat_ms", "lat", "latency", "rtt_ms", "latency_ms" })
        {
            if (!item.TryGetProperty(key, out var v)) continue;

            // EasyTier 输出的是字符串（"267.89"），本机那一行还是 "-"
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;
        }

        return -1;
    }

    static int PickFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static string ReadTail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var all = File.ReadAllLines(path);
            return string.Join("\n", all.TakeLast(lines));
        }
        catch
        {
            return "";
        }
    }
}
