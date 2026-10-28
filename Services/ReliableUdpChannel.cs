using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>
/// 跑在 UDP 上的可靠传输（精简版 ARQ）。
///
/// Minecraft 说的是 TCP，不能直接把 TCP 字节丢进 UDP 就完事 —— 丢一个包整条流就废了。
/// 这里做一个够用的可靠层：分包、序号、累计确认、超时重传、乱序缓存。
/// 窗口固定 64 包、超时 250ms、重传 10 次，局域网/家宽这点丢包率够用了。
///
/// 包头 9 字节：flags(1) seq(4) ack(4)，后面是负载。
/// </summary>
public sealed class ReliableUdpChannel : IDisposable
{
    private const byte FlagData = 0x01;
    private const byte FlagAck = 0x02;
    private const byte FlagFin = 0x04;
    private const byte FlagPunch = 0x08;

    private const int HeaderSize = 9;
    private const int MaxPayload = 1100;
    private const int WindowSize = 64;
    private const int RetransmitMs = 250;
    private const int MaxRetries = 10;

    private readonly Socket _socket;
    private readonly IPEndPoint _remote;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<uint, byte[]> _outbox = new();   // seq -> payload
    private readonly Dictionary<uint, long> _sentAt = new();
    private readonly Dictionary<uint, int> _retries = new();
    private readonly Dictionary<uint, byte[]> _inbox = new();              // 乱序缓存
    private readonly BlockingCollection<byte[]> _received = new(new ConcurrentQueue<byte[]>(), 512);

    private uint _nextSeq;
    private uint _ackSeq;
    private uint _expectedSeq;
    private int _rxData;
    private int _rxAck;
    private int _txAck;
    private volatile bool _closed;
    private long _bytesIn;
    private long _bytesOut;

    public long BytesIn => Interlocked.Read(ref _bytesIn);
    public long BytesOut => Interlocked.Read(ref _bytesOut);

    private ReliableUdpChannel(Socket socket, IPEndPoint remote)
    {
        _socket = socket;
        _remote = remote;
    }

    /// <summary>
    /// 在已经打通的 socket 上开一条可靠通道。socket 归本对象管理（Dispose 时关闭）。
    /// </summary>
    public static ReliableUdpChannel Attach(Socket socket, IPEndPoint remote)
    {
        var channel = new ReliableUdpChannel(socket, remote);
        _ = Task.Run(channel.ReceiveLoopAsync);
        _ = Task.Run(channel.RetransmitLoopAsync);
        return channel;
    }

    /// <summary>发一段字节（内部自动分包、排队、重传）。</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            ThrowIfClosed();

            // 窗口满了就等一会儿（说明对端还没确认）
            while (_outbox.Count >= WindowSize)
            {
                ct.ThrowIfCancellationRequested();
                if (_closed) throw new IOException("连接已断开。");
                await Task.Delay(5, ct);
            }

            var size = Math.Min(MaxPayload, data.Length - offset);
            var payload = data.Slice(offset, size).ToArray();
            var seq = _nextSeq++;

            _outbox[seq] = payload;
            _sentAt[seq] = Environment.TickCount64;

            // 立即发一包，别等重传循环：MC 的握手包就几十字节，
            // 等 250ms 才发会让客户端以为服务器在卡。
            SendPacket(FlagData, seq, _ackSeq, payload);

            offset += size;
        }
    }

    /// <summary>读一段字节；对端关闭或超时会返回 0 / 抛异常。</summary>
    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        ThrowIfClosed();

        byte[] chunk;
        try
        {
            chunk = _received.Take(ct);
        }
        catch (Exception)
        {
            return 0;
        }

        var size = Math.Min(chunk.Length, buffer.Length);
        chunk.AsSpan(0, size).CopyTo(buffer.Span);
        return size > 0 ? size : 0;
    }

    private void ThrowIfClosed()
    {
        if (_closed) throw new IOException("连接已断开。");
    }

    // ---------- 收 ----------

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1500];
        var token = _cts.Token;

        Log.Info($"P2P 通道：接收循环启动（远端 {_remote}）");

        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(20, token).ContinueWith(_ => { });
                continue;
            }

            if (result.ReceivedBytes < HeaderSize) continue;

            var flags = buffer[0];
            var seq = BitConverter.ToUInt32(buffer, 1);
            var ack = BitConverter.ToUInt32(buffer, 5);

            if ((flags & FlagAck) != 0)
            {
                Interlocked.Increment(ref _rxAck);
                if (_rxAck <= 3 || _rxAck % 20 == 0)
                    Log.Info($"P2P 通道：收到 ACK #{_rxAck}（ack={ack}，待确认 {_outbox.Count} 个）");
            }

            OnAck(ack);

            if ((flags & FlagPunch) != 0)
            {
                SendPunch();
                continue;
            }

            if ((flags & FlagAck) != 0) continue;

            if ((flags & FlagData) != 0)
            {
                var size = result.ReceivedBytes - HeaderSize;
                Interlocked.Increment(ref _rxData);
                if (_rxData <= 3 || _rxData % 20 == 0)
                    Log.Info($"P2P 通道：收到 DATA #{_rxData}（seq={seq} len={size} 期望={_expectedSeq}）");
                var payload = new byte[size];
                Array.Copy(buffer, HeaderSize, payload, 0, size);

                Interlocked.Add(ref _bytesIn, size);

                if (seq == _expectedSeq)
                {
                    _received.Add(payload);
                    _expectedSeq++;
                    DrainInbox();
                }
                else if (seq > _expectedSeq)
                {
                    _inbox[seq] = payload;
                }
                // seq < expected：重复包，丢弃

                // ack 语义 = 下一个期望收到的序号。
                // （之前写成 expected-1，在「一个包都没收到」时等于 0，
                //   等于告诉对端「0 号我收到了」——对端就把 seq=0 删了，从此死等。）
                _ackSeq = _expectedSeq;
                SendAck();
            }

            if ((flags & FlagFin) != 0)
            {
                _closed = true;
                try { _received.CompleteAdding(); } catch { }
                return;
            }
        }
    }

    private void DrainInbox()
    {
        while (_inbox.Remove(_expectedSeq, out var cached))
        {
            _received.Add(cached);
            _expectedSeq++;
        }
    }

    private void OnAck(uint ack)
    {
        // ack 是累计确认：<= ack 的包都收到了
        var confirmed = new List<uint>();
        foreach (var seq in _outbox.Keys)
        {
            if (seq < ack) confirmed.Add(seq);
        }

        foreach (var seq in confirmed)
        {
            _outbox.TryRemove(seq, out _);
            _sentAt.Remove(seq);
            _retries.Remove(seq);
        }
    }

    // ---------- 发 ----------

    private async Task RetransmitLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(20, token);
            }
            catch
            {
                return;
            }

            var now = Environment.TickCount64;

            foreach (var pair in _outbox)
            {
                var seq = pair.Key;

                if (!_sentAt.TryGetValue(seq, out var sentAt) || now - sentAt >= RetransmitMs)
                {
                    SendPacket(FlagData, seq, _ackSeq, pair.Value);
                    _sentAt[seq] = now;

                    _retries.TryGetValue(seq, out var tries);
                    tries++;
                    _retries[seq] = tries;

                    if (tries > MaxRetries)
                    {
                        Log.Warn("P2P：重传次数用尽，断开连接");
                        Close();
                        return;
                    }
                }
            }
        }
    }

    private void SendAck()
    {
        Interlocked.Increment(ref _txAck);
        if (_txAck <= 3 || _txAck % 20 == 0)
            Log.Info($"P2P 通道：发出 ACK #{_txAck}（ack={_ackSeq}）");

        SendPacket(FlagAck, 0, _ackSeq, Array.Empty<byte>());
    }

    private void SendPunch()
    {
        try
        {
            var packet = new byte[HeaderSize];
            packet[0] = FlagPunch;
            _socket.SendTo(packet, _remote);
        }
        catch { }
    }

    private void SendPacket(byte flags, uint seq, uint ackSeq, byte[] payload)
    {
        if (_closed) return;

        try
        {
            var packet = new byte[HeaderSize + payload.Length];
            packet[0] = flags;
            BitConverter.TryWriteBytes(packet.AsSpan(1), seq);
            BitConverter.TryWriteBytes(packet.AsSpan(5), ackSeq);
            payload.CopyTo(packet, HeaderSize);

            _socket.SendTo(packet, _remote);
            Interlocked.Add(ref _bytesOut, payload.Length);
        }
        catch (Exception ex)
        {
            if (flags != FlagAck) Log.Warn("P2P 通道：发包失败 " + ex.Message);
        }
    }

    /// <summary>发 FIN 并关闭。</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;

        try { SendPacket(FlagFin, _nextSeq, _ackSeq, Array.Empty<byte>()); } catch { }

        try { _received.CompleteAdding(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _socket.Close(); } catch { }
    }

    public void Dispose() => Close();
}
