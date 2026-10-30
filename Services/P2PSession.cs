using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>
/// P2P 联机会话：UDP 打洞 + 可靠传输，游戏流量不经过服务器。
///
/// 房主：绑定 UDP 端口 → STUN 拿到公网地址 → 发布到大厅 → 轮询访客地址 → 双向打洞
///       → 打通后把 UDP 通道接到本机游戏端口。
/// 访客：同样拿到公网地址 → 加入房间拿房主地址 → 打洞 → 打通后在本机开一个 TCP 监听，
///       游戏连 127.0.0.1:端口 就走到房主那边。
/// </summary>
public sealed class P2PSession : IDisposable
{
    public string Code { get; set; } = "";
    public bool IsHost { get; init; }
    public int LocalPort { get; set; }
    public NatInfo Nat { get; set; } = new(NatKind.Unknown, null, "");
    /// <summary>在线人数。由后台任务按 EasyTier 实际连接数刷新，人走了会减。</summary>
    public int GuestCount;

    /// <summary>房主用的游戏版本，加入前要核验本地装没装。</summary>
    public string RequiredVersion = "";

    /// <summary>房主侧：到中转节点的延迟（毫秒），大厅列表用。</summary>
    public int Latency = -1;

    /// <summary>访客侧：到房主的延迟（毫秒），-1 表示还没测出来。</summary>
    public int Ping = -1;

    /// <summary>EasyTier 通道。为 null 说明走的是旧的自研打洞（已不再使用）。</summary>
    internal EasyTierLink? Link;

    internal Task? Beat;

    public IPEndPoint? Peer { get; private set; }

    private readonly CancellationTokenSource _cts = new();
    private Socket? _socket;
    private Task? _worker;

    public CancellationToken Token => _cts.Token;
    public bool Running => _worker is { IsCompleted: false };



    internal void Attach(Socket socket, Task worker)
    {
        _socket = socket;
        _worker = worker;
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _socket?.Close(); } catch { }
        try { Link?.Stop(); } catch { }
    }

    public void Dispose() => Stop();

    internal void SetPeer(IPEndPoint peer) => Peer = peer;
}

public static class P2P
{
    /// <summary>访客本机监听的默认端口，游戏里连 127.0.0.1:这个端口。</summary>
    public const int DefaultGuestPort = 30000;

    /// <summary>打洞尝试的总时长。太短容易打架失败，太长用户等得烦。</summary>
    private static readonly TimeSpan PunchWindow = TimeSpan.FromSeconds(20);

    /// <summary>只探测 NAT，不建房间：界面上先让用户知道能不能联机。</summary>
    public static async Task<NatInfo> ProbeAsync(CancellationToken ct)
    {
        using var socket = NewSocket();
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            return await StunClient.DetectAsync(socket, ct);
        }
        catch (Exception ex)
        {
            Log.Warn("NAT 探测失败：" + ex.Message);
            return new NatInfo(NatKind.Blocked, null, ex.Message);
        }
    }

    private static Socket NewSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // 关掉 Windows 的 UDP 连接重置（打洞时会收到对方发来的"陌生"包）
            const int SioUdpConnReset = -1744830452;
            socket.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
        }
        catch { }
        return socket;
    }

    // ==================== 房主 ====================

    /// <summary>开房：发布到大厅，然后一直等访客来打洞。</summary>
    /// <summary>
    /// 开房：先在服务端登记拿房间号，再用 EasyTier 把通道立起来。
    ///
    /// 房间号就是 EasyTier 的 network-name，房间密码就是 network-secret ——
    /// 两边对不上就握不上手，等于天然的房间密码校验。
    /// </summary>
    public static async Task<P2PSession> HostAsync(string worldName, int localPort, string password,
        CancellationToken ct)
    {
        NatInfo nat;
        try
        {
            nat = await ProbeAsync(ct);
        }
        catch
        {
            nat = new NatInfo(NatKind.Unknown, null, "检测失败");
        }

        var publish = await CallApiAsync("/api/lobby/publish", new JsonObject
        {
            ["world_name"] = worldName,
            ["game_version"] = AppState.Current?.Id ?? "",
            ["nat_type"] = nat.Kind == NatKind.Cone ? "cone" : "symmetric",
            ["game_port"] = localPort,
            ["max_players"] = 8,
            ["password"] = password
        }, ct);

        if (publish.data is null)
            throw new IOException("发布房间失败：" + publish.error);

        var code = publish.data.Value.GetProperty("room").GetProperty("code").GetString() ?? "";

        Log.Info($"P2P 房主：房间 {code}，游戏端口 {localPort}，正在启动 EasyTier…");

        var link = await EasyTierLink.StartHostAsync(code, Secret(password), localPort, ct);

        var session = new P2PSession
        {
            Code = code,
            IsHost = true,
            LocalPort = localPort,
            Nat = nat,
            Link = link,
        };

        // 后台：续期房间（否则 45 秒后会被清掉）+ 按实际连接刷新人数。
        // 以前 join 时 +1、leave 时 -1，对方直接关启动器就永远减不下来。
        session.Beat = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);

                    var (peers, _) = await link.ReadPeersAsync(ct);
                    session.GuestCount = peers;
                    session.Latency = link.NodePing;

                    await CallApiAsync("/api/lobby/heartbeat", new JsonObject
                    {
                        ["code"] = code,
                        ["latency"] = link.NodePing
                    }, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, ct);

        Log.Info($"P2P 房主：EasyTier 已起来，虚拟地址 {EasyTierLink.HostIp}:{localPort}");
        return session;
    }

    private static async Task HostLoopAsync(P2PSession session, Socket socket, IPEndPoint publicAddr,
        string localAddr, CancellationToken ct)
    {
        var lastSignalId = 0;
        var lastBeat = DateTime.MinValue;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 15 秒续一次房，别让大厅里显示"已过期"
                if ((DateTime.UtcNow - lastBeat).TotalSeconds >= 15)
                {
                    lastBeat = DateTime.UtcNow;
                    var beat = await CallApiAsync("/api/lobby/heartbeat",
                        new JsonObject { ["code"] = session.Code }, ct);
                    if (beat.data is null)
                    {
                        Log.Warn("P2P 房主：续期失败，房间可能已被清理");
                    }
                }

                var signals = await CallApiAsync(
                    $"/api/lobby/signals?code={session.Code}&since={lastSignalId}", null, ct, "GET");

                if (signals.data is not null && signals.data.Value.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        lastSignalId = Math.Max(lastSignalId, item.GetProperty("id").GetInt32());

                        var guestPublic = item.GetProperty("public_addr").GetString();
                        var guestLocal = item.GetProperty("local_addr").GetString();
                        var name = item.GetProperty("user").GetString() ?? "访客";

                        if (guestPublic is null && guestLocal is null) continue;

                        Log.Info($"P2P 房主：{name} 来敲门（{guestPublic?.ToString() ?? guestLocal!.ToString()}）");

                        var peer = await PunchAsync(socket, guestPublic, guestLocal, ct);
                        if (peer is null)
                        {
                            Log.Warn($"P2P 房主：和 {name} 打洞失败");
                            continue;
                        }

                        session.SetPeer(peer);
                        session.GuestCount++;
                        Log.Info($"P2P 房主：和 {name} 打通了（{peer}）");

                        await ServeGuestAsync(session, socket, peer, session.LocalPort, name, ct);
                    }
                }

                await Task.Delay(1500, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("P2P.Host", ex);
        }
    }

    /// <summary>打洞成功后，把这条 UDP 通道接到本机游戏端口上。</summary>
    private static async Task ServeGuestAsync(P2PSession session, Socket socket, IPEndPoint peer,
        int localPort, string guestName, CancellationToken ct)
    {
        var channel = ReliableUdpChannel.Attach(socket, peer);
        using var tcp = new TcpClient();

        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, localPort, ct);
            Log.Info($"P2P 房主：已接上本地游戏端口 {localPort}，开始转发 {guestName} 的数据");
        }
        catch (Exception ex)
        {
            Log.Warn($"P2P 房主：连不上本机游戏端口 {localPort}（{ex.Message}）—— 先在游戏里点「对局域网开放」");
            channel.Close();
            return;
        }

        var stream = tcp.GetStream();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await Task.WhenAny(
            PumpTcpToChannel(stream, channel, linked),
            PumpChannelToTcp(channel, stream, linked));

        try { linked.Cancel(); } catch { }
        channel.Close();

        session.GuestCount = Math.Max(0, session.GuestCount - 1);
        Log.Info($"P2P 房主：{guestName} 断开（收 {channel.BytesIn} 字节 / 发 {channel.BytesOut} 字节）");
    }

    // ==================== 访客 ====================

    /// <summary>加入房间：打洞成功后在本机开 TCP 监听，游戏连它就等于连到房主。</summary>
    /// <summary>
    /// 加入房间：从服务端拿到房主的游戏端口，把本地端口转发过去。
    /// 连上之后游戏里填 127.0.0.1:preferredPort 即可。
    /// </summary>
    public static async Task<P2PSession> JoinAsync(string code, string password, int preferredPort,
        Action<string> status, CancellationToken ct)
    {
        status("正在查找房间…");

        var join = await CallApiAsync("/api/lobby/join", new JsonObject
        {
            ["code"] = code,
            ["password"] = password,
        }, ct);

        if (join.data is null)
            throw new IOException(join.error);

        var hostName = join.data.Value.TryGetProperty("host_name", out var hn)
            ? hn.GetString() ?? "房主"
            : "房主";

        var gamePort = join.data.Value.TryGetProperty("room", out var room)
                       && room.TryGetProperty("game_port", out var gp)
            ? gp.GetInt32()
            : 0;

        var requiredVersion = room.ValueKind == System.Text.Json.JsonValueKind.Object
                              && room.TryGetProperty("game_version", out var gv)
            ? gv.GetString() ?? ""
            : "";

        if (gamePort <= 0)
            throw new IOException("房主没有上报游戏端口。可能对方还是旧版本，让他更新之后再开房。");

        status($"正在连接 {hostName} 的世界…");

        var link = await EasyTierLink.StartGuestAsync(code, Secret(password), gamePort, preferredPort, ct);

        var (connected, ping) = await link.WaitPeerAsync(TimeSpan.FromSeconds(30), ct);
        if (!connected)
        {
            link.Stop();
            throw new IOException("没连上房主。房间号或密码可能不对，也可能房主刚把房间关了。");
        }

        var session = new P2PSession
        {
            Code = code,
            IsHost = false,
            LocalPort = preferredPort,
            Link = link,
            GuestCount = 1,
            Ping = ping,
            RequiredVersion = requiredVersion,
        };

        Log.Info($"P2P 访客：连上 {hostName}，127.0.0.1:{preferredPort} → 房主 {gamePort}，延迟 {ping}ms");

        // 在线人数和「房间里都有谁」是服务端按「90 秒内还有报到」算的。
        // 以前只在加入那一刻报一次，90 秒后人还在屋里，大厅上却已经没了。
        // 所以连上之后每 30 秒重新报一次到，Stop() 时跟着 _cts 一起停。
        session.Beat = Task.Run(async () =>
        {
            while (!session.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), session.Token);
                    await CallApiAsync("/api/lobby/join", new JsonObject
                    {
                        ["code"] = code,
                        ["password"] = password,
                    }, session.Token);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        });

        return session;
    }

    /// <summary>EasyTier 的 network-secret 至少 8 位；空密码给个固定值，两边算出来要一样。</summary>
    private static string Secret(string password) =>
        string.IsNullOrWhiteSpace(password) ? "linx-lobby" : password.PadRight(8, 'x');

    private static async Task GuestLoopAsync(P2PSession session, Socket socket, IPEndPoint peer,
        TcpListener listener, CancellationToken ct)
    {
        try
        {
            var gameClient = await listener.AcceptTcpClientAsync(ct);
            Log.Info("P2P 访客：游戏连上来了，开始转发");

            var channel = ReliableUdpChannel.Attach(socket, peer);
            var stream = gameClient.GetStream();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

            await Task.WhenAny(
                PumpTcpToChannel(stream, channel, linked),
                PumpChannelToTcp(channel, stream, linked));

            try { linked.Cancel(); } catch { }
            channel.Close();
            gameClient.Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("P2P.Guest", ex);
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    // ==================== 公共 ====================

    /// <summary>
    /// 双向打洞：同时往对方公网地址和内网地址发包，谁先回谁就是通的。
    /// 端口受限锥形要双方同时发包，所以这里是"边发边收"，不是等对方先发。
    /// </summary>
    private static async Task<IPEndPoint?> PunchAsync(Socket socket, string? publicAddr,
        string? localAddr, CancellationToken ct)
    {
        var targets = new List<IPEndPoint>(6);
        var mine = LocalIps();

        foreach (var ep in ParseEndPoints(publicAddr)) Add(ep);
        foreach (var ep in ParseEndPoints(localAddr)) Add(ep);

        // 目标里如果混进了本机地址（比如对面也装了 VMware，网段撞了），
        // 包会打回自己身上，徒增噪音还骗自己「收到包了」。
        targets.RemoveAll(t => mine.Contains(t.Address));

        void Add(IPEndPoint ep)
        {
            if (!targets.Exists(x => x.Equals(ep))) targets.Add(ep);
        }

        if (targets.Count == 0) return null;

        var deadline = DateTime.UtcNow + PunchWindow;
        var buffer = new byte[1500];
        var packet = new byte[9];
        packet[0] = 0x08;   // FlagPunch

        int sent = 0, received = 0, foreign = 0;

        // 每个目标收到几个回包：排查时一眼看出是哪条路不通
        var hits = new Dictionary<string, int>();
        foreach (var target in targets) hits[target.ToString()] = 0;

        Log.Info($"P2P：开始打洞 → {string.Join(", ", targets)}");

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            foreach (var target in targets)
            {
                try { socket.SendTo(packet, target); sent++; } catch { }
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(250);
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), wait.Token);

                if (result.ReceivedBytes >= 9)
                {
                    var from = (IPEndPoint)result.RemoteEndPoint;
                    received++;
                    // 只认我们打过的目标（防止被无关的包骗到）
                    // ★ 打洞的本质就是「包从没想到的地址来」。
                    //   对方经 NAT 转发后，源 IP/端口跟它自己报的地址经常对不上
                    //   （实测：报 192.168.21.8:54840，实际来自 192.168.1.1:54840），
                    //   这时候必须认，否则等于把唯一能用的那条路扔掉。
                    if (buffer[0] == 0x08 && !mine.Contains(from.Address))
                    {
                        var matched = targets.Find(t => t.Port == from.Port);
                        Log.Info(matched is null
                            ? $"P2P：打洞成功（发 {sent} 包 / 收 {received} 包，对端 {from} —— 实际来源与预期不同，按实际地址走）"
                            : $"P2P：打洞成功（发 {sent} 包 / 收 {received} 包，对端 {from}）");
                        return from;
                    }

                    foreign++;
                    if (foreign <= 2)
                        Log.Info($"P2P：忽略一个非打洞包（来自 {from}）");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (OperationCanceledException) { return null; }
            catch { }
        }

        var detail = string.Join("；", hits.Select(kv => $"{kv.Key} 回了 {kv.Value} 个"));
        Log.Warn($"P2P：打洞超时（发 {sent} 包 / 收到 {received} 个，其中 {foreign} 个来路不明）。各目标：{detail} —— 全是 0 的话，多半是防火墙拦了入站 UDP，或者对方已经关掉房间。");
        return null;
    }

    private static async Task PumpTcpToChannel(NetworkStream tcp, ReliableUdpChannel channel, CancellationTokenSource cts)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var read = await tcp.ReadAsync(buffer, cts.Token);
                if (read <= 0) break;
                await channel.WriteAsync(buffer.AsMemory(0, read), cts.Token);
            }
        }
        catch { }
        finally { try { cts.Cancel(); } catch { } }
    }

    private static async Task PumpChannelToTcp(ReliableUdpChannel channel, NetworkStream tcp, CancellationTokenSource cts)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var read = await channel.ReadAsync(buffer, cts.Token);
                if (read <= 0) break;
                await tcp.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                await tcp.FlushAsync(cts.Token);
            }
        }
        catch { }
        finally { try { cts.Cancel(); } catch { } }
    }

    /// <summary>依据网卡名判断是不是虚拟网卡（虚拟网卡上打洞毫无意义）。</summary>
    private static bool IsVirtualAdapter(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        var keys = new[]
        {
            "VMware", "VMnet", "VirtualBox", "Hyper-V", "vEthernet", "TAP-", "TAP ",
            "TUN", "Wintun", "WireGuard", "OpenVPN", "Docker", "WSL", "Loopback",
            "虚拟", "隧道"
        };

        foreach (var key in keys)
        {
            if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>本机当前的 IPv4 集合，用来过滤「发给自己」的打洞目标。</summary>
    private static HashSet<IPAddress> LocalIps()
    {
        var set = new HashSet<IPAddress>();

        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;

                foreach (var info in ni.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily == AddressFamily.InterNetwork)
                        set.Add(info.Address);
                }
            }
        }
        catch { }

        return set;
    }

    private static List<IPEndPoint> ParseEndPoints(string? text)
    {
        var list = new List<IPEndPoint>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ep = ParseEndPoint(part);
            if (ep is not null) list.Add(ep);
        }

        return list;
    }

    private static IPEndPoint? ParseEndPoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var idx = text.LastIndexOf(':');
        if (idx <= 0) return null;

        if (!IPAddress.TryParse(text[..idx], out var address)) return null;
        if (!int.TryParse(text[(idx + 1)..], out var port)) return null;
        if (port is <= 0 or > 65535) return null;

        // 0.0.0.0 之类的地址没意义
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return null;

        return new IPEndPoint(address, port);
    }

    /// <summary>本机内网地址（同一局域网的两个朋友可以直接用它，不用打洞）。</summary>
    /// <summary>
    /// 本机所有网卡的 IPv4 地址（都带上端口）。
    ///
    /// 以前只返回第一个，问题很大：装了 VMware / Hyper-V / Clash TUN 的机器上，
    /// 第一个往往是虚拟网卡（192.168.56.x、172.x），对方往那儿发包永远石沉大海，
    /// 表现就是「明明在同一个局域网，就是打不通」。现在全部上报，让对方逐个试。
    /// </summary>
    private static string LocalAddress(int port)
    {
        var list = new List<string>();

        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                // 虚拟网卡一律不报：VMware / Hyper-V / VirtualBox / 代理 TUN 的地址
                // 在对面机器上往往也存在（VMnet8 默认就是 192.168.118.1），
                // 发过去等于打到自己身上，日志里就全是「来路不明」的包。
                if (IsVirtualAdapter(ni.Name) || IsVirtualAdapter(ni.Description)) continue;

                foreach (var info in ni.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    var ip = info.Address.ToString();
                    if (ip.StartsWith("127.")) continue;
                    if (ip.StartsWith("169.254.")) continue;   // 自动私有地址，肯定不通
                    if (list.Contains(ip)) continue;

                    list.Add(ip);
                }
            }
        }
        catch { }

        // 真实网卡优先：192.168 / 10. / 172.16-31 打头排前面
        list.Sort((a, b) => Score(b).CompareTo(Score(a)));

        if (list.Count == 0) return "";

        return string.Join(",", list.Take(4).Select(ip => $"{ip}:{port}"));

        static int Score(string ip) =>
            ip.StartsWith("192.168.") ? 3 :
            ip.StartsWith("10.") ? 2 :
            ip.StartsWith("172.") ? 1 : 0;
    }

    private static async Task<(JsonElement? data, string error)> CallApiAsync(string path, JsonObject? body,
        CancellationToken ct, string method = "POST")
    {
        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(
                new System.Net.Http.HttpMethod(method), RemoteService.BaseUrl + path);

            var token = AppState.Settings.ServerToken;
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            if (body is not null && method != "GET")
                request.Content = new System.Net.Http.StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var resp = await Http.Client.SendAsync(request, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);

            using var doc = JsonDocument.Parse(text);
            if (!resp.IsSuccessStatusCode)
            {
                // 服务端会写清楚原因（房间不存在 / 密码不对 / 人满了），原样带上去
                var message = "服务器返回 " + (int)resp.StatusCode;
                if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                    message = err.GetString() ?? message;
                return (null, message);
            }

            return (doc.RootElement.Clone(), "");
        }
        catch (Exception ex)
        {
            Log.Warn($"大厅接口失败 {path}：{ex.Message}");
            return (null, "连不上服务器：" + ex.Message.Split('\n')[0]);
        }
    }
}
