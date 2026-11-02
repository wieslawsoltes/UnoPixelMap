using PixelMap.Data;

namespace PixelMap.Rendering;

/// <summary>An orthographic camera over wrapped Web Mercator, with clockwise compass bearing and pitch from nadir.</summary>
public readonly record struct MapCamera(double Latitude, double Longitude, double Zoom, double Bearing = 0, double Pitch = 55)
{
    public const double MaxLatitude = 85.0511287798066;
    public const double MinZoom = 0;
    public const double MaxZoom = 21;
    internal const double EarthCircumference = 40075016.68557849;
    internal double WorldSize => 512 * Math.Pow(2, Math.Clamp(Zoom, MinZoom, MaxZoom));
    internal double CenterX => (Longitude + 180) / 360;
    internal double CenterY => MercatorY(Latitude);
    internal double PitchRadians => Math.Clamp(Pitch, 0, 72) * (Math.PI / 180);

    internal static double MercatorY(double latitude) =>
        (1 - Math.Asinh(Math.Tan(Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * (Math.PI / 180))) / Math.PI) * .5;

    internal static double WrapOffset(double offset) => offset - Math.Floor(offset + .5);

    private static (double Latitude, double Longitude) Geographic(double x, double y) =>
        (Math.Atan(Math.Sinh((1 - 2 * Math.Clamp(y, 0, 1)) * Math.PI)) * (180 / Math.PI),
         (x - Math.Floor(x)) * 360 - 180);

    private (double X, double Y) GroundOffset(double dx, double dy)
    {
        var (s, c) = Math.SinCos(Bearing * (Math.PI / 180));
        dy /= Math.Cos(PitchRadians);
        return ((dx * c - dy * s) / WorldSize, (dx * s + dy * c) / WorldSize);
    }

    public (double X, double Y) Project(double latitude, double longitude, double width, double height)
    {
        var x = WrapOffset((longitude + 180) / 360 - CenterX) * WorldSize;
        var y = (MercatorY(latitude) - CenterY) * WorldSize;
        var (s, c) = Math.SinCos(Bearing * (Math.PI / 180));
        return (width * .5 + x * c + y * s, height * .5 + (-x * s + y * c) * Math.Cos(PitchRadians));
    }

    public (double Latitude, double Longitude) Unproject(double x, double y, double width, double height)
    {
        var offset = GroundOffset(x - width * .5, y - height * .5);
        return Geographic(CenterX + offset.X, CenterY + offset.Y);
    }

    /// <summary>Drag the map by a distance in logical display pixels.</summary>
    public MapCamera PanBy(double dx, double dy)
    {
        var delta = GroundOffset(-dx, -dy);
        var center = Geographic(CenterX + delta.X, CenterY + delta.Y);
        return this with { Latitude = center.Latitude, Longitude = center.Longitude };
    }

    public MapCamera ZoomAt(double steps, double x, double y, double width, double height)
    {
        var next = this with { Zoom = Math.Clamp(Zoom + steps, MinZoom, MaxZoom) };
        var before = GroundOffset(x - width * .5, y - height * .5);
        var after = next.GroundOffset(x - width * .5, y - height * .5);
        var center = Geographic(CenterX + before.X - after.X, CenterY + before.Y - after.Y);
        return next with { Latitude = center.Latitude, Longitude = center.Longitude };
    }

    /// <summary>Visible source tiles in center-first request order, unique across the antimeridian.</summary>
    public TileId[] VisibleTiles(double width, double height, int maxZoom = 14)
    {
        if (width <= 0 || height <= 0) return [];
        var z = (int)Math.Clamp(Math.Floor(Zoom), 0, Math.Clamp(maxZoom, 0, 24));
        var n = 1 << z;
        var bounds = TileBounds(width, height, n);
        var cx = CenterX * n;
        var cy = CenterY * n;
        var distances = new Dictionary<TileId, double>();
        for (int y = bounds.Top; y <= bounds.Bottom; y++)
        for (int x = bounds.Left; x <= bounds.Right; x++)
        {
            var key = new TileId(z, ((x % n) + n) % n, y);
            double dx = x + .5 - cx, dy = y + .5 - cy;
            double distance = dx * dx + dy * dy;
            if (!distances.TryGetValue(key, out var prior) || distance < prior) distances[key] = distance;
        }
        return distances.OrderBy(p => p.Value).Select(p => p.Key).ToArray();
    }

    internal (int Left, int Top, int Right, int Bottom) TileBounds(double width, double height, int n)
    {
        double extra = Zoom >= 13 ? Math.Min(height, 450 * WorldSize / (EarthCircumference * Math.Cos(Math.Clamp(Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180)) * Math.Sin(PitchRadians)) : 0;
        double xMin = double.PositiveInfinity, yMin = double.PositiveInfinity;
        double xMax = double.NegativeInfinity, yMax = double.NegativeInfinity;
        for (int i = 0; i < 4; i++)
        {
            var p = GroundOffset((i & 1) == 0 ? -width * .5 : width * .5,
                (i & 2) == 0 ? -height * .5 : height * .5 + extra);
            xMin = Math.Min(xMin, p.X); xMax = Math.Max(xMax, p.X);
            yMin = Math.Min(yMin, p.Y); yMax = Math.Max(yMax, p.Y);
        }
        return ((int)Math.Floor((CenterX + xMin) * n), Math.Clamp((int)Math.Floor((CenterY + yMin) * n), 0, n - 1),
                (int)Math.Floor((CenterX + xMax) * n), Math.Clamp((int)Math.Floor((CenterY + yMax) * n), 0, n - 1));
    }
}
