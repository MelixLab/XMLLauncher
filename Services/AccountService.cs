using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>启动器当前使用的账号（正版 / 离线）。</summary>
public static class AccountService
{
    /// <summary>是否正在使用微软正版账号。</summary>
    public static bool IsMicrosoft => AppState.Settings.UseMsAccount &&
                                      !string.IsNullOrEmpty(AppState.Settings.MsRefreshToken);

    /// <summary>展示用账号名。</summary>
    public static string DisplayName =>
        IsMicrosoft && !string.IsNullOrWhiteSpace(AppState.Settings.MsName)
            ? AppState.Settings.MsName!
            : (string.IsNullOrWhiteSpace(AppState.Settings.PlayerName) ? "Steve" : AppState.Settings.PlayerName);

    /// <summary>正版角色头像缓存路径（没有登录正版时返回 null）。</summary>
    public static string AvatarCacheFile => Path.Combine(Paths.AppData, "ms-avatar.png");

    /// <summary>
    /// 启动游戏前调用：正版账号令牌快过期时自动续期，然后给出要用的 name / uuid / token。
    /// 离线时 uuid 交给 LaunchService 按玩家名生成。
    /// </summary>
    public static async Task<(string name, string uuid, string token)> ResolveForLaunchAsync(CancellationToken ct)
    {
        var s = AppState.Settings;

        if (IsMicrosoft)
        {
            try
            {
                var expires = s.MsExpiresAt ?? DateTime.MinValue;
                if (string.IsNullOrEmpty(s.MsAccessToken) || expires <= DateTime.Now.AddMinutes(2))
                {
                    Log.Info("正版令牌即将过期，正在续期…");
                    var refreshed = await MsAuthService.RefreshAsync(s.MsRefreshToken!, ct);
                    if (refreshed is not null)
                    {
                        Apply(refreshed);
                        SettingsService.Save();
                    }
                }

                if (!string.IsNullOrEmpty(s.MsAccessToken) && !string.IsNullOrEmpty(s.MsUuid))
                    return (s.MsName ?? "Player", s.MsUuid!, s.MsAccessToken!);
            }
            catch (Exception ex)
            {
                Log.Warn("正版令牌续期失败：" + ex.Message);
            }
        }

        var name = string.IsNullOrWhiteSpace(s.PlayerName) ? "Steve" : s.PlayerName;
        return (name, "", "0");
    }

    /// <summary>把链路上拿到的账号写进设置。</summary>
    public static void Apply(MsAccount account)
    {
        var s = AppState.Settings;
        s.UseMsAccount = true;
        s.MsName = account.Name;
        s.MsUuid = account.Uuid;
        s.MsAccessToken = account.AccessToken;
        s.MsRefreshToken = account.RefreshToken;
        s.MsExpiresAt = account.ExpiresAt;
    }

    /// <summary>切回离线账号（不动正版刷新令牌，下次还能直接切回来）。</summary>
    public static void UseOffline()
    {
        AppState.Settings.UseMsAccount = false;
        SettingsService.Save();
    }

    public static void SwitchToMicrosoft()
    {
        if (!string.IsNullOrEmpty(AppState.Settings.MsRefreshToken))
        {
            AppState.Settings.UseMsAccount = true;
            SettingsService.Save();
        }
    }

    /// <summary>退出正版账号：清掉令牌与头像缓存。</summary>
    public static void Logout()
    {
        var s = AppState.Settings;
        s.UseMsAccount = false;
        s.MsName = null;
        s.MsUuid = null;
        s.MsAccessToken = null;
        s.MsRefreshToken = null;
        s.MsExpiresAt = null;
        SettingsService.Save();

        try { if (File.Exists(AvatarCacheFile)) File.Delete(AvatarCacheFile); } catch { }
    }

    /// <summary>
    /// 拉正版角色头像（64×64，来自 mc-heads），缓存到配置目录，避免每次启动都联网。
    /// 返回可用文件路径，失败返回 null。
    /// </summary>
    public static async Task<string?> EnsureAvatarAsync(CancellationToken ct)
    {
        var uuid = AppState.Settings.MsUuid;
        if (!IsMicrosoft || string.IsNullOrEmpty(uuid)) return null;

        if (File.Exists(AvatarCacheFile)) return AvatarCacheFile;

        try
        {
            var url = $"https://mc-heads.net/avatar/{uuid}/64";
            var bytes = await Http.Client.GetByteArrayAsync(url, ct);
            if (bytes.Length == 0) return null;

            Directory.CreateDirectory(Paths.AppData);
            await File.WriteAllBytesAsync(AvatarCacheFile, bytes, ct);
            return AvatarCacheFile;
        }
        catch (Exception ex)
        {
            Log.Warn("下载正版头像失败：" + ex.Message);
            return null;
        }
    }
}
