using PixelMap.Data;

namespace PixelMap.Rendering;

/// <summary>World-aligned footprint quantization. The voxel boundary follows every
/// source ring, preserving courtyards and disconnected parts with the even/odd fill rule.</summary>
internal static class BuildingStyle
{
    internal static MapFeature Prepare(MapFeature feature, float metersToTile, MapRenderStyle style)
    {
        if (!MapRendering.Blocks(style)) return feature;
        double unit = TileGeometry.Extent / feature.Extent;
        double perimeter = 0;
        foreach (var ring in feature.Paths)
        {
            if (ring.Length == 0) continue;
            var previous = ring[^1];
            foreach (var point in ring)
            {
                perimeter += Math.Abs((double)point.X - previous.X) + Math.Abs((double)point.Y - previous.Y);
                previous = point;
            }
        }
        double requested = metersToTile * (style == MapRenderStyle.Minecraft ? 2 : .5) / unit;
        // A retained footprint has at most roughly 2,048 stair steps. Very large
        // models use larger blocks while ordinary buildings retain metre-scale cells.
        double step = Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(1, Math.Max(requested, perimeter / 2048)))));
        var rings = new List<TilePoint[]>(feature.Paths.Length);
        foreach (var ring in feature.Paths)
        {
            if (ring.Length < 3) continue;
            var points = new List<TilePoint>();
            var first = Grid(ring[^1]);
            foreach (var source in ring)
            {
                var next = Grid(source);
                if (style == MapRenderStyle.Minecraft)
                {
                    long dx = next.X - first.X, dy = next.Y - first.Y;
                    long nx = Math.Abs(dx), ny = Math.Abs(dy), ix = 0, iy = 0;
                    int sx = Math.Sign(dx), sy = Math.Sign(dy);
                    var current = first;
                    Add(current);
                    while (ix < nx || iy < ny)
                    {
                        if (ix < nx && (iy == ny || (1 + 2 * ix) * ny <= (1 + 2 * iy) * nx))
                        { current.X += sx; ix++; }
                        else { current.Y += sy; iy++; }
                        Add(current);
                    }
                }
                else Add(next);
                first = next;
            }
            if (points.Count > 1 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
            if (points.Count >= 3) rings.Add(points.ToArray());
            else rings.Add(ring);

            void Add((long X, long Y) grid)
            {
                var value = new TilePoint((int)Math.Clamp(grid.X * step, int.MinValue, int.MaxValue),
                    (int)Math.Clamp(grid.Y * step, int.MinValue, int.MaxValue));
                if (points.Count > 0 && points[^1] == value) return;
                if (points.Count >= 2)
                {
                    var a = points[^2];
                    var b = points[^1];
                    if ((a.X == b.X && b.X == value.X || a.Y == b.Y && b.Y == value.Y) &&
                        ((long)b.X - a.X) * ((long)value.X - b.X) + ((long)b.Y - a.Y) * ((long)value.Y - b.Y) >= 0)
                    { points[^1] = value; return; }
                }
                points.Add(value);
            }
        }
        // Sub-cell structures retain their source footprint instead of disappearing.
        float top = (float)Math.Clamp(feature.Height ?? 8, .5, 1500);
        float bottom = (float)Math.Clamp(feature.MinHeight ?? 0, 0, top);
        float heightStep = MapRendering.HeightStep(style);
        bottom = MathF.Floor(bottom / heightStep) * heightStep;
        top = Math.Max(bottom + heightStep, MathF.Ceiling(top / heightStep) * heightStep);
        return feature with { Paths = rings.Count == 0 ? feature.Paths : rings.ToArray(), Height = top, MinHeight = bottom };

        (long X, long Y) Grid(TilePoint point) => ((long)Math.Round(point.X / step), (long)Math.Round(point.Y / step));
    }
}
