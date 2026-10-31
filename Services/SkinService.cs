using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LinxLauncher.Services;

/// <summary>
/// 皮肤管理：导入 / 从正版账号同步 / 上传，以及**自己渲染**头像和全身预览。
///
/// 为什么不继续用 mc-heads 那张现成头像：那是第三方服务，拉的是「服务器上缓存的那个皮肤」，
/// 用户在本机换了皮肤、或者用离线账户配了自定义皮肤，头像根本不会变。
/// 这里直接把皮肤 PNG 读成像素，按 Minecraft 的 UV 图上裁头像 / 拼全身，
/// 所以头像天然跟着皮肤走，断网也能显示。
/// </summary>
public static class SkinService
{
    /// <summary>渲染结果缓存：同一张皮肤、同一个尺寸不用重复算。</summary>
    private static readonly Dictionary<string, ImageSource> Cache = new();

    // ================= 皮肤文件 =================

    /// <summary>合法皮肤：宽度必须是 64，高度 64（现代）或 32（旧版）。</summary>
    public static async Task<(int width, int height)> ProbeAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    /// <summary>把用户选的 PNG 收进皮肤库，并挂到账户上。返回 null 表示成功，否则是错误文案。</summary>
    public static async Task<string?> ImportAsync(GameAccount account, string sourcePath, CancellationToken ct)
    {
        try
        {
            var (w, h) = await ProbeAsync(sourcePath);
            if (w != 64 || (h != 64 && h != 32))
                return $"这张图是 {w}×{h}，不是 Minecraft 皮肤。皮肤必须是 64×64（或旧版 64×32）的 PNG。";

            Directory.CreateDirectory(Paths.SkinsDir);
            var name = account.Id + ".png";
            var dest = Path.Combine(Paths.SkinsDir, name);

            File.Copy(sourcePath, dest, true);
            ct.ThrowIfCancellationRequested();

            account.SkinFile = name;
            account.SkinSource = "local";
            Invalidate(dest);

            // 顺手喂给 CustomSkinLoader（装了的话，离线皮肤进游戏也能看见）
            TryFeedCustomSkinLoader(account);

            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("导入皮肤失败：" + ex.Message);
            return ex.Message;
        }
    }

    /// <summary>从正版账号拉当前皮肤（需要有效的 Minecraft 令牌）。成功返回 null。</summary>
    public static async Task<string?> SyncFromOfficialAsync(GameAccount account, CancellationToken ct)
    {
        if (!account.IsMicrosoft) return "离线账户没有官方皮肤，可以用「导入皮肤」选一张本地 PNG。";

        try
        {
            var token = account.AccessToken;
            if (string.IsNullOrEmpty(token)) return "这个正版账户还没有令牌，请重新登录一次。";

            var profile = await MsAuthService.GetProfileAsync(token!, ct);
            if (profile is null) return "读取角色档案失败，令牌可能过期了，去账户页重新登录一次。";

            if (string.IsNullOrEmpty(profile.SkinUrl))
                return "这个角色在 Minecraft 里还没有设置皮肤。";

            var bytes = await Http.Client.GetByteArrayAsync(profile.SkinUrl!, ct);
            if (bytes.Length == 0) return "下载皮肤失败：服务器返回了空文件。";

            Directory.CreateDirectory(Paths.SkinsDir);
            var name = account.Id + ".png";
            var dest = Path.Combine(Paths.SkinsDir, name);
            await File.WriteAllBytesAsync(dest, bytes, ct);

            account.SkinFile = name;
            account.SkinSource = "mojang";
            account.SkinModel = string.Equals(profile.SkinVariant, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";
            if (!string.IsNullOrEmpty(profile.Uuid)) account.Uuid = GameAccount.NormalizeUuid(profile.Uuid);

            Invalidate(dest);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("同步官方皮肤失败：" + ex.Message);
            return ex.Message;
        }
    }

    /// <summary>把账户当前皮肤上传成正版皮肤（只对正版账户有效）。成功返回 null。</summary>
    public static async Task<string?> UploadToOfficialAsync(GameAccount account, CancellationToken ct)
    {
        if (!account.IsMicrosoft) return "离线账户不能上传皮肤到 Mojang。";

        var path = account.SkinPath;
        if (string.IsNullOrEmpty(path)) return "这个账户还没有皮肤，先导入一张。";

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var ok = await MsAuthService.UploadSkinAsync(account.AccessToken ?? "", bytes, account.SkinModel, ct);
            return ok ? null : "上传失败，令牌可能已过期，重新登录一次再试。";
        }
        catch (Exception ex)
        {
            Log.Warn("上传皮肤失败：" + ex.Message);
            return ex.Message;
        }
    }

    /// <summary>删掉账户的皮肤（回到默认皮肤 / 首字母头像）。</summary>
    public static void ClearSkin(GameAccount account)
    {
        try
        {
            var old = account.SkinPath;
            account.SkinFile = null;
            account.SkinSource = null;
            if (!string.IsNullOrEmpty(old) && File.Exists(old)) File.Delete(old);
        }
        catch (Exception ex)
        {
            Log.Warn("删除皮肤失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 装了 CustomSkinLoader 的整合包会把本地皮肤放在 CustomSkinLoader/LocalSkin/skin/名字.png，
    /// 这里顺手复制一份 —— 这样离线账户换了皮肤，进游戏也能看到（只影响本机）。
    /// </summary>
    private static void TryFeedCustomSkinLoader(GameAccount account)
    {
        try
        {
            var root = Path.Combine(Paths.GameDir, "CustomSkinLoader", "LocalSkin", "skin");
            if (!Directory.Exists(root)) return;

            var skin = account.SkinPath;
            if (string.IsNullOrEmpty(skin)) return;

            var dest = Path.Combine(root, account.Name + ".png");
            File.Copy(skin, dest, true);
            Log.Info("已同步离线皮肤到 CustomSkinLoader：" + dest);
        }
        catch (Exception ex)
        {
            Log.Warn("写入 CustomSkinLoader 失败：" + ex.Message);
        }
    }

    // ================= 渲染 =================

    /// <summary>内置默认皮肤文件名：经典用 Steve，细手臂用 Alex。</summary>
    public static string DefaultSkinFile(string model) =>
        string.Equals(model, "slim", StringComparison.OrdinalIgnoreCase) ? "Alex.png" : "Steve.png";

    /// <summary>
    /// 默认皮肤的完整路径（随程序发布的 Assets/Skins 里的 Steve / Alex）。
    /// 找不到就返回空串，调用方回退到首字母。
    /// </summary>
    public static string DefaultSkinPath(string model)
    {
        var name = DefaultSkinFile(model);

        var direct = Path.Combine(AppContext.BaseDirectory, "Assets", "Skins", name);
        if (File.Exists(direct)) return direct;

        // 开发时从 bin 往上找项目目录（dotnet run 的输出没有带上 Assets 也能用）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "Assets", "Skins", name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return "";
    }

    /// <summary>账户没设置皮肤时用默认皮肤，保证头像和预览永远有东西可看。</summary>
    public static string ResolveSkin(string? skinPath, string model)
    {
        if (!string.IsNullOrEmpty(skinPath) && File.Exists(skinPath)) return skinPath!;
        return DefaultSkinPath(model);
    }

    /// <summary>把皮肤渲染成正方形头像（头 + 帽子层），size 是输出边长像素。</summary>
    public static async Task<ImageSource?> RenderAvatarAsync(string? skinPath, int size, string model = "classic")
    {
        var path = ResolveSkin(skinPath, model);
        if (path.Length == 0) return null;

        var key = $"a|{path}|{size}|{Stamp(path)}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var (pixels, w, h) = await LoadSkinAsync(path);
            var dst = new byte[size * size * 4];

            Blit(pixels, w, h, 8, 8, 8, 8, dst, size, size, 0, 0, size, size);      // 头
            Blit(pixels, w, h, 40, 8, 8, 8, dst, size, size, 0, 0, size, size);     // 帽子层

            var image = await ToImageAsync(dst, size, size);
            Cache[key] = image;
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn("渲染头像失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 渲染全身（正面 / 背面），scale 是每个皮肤像素放大多少倍。
    /// 16×32 的皮肤 → scale 6 时输出 96×192。
    /// </summary>
    public static async Task<ImageSource?> RenderBodyAsync(string? skinPath, string model, bool back, int scale)
    {
        var path = ResolveSkin(skinPath, model);
        if (path.Length == 0) return null;

        var slim = string.Equals(model, "slim", StringComparison.OrdinalIgnoreCase);
        var key = $"b|{path}|{model}|{back}|{scale}|{Stamp(path)}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var (pixels, w, h) = await LoadSkinAsync(path);

            var armW = slim ? 3 : 4;
            var unitW = armW + 8 + armW;      // 4+8+4 = 16
            var unitH = 8 + 12 + 12;          // 32

            var dst = new byte[unitW * scale * unitH * scale * 4];
            var dw = unitW * scale;
            var dh = unitH * scale;

            int RightArmX = 0;
            int BodyX = armW;
            int LeftArmX = armW + 8;

            if (!back)
            {
                Blit(pixels, w, h, 8, 8, 8, 8, dst, dw, dh, BodyX * scale, 0, 8 * scale, 8 * scale);          // 头
                Blit(pixels, w, h, 40, 8, 8, 8, dst, dw, dh, BodyX * scale, 0, 8 * scale, 8 * scale);         // 帽子

                Blit(pixels, w, h, 20, 20, 8, 12, dst, dw, dh, BodyX * scale, 8 * scale, 8 * scale, 12 * scale);   // 身体

                Blit(pixels, w, h, 44, 20, armW, 12, dst, dw, dh, RightArmX * scale, 8 * scale, armW * scale, 12 * scale);
                Blit(pixels, w, h, 36, 52, armW, 12, dst, dw, dh, LeftArmX * scale, 8 * scale, armW * scale, 12 * scale);

                Blit(pixels, w, h, 4, 20, 4, 12, dst, dw, dh, BodyX * scale, 20 * scale, 4 * scale, 12 * scale);
                Blit(pixels, w, h, 20, 52, 4, 12, dst, dw, dh, (BodyX + 4) * scale, 20 * scale, 4 * scale, 12 * scale);
            }
            else
            {
                Blit(pixels, w, h, 24, 8, 8, 8, dst, dw, dh, BodyX * scale, 0, 8 * scale, 8 * scale);          // 后脑
                Blit(pixels, w, h, 56, 8, 8, 8, dst, dw, dh, BodyX * scale, 0, 8 * scale, 8 * scale);          // 帽子背面

                Blit(pixels, w, h, 32, 20, 8, 12, dst, dw, dh, BodyX * scale, 8 * scale, 8 * scale, 12 * scale);

                Blit(pixels, w, h, 48, 20, armW, 12, dst, dw, dh, RightArmX * scale, 8 * scale, armW * scale, 12 * scale);
                Blit(pixels, w, h, 40, 52, armW, 12, dst, dw, dh, LeftArmX * scale, 8 * scale, armW * scale, 12 * scale);

                Blit(pixels, w, h, 8, 20, 4, 12, dst, dw, dh, BodyX * scale, 20 * scale, 4 * scale, 12 * scale);
                Blit(pixels, w, h, 24, 52, 4, 12, dst, dw, dh, (BodyX + 4) * scale, 20 * scale, 4 * scale, 12 * scale);
            }

            var image = await ToImageAsync(dst, dw, dh);
            Cache[key] = image;
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn("渲染全身失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>皮肤换了以后把缓存里跟它相关的项清掉。</summary>
    public static void Invalidate(string? skinPath)
    {
        if (string.IsNullOrEmpty(skinPath)) return;

        var dead = new List<string>();
        foreach (var key in Cache.Keys)
            if (key.Contains(skinPath!, StringComparison.OrdinalIgnoreCase))
                dead.Add(key);

        foreach (var key in dead) Cache.Remove(key);
    }

    // ================= 像素工具 =================

    private static long Stamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; } catch { return 0; }
    }

    /// <summary>给 3D 渲染用：拿到未缩放的原始皮肤像素。</summary>
    public static async Task<SkinRenderer3D.SkinImage?> LoadSkinImageAsync(string? skinPath, string model)
    {
        var path = ResolveSkin(skinPath, model);
        if (path.Length == 0) return null;

        try
        {
            var (pixels, w, h) = await LoadSkinAsync(path);
            return new SkinRenderer3D.SkinImage { Pixels = pixels, Width = w, Height = h };
        }
        catch (Exception ex)
        {
            Log.Warn("读取皮肤失败：" + ex.Message);
            return null;
        }
    }

    private static async Task<(byte[] pixels, int w, int h)> LoadSkinAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return (data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    /// <summary>把源图的一块矩形缩放贴到目标图上（最近邻，Minecraft 的像素味）。</summary>
    private static void Blit(
        byte[] src, int sw, int sh, int sx, int sy, int srcW, int srcH,
        byte[] dst, int dw, int dh, int dx, int dy, int destW, int destH)
    {
        if (destW <= 0 || destH <= 0 || sw <= 0 || sh <= 0) return;

        for (var y = 0; y < destH; y++)
        {
            var ty = dy + y;
            if (ty < 0 || ty >= dh) continue;

            var syy = sy + (int)((long)y * srcH / destH);
            syy = Math.Clamp(syy, 0, sh - 1);

            for (var x = 0; x < destW; x++)
            {
                var tx = dx + x;
                if (tx < 0 || tx >= dw) continue;

                var sxx = sx + (int)((long)x * srcW / destW);
                sxx = Math.Clamp(sxx, 0, sw - 1);

                var si = (syy * sw + sxx) * 4;
                var di = (ty * dw + tx) * 4;

                byte a = src[si + 3];
                if (a == 0) continue;

                if (a == 255)
                {
                    dst[di] = src[si];
                    dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2];
                    dst[di + 3] = 255;
                    continue;
                }

                // 半透明像素按 alpha 叠加到已有内容上（帽子层就是靠这个）
                var sa = a / 255f;
                var da = dst[di + 3] / 255f;
                var oa = sa + da * (1 - sa);
                if (oa <= 0) continue;

                dst[di] = (byte)((src[si] * sa + dst[di] * da * (1 - sa)) / oa);
                dst[di + 1] = (byte)((src[si + 1] * sa + dst[di + 1] * da * (1 - sa)) / oa);
                dst[di + 2] = (byte)((src[si + 2] * sa + dst[di + 2] * da * (1 - sa)) / oa);
                dst[di + 3] = (byte)(oa * 255);
            }
        }
    }

    private static async Task<ImageSource> ToImageAsync(byte[] straightBgra, int w, int h)
    {
        // SoftwareBitmap 用「预乘 alpha」，Straight 的像素要先乘一遍，否则帽子边缘会发黑
        for (var i = 0; i < straightBgra.Length; i += 4)
        {
            var a = straightBgra[i + 3];
            if (a == 255) continue;
            straightBgra[i] = (byte)(straightBgra[i] * a / 255);
            straightBgra[i + 1] = (byte)(straightBgra[i + 1] * a / 255);
            straightBgra[i + 2] = (byte)(straightBgra[i + 2] * a / 255);
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        using (var writer = new DataWriter())
        {
            writer.WriteBytes(straightBgra);
            bitmap.CopyFromBuffer(writer.DetachBuffer());
        }

        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        stream.Seek(0);

        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }
}
