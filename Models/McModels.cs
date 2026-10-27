using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinxLauncher.Models;

public sealed class VersionManifest
{
    public LatestVersion? Latest { get; set; }
    public List<VersionSummary> Versions { get; set; } = new();
}

public sealed class LatestVersion
{
    public string Release { get; set; } = "";
    public string Snapshot { get; set; } = "";
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class VersionSummary
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime ReleaseTime { get; set; }
    public string? Sha1 { get; set; }

    [JsonIgnore] public bool IsRelease => Type == "release";
    [JsonIgnore] public bool IsSnapshot => Type == "snapshot";
    [JsonIgnore] public bool IsOld => Type is "old_beta" or "old_alpha";
    [JsonIgnore] public bool Installed { get; set; }
    [JsonIgnore] public string DateText => ReleaseTime == default ? "" : ReleaseTime.ToString("yy-MM-dd");
    [JsonIgnore] public string TypeLabel => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type
    };
}

public sealed class VersionDetail
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "release";
    public string? MainClass { get; set; }
    public string? Assets { get; set; }
    public AssetIndexInfo? AssetIndex { get; set; }
    public Dictionary<string, DownloadInfo>? Downloads { get; set; }
    public List<Library>? Libraries { get; set; }
    public VersionArguments? Arguments { get; set; }
    public string? MinecraftArguments { get; set; }
    public JavaVersionInfo? JavaVersion { get; set; }
    public string? InheritsFrom { get; set; }
    public string? ReleaseTime { get; set; }
    public LoggingConfig? Logging { get; set; }
}

public sealed class LoggingConfig
{
    public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    public string? Argument { get; set; }
    public LoggingFile? File { get; set; }
}

public sealed class LoggingFile
{
    public string? Id { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Url { get; set; }
}

public sealed class AssetIndexInfo
{
    public string? Id { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public long TotalSize { get; set; }
    public string? Url { get; set; }
}

public sealed class DownloadInfo
{
    public string? Path { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Url { get; set; }
}

public sealed class Library
{
    public string Name { get; set; } = "";
    public LibraryDownloads? Downloads { get; set; }
    public List<Rule>? Rules { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public string? Url { get; set; }
    public ExtractRule? Extract { get; set; }
}

public sealed class ExtractRule
{
    public List<string>? Exclude { get; set; }
}

public sealed class LibraryDownloads
{
    public DownloadInfo? Artifact { get; set; }
    public Dictionary<string, DownloadInfo>? Classifiers { get; set; }
}

public sealed class Rule
{
    public string Action { get; set; } = "allow";
    public OsRule? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Arch { get; set; }
}

public sealed class VersionArguments
{
    public List<JsonElement>? Game { get; set; }
    public List<JsonElement>? Jvm { get; set; }
}

public sealed class JavaVersionInfo
{
    public string? Component { get; set; }
    public int MajorVersion { get; set; }
}

public sealed class AssetIndexFile
{
    public Dictionary<string, AssetObject> Objects { get; set; } = new();
    [JsonIgnore] public bool IsVirtual { get; set; }
    [JsonIgnore] public bool IsMapToResources { get; set; }
}

public sealed class AssetObject
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>服务器上某个加载器（Forge/Fabric/…）的一个版本</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class LoaderVersion
{
    public string Name { get; set; } = "";
    public string LoaderKind { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public string? InstallerUrl { get; set; }
    public bool IsStable { get; set; } = true;
    public string? Description { get; set; }
}

public enum LoaderKind
{
    Vanilla,
    Forge,
    NeoForge,
    Fabric,
    Quilt,
    OptiFine
}

/// <summary>一个已安装（或部分安装）的版本条目</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class LocalVersion
{
    public string Id { get; set; } = "";
    public string Directory { get; set; } = "";
    public VersionDetail? Detail { get; set; }
    public bool HasJar { get; set; }
    public bool IsComplete { get; set; }
    public DateTime LastWrite { get; set; }
    public string Type { get; set; } = "release";
    public string DisplayKind { get; set; } = "原版";
    public string? BaseVersion { get; set; }
    public long SizeBytes { get; set; }

    [JsonIgnore]
    public string SizeText => SizeBytes <= 0
        ? "—"
        : SizeBytes >= 1073741824L
            ? $"{SizeBytes / 1073741824.0:0.0} GB"
            : $"{SizeBytes / 1048576.0:0} MB";

    [JsonIgnore]
    public string Subtitle => IsComplete
        ? $"{DisplayKind} · {SizeText}"
        : $"{DisplayKind} · 未完成";

    [JsonIgnore]
    public string DateText => LastWrite == default ? "—" : LastWrite.ToString("yyyy-MM-dd");

    [JsonIgnore]
    public string Initial => string.IsNullOrEmpty(Id) ? "?" : Id[..1].ToUpperInvariant();
}
