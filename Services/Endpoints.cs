using System;
using System.Collections.Generic;

namespace LinxLauncher.Services;

/// <summary>
/// 下载端点。默认全部走 BMCLAPI（国内镜像），失败可切回官方源。
/// </summary>
public static class Endpoints
{
    public const string Bmcl = "https://bmclapi2.bangbang93.com";
    public const string Official = "https://launchermeta.mojang.com";

    /// <summary>true = 走国内镜像（默认）；false = 官方源</summary>
    public static bool UseMirror { get; set; } = true;

    public static string ManifestUrl => UseMirror
        ? Bmcl + "/mc/game/version_manifest.json"
        : "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    public static string VersionJson(string id) => UseMirror
        ? $"{Bmcl}/version/{id}/json"
        : $"https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    public static string FabricLoaderList => UseMirror
        ? $"{Bmcl}/fabric-meta/v2/versions/loader"
        : "https://meta.fabricmc.net/v2/versions/loader";

    public static string FabricProfile(string game, string loader) => UseMirror
        ? $"{Bmcl}/fabric-meta/v2/versions/loader/{game}/{loader}/profile/json"
        : $"https://meta.fabricmc.net/v2/versions/loader/{game}/{loader}/profile/json";

    public static string QuiltProfile(string game, string loader) => UseMirror
        ? $"{Bmcl}/quilt-meta/v2/versions/loader/{game}/{loader}/profile/json"
        : $"https://meta.quiltmc.org/v3/versions/loader/{game}/{loader}/profile/json";

    public static string ForgeVersionList(string game) => $"{Bmcl}/forge/minecraft/{game}";

    public static string NeoForgeVersionList => UseMirror
        ? $"{Bmcl}/neoforge/list"
        : "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";

    private static readonly (string From, string To)[] HostMap =
    {
        ("https://launchermeta.mojang.com", ""),
        ("https://piston-meta.mojang.com", ""),
        ("https://piston-data.mojang.com", ""),
        ("https://launcher.mojang.com", ""),
        ("http://launchermeta.mojang.com", ""),
        ("http://piston-meta.mojang.com", ""),
        ("http://piston-data.mojang.com", ""),
        ("http://launcher.mojang.com", ""),
        ("https://resources.download.minecraft.net", "/assets"),
        ("http://resources.download.minecraft.net", "/assets"),
        ("https://libraries.minecraft.net", "/maven"),
        ("http://libraries.minecraft.net", "/maven"),
        ("https://files.minecraftforge.net/maven", "/maven"),
        ("http://files.minecraftforge.net/maven", "/maven"),
        ("https://maven.fabricmc.net", "/maven"),
        ("https://maven.quiltmc.org/repository/release", "/maven"),
        ("https://maven.neoforged.net/releases", "/maven"),
        ("https://maven.neoforged.net/repository/releases", "/maven"),
    };

    /// <summary>把 Mojang / 各 maven 的地址改写到国内镜像。</summary>
    public static string Mirror(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? "";
        if (!UseMirror) return url;

        if (url.StartsWith(Bmcl, StringComparison.OrdinalIgnoreCase)) return url;

        foreach (var (from, suffix) in HostMap)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return Bmcl + suffix + url[from.Length..];
        }

        return url;
    }

    /// <summary>资源文件（assets objects）直链</summary>
    public static string AssetUrl(string hash) => UseMirror
        ? $"{Bmcl}/assets/{hash[..2]}/{hash}"
        : $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
}
