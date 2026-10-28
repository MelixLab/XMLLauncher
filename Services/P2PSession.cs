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
    public int GuestCount;
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
    public static async Task<P2PSession> HostAsync(string worldName, int localPort, string password, CancellationToken ct)
    {
        var session = new P2PSession { IsHost = true, LocalPort = localPort };
        var socket = NewSocket();
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        int udpPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var localAddr = LocalAddress(udpPort);

        var nat = await StunClient.DetectAsync(socket, ct);
        session.Nat = nat;

        if (nat.Public is null)
        {
            socket.Close();
            throw new IOException("拿不到公网地址：UDP 可能被防火墙或校园网拦了。" + nat.Detail);
        }

        var publish = await CallApiAsync("/api/lobby/publish", new JsonObject
        {
            ["world_name"] = worldName,
            ["game_version"] = AppState.Current?.Id ?? "",
            ["nat_type"] = nat.Kind == NatKind.Cone ? "cone" : "symmetric",
            ["public_addr"] = nat.Public.ToString(),
            ["local_addr"] = localAddr,
            ["max_players"] = 8,
            ["password"] = password
        }, ct);

        if (publish is null) { socket.Close(); throw new IOException("发布房间失败，先登录 XML 账号。"); }

        session.Code = publish.Value.GetProperty("room").GetProperty("code").GetString() ?? "";
        Log.Info($"P2P 房主：房间 {session.Code}，UDP {udpPort}，公网 {nat.Public}");

        var worker = Task.Run(() => HostLoopAsync(session, socket, nat.Public!, localAddr, ct), ct);
        session.Attach(socket, worker);
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
                    if (beat is null)
                    {
                        Log.Warn("P2P 房主：续期失败，房间可能已被清理");
                    }
                }

                var signals = await CallApiAsync(
                    $"/api/lobby/signals?code={session.Code}&since={lastSignalId}", null, ct, "GET");

                if (signals is not null && signals.Value.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        lastSignalId = Math.Max(lastSignalId, item.GetProperty("id").GetInt32());

                        var guestPublic = ParseEndPoint(item.GetProperty("public_addr").GetString());
                        var guestLocal = ParseEndPoint(item.GetProperty("local_addr").GetString());
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
    public static async Task<P2PSession> JoinAsync(string code, string password, int preferredPort,
        Action<string> status, CancellationToken ct)
    {
        var session = new P2PSession { IsHost = false, Code = code };
        var socket = NewSocket();
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        int udpPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var localAddr = LocalAddress(udpPort);

        status("正在检测网络…");
        var nat = await StunClient.DetectAsync(socket, ct);
        session.Nat = nat;

        if (nat.Public is null)
        {
            socket.Close();
            throw new IOException("拿不到公网地址：UDP 可能被防火墙或校园网拦了。" + nat.Detail);
        }

        status("正在联系房主…");
        var join = await CallApiAsync("/api/lobby/join", new JsonObject
        {
            ["code"] = code,
            ["password"] = password,
            ["public_addr"] = nat.Public.ToString(),
            ["local_addr"] = localAddr
        }, ct);

        if (join is null) { socket.Close(); throw new IOException("加入失败：房间不存在，或者密码不对。"); }

        var hostPublic = ParseEndPoint(join.Value.GetProperty("host_public").GetString());
        var hostLocal = ParseEndPoint(join.Value.GetProperty("host_local").GetString());
        var hostName = join.Value.GetProperty("host_name").GetString() ?? "房主";

        status($"正在和 {hostName} 打洞…");
        var peer = await PunchAsync(socket, hostPublic, hostLocal, ct);
        if (peer is null)
        {
            socket.Close();
            throw new IOException(
                "打洞失败：双方网络都是对称型 NAT 时打不通。\n" +
                "可以试试让房主用手机热点，或者换一边有公网 IP 的网络。");
        }

        session.SetPeer(peer);
        Log.Info($"P2P 访客：和 {hostName} 打通了（{peer}）");

        // 本机开一个 TCP 监听，游戏连它
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        session.LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var worker = Task.Run(() => GuestLoopAsync(session, socket, peer, listener, ct), ct);
        session.Attach(socket, worker);

        status($"隧道已打通（{peer}），本地端口 {session.LocalPort}");
        return session;
    }

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
    private static async Task<IPEndPoint?> PunchAsync(Socket socket, IPEndPoint? publicAddr,
        IPEndPoint? localAddr, CancellationToken ct)
    {
        var targets = new List<IPEndPoint>(2);
        if (publicAddr is not null) targets.Add(publicAddr);
        if (localAddr is not null && !localAddr.Equals(publicAddr)) targets.Add(localAddr);
        if (targets.Count == 0) return null;

        var deadline = DateTime.UtcNow + PunchWindow;
        var buffer = new byte[1500];
        var packet = new byte[9];
        packet[0] = 0x08;   // FlagPunch

        int sent = 0, received = 0, foreign = 0;
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
                    var known = targets.Exists(t =>
                        t.Address.Equals(from.Address) && t.Port == from.Port);
                    if (known)
                    {
                        Log.Info($"P2P：打洞成功（发 {sent} 包 / 收 {received} 包）");
                        return from;
                    }
                    foreign++;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (OperationCanceledException) { return null; }
            catch { }
        }

        Log.Warn($"P2P：打洞超时（发 {sent} 包 / 收到 {received} 个包，其中 {foreign} 个来路不明）—— "
                 + "如果收到的是 0，多半是防火墙拦了入站 UDP，允许一下启动器再试。");
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
    private static string LocalAddress(int port)
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var info in ni.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = info.Address.ToString();
                    if (ip.StartsWith("127.")) continue;
                    return $"{ip}:{port}";
                }
            }
        }
        catch { }
        return "";
    }

    private static async Task<JsonElement?> CallApiAsync(string path, JsonObject? body, CancellationToken ct,
        string method = "POST")
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
            if (!resp.IsSuccessStatusCode) return null;
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            Log.Warn($"大厅接口失败 {path}：{ex.Message}");
            return null;
        }
    }
}
