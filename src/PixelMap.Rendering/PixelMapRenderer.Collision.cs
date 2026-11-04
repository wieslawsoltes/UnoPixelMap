using System.Runtime.CompilerServices;
using PixelMap.Data;

namespace PixelMap.Rendering;

public sealed partial class PixelMapRenderer
{
    private static readonly ConditionalWeakTable<MapTile, CollisionStyles> collisionStyles = new();

    /// <summary>Return immutable collision geometry with the same block footprints and
    /// heights as the renderer. Other features and provider metadata are preserved.
    /// Results are cached by source identity and style without retaining evicted tiles.</summary>
    public static MapTile PrepareCollisionTile(MapTile tile, MapRenderStyle style)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (!Enum.IsDefined(style)) throw new ArgumentOutOfRangeException(nameof(style));
        if (!MapRendering.Blocks(style)) return tile;
        var styles = collisionStyles.GetValue(tile, static _ => new());
        lock (styles)
        {
            ref var prepared = ref (style == MapRenderStyle.Minecraft ? ref styles.Minecraft : ref styles.Roblox);
            if (prepared is not null) return prepared;
            int count = 1 << tile.Id.Z;
            double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (tile.Id.Y + .5) / count)));
            float metresToTile = (float)(TileGeometry.Extent * count / (MapCamera.EarthCircumference * Math.Cos(latitude)));
            var features = new MapFeature[tile.Features.Count];
            for (int i = 0; i < features.Length; i++)
            {
                var source = tile.Features[i];
                features[i] = source.Layer == "building" && source.Type == GeometryType.Polygon &&
                    !source.Hide3D && source.Extent > 0 && source.Paths.Length > 0
                    ? BuildingStyle.Prepare(source, metresToTile, style) : source;
            }
            return prepared = tile with { Features = features };
        }
    }

    private sealed class CollisionStyles
    {
        internal MapTile? Minecraft;
        internal MapTile? Roblox;
    }
}
