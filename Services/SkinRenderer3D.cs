using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace LinxLauncher.Services;

/// <summary>
/// 把 64×64 的 MC 皮肤贴图渲染成可旋转的 3D 小人。
///
/// 纯软件渲染：6 个立方体（头/身/左右臂/左右腿），每个 6 个面，
/// 按 Y 轴旋转后做正交投影，用画家算法从远到近铺像素。
/// 不依赖 Win2D 或任何 3D 库。
/// </summary>
public static class SkinRenderer3D
{
    /// <summary>皮肤像素（64×64 RGBA）。</summary>
    public sealed class SkinImage
    {
        public byte[] Pixels { get; init; } = Array.Empty<byte>();
        public int Width { get; init; } = 64;
        public int Height { get; init; } = 64;
    }

    /* ---------------- 部件定义 ---------------- */

    private readonly record struct Box(
        string Name,
        float W, float H, float D,
        float OffsetX, float OffsetY, float OffsetZ,
        int U, int V);

    // 标准 64×64 皮肤布局：UV 原点 + 每个面的长宽
    private static readonly Box[] Parts =
    {
        // 头：8×8×8，UV (0,0)
        new("head",  8, 8, 8,   0, -4, 0,   0, 0),
        // 身体：8×12×4，UV (16,16)
        new("body",  8, 12, 4,  0,  6, 0,  16, 16),
        // 右臂：4×12×4，UV (40,16)
        new("rarm",  4, 12, 4, -6,  6, 0,  40, 16),
        // 左臂：4×12×4，UV (32,48)
        new("larm",  4, 12, 4,  6,  6, 0,  32, 48),
        // 右腿：4×12×4，UV (0,16)
        new("rleg",  4, 12, 4, -2, 18, 0,   0, 16),
        // 左腿：4×12×4，UV (16,48)
        new("lleg",  4, 12, 4,  2, 18, 0,  16, 48),
    };

    /* ---------------- 公开接口 ---------------- */

    /// <summary>渲染一帧，返回 BGRA 像素（宽 w，高 h）。</summary>
    public static byte[] Render(SkinImage skin, int w, int h, double yawDegrees,
                               double pitchDegrees = 0, double headYawDegrees = 0)
    {
        var bgra = new byte[w * h * 4];
        var depth = new float[w * h];
        for (var i = 0; i < depth.Length; i++) depth[i] = float.NegativeInfinity;

        // 皮肤是 64 宽、32 或 64 高；统一按 64 处理
        var sw = skin.Width;
        var sh = skin.Height;

        var yaw = yawDegrees * Math.PI / 180.0;
        var pitch = pitchDegrees * Math.PI / 180.0;

        // 模型整体缩放到画面里：24 高 ≈ 画面高度的 78%
        var scale = h * 0.78 / 32.0;
        var cx = w / 2.0;
        var cy = h / 2.0;

        // 收集所有面，按深度排序后再画（画家算法）
        var faces = new List<(double depth, int[] xs, int[] ys, (byte b, byte g, byte r, byte a) uv0, (double u, double v)[] uvs)>();

        foreach (var part in Parts)
        {
            // 头部额外转 headYaw：身体不动，头跟着鼠标偏
            var partYaw = part.Name == "head" ? yaw + headYawDegrees : yaw;
            CollectFaces(part, skin, sw, sh, partYaw, pitch, faces, scale, cx, cy);
        }

        faces.Sort((a, b) => a.depth.CompareTo(b.depth));

        foreach (var f in faces)
        {
            FillQuad(bgra, depth, w, h, f.xs, f.ys, f.uv0, f.uvs, skin, sw, sh);
        }

        return bgra;
    }

    public static async Task<WriteableBitmap> RenderAsync(SkinImage skin, int w, int h, double yaw,
                                                          double pitch = 0, double headYaw = 0)
    {
        var pixels = Render(skin, w, h, yaw, pitch, headYaw);
        var bmp = new WriteableBitmap(w, h);
        using (var stream = bmp.PixelBuffer.AsStream())
        {
            await stream.WriteAsync(pixels);
        }
        bmp.Invalidate();
        return bmp;
    }

    /* ---------------- 立方体面 ---------------- */

    private static void CollectFaces(
        Box box, SkinImage skin, int sw, int sh,
        double yaw, double pitch,
        List<(double, int[], int[], (byte, byte, byte, byte), (double, double)[])> faces,
        double scale, double cx, double cy)
    {
        var hw = box.W / 2f;
        var hh = box.H / 2f;
        var hd = box.D / 2f;

        // 8 个顶点：局部坐标先加上部件偏移，再统一绕模型中心旋转。
        // Y 向下为正，和皮肤贴图的 V 方向一致。
        var ox = box.OffsetX;
        var oy = box.OffsetY - 12;   // 模型中心大致在腰部
        var oz = box.OffsetZ;

        // 头部绕脖子转（而不是绕腰部），不然视角一转头就飞出去了
        if (box.Name == "head")
            oy += box.H / 2f;

        var c = new (double x, double y, double z)[]
        {
            (-hw + ox, -hh + oy, -hd + oz), ( hw + ox, -hh + oy, -hd + oz),
            ( hw + ox, -hh + oy,  hd + oz), (-hw + ox, -hh + oy,  hd + oz),
            (-hw + ox,  hh + oy, -hd + oz), ( hw + ox,  hh + oy, -hd + oz),
            ( hw + ox,  hh + oy,  hd + oz), (-hw + ox,  hh + oy,  hd + oz),
        };

        // UV 布局（标准 64×64）
        var (u0, v0) = (box.U, box.V);
        var w = (int)box.W;
        var hh2 = (int)box.H;
        var dd = (int)box.D;

        // 每个面：[4 个顶点索引]，贴图矩形 (x,y,w,h)
        // y 向下为正：顶点 0-3 在上（y=-hh），4-7 在下（y=+hh）。
        // 索引顺序必须和各自 UV 矩形的朝向一致，否则脸会朝下、颜色错位。
        var defs = new (int[] idx, int u, int v, int fw, int fh)[]
        {
            (new[] { 0, 1, 2, 3 }, u0 + dd,       v0,            w,  dd),    // 顶
            (new[] { 4, 5, 6, 7 }, u0 + dd + w,   v0 + dd + hh2, w,  dd),    // 底
            (new[] { 3, 2, 6, 7 }, u0 + dd,       v0 + dd,       w,  hh2),   // 前（+z）
            (new[] { 1, 0, 4, 5 }, u0 + dd + w,   v0 + dd,       w,  hh2),   // 后（-z）
            (new[] { 0, 3, 7, 4 }, u0,            v0 + dd,       dd, hh2),   // 左（-x）
            (new[] { 2, 1, 5, 6 }, u0 + dd + w,   v0 + dd,       dd, hh2),   // 右（+x）
        };

        foreach (var (idx, u, v, fw, fh) in defs)
        {
            var pts = new (double x, double y, double z)[4];
            var depthSum = 0.0;

            for (var i = 0; i < 4; i++)
            {
                var p = c[idx[i]];
                var (rx, ry, rz) = Rotate(p.x, p.y, p.z, yaw, pitch);
                if (box.Name == "head")
                {
                    // 绕脖子转完之后，再把头部整体挪回头该在的位置
                    var (hx, hy, hz) = Rotate(0, 0, 0, yaw, pitch);
                    ry -= 0;
                }
                pts[i] = (rx, ry, rz);
                depthSum += rz;
            }

            // 不做背面剔除：立方体只有 6 个面，全画一遍
            // 靠深度排序决定遮挡关系，比绕序判断稳（绕序方向容易写反）


            var xs = new int[4];
            var ys = new int[4];
            for (var i = 0; i < 4; i++)
            {
                xs[i] = (int)Math.Round(cx + pts[i].x * scale);
                ys[i] = (int)Math.Round(cy + pts[i].y * scale);
            }

            var uv0 = (skin.Pixels.Length > 0 ? (byte)0 : (byte)0, (byte)0, (byte)0, (byte)255);
            var uvs = new (double, double)[]
            {
                (u, v),
                (u + fw, v),
                (u + fw, v + fh),
                (u, v + fh),
            };

            faces.Add((depthSum / 4, xs, ys, uv0, uvs));
        }
    }

    private static (double x, double y, double z) Rotate(double x, double y, double z, double yaw, double pitch)
    {
        // 绕 Y 轴
        var cy = Math.Cos(yaw);
        var sy = Math.Sin(yaw);
        var x1 = x * cy - z * sy;
        var z1 = x * sy + z * cy;

        // 绕 X 轴
        var cp = Math.Cos(pitch);
        var sp = Math.Sin(pitch);
        var y1 = y * cp - z1 * sp;
        var z2 = y * sp + z1 * cp;

        return (x1, y1, z2);
    }

    /* ---------------- 像素填充 ---------------- */

    private static void FillQuad(
        byte[] bgra, float[] depth, int w, int h,
        int[] xs, int[] ys,
        (byte b, byte g, byte r, byte a) _,
        (double u, double v)[] uvs,
        SkinImage skin, int sw, int sh)
    {
        // 四边形拆成两个三角形，各自做重心插值。
        // 之前两个三角形共用前三个顶点的重心坐标，第二个三角形算出来的 UV
        // 会跑到 [0,1] 外面，整片被丢掉，画面上就只剩三角形边线。
        FillTriangle(bgra, w, h, xs[0], ys[0], xs[1], ys[1], xs[2], ys[2],
                     uvs[0], uvs[1], uvs[2], skin, sw, sh);

        FillTriangle(bgra, w, h, xs[0], ys[0], xs[2], ys[2], xs[3], ys[3],
                     uvs[0], uvs[2], uvs[3], skin, sw, sh);
    }

    private static void FillTriangle(
        byte[] bgra, int w, int h,
        int x1, int y1, int x2, int y2, int x3, int y3,
        (double u, double v) uv1, (double u, double v) uv2, (double u, double v) uv3,
        SkinImage skin, int sw, int sh)
    {
        // 有符号面积，正负都可以，用符号统一归一化
        var area = (double)(x2 - x1) * (y3 - y1) - (double)(x3 - x1) * (y2 - y1);
        if (Math.Abs(area) < 1e-6) return;

        // 正面积按 0-1-2 的顺序，负面积就全部取反，效果一样
        if (area < 0)
        {
            (x2, x3) = (x3, x2);
            (y2, y3) = (y3, y2);
            (uv2, uv3) = (uv3, uv2);
            area = -area;
        }

        var minX = Math.Max(0, Math.Min(x1, Math.Min(x2, x3)));
        var maxX = Math.Min(w - 1, Math.Max(x1, Math.Max(x2, x3)));
        var minY = Math.Max(0, Math.Min(y1, Math.Min(y2, y3)));
        var maxY = Math.Min(h - 1, Math.Max(y1, Math.Max(y2, y3)));

        for (var py = minY; py <= maxY; py++)
        {
            for (var px = minX; px <= maxX; px++)
            {
                // 用像素中心做采样，避免整块偏移半个像素
                var fx = px + 0.5;
                var fy = py + 0.5;

                var w1 = ((double)(x3 - x2) * (fy - y2) - (double)(y3 - y2) * (fx - x2)) / area;
                var w2 = ((double)(x1 - x3) * (fy - y3) - (double)(y1 - y3) * (fx - x3)) / area;
                var w3 = 1 - w1 - w2;

                if (w1 < -0.001 || w2 < -0.001 || w3 < -0.001) continue;

                var su = w1 * uv1.u + w2 * uv2.u + w3 * uv3.u;
                var sv = w1 * uv1.v + w2 * uv2.v + w3 * uv3.v;

                var sx = (int)su;
                var sy = (int)sv;
                if (sx < 0 || sy < 0 || sx >= sw || sy >= sh) continue;

                var si = (sy * sw + sx) * 4;
                if (si + 3 >= skin.Pixels.Length) continue;
                if (skin.Pixels[si + 3] == 0) continue;   // 透明像素跳过

                var di = (py * w + px) * 4;
                bgra[di + 0] = skin.Pixels[si + 0];
                bgra[di + 1] = skin.Pixels[si + 1];
                bgra[di + 2] = skin.Pixels[si + 2];
                bgra[di + 3] = 255;
            }
        }
    }

}
