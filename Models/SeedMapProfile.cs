namespace LinxLauncher.Models;

public sealed class SeedMapWorkspace
{
    public int SchemaVersion { get; set; } = 1;
    public string ActiveId { get; set; } = "";
    public List<SeedMapProfile> Profiles { get; set; } = new();
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class SeedMapProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SeedText { get; set; } = "123";
    public long Seed { get; set; } = 123;
    public string Version { get; set; } = "1.20.6";
    public int Dimension { get; set; }
    public int Y { get; set; } = 64;
    public double CenterX { get; set; }
    public double CenterZ { get; set; }
    public double PixelsPerBlock { get; set; } = 0.25;
    public bool Grid { get; set; }
    public bool Slime { get; set; }
    public bool Favorite { get; set; }
    public bool InHistory { get; set; } = true;
    public bool HideOtherBiomes { get; set; }
    public bool OnlySelectedStructures { get; set; }
    public List<int> Biomes { get; set; } = new();
    public List<int> Structures { get; set; } = new() { 5, 8, 9, 13, 100 };
    public List<SeedMapWaypoint> Waypoints { get; set; } = new();
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SeedMapWaypoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "路径点";
    public int X { get; set; }
    public int Y { get; set; } = 64;
    public int Z { get; set; }
    public int Dimension { get; set; }
    public string Note { get; set; } = "";
}
