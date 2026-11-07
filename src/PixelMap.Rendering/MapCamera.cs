using PixelMap.Data;
using PixelMap.Routing;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>A map camera over wrapped Web Mercator, with clockwise compass bearing and pitch from nadir.</summary>
public readonly partial record struct MapCamera(double Latitude, double Longitude, double Zoom, double Bearing = 0, double Pitch = 55, bool Perspective = false, double TargetAltitudeMeters = 0)
{
    /// <summary>Absolute surface elevation at the local reference plane, in metres above sea level.
    /// TargetAltitudeMeters remains the focus height above this plane.</summary>
    public double GroundAltitudeMeters { get; init; }
    /// <summary>Keep the eye at its absolute position when terrain data or relief settings change.</summary>
    public bool IsFreeLook { get; init; }
    /// <summary>Physical far-plane distance for a free camera. Zero retains the map lens policy.</summary>
    public double ViewDistanceMeters { get; init; }
    /// <summary>Viewport height used to encode a free eye into its focus and lens.</summary>
    public double ViewportHeight { get; init; }
    public const double MaxLatitude = 85.0511287798066;
    /// <summary>A free eye's virtual focus can extend beyond the Mercator data extent.
    /// The geographic poles themselves cannot encode a finite Mercator coordinate.</summary>
    public const double MaxFreeLookLatitude = 89.99999999999999;
    public const double MinZoom = 0;
    public const double MaxZoom = 26;
    public const double MaxPitch = 179.5;
    public const double MaxOrthographicPitch = 89.5;
    public const double VerticalFieldOfView = 45;
    public const double FreeLookNearMeters = .08;
    public const double MaxViewDistanceMeters = 1e7;
    internal const double Near = .10, Far = 6;
    internal static double Focal(double height) => Math.Max(1, height) * 1.2071067811865475;
    internal const double EarthCircumference = 40075016.68557849;
    internal double WorldSize => 512 * Math.Pow(2, Math.Clamp(Zoom, MinZoom, MaxZoom));
    internal double TargetPixels => Perspective ? TargetAltitudeMeters * WorldSize /
        (EarthCircumference * Math.Cos(Math.Clamp(Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180)) : 0;
    internal double GroundDistance(double height)
    {
        if (IsFreeLook) return 1;
        double cosine = Math.Cos(PitchRadians);
        if (cosine <= 1e-6) return Math.Max(1, Math.Abs(TargetPixels) / Focal(height));
        return Math.Clamp(1 + TargetPixels / (Focal(height) * cosine), Near, 1e7);
    }
    internal double NearLimit(double height) => IsFreeLook && Perspective
        ? FreeLookNearMeters * PixelsPerMeter / Focal(height) : Near;
    internal double FarLimit(double height) => IsFreeLook && Perspective
        ? Math.Max(NearLimit(height) * 2, Math.Min(ViewDistanceMeters > 0 ? ViewDistanceMeters : 6000, MaxViewDistanceMeters) * PixelsPerMeter / Focal(height))
        : Math.Max(Far, Math.Min(4096, GroundDistance(height) * 3));
    internal double CenterX => (Longitude + 180) / 360;
    internal double CenterY => IsFreeLook && Perspective
        ? MercatorYUnclamped(Math.Clamp(Latitude, -MaxFreeLookLatitude, MaxFreeLookLatitude)) : MercatorY(Latitude);
    internal double PitchRadians => ClampPitch(Pitch, Perspective) * (Math.PI / 180);

    public static double ClampPitch(double pitch, bool perspective) =>
        Math.Clamp(pitch, 0, perspective ? MaxPitch : MaxOrthographicPitch);

    /// <summary>Create a perspective camera at an absolute eye position. Pitch is measured
    /// from nadir, with 90 degrees horizontal. The near plane is eight centimetres from
    /// the eye and the far plane is expressed in metres along the viewing direction.</summary>
    public static MapCamera FromEye(double latitude, double longitude, double absoluteEyeAltitudeMeters,
        double bearing, double pitchDegreesFromNadir, double width, double height,
        double viewDistanceMeters = 6000, double groundAltitudeMeters = 0)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(absoluteEyeAltitudeMeters) ||
            !double.IsFinite(bearing) || !double.IsFinite(pitchDegreesFromNadir) || !double.IsFinite(width) ||
            !double.IsFinite(height) || width <= 0 || height <= 0 || !double.IsFinite(viewDistanceMeters) ||
            viewDistanceMeters <= FreeLookNearMeters || !double.IsFinite(groundAltitudeMeters))
            throw new ArgumentOutOfRangeException(nameof(latitude), "The eye, lens and viewport must be finite and have a positive range.");
        var camera = new MapCamera(Math.Clamp(latitude, -MaxLatitude, MaxLatitude), longitude,
            24, WrapOffset(bearing / 360) * 360, ClampPitch(pitchDegreesFromNadir, true), true)
        {
            IsFreeLook = true,
            ViewDistanceMeters = Math.Min(viewDistanceMeters, MaxViewDistanceMeters),
            GroundAltitudeMeters = groundAltitudeMeters,
            ViewportHeight = height
        };
        return camera.WithEye(camera.Latitude, longitude, absoluteEyeAltitudeMeters, height);
    }

    /// <summary>Recover the perspective eye in geographic coordinates and absolute metres.</summary>
    public (double Latitude, double Longitude, double AltitudeMeters) GetEye(double viewportHeight)
    {
        var (sin, cos) = Math.SinCos(Bearing * Math.PI / 180);
        double focal = Focal(viewportHeight), offset = focal * Math.Sin(PitchRadians) / WorldSize;
        var eye = Geographic(CenterX - offset * sin, CenterY + offset * cos);
        return (eye.Latitude, eye.Longitude,
            GroundAltitudeMeters + TargetAltitudeMeters + focal * Math.Cos(PitchRadians) / PixelsPerMeter);
    }

    /// <summary>Retain the physical free-camera eye when the viewport changes size.</summary>
    public MapCamera ForViewport(double width, double height)
    {
        if (!IsFreeLook || !Perspective || height == ViewportHeight) return this;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return this;
        if (!double.IsFinite(ViewportHeight) || ViewportHeight <= 0) return this with { ViewportHeight = height };
        var eye = GetEye(ViewportHeight);
        return WithEye(eye.Latitude, eye.Longitude, eye.AltitudeMeters, height);
    }

    private MapCamera WithEye(double latitude, double longitude, double altitudeMeters, double height)
    {
        var (sin, cos) = Math.SinCos(Bearing * Math.PI / 180);
        double distance = Focal(height) * Math.Sin(PitchRadians) / WorldSize;
        var focus = Geographic((longitude + 180) / 360 + distance * sin, MercatorY(latitude) - distance * cos,
            clampToMap: !IsFreeLook || !Perspective);
        var next = this with { Latitude = focus.Latitude, Longitude = focus.Longitude, ViewportHeight = height };
        return next with
        {
            TargetAltitudeMeters = altitudeMeters - GroundAltitudeMeters -
                Focal(height) * Math.Cos(PitchRadians) / next.PixelsPerMeter
        };
    }

    /// <summary>Orient a free perspective eye so an absolute world position appears at
    /// the requested screen position, retaining the physical eye and lens. Returns false
    /// when the target is clipped or the screen ray requires roll or an unsupported pitch.
    /// Already aligned poses retain their orientation within 0.0001 screen pixels.</summary>
    public bool TryAimAt(double latitude, double longitude, double absoluteAltitudeMeters,
        double x, double y, double width, double height, out MapCamera aimed)
    {
        const double tolerance = .0001;
        aimed = this;
        if (!IsFreeLook || !Perspective || !double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            !double.IsFinite(absoluteAltitudeMeters) || !double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return false;
        var next = ForViewport(width, height);
        // At nadir, a target on the optical axis does not determine yaw. Tiny
        // Mercator round-trip residues must not rotate an already valid pose.
        if (next.TryProject(latitude, longitude, absoluteAltitudeMeters, width, height, out var current) &&
            Math.Abs(current.X - x) + Math.Abs(current.Y - y) < tolerance)
        {
            aimed = next;
            return true;
        }
        var eye = next.GetEye(height);
        double east = WrapOffset((longitude - eye.Longitude) / 360) * WorldSize;
        double south = (MercatorY(latitude) - MercatorY(eye.Latitude)) * WorldSize;
        double horizontal = Math.Sqrt(east * east + south * south);
        double heading = Math.Atan2(east, -south);
        double u = (x - width * .5) / Focal(height), v = (y - height * .5) / Focal(height);
        double rayLength = Math.Sqrt(1 + u * u + v * v), verticalAngle = Math.Atan(v);
        double originalBearing = Bearing * Math.PI / 180, originalPitch = PitchRadians;
        if (!double.IsFinite(rayLength)) return false;
        // Yaw first fixes the horizontal screen component. Pitch then solves the
        // remaining two-dimensional rotation. Re-encoding the eye changes Mercator's
        // local height scale slightly, so repeat the analytic solution to convergence.
        for (int iteration = 0; iteration < 4; iteration++)
        {
            double vertical = (absoluteAltitudeMeters - eye.AltitudeMeters) * next.PixelsPerMeter;
            double distance = Math.Sqrt(horizontal * horizontal + vertical * vertical);
            if (!double.IsFinite(distance) || distance == 0) return false;
            double bestScore = -1, bestBearing = 0, bestPitch = 0, bestPitchError = double.PositiveInfinity;
            if (horizontal == 0)
            {
                if (Math.Abs(u) > 1e-12) return false;
                SelectOrientation(originalBearing, 0);
            }
            else
            {
                double offset = u * distance / (rayLength * horizontal);
                double angle = Math.Asin(Math.Clamp(offset, -1, 1));
                double forward = horizontal * Math.Cos(angle);
                SelectOrientation(heading - angle, forward);
                SelectOrientation(heading - (Math.PI - angle), -forward);
            }
            if (bestScore < 0) return false;
            next = (next with { Bearing = bestBearing, Pitch = bestPitch })
                .WithEye(eye.Latitude, eye.Longitude, eye.AltitudeMeters, height);
            if (next.TryProject(latitude, longitude, absoluteAltitudeMeters, width, height, out var screen) &&
                Math.Abs(screen.X - x) + Math.Abs(screen.Y - y) < tolerance)
            {
                aimed = next;
                return true;
            }

            void SelectOrientation(double bearing, double forward)
            {
                double pitch = WrapOffset((Math.Atan2(forward, -vertical) + verticalAngle) / (2 * Math.PI)) * (2 * Math.PI);
                double maximum = MaxPitch * Math.PI / 180;
                // Coordinate round-off at a pitch limit must not switch to the
                // opposite yaw branch during the Mercator scale iteration.
                double pitchError = Math.Max(0, Math.Abs(pitch - Math.Clamp(pitch, 0, maximum)) - 1e-9);
                pitch = Math.Clamp(pitch, 0, maximum);
                double yawDelta = WrapOffset((bearing - originalBearing) / (2 * Math.PI)) * (2 * Math.PI);
                double score = Math.Cos(yawDelta * .5) * Math.Cos((pitch - originalPitch) * .5);
                if (pitchError > bestPitchError || pitchError == bestPitchError && score <= bestScore) return;
                bestPitchError = pitchError;
                bestScore = score;
                bestBearing = WrapOffset(bearing / (2 * Math.PI)) * 360;
                bestPitch = pitch * 180 / Math.PI;
            }
        }
        return false;
    }

    internal static double MercatorY(double latitude) =>
        MercatorYUnclamped(Math.Clamp(latitude, -MaxLatitude, MaxLatitude));

    private static double MercatorYUnclamped(double latitude) =>
        (1 - Math.Asinh(Math.Tan(latitude * (Math.PI / 180))) / Math.PI) * .5;

    internal static double WrapOffset(double offset) => offset - Math.Floor(offset + .5);

    private static (double Latitude, double Longitude) Geographic(double x, double y, bool clampToMap = true) =>
        (Math.Clamp(Math.Atan(Math.Sinh((1 - 2 * (clampToMap ? Math.Clamp(y, 0, 1) : y)) * Math.PI)) * (180 / Math.PI),
            -MaxFreeLookLatitude, MaxFreeLookLatitude),
         (x - Math.Floor(x)) * 360 - 180);

    private (double X, double Y) GroundOffset(double dx, double dy, double height = 0)
    {
        var (s, c) = Math.SinCos(Bearing * (Math.PI / 180));
        var (tiltSin, tiltCos) = Math.SinCos(PitchRadians);
        if (Perspective && height > 0)
        {
            double focal = Focal(height);
            double eyeHeight = focal * tiltCos + TargetPixels;
            double rayHeight = dy * tiltSin + focal * tiltCos;
            double weight = rayHeight == 0 ? double.PositiveInfinity : eyeHeight / rayHeight;
            double w = weight > 0 ? Math.Clamp(weight, NearLimit(height), FarLimit(height)) : FarLimit(height);
            // A finite point on the same ray also supports horizontal and upward views.
            dx *= w;
            dy = tiltCos * dy * w + tiltSin * focal * (1 - w);
        }
        else dy /= tiltCos;
        return ((dx * c - dy * s) / WorldSize, (dx * s + dy * c) / WorldSize);
    }

    public (double X, double Y) Project(double latitude, double longitude, double width, double height) =>
        ProjectAtAltitude(latitude, longitude, GroundAltitudeMeters, width, height);

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
        var (sin, cos) = Math.SinCos(PitchRadians);
        double denominator = (y - height * .5) * sin + Focal(height) * cos;
        double weight = (Focal(height) * cos + TargetPixels) / denominator;
        return double.IsFinite(weight) && weight >= NearLimit(height) && weight <= FarLimit(height);
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
        for (int i = 0; i < (Perspective && TargetAltitudeMeters != 0 ? 4 : 1); i++)
        {
            var offset = next.GroundOffset(screenX - width*.5, screenY - height*.5, height);
            var center = Geographic(worldX - offset.X, worldY - offset.Y, clampToMap: !IsFreeLook || !Perspective);
            next = next with { Latitude = center.Latitude, Longitude = center.Longitude };
        }
        return next;
    }

    /// <summary>Drag the map by a distance in logical display pixels.</summary>
    public MapCamera PanBy(double dx, double dy)
    {
        var delta = GroundOffset(-dx, -dy);
        var center = Geographic(CenterX + delta.X, CenterY + delta.Y, clampToMap: !IsFreeLook || !Perspective);
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
            Pitch = ClampPitch(Pitch + deltaPitch, Perspective)
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
        var next = new MapCamera(center.Latitude, center.Longitude, Zoom + (destination.Zoom - Zoom) * t,
            WrapOffset(bearing / 360) * 360, Pitch + (destination.Pitch - Pitch) * t, t < .5 ? Perspective : destination.Perspective,
            TargetAltitudeMeters + (destination.TargetAltitudeMeters - TargetAltitudeMeters)*t)
        {
            GroundAltitudeMeters = GroundAltitudeMeters + (destination.GroundAltitudeMeters - GroundAltitudeMeters) * t,
            IsFreeLook = t < .5 ? IsFreeLook : destination.IsFreeLook,
            ViewDistanceMeters = ViewDistanceMeters + (destination.ViewDistanceMeters - ViewDistanceMeters) * t,
            ViewportHeight = t < .5 ? ViewportHeight : destination.ViewportHeight
        };
        if (!IsFreeLook || !destination.IsFreeLook || !Perspective || !destination.Perspective ||
            ViewportHeight <= 0 || destination.ViewportHeight <= 0) return next;
        // A free camera encodes its eye behind a moving focus. Interpolating that
        // focus makes a stationary eye orbit when only the look direction changes.
        var eye = GetEye(ViewportHeight);
        var destinationEye = destination.GetEye(destination.ViewportHeight);
        var position = Geographic((eye.Longitude + 180) / 360 +
            WrapOffset((destinationEye.Longitude - eye.Longitude) / 360) * t,
            MercatorY(eye.Latitude) + (MercatorY(destinationEye.Latitude) - MercatorY(eye.Latitude)) * t);
        return next.WithEye(position.Latitude, position.Longitude,
            eye.AltitudeMeters + (destinationEye.AltitudeMeters - eye.AltitudeMeters) * t, next.ViewportHeight);
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
        if (IsFreeLook || Perspective && Pitch >= 90)
        {
            var overview = this with { IsFreeLook = false, ViewDistanceMeters = 0, ViewportHeight = 0, Pitch = Math.Min(Pitch, 55) };
            return overview.FitRoute(route, width, height, viewport, padding);
        }
        double left = Math.Clamp(viewport.Left, 0, width), right = Math.Clamp(viewport.Right, 0, width);
        double top = Math.Clamp(viewport.Top, 0, height), bottom = Math.Clamp(viewport.Bottom, 0, height);
        double focal = Focal(height);
        var (tiltSin, tiltCos) = Math.SinCos(PitchRadians);
        if (Perspective && tiltSin > 0 && tiltCos > 1e-6)
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
        double z = (altitudeMeters - GroundAltitudeMeters) * WorldSize / (EarthCircumference * Math.Cos(Math.Clamp(Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180));
        z -= TargetPixels;
        double weight = Perspective ? 1 - (v * sp + z * cp) / Focal(height) : 1;
        double near = NearLimit(height);
        double divisor = Math.Max(near, weight);
        screen = (width * .5 + u / divisor, height * .5 + (v * cp - z * sp) / divisor);
        return weight >= near && weight <= FarLimit(height);
    }

    /// <summary>Visible source tiles in center-first request order, unique across the antimeridian.</summary>
    public TileId[] VisibleTiles(double width, double height, int maxZoom = 14)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height)) return [];
        var z = (int)Math.Clamp(Math.Floor(Zoom - Math.Log2(GroundDistance(height))), 0, Math.Clamp(maxZoom, 0, 24));
        var n = 1 << z;
        var bounds = TileBounds(width, height, n);
        // A grazing map lens can see a very large geographic area. Lower source LOD
        // before enumerating it. Free look instead prioritizes detailed nearby tiles.
        while (!IsFreeLook && z > 0 &&
            (long)Math.Min(n, (long)bounds.Right - bounds.Left + 1) * (bounds.Bottom - bounds.Top + 1) > 256)
        {
            z--;
            n = 1 << z;
            bounds = TileBounds(width, height, n);
        }
        var cx = CenterX * n;
        var cy = CenterY * n;
        int left = bounds.Left, right = bounds.Right, top = bounds.Top, bottom = bounds.Bottom;
        if (IsFreeLook)
        {
            left = Math.Max(left, (int)Math.Floor(cx) - 8);
            right = Math.Min(right, (int)Math.Floor(cx) + 8);
            top = Math.Max(top, (int)Math.Floor(cy) - 8);
            bottom = Math.Min(bottom, (int)Math.Floor(cy) + 8);
        }
        right = (int)Math.Min(right, (long)left + n - 1);
        var distances = new Dictionary<TileId, double>();
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            var key = new TileId(z, ((x % n) + n) % n, y);
            double dx = WrapOffset((key.X + .5) / n - CenterX) * n, dy = y + .5 - cy;
            double distance = dx * dx + dy * dy;
            if (!distances.TryGetValue(key, out var prior) || distance < prior) distances[key] = distance;
        }
        return distances.OrderBy(p => p.Value).Take(IsFreeLook ? 64 : 256).Select(p => p.Key).ToArray();
    }

    internal (int Left, int Top, int Right, int Bottom) TileBounds(double width, double height, int n)
    {
        double extra = Zoom >= 13 ? Math.Min(height, 450 * WorldSize / (EarthCircumference * Math.Cos(Math.Clamp(Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180)) * Math.Sin(PitchRadians)) : 0;
        double xMin = double.PositiveInfinity, yMin = double.PositiveInfinity;
        double xMax = double.NegativeInfinity, yMax = double.NegativeInfinity;
        bool frustum = Perspective && (IsFreeLook || Pitch >= 80);
        var (bearingSin, bearingCos) = Math.SinCos(Bearing * Math.PI / 180);
        var (pitchSin, pitchCos) = Math.SinCos(PitchRadians);
        for (int i = 0; i < (frustum ? 8 : 4); i++)
        {
            double dx = (i & 1) == 0 ? -width * .5 : width * .5;
            double dy = (i & 2) == 0 ? -height * .5 : height * .5 + extra;
            (double X, double Y) p;
            if (frustum)
            {
                double weight = (i & 4) == 0 ? NearLimit(height) : FarLimit(height);
                double u = dx * weight, v = pitchCos * dy * weight + pitchSin * Focal(height) * (1 - weight);
                p = ((u * bearingCos - v * bearingSin) / WorldSize, (u * bearingSin + v * bearingCos) / WorldSize);
            }
            else p = GroundOffset(dx, dy, height);
            xMin = Math.Min(xMin, p.X); xMax = Math.Max(xMax, p.X);
            yMin = Math.Min(yMin, p.Y); yMax = Math.Max(yMax, p.Y);
        }
        // Horizontal wrapping remains continuous, with bounded world copies at extreme
        // viewport/lens combinations. Clamp before conversion to protect integer bounds.
        xMin = Math.Clamp(xMin, -32, 32); xMax = Math.Clamp(xMax, -32, 32);
        return ((int)Math.Floor((CenterX + xMin) * n), (int)Math.Floor(Math.Clamp((CenterY + yMin) * n, 0, n - 1)),
                (int)Math.Floor((CenterX + xMax) * n), (int)Math.Floor(Math.Clamp((CenterY + yMax) * n, 0, n - 1)));
    }
}
