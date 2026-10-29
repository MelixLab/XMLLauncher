using System;
using System.IO;

namespace LinxLauncher.Services;

/// <summary>启动器目录布局。游戏目录可在设置里改，其余固定跟着走。</summary>
public static class Paths
{
    /// <summary>启动器自己的数据目录（设置、日志、运行时）</summary>
    public static string AppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinxLauncher");

    private static string _gameDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".linx");

    /// <summary>Minecraft 游戏根目录（.minecraft 的等价物）</summary>
    public static string GameDir
    {
        get => _gameDir;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            _gameDir = value;
            EnsureCore();
        }
    }

    public static string VersionsDir => Path.Combine(GameDir, "versions");
    public static string LibrariesDir => Path.Combine(GameDir, "libraries");
    public static string AssetsDir => Path.Combine(GameDir, "assets");
    public static string AssetsIndexes => Path.Combine(AssetsDir, "indexes");
    public static string AssetsObjects => Path.Combine(AssetsDir, "objects");
    public static string AssetsVirtual => Path.Combine(AssetsDir, "virtual");
    public static string ModsDir => Path.Combine(GameDir, "mods");
    public static string RuntimeDir => Path.Combine(AppData, "runtime");
    public static string CacheDir => Path.Combine(AppData, "cache");
    public static string SettingsFile => Path.Combine(AppData, "settings.json");

    /// <summary>多账户列表（含正版令牌、离线名字、各自皮肤）。</summary>
    public static string AccountsFile => Path.Combine(AppData, "accounts.json");

    /// <summary>皮肤库：每个账户一张 64×64 PNG（文件名记在账户里）。</summary>
    public static string SkinsDir => Path.Combine(AppData, "skins");

    /// <summary>由皮肤渲染出来的头像缓存。</summary>
    public static string AvatarsDir => Path.Combine(AppData, "avatars");

    public static string VersionDir(string id) => Path.Combine(VersionsDir, id);
    public static string VersionJson(string id) => Path.Combine(VersionsDir, id, id + ".json");
    public static string VersionJar(string id) => Path.Combine(VersionsDir, id, id + ".jar");
    public static string VersionNatives(string id) => Path.Combine(VersionsDir, id, "natives");

    public static void EnsureCore()
    {
        foreach (var d in new[] { GameDir, VersionsDir, LibrariesDir, AssetsDir, AssetsIndexes, AssetsObjects, AppData, CacheDir, SkinsDir, AvatarsDir })
            Directory.CreateDirectory(d);
    }

    /// <summary>asset 对象路径：objects/ab/abcdef...</summary>
    public static string AssetObjectPath(string hash) =>
        Path.Combine(AssetsObjects, hash.Length >= 2 ? hash[..2] : "00", hash);

    public static string ToLocal(this string relative) =>
        Path.Combine(LibrariesDir, relative.Replace('/', Path.DirectorySeparatorChar));
}
