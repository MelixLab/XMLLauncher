using LinxLauncher.Models;
using SeedMap = RZeroF1.Minecraft.SeedMap.SeedMap;

namespace LinxLauncher.Services;

/// <summary>
/// XMLLauncher adapter for the SDK export API. It only maps launcher session
/// state to the package model; rendering remains in RZeroF1.Minecraft.SeedMap.
/// </summary>
internal static class SeedMapExport
{
    public static Task<RZeroF1.Minecraft.SeedMap.SeedMapExport.Image> RenderAsync(
        SeedMap.World world,
        SeedMap.Bounds bounds,
        int step,
        uint[] colors,
        SeedMapProfile profile,
        CancellationToken cancellationToken)
    {
        var sdkProfile = new RZeroF1.Minecraft.SeedMap.SeedMapProfile
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
            Waypoints = profile.Waypoints.Select(w => new RZeroF1.Minecraft.SeedMap.SeedMapWaypoint
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
        return RZeroF1.Minecraft.SeedMap.SeedMapExport.RenderAsync(
            world, bounds, step, colors, sdkProfile, cancellationToken);
    }
}
