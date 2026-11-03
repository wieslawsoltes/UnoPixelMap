using PixelMap.Data;
using PixelMap.Routing;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>A map camera over wrapped Web Mercator, with clockwise compass bearing and pitch from nadir.</summary>
public readonly record struct MapCamera(double Latitude, double Longitude, double Zoom, double Bearing = 0, double Pitch = 55, bool Perspective = false, double TargetAltitudeMeters = 0)
{
    public const double MaxLatitude = 85.0511287798066;
    public const double MinZoom = 0;
    public const double MaxZoom = 21;
    public const double VerticalFieldOfView = 45;
    internal const double Near = .10, Far = 6;
    internal static double Focal(double height) => Math.Max(1, height) * 1.2071067811865475;
    internal const double EarthCircumference = 40075016.68557849;
    internal double WorldSize => 512 * Math.Pow(2, Math.Clamp(Zoom, MinZoom, MaxZoom));
    internal double TargetPixels => Perspective ? Math.Max(0, TargetAltitudeMeters) * WorldSize /
        (EarthCircumference * Math.Cos(Math.Clamp(Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180)) : 0;
    internal double GroundDistance(double height) => 1 + TargetPixels / (Focal(height) * Math.Cos(PitchRadians));
    internal double FarLimit(double height) => Math.Max(Far, Math.Min(4096, GroundDistance(height) * 3));
    internal double CenterX => (Longitude + 180) / 360;
    internal double CenterY => MercatorY(Latitude);
    internal double PitchRadians => Math.Clamp(Pitch, 0, 72) * (Math.PI / 180);

    internal static double MercatorY(double latitude) =>
        (1 - Math.Asinh(Math.Tan(Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * (Math.PI / 180))) / Math.PI) * .5;

    internal static double WrapOffset(double offset) => offset - Math.Floor(offset + .5);

    private static (double Latitude, double Longitude) Geographic(double x, double y) =>
        (Math.Atan(Math.Sinh((1 - 2 * Math.Clamp(y, 0, 1)) * Math.PI)) * (180 / Math.PI),
         (x - Math.Floor(x)) * 360 - 180);

    private (double X, double Y) GroundOffset(double dx, double dy, double height = 0)
    {
        var (s, c) = Math.SinCos(Bearing * (Math.PI / 180));
        var (tiltSin, tiltCos) = Math.SinCos(PitchRadians);
        if (Perspective && height > 0)
        {
            double focal = Focal(height);
            double inv = (1 + dy * tiltSin / (focal * tiltCos)) / GroundDistance(height);
            double w = 1 / Math.Clamp(inv, 1 / FarLimit(height), 1 / Near);
            // Reconstruct the finite far-plane intersection for rays which miss the ground.
            dx *= w;
            dy = tiltSin < 1e-10 ? dy*w / tiltCos : (focal * (1 - w) + TargetPixels*tiltCos) / tiltSin;
        }
        else dy /= tiltCos;
        return ((dx * c - dy * s) / WorldSize, (dx * s + dy * c) / WorldSize);
    }

    public (double X, double Y) Project(double latitude, double longitude, double width, double height) =>
        ProjectAtAltitude(latitude, longitude, 0, width, height);

    /// <summary>Returns the bounded ground intersection. Use TryUnproject to distinguish sky.</summary>
    public (double Latitude, double Longitude) Unproject(double x, double y, double width, double height)
    {
        var offset = GroundOffset(x - width * .5, y - height * .5, height);
        return Geographic(CenterX + offset.X, CenterY + offset.Y);
    }

    public bool TryUnproject(double x, double y, double width, double height, out (double Latitude, double Longitude) location)
    {
        location = Unproject(x, y, width, height);
        if (!Perspective) return true;
        double invW = 1 + (y - height * .5) * Math.Tan(PitchRadians) / Focal(height);
        invW /= GroundDistance(height);
        return invW >= 1 / FarLimit(height) && invW <= 1 / Near;
    }

    public MapCamera PanFromTo(double fromX, double fromY, double toX, double toY, double width, double height)
    {
        var a = GroundOffset(fromX - width * .5, fromY - height * .5, height);
        return AtAnchor(CenterX + a.X, CenterY + a.Y, toX, toY, width, height);
    }

    private MapCamera AtAnchor(double worldX, double worldY, double screenX, double screenY, double width, double height)
    {
        var next = this;
        // Mercator's metres-per-pixel varies with latitude. Re-evaluate the elevated
        // focus scale as the anchor changes latitude, keeping an airborne orbit exact.
        for (int i = 0; i < (Perspective && TargetAltitudeMeters > 0 ? 4 : 1); i++)
        {
            var offset = next.GroundOffset(screenX - width*.5, screenY - height*.5, height);
            var center = Geographic(worldX - offset.X, worldY - offset.Y);
            next = next with { Latitude = center.Latitude, Longitude = center.Longitude };
        }
        return next;
    }

    /// <summary>Drag the map by a distance in logical display pixels.</summary>
    public MapCamera PanBy(double dx, double dy)
    {
        var delta = GroundOffset(-dx, -dy);
        var center = Geographic(CenterX + delta.X, CenterY + delta.Y);
        return this with { Latitude = center.Latitude, Longitude = center.Longitude };
    }

    public MapCamera ZoomAt(double steps, double x, double y, double width, double height) =>
        TransformAt(steps, 0, 0, x, y, width, height);

    /// <summary>Orbit around the ground point under the given display position. Deltas are degrees.</summary>
    public MapCamera OrbitAt(double deltaBearing, double deltaPitch, double x, double y, double width, double height) =>
        TransformAt(0, deltaBearing, deltaPitch, x, y, width, height);

    /// <summary>Apply simultaneous scale, rotation and tilt about one geographic anchor.</summary>
    public MapCamera TransformAt(double deltaZoom, double deltaBearing, double deltaPitch,
        double x, double y, double width, double height)
    {
        var next = this with
        {
            Zoom = Math.Clamp(Zoom + deltaZoom, MinZoom, MaxZoom),
            Bearing = WrapOffset((Bearing + deltaBearing) / 360) * 360,
            Pitch = Math.Clamp(Pitch + deltaPitch, 0, 72)
        };
        var before = GroundOffset(x - width * .5, y - height * .5, height);
        return next.AtAnchor(CenterX + before.X, CenterY + before.Y, x, y, width, height);
    }

    /// <summary>Interpolate a Mercator flight, taking the shorter longitude and compass arcs.
    /// Supply eased progress for a cinematic flight, or linear progress for direct manipulation.</summary>
    public MapCamera Interpolate(MapCamera destination, double t)
    {
        if (t <= 0) return this;
        if (t >= 1) return destination;
        var center = Geographic(CenterX + WrapOffset(destination.CenterX - CenterX) * t,
            CenterY + (destination.CenterY - CenterY) * t);
        double bearing = Bearing + WrapOffset((destination.Bearing - Bearing) / 360) * 360 * t;
        return new(center.Latitude, center.Longitude, Zoom + (destination.Zoom - Zoom) * t,
            WrapOffset(bearing / 360) * 360, Pitch + (destination.Pitch - Pitch) * t, t < .5 ? Perspective : destination.Perspective,
            TargetAltitudeMeters + (destination.TargetAltitudeMeters - TargetAltitudeMeters)*t);
    }

    /// <summary>Fit the ordered route to the current bearing and pitch. Padding is in display
    /// pixels on each side; longitudes are unwrapped along the route across the antimeridian.</summary>
    public MapCamera FitRoute(IReadOnlyList<RoutePoint> route, double width, double height, double padding = 64) =>
        FitRoute(route, width, height, new SKRect(0, 0, (float)width, (float)height), padding);

    /// <summary>Fit into a usable screen rectangle while retaining the full display's
    /// perspective lens. The result targets ground level and accounts for asymmetric insets.</summary>
    public MapCamera FitRoute(IReadOnlyList<RoutePoint> route, double width, double height, SKRect viewport, double padding = 64)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Count == 0 || width <= 0 || height <= 0) return this;
        double left = Math.Clamp(viewport.Left, 0, width), right = Math.Clamp(viewport.Right, 0, width);
        double top = Math.Clamp(viewport.Top, 0, height), bottom = Math.Clamp(viewport.Bottom, 0, height);
        double focal = Focal(height);
        var (tiltSin, tiltCos) = Math.SinCos(PitchRadians);
        if (Perspective && tiltSin > 0)
        {
            top = Math.Max(top, height * .5 + focal * tiltCos * (1 / Far - 1) / tiltSin + .01);
            bottom = Math.Min(bottom, height * .5 + focal * tiltCos * (1 / Near - 1) / tiltSin - .01);
        }
        if (right <= left || bottom <= top) return this;
        padding = Math.Clamp(padding, 0, .45 * Math.Min(right - left, bottom - top));
        left += padding; right -= padding; top += padding; bottom -= padding;
        double anchorX = (left + right) * .5, anchorY = (top + bottom) * .5;
        var origin = route[0];
        double originX = (origin.Longitude + 180) / 360, originY = MercatorY(origin.Latitude);
        double lastX = originX, minX = 0, maxX = 0, minY = 0, maxY = 0;
        var (sin, cos) = Math.SinCos(Bearing * Math.PI / 180);
        for (int i = 1; i < route.Count; i++)
        {
            var point = route[i];
            lastX += WrapOffset((point.Longitude + 180) / 360 - lastX);
            double dx = lastX - originX, dy = MercatorY(point.Latitude) - originY;
            double x = dx * cos + dy * sin, y = -dx * sin + dy * cos;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        double midX = (minX + maxX) * .5, midY = (minY + maxY) * .5;
        var center = Geographic(originX + midX * cos - midY * sin, originY + midX * sin + midY * cos);
        double anchorWeight = Perspective ? 1 / (1 + (anchorY - height * .5) * tiltSin / (focal * tiltCos)) : 1;
        double anchorU = (anchorX - width * .5) * anchorWeight;
        double anchorV = (anchorY - height * .5) * anchorWeight;
        left -= width * .5; right -= width * .5; top -= height * .5; bottom -= height * .5;
        double scale = double.PositiveInfinity, unfolded = originX;
        foreach (var point in route)
        {
            unfolded += WrapOffset((point.Longitude + 180) / 360 - unfolded);
            double dx = unfolded - originX, dy = MercatorY(point.Latitude) - originY;
            double u = dx * cos + dy * sin - midX, v = -dx * sin + dy * cos - midY;
            double approach = Perspective ? v * tiltSin / focal : 0;
            Limit(u + right * approach, right * anchorWeight - anchorU);
            Limit(-u - left * approach, anchorU - left * anchorWeight);
            Limit(v * tiltCos + bottom * approach, bottom * anchorWeight - anchorV);
            Limit(-v * tiltCos - top * approach, anchorV - top * anchorWeight);
            Limit(approach, anchorWeight - Near);
            Limit(-approach, Far - anchorWeight);
        }
        double zoom = double.IsFinite(scale) ? Math.Clamp(Math.Log2(scale / 512), MinZoom, MaxZoom) : Zoom;
        var fitted = this with { Latitude = center.Latitude, Longitude = center.Longitude, Zoom = zoom, TargetAltitudeMeters = 0 };
        return fitted.PanFromTo(width * .5, height * .5, anchorX, anchorY, width, height);

        void Limit(double coefficient, double available)
        {
            if (coefficient > 0) scale = Math.Min(scale, available / coefficient);
        }
    }

    public (double X, double Y) ProjectAtAltitude(double latitude, double longitude, double altitudeMeters, double width, double height)
    {
        TryProject(latitude, longitude, altitudeMeters, width, height, out var screen);
        return screen;
    }

    public bool TryProject(double latitude, double longitude, double altitudeMeters, double width, double height,
        out (double X, double Y) screen)
    {
        double dx = WrapOffset((longitude + 180) / 360 - CenterX) * WorldSize;
        double dy = (MercatorY(latitude) - CenterY) * WorldSize;
        var (s, c) = Math.SinCos(Bearing * Math.PI / 180);
        var (sp, cp) = Math.SinCos(PitchRadians);
        double u = dx * c + dy * s, v = -dx * s + dy * c;
        double z = altitudeMeters * WorldSize / (EarthCircumference * Math.Cos(Math.Clamp(Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180));
        z -= TargetPixels;
        double weight = Perspective ? 1 - (v * sp + z * cp) / Focal(height) : 1;
        double divisor = Math.Max(Near, weight);
        screen = (width * .5 + u / divisor, height * .5 + (v * cp - z * sp) / divisor);
        return weight >= Near && weight <= FarLimit(height);
    }

    /// <summary>Visible source tiles in center-first request order, unique across the antimeridian.</summary>
    public TileId[] VisibleTiles(double width, double height, int maxZoom = 14)
    {
        if (width <= 0 || height <= 0) return [];
        var z = (int)Math.Clamp(Math.Floor(Zoom - Math.Log2(GroundDistance(height))), 0, Math.Clamp(maxZoom, 0, 24));
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
                (i & 2) == 0 ? -height * .5 : height * .5 + extra, height);
            xMin = Math.Min(xMin, p.X); xMax = Math.Max(xMax, p.X);
            yMin = Math.Min(yMin, p.Y); yMax = Math.Max(yMax, p.Y);
        }
        return ((int)Math.Floor((CenterX + xMin) * n), Math.Clamp((int)Math.Floor((CenterY + yMin) * n), 0, n - 1),
                (int)Math.Floor((CenterX + xMax) * n), Math.Clamp((int)Math.Floor((CenterY + yMax) * n), 0, n - 1));
    }
}
