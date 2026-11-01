using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LinxLauncher.Services;

/// <summary>Minecraft 皮肤的软件渲染：共享贴图布局、双层模型、逐像素深度与透明度。</summary>
public static class SkinRenderer3D
{
    /// <summary>未预乘的 BGRA 皮肤像素；加载时统一为现代布局。</summary>
    public sealed class SkinImage
    {
        public byte[] Pixels { get; init; } = Array.Empty<byte>();
        public int Width { get; init; } = 64;
        public int Height { get; init; } = 64;
        public string Model { get; init; } = "classic";
        public bool IsSlim => string.Equals(Model, "slim", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Part(
        int Width, int Height, int Depth, double X, double Y, bool IsHead,
        int U, int V, int OuterU, int OuterV);
    private readonly record struct Point3(double X, double Y, double Z);
    private readonly record struct Vertex(double X, double Y, double Z, double U, double V);
    private readonly record struct Face(int A, int B, int C, int D, int U, int V, int Width, int Height, bool FlipV = false);
    private readonly record struct Fragment(double Depth, int TextureOffset, int Next);

    private static readonly Part[] ClassicParts = CreateParts(false);
    private static readonly Part[] SlimParts = CreateParts(true);

    private static Part[] CreateParts(bool slim)
    {
        int armWidth = slim ? 3 : 4;
        double armX = 4 + armWidth / 2.0;
        // Y 向下，颈部为 0；头顶 -8、脚底 24，整体中心位于 Y=8。
        return
        [
            new(8, 8, 8, 0, -4, true, 0, 0, 32, 0),
            new(8, 12, 4, 0, 6, false, 16, 16, 16, 32),
            new(armWidth, 12, 4, -armX, 6, false, 40, 16, 40, 32),
            new(armWidth, 12, 4, armX, 6, false, 32, 48, 48, 48),
            new(4, 12, 4, -2, 18, false, 0, 16, 0, 32),
            new(4, 12, 4, 2, 18, false, 16, 48, 0, 48),
        ];
    }

    /// <summary>转换旧版肢体、恢复基础层不透明度，供头像、2D 和 3D 预览共同使用。</summary>
    public static SkinImage CreateSkinImage(byte[] pixels, int width, int height, string model = "classic")
    {
        ValidateTexture(pixels, width, height);
        var normalized = new byte[64 * 64 * 4];
        pixels.AsSpan(0, width * height * 4).CopyTo(normalized);
        if (height == 32)
        {
            // 旧版右半区全不透明时，它是背景色而非帽子；保留实际带透明区域的帽子。
            bool opaque = true;
            for (int y = 0; y < 32 && opaque; y++)
                for (int x = 32; x < 64; x++)
                    if (normalized[(y * 64 + x) * 4 + 3] < 128) { opaque = false; break; }
            if (opaque)
                for (int y = 0; y < 16; y++)
                    for (int x = 32; x < 64; x++) normalized[(y * 64 + x) * 4 + 3] = 0;

            MirrorLimb(normalized, 0, 16, 16, 48);
            MirrorLimb(normalized, 40, 16, 32, 48);
        }

        MakeOpaque(normalized, 0, 0, 32, 16);
        MakeOpaque(normalized, 0, 16, 64, 16);
        MakeOpaque(normalized, 16, 48, 32, 16);
        return new SkinImage
        {
            Pixels = normalized,
            Model = height != 32 && string.Equals(model, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic",
        };
    }

    private static void ValidateTexture(byte[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width != 64 || height is not (32 or 64) || pixels.Length < width * height * 4)
            throw new ArgumentException("皮肤必须是完整的 64×64 或 64×32 BGRA 像素。");
    }

    private static SkinImage Prepare(SkinImage skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        ValidateTexture(skin.Pixels, skin.Width, skin.Height);
        return skin.Height == 32 ? CreateSkinImage(skin.Pixels, skin.Width, skin.Height, skin.Model) : skin;
    }

    private static void MakeOpaque(byte[] pixels, int x, int y, int width, int height)
    {
        for (int row = y; row < y + height; row++)
            for (int column = x; column < x + width; column++) pixels[(row * 64 + column) * 4 + 3] = 255;
    }

    private static void MirrorLimb(byte[] pixels, int sourceU, int sourceV, int targetU, int targetV)
    {
        CopyMirrored(pixels, sourceU + 4, sourceV, targetU + 4, targetV, 4, 4);
        CopyMirrored(pixels, sourceU + 8, sourceV, targetU + 8, targetV, 4, 4);
        CopyMirrored(pixels, sourceU + 8, sourceV + 4, targetU, targetV + 4, 4, 12);
        CopyMirrored(pixels, sourceU + 4, sourceV + 4, targetU + 4, targetV + 4, 4, 12);
        CopyMirrored(pixels, sourceU, sourceV + 4, targetU + 8, targetV + 4, 4, 12);
        CopyMirrored(pixels, sourceU + 12, sourceV + 4, targetU + 12, targetV + 4, 4, 12);
    }

    private static void CopyMirrored(byte[] pixels, int sx, int sy, int dx, int dy, int width, int height)
    {
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                pixels.AsSpan(((sy + y) * 64 + sx + width - 1 - x) * 4, 4)
                    .CopyTo(pixels.AsSpan(((dy + y) * 64 + dx + x) * 4, 4));
    }

    /// <summary>渲染一帧，返回可直接写入 WriteableBitmap 的预乘 BGRA 像素。</summary>
    public static byte[] Render(SkinImage skin, int w, int h, double yawDegrees,
        double pitchDegrees = 0, double headYawDegrees = 0)
    {
        skin = Prepare(skin);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(w);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(h);
        if (!double.IsFinite(yawDegrees) || !double.IsFinite(pitchDegrees) || !double.IsFinite(headYawDegrees))
            throw new ArgumentOutOfRangeException(nameof(yawDegrees), "旋转角度必须是有限数值。");

        var frame = new Frame(skin.Pixels, w, h);
        double scale = Math.Min(h * 0.84 / 34, w * 0.86 / 18);
        double yaw = yawDegrees * Math.PI / 180;
        double pitch = pitchDegrees * Math.PI / 180;
        double headYaw = headYawDegrees * Math.PI / 180;
        foreach (var part in skin.IsSlim ? SlimParts : ClassicParts)
        {
            RenderBox(frame, part, false, yaw, pitch, headYaw, scale);
            RenderBox(frame, part, true, yaw, pitch, headYaw, scale);
        }
        frame.CompositeTransparent();
        return frame.Pixels;
    }

    public static async Task<WriteableBitmap> RenderAsync(SkinImage skin, int w, int h, double yaw,
        double pitch = 0, double headYaw = 0)
    {
        var pixels = Render(skin, w, h, yaw, pitch, headYaw);
        var bitmap = new WriteableBitmap(w, h);
        using (var stream = bitmap.PixelBuffer.AsStream()) await stream.WriteAsync(pixels);
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>正面或背面 2D 全身预览，与 3D 共用六个部件的基础层及外层 UV。</summary>
    public static byte[] RenderBody(SkinImage skin, bool back, int scale, out int width, out int height)
    {
        skin = Prepare(skin);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        int unitWidth = skin.IsSlim ? 14 : 16;
        width = checked(unitWidth * scale);
        height = checked(32 * scale);
        var pixels = new byte[checked(width * height * 4)];
        foreach (var part in skin.IsSlim ? SlimParts : ClassicParts)
        {
            int x = (int)(unitWidth / 2.0 + (back ? -part.X : part.X) - part.Width / 2.0) * scale;
            int y = (int)(part.Y - part.Height / 2.0 + 8) * scale;
            int uOffset = back ? 2 * part.Depth + part.Width : part.Depth;
            Blit(skin.Pixels, part.U + uOffset, part.V + part.Depth, part.Width, part.Height,
                pixels, width, x, y, scale, false);
            Blit(skin.Pixels, part.OuterU + uOffset, part.OuterV + part.Depth, part.Width, part.Height,
                pixels, width, x, y, scale, true);
        }
        return pixels;
    }

    /// <summary>头像预览，返回预乘 BGRA 像素。</summary>
    public static byte[] RenderAvatar(SkinImage skin, int size)
    {
        skin = Prepare(skin);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var pixels = new byte[checked(size * size * 4)];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int source = ((8 + y * 8 / size) * 64 + 8 + x * 8 / size) * 4;
                int target = (y * size + x) * 4;
                Blend(pixels, target, skin.Pixels, source, 255);
                Blend(pixels, target, skin.Pixels, source + 32 * 4, skin.Pixels[source + 32 * 4 + 3]);
            }
        return pixels;
    }

    private static void Blit(byte[] source, int u, int v, int width, int height,
        byte[] target, int targetWidth, int x, int y, int scale, bool outer)
    {
        for (int py = 0; py < height * scale; py++)
            for (int px = 0; px < width * scale; px++)
            {
                int si = ((v + py / scale) * 64 + u + px / scale) * 4;
                int di = ((y + py) * targetWidth + x + px) * 4;
                Blend(target, di, source, si, outer ? source[si + 3] : (byte)255);
            }
    }

    private static void RenderBox(Frame frame, Part part, bool outer, double yaw, double pitch, double headYaw, double scale)
    {
        double inflation = outer ? (part.IsHead ? 0.5 : 0.25) : 0;
        double hw = part.Width / 2.0 + inflation;
        double hh = part.Height / 2.0 + inflation;
        double hd = part.Depth / 2.0 + inflation;
        double cosY = Math.Cos(yaw), sinY = Math.Sin(yaw);
        double cosP = Math.Cos(pitch), sinP = Math.Sin(pitch);
        double cosH = Math.Cos(headYaw), sinH = Math.Sin(headYaw);
        Span<Point3> points = stackalloc Point3[8];
        for (int index = 0; index < points.Length; index++)
        {
            double x = (index is 1 or 2 or 5 or 6 ? hw : -hw) + part.X;
            double y = (index < 4 ? -hh : hh) + part.Y - 8;
            double z = index is 2 or 3 or 6 or 7 ? hd : -hd;
            if (part.IsHead)
                (x, z) = (x * cosH - z * sinH, x * sinH + z * cosH);
            double rx = x * cosY - z * sinY;
            double rz = x * sinY + z * cosY;
            points[index] = new Point3(frame.Width / 2.0 + rx * scale,
                frame.Height / 2.0 + (y * cosP - rz * sinP) * scale, y * sinP + rz * cosP);
        }

        int u = outer ? part.OuterU : part.U;
        int v = outer ? part.OuterV : part.V;
        int w = part.Width, h = part.Height, d = part.Depth;
        Span<Face> faces = stackalloc Face[]
        {
            new(0, 1, 2, 3, u + d, v, w, d),
            new(7, 6, 5, 4, u + d + w, v, w, d, true),
            new(3, 2, 6, 7, u + d, v + d, w, h),
            new(1, 0, 4, 5, u + 2 * d + w, v + d, w, h),
            new(0, 3, 7, 4, u, v + d, d, h),
            new(2, 1, 5, 6, u + d + w, v + d, d, h),
        };
        foreach (var face in faces)
        {
            int top = face.FlipV ? face.V + face.Height : face.V;
            int bottom = face.FlipV ? face.V : face.V + face.Height;
            var a = WithUv(points[face.A], face.U, top);
            var b = WithUv(points[face.B], face.U + face.Width, top);
            var c = WithUv(points[face.C], face.U + face.Width, bottom);
            var e = WithUv(points[face.D], face.U, bottom);
            // 基础层只画朝向观察者的面；外层保留双面，正确显示衣服边缘与内侧。
            if (!outer && Edge(a, b, c.X, c.Y) <= 1e-9) continue;
            frame.FillTriangle(a, b, c, face, outer);
            frame.FillTriangle(a, c, e, face, outer);
        }
    }

    private static Vertex WithUv(Point3 point, double u, double v) => new(point.X, point.Y, point.Z, u, v);
    private static double Edge(Vertex a, Vertex b, double x, double y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
    private static bool IsTopLeft(Vertex a, Vertex b) => b.Y < a.Y || b.Y == a.Y && b.X > a.X;
    private static bool Inside(double edge, bool topLeft) => edge > 1e-9 || Math.Abs(edge) <= 1e-9 && topLeft;

    private static void Blend(byte[] target, int di, byte[] source, int si, byte alpha)
    {
        if (alpha == 0) return;
        int inverse = 255 - alpha;
        target[di] = (byte)((source[si] * alpha + target[di] * inverse + 127) / 255);
        target[di + 1] = (byte)((source[si + 1] * alpha + target[di + 1] * inverse + 127) / 255);
        target[di + 2] = (byte)((source[si + 2] * alpha + target[di + 2] * inverse + 127) / 255);
        target[di + 3] = (byte)(alpha + (target[di + 3] * inverse + 127) / 255);
    }

    private sealed class Frame
    {
        private readonly byte[] _texture;
        private readonly float[] _depth;
        private int[]? _fragmentHeads;
        private List<Fragment>? _fragments;
        public byte[] Pixels { get; }
        public int Width { get; }
        public int Height { get; }

        public Frame(byte[] texture, int width, int height)
        {
            Width = width;
            Height = height;
            _texture = texture;
            Pixels = new byte[checked(width * height * 4)];
            _depth = new float[checked(width * height)];
            Array.Fill(_depth, float.NegativeInfinity);
        }

        public void FillTriangle(Vertex a, Vertex b, Vertex c, Face face, bool outer)
        {
            double area = Edge(a, b, c.X, c.Y);
            if (Math.Abs(area) < 1e-9) return;
            if (area < 0) { (b, c) = (c, b); area = -area; }
            int minX = Math.Max(0, (int)Math.Ceiling(Math.Min(a.X, Math.Min(b.X, c.X)) - 0.5));
            int maxX = Math.Min(Width - 1, (int)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X)) - 0.5));
            int minY = Math.Max(0, (int)Math.Ceiling(Math.Min(a.Y, Math.Min(b.Y, c.Y)) - 0.5));
            int maxY = Math.Min(Height - 1, (int)Math.Floor(Math.Max(a.Y, Math.Max(b.Y, c.Y)) - 0.5));
            bool topLeftA = IsTopLeft(b, c), topLeftB = IsTopLeft(c, a), topLeftC = IsTopLeft(a, b);
            double inverseArea = 1 / area;
            for (int y = minY; y <= maxY; y++)
            {
                double ea = Edge(b, c, minX + 0.5, y + 0.5);
                double eb = Edge(c, a, minX + 0.5, y + 0.5);
                double ec = Edge(a, b, minX + 0.5, y + 0.5);
                for (int x = minX; x <= maxX; x++, ea -= c.Y - b.Y, eb -= a.Y - c.Y, ec -= b.Y - a.Y)
                {
                    // 共享边仅归属一个三角形，避免半透明外层的对角线被重复混合。
                    if (!Inside(ea, topLeftA) || !Inside(eb, topLeftB) || !Inside(ec, topLeftC)) continue;
                    double wa = ea * inverseArea, wb = eb * inverseArea, wc = ec * inverseArea;
                    double depth = wa * a.Z + wb * b.Z + wc * c.Z;
                    int pixel = y * Width + x;
                    if (depth <= _depth[pixel] + 1e-5) continue;
                    int u = Math.Clamp((int)Math.Floor(wa * a.U + wb * b.U + wc * c.U), face.U, face.U + face.Width - 1);
                    int v = Math.Clamp((int)Math.Floor(wa * a.V + wb * b.V + wc * c.V), face.V, face.V + face.Height - 1);
                    int source = (v * 64 + u) * 4;
                    byte alpha = outer ? _texture[source + 3] : (byte)255;
                    if (alpha == 0) continue;
                    if (alpha == 255)
                    {
                        _depth[pixel] = (float)depth;
                        Buffer.BlockCopy(_texture, source, Pixels, pixel * 4, 3);
                        Pixels[pixel * 4 + 3] = 255;
                    }
                    else AddTransparent(pixel, depth, source);
                }
            }
        }

        private void AddTransparent(int pixel, double depth, int source)
        {
            if (_fragmentHeads is null)
            {
                _fragmentHeads = new int[_depth.Length];
                Array.Fill(_fragmentHeads, -1);
                _fragments = new List<Fragment>();
            }
            var fragments = _fragments!;
            int head = _fragmentHeads[pixel];
            int index = fragments.Count;
            if (head < 0 || depth < fragments[head].Depth)
            {
                fragments.Add(new Fragment(depth, source, head));
                _fragmentHeads[pixel] = index;
                return;
            }
            int previous = head;
            while (fragments[previous].Next >= 0 && fragments[fragments[previous].Next].Depth <= depth)
                previous = fragments[previous].Next;
            var old = fragments[previous];
            fragments.Add(new Fragment(depth, source, old.Next));
            fragments[previous] = old with { Next = index };
        }

        public void CompositeTransparent()
        {
            if (_fragmentHeads is null) return;
            // 不透明面全部完成后，再按每个像素的实际深度由远到近合成；不能依赖整面的平均深度。
            var fragments = _fragments!;
            for (int pixel = 0; pixel < _fragmentHeads.Length; pixel++)
                for (int index = _fragmentHeads[pixel]; index >= 0; index = fragments[index].Next)
                {
                    var fragment = fragments[index];
                    if (fragment.Depth > _depth[pixel] + 1e-5)
                        Blend(Pixels, pixel * 4, _texture, fragment.TextureOffset, _texture[fragment.TextureOffset + 3]);
                }
        }
    }
}
