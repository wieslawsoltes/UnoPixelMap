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
        double cx = width * .5 / pixelSize, cy = height * .5 / pixelSize;
        double u = bearing.C*x + bearing.S*y, v = -bearing.S*x + bearing.C*y;
        double sp = Math.Sin(camera.PitchRadians);
        double lens = camera.Perspective ? pixelSize / MapCamera.Focal(height) : 0;
        double groundZ = -camera.TargetPixels / pixelSize;
        double pw = 1 - (v*sp + groundZ*tilt)*lens, px = scale*bearing.S*sp*lens, py = -scale*bearing.C*sp*lens;
        return (new((float)(cx*pw + u), (float)(cy*pw + v*tilt-groundZ*sp),
            (float)(cx*px + scale*bearing.C), (float)(cx*py + scale*bearing.S),
            (float)(cy*px - scale*bearing.S*tilt), (float)(cy*py + scale*bearing.C*tilt),
            (float)px,(float)py,(float)pw,(float)cx,(float)cy,(float)lens,(float)sp,(float)tilt,(float)-groundZ,(float)camera.FarLimit(height)),
            (float)(scale/camera.GroundDistance(height)));
    }
}
