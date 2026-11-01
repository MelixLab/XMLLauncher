using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LinxLauncher.Services;

/// <summary>
/// 从 level.dat 里读出这个世界是用哪个版本存的。
///
/// level.dat 是 gzip 压缩的 NBT。不用完整解析整棵树，只要顺着
/// Data → Version → Name 找到那个字符串就够了——高版本存档在低版本打开会损坏，
/// 所以云存档列表上得标清楚。
/// </summary>
public static class LevelDatReader
{
    public sealed class Info
    {
        public string Version { get; init; } = "";

        /// <summary>游戏内记录的「上次游玩时间」。上传时间和它无关，比新旧只能靠这个。</summary>
        public DateTime LastPlayedUtc { get; init; }
    }

    public static Info? Read(string levelDatPath)
    {
        try
        {
            if (!File.Exists(levelDatPath)) return null;

            using var fs = File.OpenRead(levelDatPath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);

            var version = "";
            long lastPlayed = 0;

            Walk(gz, 0, ref version, ref lastPlayed);

            return string.IsNullOrEmpty(version) && lastPlayed == 0
                ? null
                : new Info
                {
                    Version = version,
                    // MC 存的是毫秒时间戳
                    LastPlayedUtc = lastPlayed > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(lastPlayed).UtcDateTime
                        : DateTime.MinValue,
                };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 递归读 NBT。只看两条路径：
    ///   Version/Name          → 版本号
    ///   LastPlayed            → 上次游玩时间
    /// 其它一律跳过（按类型算长度）。
    /// </summary>
    private static void Walk(Stream s, int depth, ref string version, ref long lastPlayed)
    {
        if (depth > 6) return;   // 防止畸形文件把栈打穿

        while (true)
        {
            var type = s.ReadByte();
            if (type <= 0) return;   // End

            var name = ReadString(s, out var ok);
            if (!ok) return;

            switch (type)
            {
                case 0x01: Skip(s, 1); break;                       // Byte
                case 0x02: Skip(s, 2); break;                       // Short
                case 0x03: Skip(s, 4); break;                       // Int
                case 0x04:                                          // Long
                {
                    var v = ReadLong(s);
                    if (name == "LastPlayed" && lastPlayed == 0) lastPlayed = v;
                    break;
                }
                case 0x05: Skip(s, 4); break;                       // Float
                case 0x06: Skip(s, 8); break;                       // Double

                case 0x07:                                          // ByteArray
                {
                    var len = ReadInt(s);
                    Skip(s, Math.Max(0, len));
                    break;
                }

                case 0x08:                                          // String
                {
                    var value = ReadString(s, out var ok2);
                    if (!ok2) return;

                    if (name == "Name" && string.IsNullOrEmpty(version) && LooksLikeVersion(value))
                        version = value;
                    break;
                }

                case 0x09:                                          // List
                {
                    var elem = s.ReadByte();
                    var count = ReadInt(s);
                    for (var i = 0; i < count && i < 100000; i++)
                    {
                        switch (elem)
                        {
                            case 0x01: Skip(s, 1); break;
                            case 0x02: Skip(s, 2); break;
                            case 0x03: Skip(s, 4); break;
                            case 0x04: Skip(s, 8); break;
                            case 0x05: Skip(s, 4); break;
                            case 0x06: Skip(s, 8); break;
                            case 0x07: Skip(s, ReadInt(s)); break;
                            case 0x08: ReadString(s, out _); break;
                            case 0x0A: Walk(s, depth + 1, ref version, ref lastPlayed); break;
                            case 0x0B: Skip(s, 4 * ReadInt(s)); break;
                            case 0x0C: Skip(s, 8 * ReadInt(s)); break;
                            default: return;
                        }
                    }
                    break;
                }

                case 0x0A:                                          // Compound
                {
                    // 只关心 Data 和 Version 两层，其它也进去看一眼，代价很小
                    Walk(s, depth + 1, ref version, ref lastPlayed);
                    break;
                }

                case 0x0B:                                          // IntArray
                {
                    var len = ReadInt(s);
                    Skip(s, Math.Max(0, len) * 4);
                    break;
                }

                case 0x0C:                                          // LongArray
                {
                    var len = ReadInt(s);
                    Skip(s, Math.Max(0, len) * 8);
                    break;
                }

                default:
                    return;
            }

            if (name == "LastPlayed" && lastPlayed == 0)
            {
                // 上一次读 Long 时已经跳过了，这里读不到值，留给调用方兜底
            }
        }
    }

    private static bool LooksLikeVersion(string s)
    {
        // "1.19.2" / "1.20.1" 这类
        if (s.Length < 3 || s.Length > 24) return false;
        return char.IsDigit(s[0]) && s.Contains('.');
    }

    private static string ReadString(Stream s, out bool ok)
    {
        ok = false;
        var b1 = s.ReadByte();
        var b2 = s.ReadByte();
        if (b1 < 0 || b2 < 0) return "";

        var len = (b1 << 8) | b2;
        if (len <= 0 || len > 4096) { ok = len == 0; return ""; }

        var buf = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = s.Read(buf, read, len - read);
            if (n <= 0) return "";
            read += n;
        }

        ok = true;
        return Encoding.UTF8.GetString(buf);
    }

    private static long ReadLong(Stream s)
    {
        var b = new byte[8];
        var read = 0;
        while (read < 8)
        {
            var n = s.Read(b, read, 8 - read);
            if (n <= 0) return 0;
            read += n;
        }

        long v = 0;
        for (var i = 0; i < 8; i++) v = (v << 8) | b[i];
        return v;
    }

    private static int ReadInt(Stream s)
    {
        var b = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = s.Read(b, read, 4 - read);
            if (n <= 0) return 0;
            read += n;
        }

        return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }

    private static void Skip(Stream s, int count)
    {
        if (count <= 0) return;

        var buf = new byte[Math.Min(count, 8192)];
        var left = count;
        while (left > 0)
        {
            var n = s.Read(buf, 0, Math.Min(buf.Length, left));
            if (n <= 0) return;
            left -= n;
        }
    }
}
