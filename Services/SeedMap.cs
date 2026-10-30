using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>
/// 种子地图：算出生点附近的结构（村庄、神殿、要塞…）。
///
/// 计算靠 cubiomes —— MC 地形生成算法的 C 实现，编译成 dll 后随 exe 发布。
/// 首次调用时释放到 AppData，再用 DllImportResolver 指过去。
///
/// 注意：这里必须用 DllImport 而不是 GetDelegateForFunctionPointer ——
/// 后者需要动态代码生成，AOT 下直接抛 ExecutionEngineException。
/// </summary>
public static class SeedMap
{
    private static string LibPath => Path.Combine(Paths.AppData, "native", "cubiomes_v3.dll");

    private const string LibName = "cubiomes_v3";

    private static bool _resolverReady;

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int find_nearest_structure(
        ulong seed, int structureType, int mc, int blockX, int blockZ,
        out int outX, out int outZ);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int structure_in_region(
        ulong seed, int structureType, int mc, int blockX, int blockZ,
        out int outX, out int outZ);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int render_map_hd(
        ulong seed, int mc, int centerX, int centerZ, int w, int h, byte[] rgb);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int cubiomes_wrapper_version();

    private static bool _tried;
    private static bool _available;

    /// <summary>把嵌在 exe 里的 dll 释放出来，并告诉运行时去哪找。</summary>
    private static bool EnsureReady()
    {
        if (_tried) return _available;
        _tried = true;

        try
        {
            EnsureExtracted();

            if (!File.Exists(LibPath)) return _available = false;

            if (!_resolverReady)
            {
                NativeLibrary.SetDllImportResolver(typeof(SeedMap).Assembly, (name, assembly, searchPath) =>
                {
                    if (name.Equals(LibName, StringComparison.OrdinalIgnoreCase))
                        return NativeLibrary.Load(LibPath);
                    return IntPtr.Zero;
                });
                _resolverReady = true;
            }

            // 探一下能不能真的调通
            _available = cubiomes_wrapper_version() > 0;
            Log.Info(_available ? "地形库已就绪" : "地形库加载了但版本号读不出来");
        }
        catch (Exception ex)
        {
            Log.Warn("地形库不可用：" + ex.Message);
            _available = false;
        }

        return _available;
    }

    /// <summary>把嵌在 exe 里的 cubiomes.dll 释放到 AppData（只做一次）。</summary>
    private static void EnsureExtracted()
    {
        try
        {
            var dir = Path.GetDirectoryName(LibPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var stream = System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream("LinxLauncher.Assets.native.cubiomes_v3.dll");

            if (stream is null)
            {
                Log.Warn("exe 里没有找到 cubiomes_v3.dll 资源");
                return;
            }

            // 大小一致就当是最新的；不一致说明旧版本还在，直接覆盖
            if (File.Exists(LibPath) && new FileInfo(LibPath).Length == stream.Length)
                return;

            try
            {
                using var fs = File.Create(LibPath);
                stream.CopyTo(fs);
                Log.Info("地形库已释放到 " + LibPath);
            }
            catch (IOException)
            {
                // 旧 dll 被占用时删掉重试一次
                try
                {
                    File.Delete(LibPath);
                    stream.Position = 0;
                    using var fs2 = File.Create(LibPath);
                    stream.CopyTo(fs2);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("释放地形库失败：" + ex.Message);
        }
    }

    /* 编号对齐 cubiomes 的 enum StructureType */
    private static readonly (int Id, string Name)[] Structures =
    {
        (5, "村庄"),
        (8, "海底神殿"),
        (9, "林地府邸"),
        (15, "废弃矿井"),
        (1, "沙漠神殿"),
        (13, "远古城市"),
    };

    private static int VersionCode(string v) => v switch
    {
        "1.20" => 29,
        "1.19" => 28,
        "1.18" => 27,
        "1.17" => 26,
        "1.16" => 25,
        _ => 29,
    };

    /// <summary>
    /// 渲染一块区域的生物群系地图，返回 RGBA 像素（供 WriteableBitmap 直接写）。
    /// scale 是每像素多少格：4 表示 1:4。
    /// </summary>
    public static async Task<byte[]?> RenderAsync(string seedText, string version, int size, int scale = 4)
    {
        if (!EnsureReady()) return null;

        var seed = ParseSeed(seedText);
        var ver = VersionCode(version);

        return await Task.Run(() =>
        {
            var rgb = new byte[size * size * 3];
            if (render_map_hd(seed, ver, 0, 0, size, size, rgb) != 1) return null;
            return (byte[]?)rgb;
        });
    }

    /// <summary>一个结构的位置。</summary>
    public sealed class StructureHit
    {
        public string Name { get; init; } = "";
        public int X { get; init; }
        public int Z { get; init; }

        public double Distance => Math.Sqrt((double)X * X + (double)Z * Z);
    }

    /// <summary>
    /// 找出生点附近的结构，返回结构化列表（给地图标点和列表用）。
    /// 地形库不可用时返回 null。
    /// </summary>
    /// <summary>
    /// 扫描地图覆盖范围内的结构。
    /// 地图边长 size*4 格（1 像素 4 格），区域宽 512 格，
    /// 所以按区域一个个查，能拿到一片范围内所有的村庄/神殿，而不是只有最近那一个。
    /// </summary>
    public static async Task<List<StructureHit>?> FindStructuresAsync(
        string seedText, string version, int mapBlocks = 2048)
    {
        if (!EnsureReady()) return null;

        var seed = ParseSeed(seedText);
        var ver = VersionCode(version);

        return await Task.Run(() =>
        {
            var list = new List<StructureHit>();

            // 覆盖地图范围需要的区域数（每边）
            var rings = (int)Math.Ceiling(mapBlocks / 2.0 / 512.0) + 1;

            foreach (var (id, name) in Structures)
            {
                for (var rz = -rings; rz <= rings; rz++)
                {
                    for (var rx = -rings; rx <= rings; rx++)
                    {
                        try
                        {
                            if (structure_in_region(seed, id, ver, rx * 512, rz * 512,
                                    out var px, out var pz) == 1)
                            {
                                // 同一个结构可能被相邻区域重复命中，按坐标去重
                                if (!list.Exists(h => h.X == px && h.Z == pz))
                                    list.Add(new StructureHit { Name = name, X = px, Z = pz });
                            }
                        }
                        catch { }
                    }
                }
            }

            return list;
        });
    }

    public static async Task<string> DescribeAsync(string seedText, string version)
    {
        var seed = ParseSeed(seedText);

        if (!EnsureReady())
            return OpenChunkbase(seedText, version);

        var sb = new StringBuilder();
        sb.AppendLine($"种子 {seedText}（{version}）");
        sb.AppendLine("从出生点往外找最近的：");
        sb.AppendLine();

        var ver = VersionCode(version);

        await Task.Run(() =>
        {
            foreach (var (id, name) in Structures)
            {
                try
                {
                    if (find_nearest_structure(seed, id, ver, 0, 0, out var px, out var pz) == 1)
                    {
                        var dist = Math.Sqrt((double)px * px + (double)pz * pz);
                        sb.AppendLine($"  {name}：({px}, {pz})  约 {dist:F0} 格");
                    }
                    else
                    {
                        sb.AppendLine($"  {name}：附近没找到");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  {name}：计算失败（{ex.Message}）");
                }
            }
        });

        return sb.ToString();
    }

    /// <summary>没有本地库时，用同一个种子打开 Chunkbase。</summary>
    private static string OpenChunkbase(string seedText, string version)
    {
        try
        {
            var mcVersion = version switch
            {
                "1.16" => "1.16.5",
                "1.17" => "1.17.1",
                "1.18" => "1.18.2",
                "1.19" => "1.19.4",
                _ => "1.20.1",
            };

            var url = $"https://www.chunkbase.com/apps/seed-map#seed={Uri.EscapeDataString(seedText)}&platform=java_{mcVersion.Replace(".", "_")}";
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

            return $"地形库没加载成功，已用同一个种子打开 Chunkbase。\n\n"
                 + $"种子 {seedText} · Java {mcVersion}\n\n"
                 + "本地库路径：" + LibPath;
        }
        catch (Exception ex)
        {
            return "打不开浏览器：" + ex.Message
                 + "\n\n手动访问 https://www.chunkbase.com/apps/seed-map";
        }
    }

    private static ulong ParseSeed(string text)
    {
        if (ulong.TryParse(text, out var v)) return v;

        // 文本种子按 Java 的 String.hashCode 折算成数值
        long h = 0;
        foreach (var c in text) h = (31 * h + c) & 0xFFFFFFFFL;
        return (ulong)h;
    }
}
