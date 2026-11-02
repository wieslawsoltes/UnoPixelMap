using PixelMap.Data;
using SkiaSharp;

namespace PixelMap.Rendering;

public sealed partial class PixelMapRenderer
{
    private readonly BuildingRasterizer pickRaster = new();
    private static readonly Material PickInk = new(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue,
        uint.MaxValue, uint.MaxValue, uint.MaxValue);

    /// <summary>Inspect the visible surface at a logical screen position. Model picking replays the
    /// orthographic depth pass into a single art pixel, so idle drawing carries no object-ID buffer.</summary>
    public MapFeature? Pick(double x, double y, int width, int height, MapCamera camera, IReadOnlyList<MapTile> tiles)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return null;
        int pixelSize = lastView.PixelSize > 0 ? lastView.PixelSize : 2;
        PrepareVisible(width, height, pixelSize, camera, tiles);
        if (camera.Zoom >= 12)
        {
            float offsetX = (float)Math.Floor(x / pixelSize), offsetY = (float)Math.Floor(y / pixelSize);
            float meters = (float)(camera.WorldSize / pixelSize / (MapCamera.EarthCircumference *
                Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)));
            pickRaster.Begin(1, 1, (float)Math.Sin(camera.PitchRadians), meters);
            Span<uint> hit = stackalloc uint[1];
            MapFeature? surface = null;
            foreach (var (tile, transform, _) in visible)
            {
                var sample = transform with { X = transform.X - offsetX, Y = transform.Y - offsetY };
                foreach (var building in tile.Buildings)
                {
                    hit[0] = 0;
                    pickRaster.Draw(hit, building, sample, PickInk);
                    if (hit[0] != 0) surface = building.Feature;
                }
            }
            if (surface is not null) return surface;
        }
        return PickGround((float)(x / pixelSize), (float)(y / pixelSize), pixelSize);
    }

    private MapFeature? PickGround(float x, float y, int pixelSize)
    {
        MapFeature? best = null;
        int bestRank = int.MaxValue;
        float bestDistance = float.PositiveInfinity;
        foreach (var (tile, t, _) in visible)
        {
            float determinant = t.XX * t.YY - t.XY * t.YX;
            if (determinant == 0) continue;
            float dx = x - t.X, dy = y - t.Y;
            var local = new SKPoint((dx * t.YY - dy * t.XY) / determinant, (dy * t.XX - dx * t.YX) / determinant);
            float margin = 15f / (pixelSize * Math.Max(.00001f, MathF.Sqrt(t.YX * t.YX + t.YY * t.YY)));
            if (local.X < -margin || local.Y < -margin || local.X > TileGeometry.Extent + margin || local.Y > TileGeometry.Extent + margin) continue;
            foreach (var f in tile.Source.Features)
            {
                if (f.Layer == "building" || f.Extent <= 0) continue;
                bool named = !string.IsNullOrEmpty(f.Name);
                int rank;
                float distance;
                float unit = TileGeometry.Extent / f.Extent;
                switch (f.Type)
                {
                    case GeometryType.Point:
                        if (!named) continue;
                        rank = f.Layer == "poi" ? 0 : 2;
                        distance = float.PositiveInfinity;
                        foreach (var path in f.Paths)
                        foreach (var p in path)
                        {
                            var s = t.At(p.X * unit, p.Y * unit);
                            float px = s.X - x, py = s.Y - y;
                            distance = Math.Min(distance, px * px + py * py);
                        }
                        if (distance * pixelSize * pixelSize > 14 * 14) continue;
                        break;
                    case GeometryType.LineString:
                        rank = named ? 1 : 4;
                        if (rank > bestRank) continue;
                        distance = float.PositiveInfinity;
                        foreach (var path in f.Paths)
                        {
                            if (path.Length < 2) continue;
                            var a = t.At(path[0].X * unit, path[0].Y * unit);
                            for (int i = 1; i < path.Length; i++)
                            {
                                var b = t.At(path[i].X * unit, path[i].Y * unit);
                                distance = Math.Min(distance, SegmentDistanceSquared(x, y, a, b));
                                a = b;
                            }
                        }
                        if (distance * pixelSize * pixelSize > 7 * 7) continue;
                        break;
                    case GeometryType.Polygon:
                        rank = named ? 3 : 5;
                        if (rank > bestRank || !Contains(f.Paths, local.X / unit, local.Y / unit, out distance)) continue;
                        distance *= unit * unit * determinant;
                        break;
                    default:
                        continue;
                }
                if (rank < bestRank || rank == bestRank && distance < bestDistance)
                {
                    best = f;
                    bestRank = rank;
                    bestDistance = distance;
                }
            }
        }
        return best;
    }

    private static float SegmentDistanceSquared(float x, float y, SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float length = dx * dx + dy * dy;
        float t = length == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1);
        float px = a.X + dx * t - x, py = a.Y + dy * t - y;
        return px * px + py * py;
    }

    private static bool Contains(TilePoint[][] rings, float x, float y, out float area)
    {
        bool inside = false;
        double twiceArea = 0;
        foreach (var ring in rings)
        {
            if (ring.Length < 3) continue;
            var a = ring[^1];
            foreach (var b in ring)
            {
                if ((a.Y > y) != (b.Y > y) && x < a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y))
                    inside = !inside;
                twiceArea += (long)a.X * b.Y - (long)b.X * a.Y;
                a = b;
            }
        }
        area = (float)Math.Abs(twiceArea);
        return inside;
    }
}
