using PixelMap.Data;
using PixelMap.Routing;
using SkiaSharp;

namespace PixelMap.Rendering;

public readonly partial record struct MapCamera
{
    /// <summary>Set the local datum from the sampled surface. An optional viewport height also
    /// keeps a perspective eye above rising terrain while orbiting.</summary>
    public MapCamera OnTerrain(TerrainOptions? terrain, double viewportHeight = 0)
    {
        if (IsFreeLook)
        {
            var freeEye = GetEye(viewportHeight > 0 ? viewportHeight : ViewportHeight);
            double datum = terrain is { Relief: true, Elevation.Count: > 0 }
                ? terrain.Elevation.SampleSurfaceMeters(freeEye.Latitude, freeEye.Longitude) : 0;
            return this with
            {
                GroundAltitudeMeters = datum,
                TargetAltitudeMeters = TargetAltitudeMeters + GroundAltitudeMeters - datum
            };
        }
        if (terrain is not { Relief: true } || terrain.Elevation.Count == 0)
            return this with { GroundAltitudeMeters = 0 };
        var next = this with { GroundAltitudeMeters = terrain.Elevation.SampleSurfaceMeters(Latitude, Longitude) };
        if (!Perspective || viewportHeight <= 0) return next;
        var (sin, cos) = Math.SinCos(Bearing * Math.PI / 180);
        var (pitchSin, pitchCos) = Math.SinCos(PitchRadians);
        double focal = Focal(viewportHeight);
        double offset = focal * pitchSin / WorldSize;
        var eye = Geographic(CenterX - offset * sin, CenterY + offset * cos);
        double surface = terrain.Elevation.SampleSurfaceMeters(eye.Latitude, eye.Longitude);
        double eyeHeight = focal * pitchCos / next.PixelsPerMeter + next.TargetAltitudeMeters;
        double clearance = (surface - next.GroundAltitudeMeters) * terrain.HeightScale + 8 - eyeHeight;
        return clearance > 0 ? next with { TargetAltitudeMeters = next.TargetAltitudeMeters + clearance } : next;
    }

    private double PixelsPerMeter => WorldSize /
        (EarthCircumference * Math.Cos(Math.Clamp(Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180));

    /// <summary>Find the first intersection of a screen ray with the sampled height field.
    /// Returned elevation is the unexaggerated absolute surface height.</summary>
    public bool TryUnprojectTerrain(double x, double y, double width, double height, TerrainOptions terrain,
        out TerrainHit hit)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        hit = default;
        if (width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(x) || !double.IsFinite(y)) return false;
        if (!terrain.Relief || terrain.Elevation.Count == 0)
        {
            if (!TryUnproject(x, y, width, height, out var flat)) return false;
            hit = new(flat.Latitude, flat.Longitude, terrain.Elevation.SampleSurfaceMeters(flat.Latitude, flat.Longitude));
            return true;
        }
        double exaggeration = Math.Clamp(terrain.HeightScale, .25, 8);
        double low = (Math.Min(0, terrain.Elevation.MinimumMeters) - GroundAltitudeMeters) * exaggeration;
        double high = (Math.Max(0, terrain.Elevation.MaximumMeters) - GroundAltitudeMeters) * exaggeration;
        var (sp, cp) = Math.SinCos(PitchRadians);
        double dx = x - width * .5, dy = y - height * .5;
        double focal = Focal(height), ppm = PixelsPerMeter;
        double targetPixels = TargetPixels, world = WorldSize, centerX = CenterX, centerY = CenterY;
        double ground = GroundAltitudeMeters;
        bool perspective = Perspective;
        double start, end;
        if (Perspective)
        {
            double slope = -(dy * sp + focal * cp) / ppm;
            double origin = (focal * cp + targetPixels) / ppm;
            if (Math.Abs(slope) < 1e-12)
            {
                if (origin < low - 2 || origin > high + 2) return false;
                start = NearLimit(height);
                end = FarLimit(height);
            }
            else
            {
                double a = (low - 2 - origin) / slope, b = (high + 2 - origin) / slope;
                start = Math.Max(NearLimit(height), Math.Min(a, b));
                end = Math.Min(FarLimit(height), Math.Max(a, b));
            }
        }
        else { start = high + 2; end = low - 2; }
        if (Perspective && end < start) return false;
        var (bearingSin, bearingCos) = Math.SinCos(Bearing * Math.PI / 180);
        var first = Ray(start);
        var last = Ray(end);
        double span = Math.Max(Math.Abs(last.X - first.X), Math.Abs(last.Y - first.Y));
        double cell = 1d / ((1 << Math.Min(terrain.Elevation.MaximumZoom, 20)) * 256d);
        int steps = Math.Clamp((int)Math.Min(2048, Math.Ceiling(span / (cell * .5))), 32, 2048);
        double prior = start;
        double previous = Difference(first);
        for (int i = 1; i <= steps; i++)
        {
            double current = start + (end - start) * i / steps;
            var ray = Ray(current);
            double difference = Difference(ray);
            bool descending = previous >= 0 && difference <= 0;
            if (descending || previous <= 0 && difference >= 0)
            {
                double before = prior, after = current;
                double beforeDifference = previous, afterDifference = difference;
                for (int refinement = 0; refinement < 18; refinement++)
                {
                    double mid = (before + after) * .5;
                    double residual = Difference(Ray(mid));
                    if (descending ? residual > 0 : residual < 0) { before = mid; beforeDifference = residual; }
                    else { after = mid; afterDifference = residual; }
                }
                // Interpolate within the final bracket. This recovers a flat or planar
                // surface exactly, including sub-metre rays from a first-person eye.
                double range = beforeDifference - afterDifference;
                double fraction = range != 0 ? beforeDifference / range : .5;
                ray = Ray(before + (after - before) * fraction);
                if (ray.Y is < 0 or > 1) return false;
                var location = Geographic(ray.X, ray.Y);
                hit = new(location.Latitude, location.Longitude,
                    terrain.Elevation.SampleSurfaceMercator(ray.X, ray.Y));
                return true;
            }
            prior = current;
            previous = difference;
        }
        return false;

        (double X, double Y, double Z) Ray(double parameter)
        {
            double u, v, z;
            if (perspective)
            {
                u = dx * parameter;
                v = cp * dy * parameter + sp * focal * (1 - parameter);
                z = (-sp * dy * parameter + cp * focal * (1 - parameter) + targetPixels) / ppm;
            }
            else
            {
                u = dx;
                v = (dy + parameter * ppm * sp) / cp;
                z = parameter;
            }
            return (centerX + (u * bearingCos - v * bearingSin) / world,
                centerY + (u * bearingSin + v * bearingCos) / world, z);
        }

        double Difference((double X, double Y, double Z) ray) => ray.Z -
            (terrain.Elevation.SampleSurfaceMercator(ray.X, ray.Y) - ground) * exaggeration;
    }

    /// <summary>Drag using the visible terrain as the geographic anchor.</summary>
    public MapCamera PanFromTo(double fromX, double fromY, double toX, double toY, double width, double height,
        TerrainOptions terrain)
    {
        if (!terrain.Relief || !TryUnprojectTerrain(fromX, fromY, width, height, terrain, out var hit))
            return PanFromTo(fromX, fromY, toX, toY, width, height).OnTerrain(terrain, height);
        return AtTerrainAnchor(hit, toX, toY, width, height, terrain);
    }

    /// <summary>Apply zoom, bearing and pitch together while retaining a terrain anchor.</summary>
    public MapCamera TransformAt(double deltaZoom, double deltaBearing, double deltaPitch,
        double x, double y, double width, double height, TerrainOptions terrain)
    {
        if (!terrain.Relief || !TryUnprojectTerrain(x, y, width, height, terrain, out var hit))
            return TransformAt(deltaZoom, deltaBearing, deltaPitch, x, y, width, height).OnTerrain(terrain, height);
        var next = this with
        {
            Zoom = Math.Clamp(Zoom + deltaZoom, MinZoom, MaxZoom),
            Bearing = WrapOffset((Bearing + deltaBearing) / 360) * 360,
            Pitch = ClampPitch(Pitch + deltaPitch, Perspective)
        };
        return next.AtTerrainAnchor(hit, x, y, width, height, terrain);
    }

    /// <summary>Place a known absolute altitude at a screen location. This also supports
    /// following an airborne vehicle through an inset viewport over uneven ground.</summary>
    public MapCamera AnchorAltitude(double latitude, double longitude, double absoluteMsl,
        double x, double y, double width, double height, TerrainOptions terrain)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        double surface = terrain.Elevation.SampleSurfaceMeters(latitude, longitude);
        return AtTerrainAnchor(new(latitude, longitude, surface), x, y, width, height, terrain, absoluteMsl - surface);
    }

    private MapCamera AtTerrainAnchor(TerrainHit hit, double x, double y, double width, double height,
        TerrainOptions terrain, double aboveSurface = 0)
    {
        var next = OnTerrain(terrain, height);
        // A terrain-dependent datum makes simple plane iteration unstable on steep slopes.
        // Solve the two screen coordinates with a bounded numerical Jacobian instead.
        for (int iteration = 0; iteration < 16; iteration++)
        {
            var screen = ProjectAnchor(next);
            double errorX = x - screen.X, errorY = y - screen.Y;
            double error = errorX * errorX + errorY * errorY;
            if (error < .0001) break;
            double step = 1 / next.WorldSize;
            var horizontal = Shift(next, step, 0);
            var vertical = Shift(next, 0, step);
            var h = ProjectAnchor(horizontal);
            var v = ProjectAnchor(vertical);
            double hx = h.X - screen.X, hy = h.Y - screen.Y;
            double vx = v.X - screen.X, vy = v.Y - screen.Y;
            double determinant = hx * vy - vx * hy;
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-10) break;
            double dx = (errorX * vy - vx * errorY) / determinant;
            double dy = (hx * errorY - errorX * hy) / determinant;
            double limit = Math.Max(width, height) * 2;
            double magnitude = Math.Max(Math.Abs(dx), Math.Abs(dy));
            double damping = magnitude > limit ? limit / magnitude : 1;
            bool improved = false;
            for (int trial = 0; trial < 6; trial++)
            {
                var candidate = Shift(next, dx * step * damping, dy * step * damping);
                var projected = ProjectAnchor(candidate);
                double ex = x - projected.X, ey = y - projected.Y;
                if (ex * ex + ey * ey < error)
                {
                    next = candidate;
                    improved = true;
                    break;
                }
                damping *= .5;
            }
            if (!improved) break;
        }
        double anchoredAltitude = next.GroundAltitudeMeters + terrain.LocalAltitude(next, hit.ElevationMeters) + aboveSurface;
        bool visible = next.TryProject(hit.Latitude, hit.Longitude, anchoredAltitude, width, height, out var anchored);
        double finalX = anchored.X - x, finalY = anchored.Y - y;
        if (visible && finalX * finalX + finalY * finalY < .0001) return next;
        return BracketTerrainAnchor(hit, x, y, width, height, terrain, aboveSurface, next);

        (double X, double Y) ProjectAnchor(MapCamera camera)
        {
            double altitude = camera.GroundAltitudeMeters + terrain.LocalAltitude(camera, hit.ElevationMeters) + aboveSurface;
            return camera.ProjectAtAltitude(hit.Latitude, hit.Longitude, altitude, width, height);
        }

        MapCamera Shift(MapCamera camera, double dx, double dy)
        {
            var center = Geographic(camera.CenterX + dx, camera.CenterY + dy);
            return (camera with { Latitude = center.Latitude, Longitude = center.Longitude }).OnTerrain(terrain, height);
        }
    }

    private MapCamera BracketTerrainAnchor(TerrainHit hit, double x, double y, double width, double height,
        TerrainOptions terrain, double aboveSurface, MapCamera best)
    {
        var basis = this;
        double worldX = (hit.Longitude + 180) / 360, worldY = MercatorY(hit.Latitude);
        double dx = x - width * .5, dy = y - height * .5, focal = Focal(height), world = WorldSize;
        var (sp, cp) = Math.SinCos(PitchRadians);
        var (sin, cos) = Math.SinCos(Bearing * Math.PI / 180);
        double bestError = Error(best);

        if (Perspective)
        {
            // Screen position and projective depth determine the geographic camera centre
            // exactly. Solve its focus height, then retain only terrain-cleared, visible poses.
            // This also handles slopes for which a fixed focus height has no frustum solution.
            for (int step = 0; step < 40; step++)
            {
                double distance = Math.Pow(1.3, step);
                if (1 / distance >= NearLimit(height) * 1.001 && TryDepth(1 / distance, out var nearer)) return nearer;
                if (step > 0 && distance <= 4096 && TryDepth(distance, out var farther)) return farther;
            }
            return best;
        }

        // The orthographic anchor depends on the centre's sampled datum. Its signed range
        // brackets a root even when steep slopes reverse the local screen-space derivative.
        double scale = terrain.Relief ? terrain.HeightScale : 0;
        double low = Math.Min(0, terrain.Elevation.MinimumMeters) * scale;
        double high = Math.Max(0, terrain.Elevation.MaximumMeters) * scale;
        double absolute = hit.ElevationMeters * scale + aboveSurface;
        for (int iteration = 0; iteration < 48; iteration++)
        {
            double datum = (low + high) * .5;
            var candidate = basis;
            // Re-evaluate the Mercator metres-per-pixel as the trial centre changes latitude.
            for (int refinement = 0; refinement < 12; refinement++)
            {
                double z = (absolute - datum) * candidate.PixelsPerMeter;
                candidate = CenterAt(dx, (dy + z * sp) / cp);
            }
            candidate = candidate.OnTerrain(terrain, height);
            double error = Error(candidate);
            if (error < .0001) return candidate;
            if (error < bestError) { best = candidate; bestError = error; }
            if (candidate.GroundAltitudeMeters * scale > datum) low = datum;
            else high = datum;
        }
        return best;

        bool TryDepth(double weight, out MapCamera candidate)
        {
            double z = focal * cp - weight * (focal * cp + dy * sp);
            candidate = CenterAt(dx * weight, cp * dy * weight + sp * focal * (1 - weight)).OnTerrain(terrain);
            double target = terrain.LocalAltitude(candidate, hit.ElevationMeters) + aboveSurface - z / candidate.PixelsPerMeter;
            if ((!basis.IsFreeLook && target < 0) || !double.IsFinite(target)) return false;
            candidate = (candidate with { TargetAltitudeMeters = target }).OnTerrain(terrain, height);
            double error = Error(candidate);
            if (error < bestError) { best = candidate; bestError = error; }
            return error < .0001;
        }

        MapCamera CenterAt(double u, double v)
        {
            var location = Geographic(worldX - (u * cos - v * sin) / world, worldY - (u * sin + v * cos) / world);
            return basis with { Latitude = location.Latitude, Longitude = location.Longitude };
        }

        double Error(MapCamera camera)
        {
            double altitude = camera.GroundAltitudeMeters + terrain.LocalAltitude(camera, hit.ElevationMeters) + aboveSurface;
            if (!camera.TryProject(hit.Latitude, hit.Longitude, altitude, width, height, out var screen)) return double.PositiveInfinity;
            double ex = x - screen.X, ey = y - screen.Y;
            return ex * ex + ey * ey;
        }
    }

    /// <summary>Fit a route including terrain height into an inset viewport.</summary>
    public MapCamera FitRoute(IReadOnlyList<RoutePoint> route, double width, double height,
        SKRect viewport, double padding, TerrainOptions terrain)
    {
        var next = FitRoute(route, width, height, viewport, padding).OnTerrain(terrain, height);
        if (!terrain.Relief || route.Count == 0) return next;
        int samples = Math.Min(512, route.Count - 1);
        for (int pass = 0; pass <= 4 * (MaxZoom - MinZoom); pass++)
        {
            double left = double.PositiveInfinity, top = double.PositiveInfinity;
            double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
            bool visible = true;
            for (int sample = 0; sample <= samples; sample++)
            {
                int index = (int)((long)sample * (route.Count - 1) / Math.Max(1, samples));
                var point = route[index];
                double surface = terrain.Elevation.SampleSurfaceMeters(point.Latitude, point.Longitude);
                double altitude = next.GroundAltitudeMeters + (surface - next.GroundAltitudeMeters) * terrain.HeightScale;
                visible &= next.TryProject(point.Latitude, point.Longitude, altitude, width, height, out var screen);
                left = Math.Min(left, screen.X); right = Math.Max(right, screen.X);
                top = Math.Min(top, screen.Y); bottom = Math.Max(bottom, screen.Y);
            }
            if (visible && left >= viewport.Left + padding && right <= viewport.Right - padding &&
                top >= viewport.Top + padding && bottom <= viewport.Bottom - padding) break;
            if (next.Zoom <= MinZoom) break;
            next = (next with { Zoom = Math.Max(MinZoom, next.Zoom - .25) }).OnTerrain(terrain, height);
        }
        return next;
    }
}
