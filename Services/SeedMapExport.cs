using SeedMap = RZeroF1.Minecraft.SeedMap;
using SeedMapProfile = LinxLauncher.Models.SeedMapProfile;

namespace LinxLauncher.Services;

/// <summary>
/// XMLLauncher adapter for the SDK export API. It only maps launcher session
/// state to the package model; rendering remains in RZeroF1.Minecraft.SeedMap.
/// </summary>
internal static class SeedMapExport
{
    public static Task<SeedMap.Export.Image> RenderAsync(
        SeedMap.World world,
        SeedMap.Bounds bounds,
        int step,
        uint[] colors,
        SeedMapProfile profile,
        CancellationToken cancellationToken)
    {
        var sdkProfile = new SeedMap.Profile
        {
            SeedText = profile.SeedText,
            Seed = profile.Seed,
            Version = profile.Version,
            Dimension = profile.Dimension,
            Y = profile.Y,
            CenterX = profile.CenterX,
            CenterZ = profile.CenterZ,
            PixelsPerBlock = profile.PixelsPerBlock,
            Grid = profile.Grid,
            Slime = profile.Slime,
            Favorite = profile.Favorite,
            InHistory = profile.InHistory,
            HideOtherBiomes = profile.HideOtherBiomes,
            OnlySelectedStructures = profile.OnlySelectedStructures,
            Biomes = new(profile.Biomes),
            Structures = new(profile.Structures),
            Waypoints = profile.Waypoints.Select(w => new SeedMap.Waypoint
            {
                Id = w.Id,
                Name = w.Name,
                X = w.X,
                Y = w.Y,
                Z = w.Z,
                Dimension = w.Dimension,
                Note = w.Note
            }).ToList(),
            LastUsedUtc = profile.LastUsedUtc
        };
        return SeedMap.Export.RenderAsync(
            world, bounds, step, colors, sdkProfile, cancellationToken);
    }
}
