using PixelMap.Data;

namespace PixelMap.Rendering;

/// <summary>One double-precision floating origin for vector meshes and geographic image quads.</summary>
internal struct TileProjection(MapCamera camera, int width, int height, int pixelSize)
{
    private readonly double world = camera.WorldSize / pixelSize;
    private readonly (double S, double C) bearing = Math.SinCos(camera.Bearing * Math.PI / 180);
    private readonly double tilt = Math.Cos(camera.PitchRadians);
    private readonly double centerX = camera.CenterX;
    private readonly double centerY = camera.CenterY;
    private readonly double cx = width * .5 / pixelSize;
    private readonly double cy = height * .5 / pixelSize;
    private readonly double sp = Math.Sin(camera.PitchRadians);
    private readonly double lens = camera.Perspective ? pixelSize / MapCamera.Focal(height) : 0;
    private readonly double groundZ = -camera.TargetPixels / pixelSize;
    private readonly double groundDistance = camera.GroundDistance(height);
    private readonly float far = (float)camera.FarLimit(height);
    private int boundsZoom = -1;
    private (int Left, int Top, int Right, int Bottom) bounds;

    public (int First, int Last) Copies(TileId id)
    {
        int n = 1 << id.Z;
        if (boundsZoom != id.Z)
        {
            bounds = camera.TileBounds(width, height, n);
            boundsZoom = id.Z;
        }
        if (id.Y < bounds.Top || id.Y > bounds.Bottom) return (1, 0);
        return ((int)Math.Ceiling((bounds.Left - id.X) / (double)n), (int)Math.Floor((bounds.Right - id.X) / (double)n));
    }

    public readonly (TileTransform Transform, float Scale) Get(TileId id, int copy)
    {
        int n = 1 << id.Z;
        double x = ((double)id.X / n + copy - centerX) * world;
        double y = ((double)id.Y / n - centerY) * world;
        float scale = (float)(world / (n * TileGeometry.Extent));
        double u = bearing.C*x + bearing.S*y, v = -bearing.S*x + bearing.C*y;
        double pw = 1 - (v*sp + groundZ*tilt)*lens, px = scale*bearing.S*sp*lens, py = -scale*bearing.C*sp*lens;
        return (new((float)(cx*pw + u), (float)(cy*pw + v*tilt-groundZ*sp),
            (float)(cx*px + scale*bearing.C), (float)(cx*py + scale*bearing.S),
            (float)(cy*px - scale*bearing.S*tilt), (float)(cy*py + scale*bearing.C*tilt),
            (float)px,(float)py,(float)pw,(float)cx,(float)cy,(float)lens,(float)sp,(float)tilt,(float)-groundZ,far),
            (float)(scale/groundDistance));
    }
}
