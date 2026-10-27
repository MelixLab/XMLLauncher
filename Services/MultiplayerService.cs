using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>服务器返回的联机房间信息。</summary>
public sealed class RelayRoom
{
    public string Code { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string GameVersion { get; init; } = "";
    public string ModsHash { get; init; } = "";

    /// <summary>房主的游戏是否已经开局域网（访客加入时用）。</summary>
    public bool Ready { get; init; }
}

/// <summary>
/// 一条正在运行的联机隧道。停止时会关掉所有套接字并退出转发循环。
/// </summary>
public sealed class MultiplayerSession
{
    public string Code { get; init; } = "";
    public bool IsHost { get; init; }
    public int LocalPort { get; init; }

    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _worker;

    internal void Attach(TcpListener? listener, Task worker)
    {
        _listener = listener;
        _worker = worker;
    }

    private int _guestCount;

    /// <summary>这一轮联机里已经接入过的朋友数量（每接住一个访客 +1）。</summary>
    public int GuestCount => Volatile.Read(ref _guestCount);

    internal void CountGuest() => Interlocked.Increment(ref _guestCount);

    internal CancellationToken Token => _cts.Token;

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
    }
}

/// <summary>
/// 联机：房间 API 调用 + 本地 TCP 隧道。
///
/// 原理（和 PCL / HMCL 的做法一致）：
///   · 房主在游戏里「对局域网开放」后，MC 会在本机开一个端口（通常 25565 起的几个）；
///   · 房主把这条本地连接接到服务器的中继端口，服务器记下「这个连接码有人在等」；
///   · 访客在本机监听 127.0.0.1:30000 起的高位端口，玩家在游戏里直连它即可；
///     访客的隧道连到服务器，服务器把两边字节流对接起来 —— 等于一根网线。
/// </summary>
public static class MultiplayerService
{
    /// <summary>
    /// 扫描范围：MC 的局域网世界端口是「从 25565 起找空端口」，被占用时会往后跳，
    /// 少数版本还会跳到更高位，所以范围放宽到 25560–25620，够覆盖绝大多数情况。
    /// </summary>
    private const int LanPortStart = 25560;
    private const int LanPortEnd = 25620;

    /// <summary>访客本地监听的端口，玩家在游戏里连 127.0.0.1 就是这个。</summary>
    public const int GuestLocalPort = 30000;

    /// <summary>服务器开放的高位端口上界，本地监听不越过它。</summary>
    public const int GuestPortMax = 31000;

    // ================= 环境核对 =================

    /// <summary>
    /// 用 mods 目录里所有 jar 的「文件名 + 大小」算一个指纹。
    /// 两端指纹不一致就说明装的模组对不上，提前提示，别等进游戏才崩。
    /// </summary>
    public static string ModsHash(string versionId)
    {
        try
        {
            var isolated = AppState.Settings.Instances.TryGetValue(versionId, out var inst) && inst.Isolated;
            var modsDir = isolated ? Path.Combine(Paths.VersionDir(versionId), "mods") : Paths.ModsDir;

            if (!Directory.Exists(modsDir)) return "";

            var entries = Directory.GetFiles(modsDir)
                .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                .Select(f => $"{Path.GetFileName(f)}:{new FileInfo(f).Length}")
                .OrderBy(s => s, StringComparer.Ordinal);

            var text = string.Join("|", entries);
            if (text.Length == 0) return "";

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Log.Warn("计算模组指纹失败：" + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// 探测本机哪个端口上开着 MC 的局域网世界。
    ///
    /// 不能靠猜端口范围：新版 Minecraft 的局域网世界用的是**随机高位端口**
    /// （实测出现过 51948 这种），25565 那套只适用于老版本。
    /// 正确做法是看「java 进程在监听哪个 TCP 端口」，这里用 netstat -ano 拿到
    /// 「端口 → PID」再和 java 进程 PID 对上。
    /// </summary>
    public static int DetectLanPort()
    {
        try
        {
            var javaPids = new HashSet<int>();

            foreach (var name in new[] { "java", "javaw" })
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try { javaPids.Add(process.Id); } catch { }
                }
            }

            if (javaPids.Count == 0) return 0;

            var output = RunNetstat();
            if (string.IsNullOrEmpty(output)) return 0;

            var lines = output.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            int fallback = 0;

            foreach (var raw in lines)
            {
                var line = raw.Trim();

                if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;

                // 形如：TCP    0.0.0.0:51948    0.0.0.0:0    LISTENING    12345
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;

                var local = parts[1];
                var colon = local.LastIndexOf(':');
                if (colon <= 0) continue;
                if (!int.TryParse(local[(colon + 1)..], out var port)) continue;
                if (port <= 1024) continue;

                if (!int.TryParse(parts[^1], out var pid)) continue;
                if (!javaPids.Contains(pid)) continue;

                // 我们自己开的监听端口（访客本地 25565、联机中继）要排除
                if (port == GuestLocalPort) continue;

                // 优先取非回环绑定的那个（局域网世界会绑 0.0.0.0）
                if (!local.StartsWith("127.0.0.1", StringComparison.Ordinal)) return port;

                if (fallback == 0) fallback = port;
            }

            return fallback;
        }
        catch (Exception ex)
        {
            Log.Warn("探测局域网端口失败：" + ex.Message);
            return 0;
        }
    }

    /// <summary>跑一次 netstat 拿监听列表（Windows 自带，不需要额外权限）。</summary>
    private static string RunNetstat()
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p TCP")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc is null) return "";

            var text = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return text;
        }
        catch (Exception ex)
        {
            Log.Warn("netstat 执行失败：" + ex.Message);
            return "";
        }
    }

    // ================= 房间 API =================

    public static async Task<RelayRoom> CreateRoomAsync(string versionId, CancellationToken ct)
    {
        // JsonObject：匿名类型在 Native AOT 下没法序列化
        var (ok, doc, error) = await LinxAccountService.CallAsync("/api/relay/room", new JsonObject
        {
            ["game_version"] = GameVersionOf(versionId),
            ["mods_hash"] = ModsHash(versionId)
        }, ct);

        if (!ok || doc is null) throw new IOException($"创建房间失败：{error}");

        var root = doc.RootElement;
        return new RelayRoom
        {
            Code = Str(root, "code"),
            Host = Str(root, "relay_host"),
            Port = Int(root, "relay_port"),
            GameVersion = GameVersionOf(versionId),
            ModsHash = ModsHash(versionId)
        };
    }

    public static async Task<RelayRoom> JoinRoomAsync(string code, CancellationToken ct)
    {
        var (ok, doc, error) = await LinxAccountService.CallAsync("/api/relay/join", new JsonObject { ["code"] = code }, ct);
        if (!ok || doc is null) throw new IOException(error);

        var root = doc.RootElement;
        return new RelayRoom
        {
            Code = Str(root, "code"),
            Host = Str(root, "relay_host"),
            Port = Int(root, "relay_port"),
            GameVersion = Str(root, "game_version"),
            ModsHash = Str(root, "mods_hash"),
            Ready = root.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True
        };
    }

    /// <summary>房主每 5 分钟换一次码（旧码立即失效）。</summary>
    public static async Task<string?> RefreshRoomAsync(string currentCode, CancellationToken ct)
    {
        var (ok, doc, error) = await LinxAccountService.CallAsync("/api/relay/room/refresh",
            new JsonObject { ["code"] = currentCode }, ct);

        if (!ok || doc is null)
        {
            Log.Warn("刷新联机码失败：" + error);
            return null;
        }

        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    /// <summary>告诉服务器"我的游戏有没有开好局域网"，访客加入时会看到。</summary>
    public static async Task ReportStatusAsync(bool ready, CancellationToken ct)
    {
        await LinxAccountService.CallAsync("/api/relay/status", new JsonObject { ["ready"] = ready }, ct);
    }

    private static string GameVersionOf(string versionId)
    {
        var local = AppState.Installed.FirstOrDefault(v => v.Id == versionId);
        return string.IsNullOrWhiteSpace(local?.BaseVersion) ? versionId : local!.BaseVersion;
    }

    // ================= 隧道 =================

    /// <summary>房主：连服务器并等待访客；访客来了就把数据转到本地局域网端口。</summary>
    public static MultiplayerSession StartHost(RelayRoom room, int lanPort)
    {
        var session = new MultiplayerSession { Code = room.Code, IsHost = true, LocalPort = lanPort };

        var worker = Task.Run(async () =>
        {
            // 一条中继连接只能服务一个访客（服务器把它和访客那条对接完就收摊）。
            // 所以这里必须循环：访客断开后立刻重新挂回中继，等下一个朋友。
            // 早先只连一次，结果是一次「对局域网开放」只能进一个人。
            while (!session.Token.IsCancellationRequested)
            {
                var retryDelay = TimeSpan.FromMilliseconds(400);

                try
                {
                    using var upstream = await DialRelayAsync(room, "host", session.Token);

                    using var local = new TcpClient();
                    await local.ConnectAsync(IPAddress.Loopback, lanPort, session.Token);

                    session.CountGuest();
                    Log.Info($"联机（房主）：已连上服务器，转发到本地 {lanPort}");
                    await BridgeAsync(upstream.GetStream(), local.GetStream(), session.Token);
                    Log.Info("联机（房主）：这段隧道结束，重新挂回中继等下一个访客");
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 游戏关了 / 端口变了都会走到这里：慢一点再试，别把 CPU 打满
                    Log.Warn("联机（房主）重新挂载失败：" + ex.Message);
                    retryDelay = TimeSpan.FromSeconds(2);
                }

                try { await Task.Delay(retryDelay, session.Token); }
                catch (OperationCanceledException) { return; }
            }
        }, session.Token);

        session.Attach(null, worker);
        return session;
    }

    /// <summary>
    /// 访客侧：在本机开一个 TCP 监听，玩家在游戏里连「127.0.0.1:端口」就走这条隧道。
    ///
    /// 为什么需要这个本地监听：Minecraft 客户端不支持自定义协议头，
    /// 没法直接连我们的中继（中继要先做一行动 JSON 握手）。所以本机起一个"假服务器"，
    /// 游戏连它，它再连中继，等于把网线接到了朋友那边。
    ///
    /// 端口从 30000 开始试（服务器只对外开放 30000-31000 这段高位端口），
    /// 被占用就顺延到 30001、30002…，并把最终端口显示给用户。
    /// 同机部署时中继自己就占着 30000，这里会自动跳到下一个。
    /// </summary>
    public static MultiplayerSession StartGuest(RelayRoom room, int preferredPort = GuestLocalPort)
    {
        TcpListener? listener = null;
        int port = 0;

        for (int candidate = preferredPort; candidate <= Math.Min(preferredPort + 10, GuestPortMax); candidate++)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, candidate);
                probe.Start();
                listener = probe;
                port = candidate;
                break;
            }
            catch (SocketException)
            {
                // 端口被占，试下一个
            }
        }

        if (listener is null)
            throw new IOException($"从 {preferredPort} 起连续 11 个端口都被占用了，请关掉占用的程序再试。");

        var session = new MultiplayerSession { Code = room.Code, IsHost = false, LocalPort = port };
        var boundListener = listener;
        var boundPort = port;

        var worker = Task.Run(async () =>
        {
            try
            {
                Log.Info($"联机（访客）：本地监听 127.0.0.1:{boundPort}");

                while (!session.Token.IsCancellationRequested)
                {
                    using var incoming = await boundListener.AcceptTcpClientAsync(session.Token);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var upstream = await DialRelayAsync(room, "guest", session.Token);

                            // 服务器会先回一行：OK 表示房主在线，NO_HOST 表示还没来
                            var line = await ReadLineAsync(upstream.GetStream(), session.Token);
                            if (!line.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Warn("联机（访客）：房主还没就绪（" + line.Trim() + "）");
                                return;
                            }

                            await BridgeAsync(incoming.GetStream(), upstream.GetStream(), session.Token);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            Log.Warn("联机（访客）连接失败：" + ex.Message);
                        }
                    }, session.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn("联机（访客）监听失败：" + ex.Message);
            }
            finally
            {
                try { boundListener.Stop(); } catch { }
            }
        }, session.Token);

        session.Attach(boundListener, worker);
        return session;
    }

    private static async Task<TcpClient> DialRelayAsync(RelayRoom room, string role, CancellationToken ct)
    {
        var client = new TcpClient();
        await client.ConnectAsync(room.Host, room.Port, ct);

        var handshake = new JsonObject
        {
            ["role"] = role,
            ["code"] = room.Code,
            ["token"] = AppState.Settings.ServerToken ?? ""
        }.ToJsonString() + "\n";

        var bytes = Encoding.UTF8.GetBytes(handshake);
        await client.GetStream().WriteAsync(bytes, ct);
        return client;
    }

    /// <summary>双向转发；任一方向断开就整体收摊。</summary>
    private static async Task BridgeAsync(NetworkStream a, NetworkStream b, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task Pump(NetworkStream src, NetworkStream dst)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    var read = await src.ReadAsync(buffer, linked.Token);
                    if (read <= 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                    await dst.FlushAsync(linked.Token);
                }
            }
            catch (Exception) { /* 对端断开 */ }
            finally
            {
                try { linked.Cancel(); } catch { }
            }
        }

        await Task.WhenAll(Pump(a, b), Pump(b, a));
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 64)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read <= 0) break;
            if (one[0] == (byte)'\n') break;
            sb.Append((char)one[0]);
        }
        return sb.ToString();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
}
