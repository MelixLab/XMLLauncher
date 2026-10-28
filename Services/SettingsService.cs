using System;
using System.IO;
using System.Text.Json;

namespace LinxLauncher.Services;

public sealed class AppSettings
{
    public string GameDir { get; set; } = "";
    public string PlayerName { get; set; } = "Steve";
    public int MemoryMb { get; set; } = 4096;
    public string? JavaHome { get; set; }
    public string? LastVersionId { get; set; }
    public bool UseMirror { get; set; } = true;
    public int DownloadThreads { get; set; } = 10;
    public string? ExtraJvmArgs { get; set; }
    public string? ExtraGameArgs { get; set; }
    public int GameWidth { get; set; } = 925;
    public int GameHeight { get; set; } = 520;
    public bool Fullscreen { get; set; }

    /// <summary>标题是否使用 Noto Sans（参考 XIGUASecurity 的字体选项）。默认开启。</summary>
    public bool UseNotoSans { get; set; } = true;

    /// <summary>
    /// 是否已经走完首次登录向导（跳过也算走完）。
    /// 老用户升级上来时这个字段是 false，会弹一次向导；因为本地已经有账号/版本，
    /// 向导会自动跳过已经完成的部分，不会让人重复设置。
    /// </summary>
    public bool OnboardingDone { get; set; }

    /// <summary>
    /// 用户体验改进计划：把启动器启动、开游戏这些事件匿名上报给官方服务器。
    /// 默认开启，向导第一页可以取消，设置页也能随时关。
    /// </summary>
    public bool TelemetryEnabled { get; set; } = true;

    /// <summary>设备号：第一次启动随机生成，用来在后台区分「谁在线」，不含任何个人信息。</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>自定义头像图片（复制到配置目录后的路径），为空就用名字首字母。</summary>
    public string? AvatarPath { get; set; }

    /// <summary>官方服务器地址（公告 / 更新检查 / 联机）。留空则用默认本地地址。</summary>
    /// <summary>历史字段：早期允许用户自填服务器。现在固定连官方服务器，这里只为兼容旧配置保留。</summary>
    public string? ServerUrl { get; set; } = "";

    /// <summary>已隐藏的版本 id：装加载器后把原版入口藏起来，列表里只留一个。</summary>
    public List<string> HiddenVersions { get; set; } = new();

    /// <summary>服务器账号的会话令牌（联机房间需要登录）。</summary>
    public string? ServerToken { get; set; }

    /// <summary>服务器账号用户名（仅用于界面显示）。</summary>
    public string? ServerUser { get; set; }

    /// <summary>服务器账号的头像图片（和游戏账号分开，登录后优先显示这张）。</summary>
    public string? ServerAvatarPath { get; set; }

    // ---- 微软正版账号 ----
    public bool UseMsAccount { get; set; }
    public string? MsName { get; set; }
    public string? MsUuid { get; set; }
    public string? MsAccessToken { get; set; }
    public string? MsRefreshToken { get; set; }
    public DateTime? MsExpiresAt { get; set; }

    /// <summary>按实例 id 存的独立配置（没存过就用全局设置）。</summary>
    public Dictionary<string, InstanceSettings> Instances { get; set; } = new();
}

/// <summary>单个版本的独立启动配置。</summary>
public sealed class InstanceSettings
{
    public int? MemoryMb { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool? Fullscreen { get; set; }

    /// <summary>版本隔离：该版本的 mods / saves 等独立存放。</summary>
    public bool Isolated { get; set; }
}

public static class SettingsService
{
    private static readonly object Gate = new();

    private static AppSettings? _current;

    /// <summary>上一次从磁盘读到（或写回磁盘）的样子，用来算「我们到底改了哪些字段」。</summary>
    private static AppSettings? _baseline;

    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    var json = File.ReadAllText(Paths.SettingsFile);
                    var s = Json.Parse<AppSettings>(json);
                    if (s is not null)
                    {
                        // ★ 必须回写到 _current：Save() 那个「_current 为空就直接返回」的守卫
                        //   一旦漏了这句，所有保存都会静默失败。
                        _current = s;
                        _baseline = Clone(s);
                        Apply(s);
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取设置失败：" + ex.Message);
            }

            var fresh = new AppSettings { GameDir = Paths.GameDir };
            _current = fresh;
            _baseline = Clone(fresh);
            Apply(fresh);
            return fresh;
        }
    }

    /// <summary>
    /// 保存设置。
    ///
    /// 直接整份覆盖写有个老问题：同时开两个启动器时，后退出那个会把先改的内容抹掉。
    /// 这里改成「三方合并」：读回磁盘上的最新版本，只把我们**确实改过**的字段盖上去，
    /// 再原子替换文件（先写临时文件再替换），避免写一半被读到半截 JSON。
    /// </summary>
    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                if (_current is null) _current = Load();
                if (_current is null) return;

                var disk = TryReadFromDisk() ?? new AppSettings { GameDir = Paths.GameDir };
                var merged = Merge(disk, _current, _baseline ?? new AppSettings());

                WriteAtomic(merged);

                _baseline = Clone(merged);

                // 内存里的对象是 AppState.Settings 指着的那一份，别换掉它；
                // 只把「磁盘上有、我们没动」的字段回填进来（比如实例配置）。
                SyncFromMerged(merged, _current);
            }
            catch (Exception ex)
            {
                Log.Warn("保存设置失败：" + ex.Message);
            }
        }
    }

    private static AppSettings? TryReadFromDisk()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile)) return null;
            var text = File.ReadAllText(Paths.SettingsFile);
            return Json.Parse<AppSettings>(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 current 相对 baseline 的改动应用到 disk 上。</summary>
    private static AppSettings Merge(AppSettings disk, AppSettings current, AppSettings baseline)
    {
        foreach (var prop in typeof(AppSettings).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.Name == nameof(AppSettings.Instances)) continue;

            var now = prop.GetValue(current);
            var before = prop.GetValue(baseline);
            if (Equals(now, before)) continue; // 我们没改，就保留磁盘上的值

            prop.SetValue(disk, now);
        }

        // Instances 按 key 逐个合并：只覆盖我们改过/新增的 key，不碰别人的
        var currentInst = Json.Write(current.Instances);
        var baselineInst = Json.Write(baseline.Instances);

        if (!string.Equals(currentInst, baselineInst, StringComparison.Ordinal))
        {
            foreach (var pair in current.Instances)
                disk.Instances[pair.Key] = pair.Value;
        }

        return disk;
    }

    private static void SyncFromMerged(AppSettings merged, AppSettings current)
    {
        try
        {
            foreach (var pair in merged.Instances)
            {
                if (!current.Instances.ContainsKey(pair.Key))
                    current.Instances[pair.Key] = pair.Value;
            }
        }
        catch { }
    }

    private static void WriteAtomic(AppSettings settings)
    {
        Directory.CreateDirectory(Paths.AppData);

        var json = Json.Write(settings);
        var tmp = Paths.SettingsFile + ".tmp";

        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                File.WriteAllText(tmp, json);

                if (File.Exists(Paths.SettingsFile))
                    File.Replace(tmp, Paths.SettingsFile, null);
                else
                    File.Move(tmp, Paths.SettingsFile);

                return;
            }
            catch (IOException)
            {
                // 另一个实例正占着文件，等一下再试
                System.Threading.Thread.Sleep(60 * (attempt + 1));
            }
        }

        // 实在换不过去就退回到普通写入，至少让这次改动落地
        File.WriteAllText(Paths.SettingsFile, json);
        try { File.Delete(tmp); } catch { }
    }

    private static AppSettings Clone(AppSettings s)
    {
        try
        {
            return Json.Parse<AppSettings>(Json.Write(s)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static void Apply(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.GameDir)) Paths.GameDir = s.GameDir;
        else s.GameDir = Paths.GameDir;

        Endpoints.UseMirror = s.UseMirror;
        Paths.EnsureCore();
    }
}
