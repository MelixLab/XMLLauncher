using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LinxLauncher.Services;

/// <summary>
/// 「当前用哪个账户进游戏」的唯一入口。
///
/// 真正的账户列表在 <see cref="AccountStore"/>（accounts.json），这里只做三件事：
///   1. 给界面 / 启动流程提供当前账户（名字、UUID、令牌）；
///   2. 正版令牌快过期时自动续期，并把新令牌写回账户列表；
///   3. 把旧代码还在读的 settings 字段同步一份（向导、设置页不用改）。
/// </summary>
public static class AccountService
{
    public static AccountStore Store => AccountStore.Instance;

    public static GameAccount? Current => Store.Current;

    /// <summary>是否正在使用微软正版账号。</summary>
    public static bool IsMicrosoft => Current?.IsMicrosoft == true;

    /// <summary>展示用账号名。</summary>
    public static string DisplayName
    {
        get
        {
            var name = Current?.Name;
            return string.IsNullOrWhiteSpace(name) ? "Steve" : name!;
        }
    }

    /// <summary>当前账户的 UUID（离线账户是现算的 OfflinePlayer UUID）。</summary>
    public static string Uuid => Current?.EffectiveUuid ?? "";

    /// <summary>兼容旧调用：正版头像文件路径。现在头像由皮肤实时渲染，这里只留一个落点。</summary>
    public static string AvatarCacheFile => Path.Combine(Paths.AppData, "ms-avatar.png");

    /// <summary>
    /// 启动游戏前调用：正版账号令牌快过期时自动续期，然后给出要用的 name / uuid / token。
    /// </summary>
    public static async Task<(string name, string uuid, string token)> ResolveForLaunchAsync(CancellationToken ct)
    {
        var account = Current;

        if (account is null)
        {
            var fallback = string.IsNullOrWhiteSpace(AppState.Settings.PlayerName) ? "Steve" : AppState.Settings.PlayerName!;
            return (fallback, GameAccount.OfflineUuid(fallback), "0");
        }

        if (account.IsMicrosoft)
        {
            try
            {
                if (!account.HasUsableToken && !string.IsNullOrEmpty(account.RefreshToken))
                {
                    Log.Info($"正版令牌即将过期，正在为 {account.Name} 续期…");
                    var refreshed = await MsAuthService.RefreshAsync(account.RefreshToken!, ct);
                    if (refreshed is not null)
                    {
                        CopyMicrosoftTokens(account, refreshed);
                        Store.Touch(account);
                    }
                }

                if (!string.IsNullOrEmpty(account.AccessToken))
                    return (account.Name, account.EffectiveUuid, account.AccessToken!);

                Log.Warn("正版令牌续期失败，这次先用离线身份进游戏。");
            }
            catch (Exception ex)
            {
                Log.Warn("正版令牌续期失败：" + ex.Message);
            }
        }

        return (account.Name, account.EffectiveUuid, "0");
    }

    // ================= 账户操作 =================

    /// <summary>添加 / 更新一个离线账户并切成当前。</summary>
    public static GameAccount AddOffline(string name)
    {
        var acc = Store.Add(new GameAccount
        {
            Kind = "offline",
            Name = string.IsNullOrWhiteSpace(name) ? "Steve" : name.Trim(),
            Uuid = GameAccount.OfflineUuid(name)
        });

        SyncLegacySettings(acc);
        return acc;
    }

    /// <summary>把一条微软登录结果写进账户列表（同名账户会被更新），并切成当前。</summary>
    public static GameAccount Apply(MsAccount account)
    {
        var acc = Store.Add(new GameAccount
        {
            Kind = "microsoft",
            Name = account.Name,
            Uuid = GameAccount.NormalizeUuid(account.Uuid),
            AccessToken = account.AccessToken,
            RefreshToken = account.RefreshToken,
            ExpiresAt = account.ExpiresAt,
            SkinModel = string.Equals(account.SkinVariant, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic"
        });

        SyncLegacySettings(acc);
        return acc;
    }

    /// <summary>切到指定账户（界面上点「使用」）。</summary>
    public static void SetCurrent(string accountId)
    {
        Store.SetCurrent(accountId);
        SyncLegacySettings(Current);
    }

    /// <summary>设置页改了「玩家名（离线）」：同步到当前离线账户，避免两处名字不一样。</summary>
    public static void RenameCurrentOffline(string name)
    {
        var acc = Current;
        if (acc is null || acc.IsMicrosoft) return;
        if (string.IsNullOrWhiteSpace(name) || string.Equals(acc.Name, name, StringComparison.Ordinal)) return;

        acc.Name = name.Trim();
        acc.Uuid = GameAccount.OfflineUuid(acc.Name);
        Store.Touch(acc);
    }

    /// <summary>切回离线账户（不动正版刷新令牌，下次还能直接切回来）。</summary>
    public static void UseOffline()
    {
        var offline = Store.Accounts.FirstOrDefault(a => !a.IsMicrosoft);
        if (offline is null)
        {
            AddOffline(string.IsNullOrWhiteSpace(AppState.Settings.PlayerName) ? "Steve" : AppState.Settings.PlayerName!);
            return;
        }

        Store.SetCurrent(offline.Id);
        SyncLegacySettings(offline);
    }

    public static void SwitchToMicrosoft()
    {
        var ms = Store.Accounts.FirstOrDefault(a => a.IsMicrosoft);
        if (ms is null) return;

        Store.SetCurrent(ms.Id);
        SyncLegacySettings(ms);
    }

    /// <summary>退出当前正版账号：从列表里删掉它（离线账户不受影响）。</summary>
    public static void Logout()
    {
        var account = Current;
        if (account is null) return;

        if (account.IsMicrosoft)
        {
            Store.Remove(account.Id);
            UseOffline();
        }

        SyncLegacySettings(Current);
    }

    public static void Remove(string accountId)
    {
        Store.Remove(accountId);
        SyncLegacySettings(Current);
    }

    /// <summary>账户信息（续期、换皮肤）保存并通知界面。</summary>
    public static void Save(GameAccount account) => Store.Touch(account);

    // ================= 皮肤 / 头像 =================

    /// <summary>兼容旧调用：现在不下载 mc-heads 头像了，皮肤渲染由 SkinService 负责。</summary>
    public static Task<string?> EnsureAvatarAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>当前账户的头像（没有皮肤返回 null，界面回退到首字母）。</summary>
    public static Task<Microsoft.UI.Xaml.Media.ImageSource?> GetAvatarAsync(int size = 60)
    {
        var account = Current;
        return SkinService.RenderAvatarAsync(account?.SkinPath, size, account?.SkinModel ?? "classic");
    }

    // ================= 内部 =================

    private static void CopyMicrosoftTokens(GameAccount target, MsAccount source)
    {
        target.AccessToken = source.AccessToken;
        target.RefreshToken = string.IsNullOrEmpty(source.RefreshToken) ? target.RefreshToken : source.RefreshToken;
        target.ExpiresAt = source.ExpiresAt;
        target.Uuid = GameAccount.NormalizeUuid(source.Uuid);
        target.Name = source.Name;
        if (!string.IsNullOrEmpty(source.SkinVariant))
            target.SkinModel = string.Equals(source.SkinVariant, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";
    }

    /// <summary>
    /// 旧代码（新手向导、设置页、托盘菜单）还在读 settings 里的 MsName / PlayerName / UseMsAccount，
    /// 这里把当前账户同步过去，免得两套数据打架。
    /// </summary>
    private static void SyncLegacySettings(GameAccount? account)
    {
        try
        {
            var s = AppState.Settings;
            if (account is null)
            {
                s.UseMsAccount = false;
                return;
            }

            if (account.IsMicrosoft)
            {
                s.UseMsAccount = true;
                s.MsName = account.Name;
                s.MsUuid = account.Uuid;
                s.MsAccessToken = account.AccessToken;
                s.MsRefreshToken = account.RefreshToken;
                s.MsExpiresAt = account.ExpiresAt;
            }
            else
            {
                s.UseMsAccount = false;
                s.PlayerName = account.Name;
            }

            SettingsService.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("同步旧设置字段失败：" + ex.Message);
        }
    }
}
