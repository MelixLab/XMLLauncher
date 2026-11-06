using System.Text.Json;
using SeedMap = RZeroF1.Minecraft.SeedMap;
using SeedMapProfile = LinxLauncher.Models.SeedMapProfile;
using SeedMapWorkspace = LinxLauncher.Models.SeedMapWorkspace;

namespace LinxLauncher.Services;

/// <summary>Light state shared by page and auxiliary window; no images or generators.</summary>
public sealed class SeedMapSession
{
    public static SeedMapSession Current { get; } = new();
    private string FilePath => Path.Combine(Paths.AppData, "seed-map.json");
    public SeedMapWorkspace Workspace { get; private set; } = new();
    public SeedMapProfile Profile { get; private set; } = new();
    public event Action<object?>? Changed;
    private bool _loaded;

    public void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
            {
                Workspace = JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonContext.Default.SeedMapWorkspace)
                    ?? throw new InvalidDataException("地图配置为空。");
                if (Workspace.SchemaVersion != 1) throw new InvalidDataException("不支持的地图配置版本。");
            }
            Profile = Workspace.Profiles.FirstOrDefault(p => p.Id == Workspace.ActiveId) ?? Workspace.Profiles.FirstOrDefault() ?? new();
            Normalize(Profile);
        }
        catch (Exception ex)
        {
            Log.Warn("读取地图配置失败：" + ex.Message);
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
            Workspace = new(); Profile = new();
        }
        if (!Workspace.Profiles.Contains(Profile)) Workspace.Profiles.Add(Profile);
        Workspace.ActiveId = Profile.Id;
    }

    private static void Normalize(SeedMapProfile p)
    {
        p.CenterX = SeedMap.Api.ClampCoordinate(p.CenterX); p.CenterZ = SeedMap.Api.ClampCoordinate(p.CenterZ);
        p.PixelsPerBlock = Math.Clamp(double.IsFinite(p.PixelsPerBlock) ? p.PixelsPerBlock : 0.25, 1.0 / 256, 8);
        p.Y = Math.Clamp(p.Y, -64, 320);
        p.Biomes ??= new(); p.Structures ??= new(); p.Waypoints ??= new();
        p.Seed = SeedMap.Api.ParseSeed(p.SeedText);
    }

    public void Select(string seed, string version, int dimension, int y, object? source)
    {
        long value = SeedMap.Api.ParseSeed(seed);
        var p = Workspace.Profiles.FirstOrDefault(p => p.Seed == value && p.Version == version && p.Dimension == dimension);
        if (p is null)
        {
            p = new() { SeedText = seed.Trim(), Seed = value, Version = version, Dimension = dimension, Y = y };
            Workspace.Profiles.Add(p);
        }
        p.SeedText = seed.Trim(); p.Y = y; p.LastUsedUtc = DateTime.UtcNow; p.InHistory = true;
        Profile = p; Workspace.ActiveId = p.Id;
        Workspace.Profiles = Workspace.Profiles.OrderByDescending(p => p.Favorite).ThenByDescending(p => p.LastUsedUtc)
            .Where((p, i) => p.Favorite || p.Waypoints is { Count: > 0 } || i < 30 || ReferenceEquals(p, Profile)).ToList();
        Notify(source);
    }

    public void Restore(SeedMapProfile profile, object? source)
    {
        Profile = profile; Workspace.ActiveId = profile.Id; profile.LastUsedUtc = DateTime.UtcNow; profile.InHistory = true;
        Normalize(profile); Notify(source);
    }

    public void Notify(object? source) => Changed?.Invoke(source);

    public void Save()
    {
        Directory.CreateDirectory(Paths.AppData);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Workspace, AppJsonContext.Default.SeedMapWorkspace));
        File.Move(temporary, FilePath, true);
    }
}
