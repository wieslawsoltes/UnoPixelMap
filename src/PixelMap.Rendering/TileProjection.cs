using PixelMap.Data;

namespace PixelMap.Rendering;

/// <summary>One double-precision floating origin for vector meshes and geographic image quads.</summary>
internal readonly struct TileProjection(MapCamera camera, int width, int height, int pixelSize)
{
    private readonly double world = camera.WorldSize / pixelSize;
    private readonly (double S, double C) bearing = Math.SinCos(camera.Bearing * Math.PI / 180);
    private readonly double tilt = Math.Cos(camera.PitchRadians);

    public (int First, int Last) Copies(TileId id)
    {
        int n = 1 << id.Z;
        var area = camera.TileBounds(width, height, n);
        if (id.Y < area.Top || id.Y > area.Bottom) return (1, 0);
        return ((int)Math.Ceiling((area.Left - id.X) / (double)n), (int)Math.Floor((area.Right - id.X) / (double)n));
    }

    public (TileTransform Transform, float Scale) Get(TileId id, int copy)
    {
        int n = 1 << id.Z;
        double x = ((double)id.X / n + copy - camera.CenterX) * world;
        double y = ((double)id.Y / n - camera.CenterY) * world;
        float scale = (float)(world / (n * TileGeometry.Extent));
        return (new((float)(width * .5 / pixelSize + bearing.C * x + bearing.S * y),
            (float)(height * .5 / pixelSize + (-bearing.S * x + bearing.C * y) * tilt),
            (float)(scale * bearing.C), (float)(scale * bearing.S),
            (float)(-scale * bearing.S * tilt), (float)(scale * bearing.C * tilt)), scale);
    }
}
