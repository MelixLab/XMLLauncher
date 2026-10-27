using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>服务器下发的一条公告。</summary>
public sealed class Announcement
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Level { get; set; } = "info";
    public long UpdatedAt { get; set; }
}

/// <summary>更新检查结果。</summary>
public sealed class UpdateInfo
{
    public string Current { get; set; } = "";
    public string Latest { get; set; } = "";
    public bool Mandatory { get; set; }
    public string Changelog { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    /// <summary>服务端给的文件哈希，用来确认下载到的确实是新版本。</summary>
    public string Sha256 { get; set; } = "";
    public long CheckedAt { get; set; }
}

/// <summary>
/// 与官方服务器交互的最小集合：公告、版本更新检查。
/// 所有请求都很短（10 秒超时），服务器连不上就当作"没有公告/不需要更新"，
/// 绝不因为服务器挂了导致启动器不能用。
/// </summary>
public static class RemoteService
{
    /// <summary>
    /// 当前客户端版本。以前是写在代码里的常量，每次发版都得改源码；
    /// 现在直接读程序集版本（打包脚本 pack.ps1 -Version 1.2.0 注入），发版不会再漏改。
    /// </summary>
    public static string ClientVersion { get; } = ReadClientVersion();

    private static string ReadClientVersion()
    {
        var v = typeof(RemoteService).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private const string Platform = "win-x64";

    /// <summary>
    /// 更新状态缓存。强制更新一旦被服务器下发过，就会写在这里；
    /// 之后即使断网启动，也能凭这份记录拦住用户 —— 这正是需求里要的效果。
    /// </summary>
    public static string StateFile => Path.Combine(Paths.AppData, "update-state.json");

    /// <summary>
    /// 官方服务器地址，写死、不可修改：公告、版本更新、账号与联机都走这里。
    /// 不在界面上暴露 —— 用户不需要知道，也不该有机会填错。
    /// </summary>
    /// <summary>
    /// 官方服务器地址从 LocalSecrets.cs 取 —— 真实地址不进仓库，
    /// 仓库里只有 LocalSecrets.example.cs.txt 这个模板。
    /// </summary>
    public const string OfficialServer = LocalSecrets.ServerUrl;

    /// <summary>
    /// 实际使用的地址。界面上不可修改，但允许用环境变量 XML_SERVER_OVERRIDE 临时指向本机，
    /// 这是给自己测「更新 / 联机」用的开关，普通用户不会碰到。
    /// </summary>
    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("XML_SERVER_OVERRIDE") is { Length: > 0 } o ? o.TrimEnd('/') : OfficialServer;

    // ================= 公告 =================

    public static async Task<Announcement?> GetAnnouncementAsync(CancellationToken outer = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var text = await Http.Client.GetStringAsync($"{BaseUrl}/api/announcement", cts.Token);
            using var doc = JsonDocument.Parse(text);

            if (!doc.RootElement.TryGetProperty("announcement", out var ann) ||
                ann.ValueKind != JsonValueKind.Object)
                return null;

            return new Announcement
            {
                Title = Str(ann, "title"),
                Content = Str(ann, "content"),
                Level = Str(ann, "level"),
                UpdatedAt = ann.TryGetProperty("updated_at", out var t) && t.TryGetInt64(out var ts) ? ts : 0
            };
        }
        catch (Exception ex)
        {
            Log.Info("拉取公告失败（不影响使用）：" + ex.Message);
            return null;
        }
    }

    // ================= 更新检查 =================

    public static async Task<UpdateInfo?> CheckUpdateAsync(CancellationToken outer = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var url = $"{BaseUrl}/api/update/check?version={ClientVersion}&platform={Platform}";
            var text = await Http.Client.GetStringAsync(url, cts.Token);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var info = new UpdateInfo
            {
                Current = Str(root, "current"),
                Latest = Str(root, "latest"),
                Mandatory = root.TryGetProperty("mandatory", out var m) && m.ValueKind == JsonValueKind.True,
                Changelog = Str(root, "changelog"),
                DownloadUrl = Str(root, "download_url"),
                Sha256 = Str(root, "sha256"),
                CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            if (string.IsNullOrEmpty(info.Latest))
            {
                // 服务器还没登记任何版本：清掉旧的强制更新状态，别把用户永久锁死
                ClearState();
                return null;
            }

            SaveState(info);
            return info;
        }
        catch (Exception ex)
        {
            Log.Info("检查更新失败（离线启动）：" + ex.Message);
            return null;
        }
    }

    /// <summary>断网时用的兜底：读上次写下的状态。</summary>
    public static UpdateInfo? LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            return Json.Parse<UpdateInfo>(File.ReadAllText(StateFile));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveState(UpdateInfo info)
    {
        try
        {
            Directory.CreateDirectory(Paths.AppData);
            File.WriteAllText(StateFile, Json.Write(info));
        }
        catch (Exception ex)
        {
            Log.Warn("写入更新状态失败：" + ex.Message);
        }
    }

    private static void ClearState()
    {
        try { if (File.Exists(StateFile)) File.Delete(StateFile); } catch { }
    }

    // ================= 可选更新的提示记录 =================

    /// <summary>
    /// 记录"哪个版本已经提示过用户"。
    /// 非强制更新只打扰一次：点过「稍后再说」之后，同一版本不再弹。
    /// </summary>
    private static string PromptedFile => Path.Combine(Paths.AppData, "update-prompted.txt");

    public static bool HasPrompted(string version)
    {
        try
        {
            return !string.IsNullOrEmpty(version)
                && File.Exists(PromptedFile)
                && File.ReadAllText(PromptedFile).Trim() == version;
        }
        catch { return false; }
    }

    public static void MarkPrompted(string version)
    {
        try
        {
            Directory.CreateDirectory(Paths.AppData);
            File.WriteAllText(PromptedFile, version ?? "");
        }
        catch (Exception ex)
        {
            Log.Warn("写入更新提示记录失败：" + ex.Message);
        }
    }

    /// <summary>a 是否比 b 新（点分版本号）。</summary>
    public static bool IsNewer(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return false;
        if (string.IsNullOrEmpty(b)) return true;

        static int[] Parts(string v)
        {
            var pieces = v.Replace('-', '.').Split('.');
            var result = new int[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
                result[i] = int.TryParse(pieces[i], out var n) ? n : 0;
            return result;
        }

        var pa = Parts(a);
        var pb = Parts(b);
        var len = Math.Max(pa.Length, pb.Length);

        for (int i = 0; i < len; i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x > y;
        }

        return false;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
