using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace LinxLauncher.Services;

/// <summary>
/// 一个游戏账户。
///
/// 微软账户：Name / Uuid 来自角色档案，AccessToken + RefreshToken 用来进游戏和续期。
/// 离线账户：只有名字，Uuid 按 Java 的 OfflinePlayer 规则算（同 HMCL / PCL2）。
/// 两种账户都能挂一张皮肤（64×64 PNG，本地皮肤库），头像由这张皮肤实时渲染。
/// </summary>
public sealed class GameAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>"microsoft" 或 "offline"。</summary>
    public string Kind { get; set; } = "offline";

    public string Name { get; set; } = "";

    /// <summary>32 位无横线小写 UUID；离线账户为空，由 <see cref="EffectiveUuid"/> 现算。</summary>
    public string? Uuid { get; set; }

    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>"classic" 或 "slim"（细手臂）。</summary>
    public string SkinModel { get; set; } = "classic";

    /// <summary>皮肤库里的文件名（不含目录）。为空表示还没设置皮肤。</summary>
    public string? SkinFile { get; set; }

    /// <summary>皮肤来源："local"（自己导入）/ "mojang"（从正版账号拉下来的）。</summary>
    public string? SkinSource { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public bool IsMicrosoft => string.Equals(Kind, "microsoft", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string KindText => IsMicrosoft ? "正版账号" : "离线账号";

    [JsonIgnore]
    public string ModelText => SkinModel == "slim" ? "Slim · 细手臂" : "Classic · 经典";

    /// <summary>进游戏 / 显示用的 UUID：正版用档案里的，离线按名字算。</summary>
    [JsonIgnore]
    public string EffectiveUuid => !string.IsNullOrWhiteSpace(Uuid) ? Uuid! : OfflineUuid(Name);

    /// <summary>皮肤 PNG 的完整路径；没设置或文件被删了返回空串。</summary>
    [JsonIgnore]
    public string SkinPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SkinFile)) return "";
            var full = Path.Combine(Paths.SkinsDir, SkinFile!);
            return File.Exists(full) ? full : "";
        }
    }

    [JsonIgnore]
    public bool HasSkin => SkinPath.Length > 0;

    /// <summary>令牌还能不能直接用（留 2 分钟余量）。</summary>
    [JsonIgnore]
    public bool HasUsableToken =>
        IsMicrosoft && !string.IsNullOrEmpty(AccessToken) &&
        ExpiresAt is { } exp && exp > DateTime.Now.AddMinutes(2);

    /// <summary>Java 的 OfflinePlayer UUID（nameUUIDFromBytes("OfflinePlayer:" + name)）。</summary>
    public static string OfflineUuid(string name)
    {
        try
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + (name ?? "")));
            hash[6] = (byte)((hash[6] & 0x0F) | 0x30);   // version 3
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);   // variant
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>正版 UUID 从接口拿到时可能带横线，统一去掉。</summary>
    public static string NormalizeUuid(string? uuid) =>
        string.IsNullOrWhiteSpace(uuid) ? "" : uuid!.Replace("-", "").Trim().ToLowerInvariant();

    public GameAccount Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Name = Name,
        Uuid = Uuid,
        AccessToken = AccessToken,
        RefreshToken = RefreshToken,
        ExpiresAt = ExpiresAt,
        SkinModel = SkinModel,
        SkinFile = SkinFile,
        SkinSource = SkinSource,
        AddedAt = AddedAt
    };
}

/// <summary>accounts.json 的磁盘格式。</summary>
public sealed class AccountBook
{
    public string? CurrentId { get; set; }
    public List<GameAccount> Accounts { get; set; } = new();
}

/// <summary>
/// 账户仓库：读写 accounts.json、维护「当前用哪个账户」。
///
/// 老版本把正版账号直接摊在 settings.json 里（MsName / MsRefreshToken …）。
/// <see cref="Load"/> 会把那份数据迁移成一条正版账户，迁移后只在 accounts.json 里维护，
/// settings 里的旧字段保持原样不动（回退到旧版本也不会丢账号）。
/// </summary>
public sealed class AccountStore
{
    private static AccountStore? _current;
    private static readonly object Gate = new();

    public static AccountStore Instance
    {
        get
        {
            lock (Gate) return _current ??= Load();
        }
    }

    public List<GameAccount> Accounts { get; } = new();

    public string? CurrentId { get; private set; }

    /// <summary>当前账户；列表为空时返回 null（调用方自己兜底）。</summary>
    public GameAccount? Current
    {
        get
        {
            var hit = Accounts.FirstOrDefault(a => a.Id == CurrentId);
            if (hit is not null) return hit;

            var first = Accounts.FirstOrDefault();
            if (first is not null) CurrentId = first.Id;
            return first;
        }
    }

    /// <summary>列表或当前账户变了就通知界面刷新。</summary>
    public event Action? Changed;

    private void Raise() => Changed?.Invoke();

    public static AccountStore Load()
    {
        var store = new AccountStore();

        try
        {
            if (File.Exists(Paths.AccountsFile))
            {
                var book = Json.Parse<AccountBook>(File.ReadAllText(Paths.AccountsFile));
                if (book is not null)
                {
                    foreach (var acc in book.Accounts ?? new List<GameAccount>())
                    {
                        if (string.IsNullOrWhiteSpace(acc.Id)) acc.Id = Guid.NewGuid().ToString("n");
                        if (string.IsNullOrWhiteSpace(acc.Kind)) acc.Kind = "offline";
                        if (string.IsNullOrWhiteSpace(acc.SkinModel)) acc.SkinModel = "classic";
                        store.Accounts.Add(acc);
                    }
                    store.CurrentId = book.CurrentId;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取账户列表失败：" + ex.Message);
        }

        store.MigrateLegacy();
        store.EnsureAtLeastOne();
        return store;
    }

    /// <summary>把 settings.json 里的单账户字段搬进账户列表（只搬一次）。</summary>
    private void MigrateLegacy()
    {
        var s = AppState.Settings;

        var legacyMs = s.MsUuid is not null && !string.IsNullOrEmpty(s.MsRefreshToken);
        if (legacyMs && Accounts.All(a => !a.IsMicrosoft))
        {
            Accounts.Add(new GameAccount
            {
                Kind = "microsoft",
                Name = string.IsNullOrWhiteSpace(s.MsName) ? "Player" : s.MsName!,
                Uuid = GameAccount.NormalizeUuid(s.MsUuid),
                AccessToken = s.MsAccessToken,
                RefreshToken = s.MsRefreshToken,
                ExpiresAt = s.MsExpiresAt,
                AddedAt = DateTime.Now
            });

            if (s.UseMsAccount) CurrentId = Accounts[^1].Id;
            Log.Info("已把旧版单账户迁移到 accounts.json");
        }

        // 离线名字也搬过来：旧版 PlayerName 就是唯一那个离线身份
        var offlineName = string.IsNullOrWhiteSpace(s.PlayerName) ? "Steve" : s.PlayerName!;
        if (Accounts.All(a => !string.Equals(a.Name, offlineName, StringComparison.OrdinalIgnoreCase)))
        {
            Accounts.Add(new GameAccount { Kind = "offline", Name = offlineName, AddedAt = DateTime.Now });
            if (string.IsNullOrEmpty(CurrentId)) CurrentId = Accounts[^1].Id;
        }

        Save();
    }

    /// <summary>无论如何至少留一个能用的账户，避免界面空白 / 启动时没名字。</summary>
    private void EnsureAtLeastOne()
    {
        if (Accounts.Count > 0)
        {
            if (CurrentId is null || Accounts.All(a => a.Id != CurrentId)) CurrentId = Accounts[0].Id;
            return;
        }

        var name = string.IsNullOrWhiteSpace(AppState.Settings.PlayerName) ? "Steve" : AppState.Settings.PlayerName!;
        var acc = new GameAccount { Kind = "offline", Name = name, AddedAt = DateTime.Now };
        Accounts.Add(acc);
        CurrentId = acc.Id;
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.AppData);
            var book = new AccountBook { CurrentId = CurrentId, Accounts = Accounts.ToList() };
            var json = Json.Write(book);

            var tmp = Paths.AccountsFile + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(Paths.AccountsFile)) File.Replace(tmp, Paths.AccountsFile, null);
            else File.Move(tmp, Paths.AccountsFile);
        }
        catch (Exception ex)
        {
            Log.Warn("保存账户列表失败：" + ex.Message);
        }
    }

    public GameAccount Add(GameAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Id)) account.Id = Guid.NewGuid().ToString("n");
        account.AddedAt = DateTime.Now;

        // 同名同类型的账户不重复添加（离线账户尤其容易撞）
        var dup = Accounts.FirstOrDefault(a =>
            a.IsMicrosoft == account.IsMicrosoft &&
            string.Equals(a.Name, account.Name, StringComparison.OrdinalIgnoreCase));
        if (dup is not null)
        {
            dup.Kind = account.Kind;
            dup.Uuid = account.Uuid ?? dup.Uuid;
            dup.AccessToken = account.AccessToken ?? dup.AccessToken;
            dup.RefreshToken = account.RefreshToken ?? dup.RefreshToken;
            dup.ExpiresAt = account.ExpiresAt ?? dup.ExpiresAt;
            CurrentId = dup.Id;
            Save();
            Raise();
            return dup;
        }

        Accounts.Add(account);
        CurrentId = account.Id;
        Save();
        Raise();
        return account;
    }

    public void SetCurrent(string id)
    {
        if (Accounts.All(a => a.Id != id)) return;
        CurrentId = id;
        Save();
        Raise();
    }

    /// <summary>删除账户并顺手清掉它自己的皮肤文件。</summary>
    public void Remove(string id)
    {
        var acc = Accounts.FirstOrDefault(a => a.Id == id);
        if (acc is null) return;

        Accounts.Remove(acc);

        try
        {
            if (!string.IsNullOrEmpty(acc.SkinFile))
            {
                var owned = Accounts.All(a => !string.Equals(a.SkinFile, acc.SkinFile, StringComparison.OrdinalIgnoreCase));
                var path = Path.Combine(Paths.SkinsDir, acc.SkinFile!);
                if (owned && File.Exists(path)) File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("删除账户皮肤失败：" + ex.Message);
        }

        if (CurrentId == id) CurrentId = Accounts.FirstOrDefault()?.Id;
        EnsureAtLeastOne();
        Save();
        Raise();
    }

    /// <summary>账户信息有变（续期、换皮肤）时调用：落盘 + 通知界面。</summary>
    public void Touch(GameAccount account)
    {
        if (Accounts.All(a => a.Id != account.Id)) Accounts.Add(account);
        Save();
        Raise();
    }

    public GameAccount? FindById(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Accounts.FirstOrDefault(a => a.Id == id);
}
