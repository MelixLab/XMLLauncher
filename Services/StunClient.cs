using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

public enum NatKind
{
    Unknown,
    /// <summary>锥形（全锥 / 受限 / 端口受限都归这一档，打洞成功率都不低）</summary>
    Cone,
    /// <summary>对称型：每个目的地址分配不同端口，打洞基本打不通</summary>
    Symmetric,
    /// <summary>没有从 STUN 拿到地址（多半是完全连不上外网或 UDP 被拦）</summary>
    Blocked
}

public readonly record struct NatInfo(NatKind Kind, IPEndPoint? Public, string Detail)
{
    public string Describe() => Kind switch
    {
        NatKind.Cone => "锥形网络（可以打洞）",
        NatKind.Symmetric => "对称型网络（打洞很难成功）",
        NatKind.Blocked => "拿不到公网地址（UDP 可能被拦）",
        _ => "未知"
    };
}

/// <summary>
/// 极简 STUN 客户端（RFC 5389）：只做 Binding Request，读 XOR-MAPPED-ADDRESS。
///
/// 没有引第三方库，因为需要的功能就一个：告诉我「我这个 UDP 端口在公网长什么样」。
/// 顺便靠「同一本地端口问两台不同服务器，公网端口变不变」判断是不是对称型 NAT ——
/// 这决定了值不值得让用户去等打洞。
/// </summary>
public static class StunClient
{
    private const uint MagicCookie = 0x2112A442;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingResponse = 0x0101;
    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrXorMappedAddress = 0x0020;

    /// <summary>国内可用的公共 STUN，按顺序试，拿到一个就够。</summary>
    public static readonly (string Host, int Port)[] Servers =
    {
        ("stun.miwifi.com", 3478),
        ("stun.chat.bilibili.com", 3478),
        ("stun.hitv.com", 3478),
        ("stun.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
    };

    public static async Task<IPEndPoint?> QueryAsync(string host, int port, Socket socket, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch
        {
            return null;
        }

        var target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (target is null) return null;

        var request = BuildRequest(out var transactionId);

        try
        {
            await socket.SendToAsync(request, SocketFlags.None, new IPEndPoint(target, port), ct);
        }
        catch
        {
            return null;
        }

        var buffer = new byte[1500];
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(600));

            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;   // 没等到就再发一轮
            }
            catch
            {
                return null;
            }

            var parsed = Parse(buffer.AsSpan(0, result.ReceivedBytes), transactionId);
            if (parsed is not null) return parsed;

            // 没解析出来（可能是别的包），再发一次请求继续等
            try
            {
                await socket.SendToAsync(request, SocketFlags.None, new IPEndPoint(target, port), ct);
            }
            catch { return null; }
        }

        return null;
    }

    /// <summary>
    /// 检测本机 NAT 类型。传进来的 socket 必须已经绑定了本地端口，而且之后要拿它去打洞 ——
    /// 因为公网映射是「本地端口 + 目的地址」绑定的，换端口就得重新测。
    /// </summary>
    public static async Task<NatInfo> DetectAsync(Socket socket, CancellationToken ct)
    {
        IPEndPoint? first = null;
        string firstName = "";

        foreach (var (host, port) in Servers)
        {
            var mapped = await QueryAsync(host, port, socket, ct);
            if (mapped is null) continue;

            first = mapped;
            firstName = $"{host}:{port}";
            break;
        }

        if (first is null)
            return new NatInfo(NatKind.Blocked, null, "所有 STUN 服务器都没响应");

        // 换一台服务器再问一次：公网端口变了就是对称型
        foreach (var (host, port) in Servers)
        {
            if ($"{host}:{port}" == firstName) continue;

            var second = await QueryAsync(host, port, socket, ct);
            if (second is null) continue;

            if (!second.Address.Equals(first.Address))
                return new NatInfo(NatKind.Symmetric, first, $"两个服务器看到 {first.Address} / {second.Address}");

            return new NatInfo(NatKind.Cone, first, $"两台服务器都看到 {first.Address}");
        }

        // 只有一个服务器可用：没法判断对称型，按锥形处理（打洞时再验证）
        return new NatInfo(NatKind.Cone, first, $"只有 {firstName} 可用，按锥形处理");
    }

    /// <summary>
    /// 把大端读出来的 32 位地址转成 IPAddress。
    /// 不能用 new IPAddress(uint)：它按主机字节序解释，x86 上会把
    /// 112.9.125.101 读成 101.125.9.112 —— 打洞就会打到不存在的地址上。
    /// </summary>
    private static IPAddress XorToAddress(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }

    private static byte[] BuildRequest(out byte[] transactionId)
    {
        transactionId = RandomNumberGenerator.GetBytes(12);

        var buffer = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0), BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), MagicCookie);
        transactionId.CopyTo(buffer.AsSpan(8));
        return buffer;
    }

    private static IPEndPoint? Parse(ReadOnlySpan<byte> data, byte[] transactionId)
    {
        if (data.Length < 20) return null;

        var type = BinaryPrimitives.ReadUInt16BigEndian(data);
        if (type != BindingResponse && type != 0x0103) return null;   // 只认成功响应

        var bodyLength = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var cookie = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (cookie != MagicCookie) return null;
        if (!data.Slice(8, 12).SequenceEqual(transactionId)) return null;

        var offset = 20;
        var end = Math.Min(data.Length, 20 + bodyLength);

        while (offset + 4 <= end)
        {
            var attrType = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            var attrLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var valueStart = offset + 4;

            if (valueStart + attrLength > end) break;

            if (attrType == AttrXorMappedAddress && attrLength >= 8)
            {
                var family = data[valueStart + 1];
                var xPort = BinaryPrimitives.ReadUInt16BigEndian(data[(valueStart + 2)..]);
                var port = (ushort)(xPort ^ (MagicCookie >> 16));

                if (family == 0x01)
                {
                    var xAddr = BinaryPrimitives.ReadUInt32BigEndian(data[(valueStart + 4)..]);
                    var address = XorToAddress(xAddr ^ MagicCookie);
                    return new IPEndPoint(address, port);
                }
            }

            if (attrType == AttrMappedAddress && attrLength >= 8)
            {
                var family = data[valueStart + 1];
                var port = BinaryPrimitives.ReadUInt16BigEndian(data[(valueStart + 2)..]);
                if (family == 0x01)
                {
                    var raw = BinaryPrimitives.ReadUInt32BigEndian(data[(valueStart + 4)..]);
                    return new IPEndPoint(XorToAddress(raw), port);
                }
            }

            offset = valueStart + ((attrLength + 3) & ~3);
        }

        return null;
    }
}
