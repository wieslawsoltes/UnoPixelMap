using PixelMap.App;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

internal static class FreeCameraChecks
{
    private const double Circumference = 40075016.68557849;

    internal static void Run()
    {
        const int width = 900, height = 600;
        int poses = 0;
        foreach (double latitude in new[] { -70d, 0, 70 })
        foreach (double bearing in new[] { -179d, 0, 87, 179 })
        foreach (double pitch in new[] { 0d, 55, 89.999, 90, 90.001, 120, 179.5 })
        {
            const double longitude = 179.999999, altitude = 1234.5;
            var camera = MapCamera.FromEye(latitude, longitude, altitude, bearing, pitch,
                width, height, viewDistanceMeters: 600, groundAltitudeMeters: 1233);
            var eye = camera.GetEye(height);
            Assert(Math.Abs(eye.Latitude - latitude) < 1e-10 && LongitudeError(eye.Longitude, longitude) < 1e-10 &&
                Math.Abs(eye.AltitudeMeters - altitude) < 1e-10, "Free camera did not preserve its absolute eye");
            Assert(camera.IsFreeLook && camera.Perspective && camera.Pitch == pitch,
                "Free look lost its projection mode or upward angle");
            foreach (double distance in new[] { .04, .16, 12, 550, 650 })
            {
                var point = Forward(camera, height, distance);
                bool visible = camera.TryProject(point.Latitude, point.Longitude, point.Altitude,
                    width, height, out var projected);
                Assert(visible == (distance >= MapCamera.FreeLookNearMeters && distance <= 600),
                    $"Physical free-camera clipping failed at {distance}m, latitude {latitude}, pitch {pitch}");
                if (visible)
                    Assert(Math.Abs(projected.X - width * .5) + Math.Abs(projected.Y - height * .5) < .02,
                        "A point ahead of the eye left the optical axis");
            }
            var tiles = camera.VisibleTiles(width, height);
            Assert(tiles.Length is > 0 and <= 64 && tiles.Distinct().Count() == tiles.Length,
                "Free-camera tile requests were empty, unbounded or duplicated");
            foreach (var viewport in new[] { (Width: 390d, Height: 844d), (Width: 1200d, Height: 360d) })
            {
                var resized = camera.ForViewport(viewport.Width, viewport.Height);
                var resizedEye = resized.GetEye(viewport.Height);
                Assert(Math.Abs(resizedEye.Latitude - eye.Latitude) < 1e-10 &&
                    LongitudeError(resizedEye.Longitude, eye.Longitude) < 1e-10 &&
                    Math.Abs(resizedEye.AltitudeMeters - eye.AltitudeMeters) < 1e-9 &&
                    resized.ViewportHeight == viewport.Height, "Resizing the viewport moved the physical eye");
            }
            poses++;
        }

        TerrainDatum();
        FixedEyeInterpolation();
        TerrainSpringClearance();
        AimStablePoses();
        AimInsets();
        MercatorEdges();
        NearGeometry();
        LensBounds();
        Links();
        Console.WriteLine($"Passed {poses} free-camera poses, physical-eye interpolation, analytic inset aim, physical near/far clipping, terrain datum, close model picking, bounded horizon requests and v5 style/view links.");
    }

    private static void TerrainDatum()
    {
        const int width = 900, height = 600;
        var elevation = new ElevationSnapshot([new ElevationTile(new(0, 0, 0), 2, new float[] { 1200, 1200, 1200, 1200 })]);
        foreach (double pitch in new[] { 0d, 55, 90, 120, 179.5 })
        foreach (double scale in new[] { 1d, 4 })
        {
            var terrain = new TerrainOptions(elevation, Exaggeration: scale);
            var camera = MapCamera.FromEye(45, 179.9999, 1201.7, 32, pitch, width, height).OnTerrain(terrain, height);
            var eye = camera.GetEye(height);
            Assert(camera.GroundAltitudeMeters == 1200 && Math.Abs(eye.AltitudeMeters - 1201.7) < 1e-9,
                "Terrain clearance raised the first-person eye or lost its absolute datum");
            var flat = camera.OnTerrain(null, height);
            Assert(flat.GroundAltitudeMeters == 0 && Math.Abs(flat.GetEye(height).AltitudeMeters - 1201.7) < 1e-9,
                "Disabling terrain moved the free-camera eye");
            if (pitch <= 90)
            {
                Assert(camera.TryUnprojectTerrain(width * .5, height * .85, width, height, terrain, out var hit),
                    "A downward street-level ray missed flat terrain");
                Assert(Math.Abs(hit.ElevationMeters - 1200) < 1e-6 &&
                    camera.TryProject(hit.Latitude, hit.Longitude, hit.ElevationMeters, width, height, out var projected) &&
                    Math.Abs(projected.X - width * .5) + Math.Abs(projected.Y - height * .85) < .03,
                    "Street-level terrain picking disagreed with the displayed camera");
            }
        }
        foreach (float surface in new[] { -300f, 1000 })
        foreach (double pitch in new[] { 91d, 120, 179.5 })
        {
            var terrain = new TerrainOptions(new ElevationSnapshot([
                new ElevationTile(new(0, 0, 0), 2, new[] { surface, surface, surface, surface })]));
            var camera = MapCamera.FromEye(0, 179.9999, surface - 100, 32, pitch, width, height,
                groundAltitudeMeters: surface);
            Assert(camera.TryUnprojectTerrain(width * .5, height * .5, width, height, terrain, out var hit) &&
                Math.Abs(hit.ElevationMeters - surface) < 1e-6 &&
                camera.TryProject(hit.Latitude, hit.Longitude, hit.ElevationMeters, width, height, out var screen) &&
                Math.Abs(screen.X - width * .5) + Math.Abs(screen.Y - height * .5) < .001,
                "An upward ray from beneath signed terrain missed the first visible surface");
            var above = MapCamera.FromEye(0, 179.9999, surface + 100, 32, pitch, width, height,
                groundAltitudeMeters: surface);
            Assert(!above.TryUnprojectTerrain(width * .5, height * .5, width, height, terrain, out _),
                "An upward ray above flat terrain reported an intersection behind the eye");
            Assert(!camera.TryUnprojectTerrain(450, 300, double.NaN, height, terrain, out _) &&
                !camera.TryUnprojectTerrain(450, 300, width, double.PositiveInfinity, terrain, out _),
                "A terrain ray accepted a nonfinite viewport");
        }
    }

    private static void FixedEyeInterpolation()
    {
        foreach (double latitude in new[] { -70d, 0, 70 })
        foreach (double pitch in new[] { 0d, 90, 120, 179.5 })
        {
            var first = MapCamera.FromEye(latitude, 179.9999, 1234.5, 0, 0, 900, 600,
                groundAltitudeMeters: 1000);
            var last = MapCamera.FromEye(latitude, 179.9999, 1234.5, 179, pitch, 390, 844,
                groundAltitudeMeters: 400);
            foreach (double t in new[] { .01, .25, .49, .5, .75, .99 })
            {
                var camera = first.Interpolate(last, t);
                var eye = camera.GetEye(camera.ViewportHeight);
                Assert(Math.Abs(eye.Latitude - latitude) < 1e-10 &&
                    LongitudeError(eye.Longitude, 179.9999) < 1e-10 &&
                    Math.Abs(eye.AltitudeMeters - 1234.5) < 1e-8,
                    "Interpolating a look rotation, viewport or terrain datum moved the stationary physical eye");
            }
            Assert(first.Interpolate(last, 0) == first && first.Interpolate(last, 1) == last,
                "Free-camera interpolation changed its endpoint poses");
        }
        var start = MapCamera.FromEye(70, 179.9999, -80, 150, 35, 900, 600);
        var destination = MapCamera.FromEye(70, -179.9999, 120, -150, 135, 900, 600);
        var midpoint = start.Interpolate(destination, .5).GetEye(600);
        Assert(LongitudeError(midpoint.Longitude, 180) < 1e-10 && Math.Abs(midpoint.Latitude - 70) < 1e-10 &&
            Math.Abs(midpoint.AltitudeMeters - 20) < 1e-8,
            "The free eye did not take the short Mercator path across the antimeridian");
        var direct = start.Interpolate(destination, 1 - Math.Exp(-12 * .15));
        var partitioned = start;
        foreach (double elapsed in new[] { .01, .025, .045, .07 })
            partitioned = partitioned.Interpolate(destination, 1 - Math.Exp(-12 * elapsed));
        var directEye = direct.GetEye(600);
        var partitionedEye = partitioned.GetEye(600);
        Assert(Math.Abs(directEye.Latitude - partitionedEye.Latitude) < 1e-10 &&
            LongitudeError(directEye.Longitude, partitionedEye.Longitude) < 1e-10 &&
            Math.Abs(directEye.AltitudeMeters - partitionedEye.AltitudeMeters) < 1e-8 &&
            LongitudeError(direct.Bearing, partitioned.Bearing) < 1e-10 &&
            Math.Abs(direct.Pitch - partitioned.Pitch) < 1e-10,
            "Physical free-eye smoothing depends on how elapsed time is split into frames");
    }

    private static void TerrainSpringClearance()
    {
        const int size = 16, width = 900, height = 600;
        const double clearance = .35;
        var id = new TileId(14, 8192, 8192);
        var samples = new float[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) samples[y * size + x] = x is >= 6 and <= 9 ? 180 : -40;
        var elevation = new ElevationSnapshot([new ElevationTile(id, size, samples)]);
        var terrain = new TerrainOptions(elevation, Exaggeration: 4);
        var first = Point(.2);
        var last = Point(.8);
        int cases = 0;
        foreach (double bearing in new[] { -179d, 0, 87, 179 })
        foreach (double pitch in new[] { 0d, 55, 89.999, 90, 120, 179.5 })
        {
            var start = MapCamera.FromEye(first.Latitude, first.Longitude, -20, bearing, pitch,
                width, height, groundAltitudeMeters: -40);
            var destination = MapCamera.FromEye(last.Latitude, last.Longitude, -20, bearing + 20, pitch,
                width, height, groundAltitudeMeters: -40);
            Assert(start.GetEye(height).AltitudeMeters > elevation.SampleSurfaceMeters(first.Latitude, first.Longitude) + clearance &&
                destination.GetEye(height).AltitudeMeters > elevation.SampleSurfaceMeters(last.Latitude, last.Longitude) + clearance,
                "The chase-camera ridge regression did not start with two safe endpoints");
            var smoothed = start.Interpolate(destination, .5);
            var before = smoothed.GetEye(height);
            double floor = elevation.SampleSurfaceMeters(before.Latitude, before.Longitude) + clearance;
            Assert(before.AltitudeMeters < floor - 150,
                "The chase spring fixture did not cross beneath the ridge between its safe poses");
            var safe = smoothed.WithMinimumEyeAltitude(floor, height).OnTerrain(terrain, height);
            var after = safe.GetEye(height);
            Assert(Math.Abs(after.Latitude - before.Latitude) < 1e-10 &&
                LongitudeError(after.Longitude, before.Longitude) < 1e-10 &&
                Math.Abs(after.AltitudeMeters - floor) < 1e-8 &&
                safe.Bearing == smoothed.Bearing && safe.Pitch == smoothed.Pitch &&
                safe.Zoom == smoothed.Zoom && safe.ViewDistanceMeters == smoothed.ViewDistanceMeters,
                "Terrain clearance moved the spring eye horizontally, changed its lens, or left it beneath the ridge");
            Assert(smoothed.WithMinimumEyeAltitude(before.AltitudeMeters - 1, height) == smoothed,
                "An already-safe camera changed when checking its altitude floor");
            var resized = safe.WithMinimumEyeAltitude(floor + 10, 844);
            var resizedEye = resized.GetEye(844);
            Assert(resized.ViewportHeight == 844 &&
                Math.Abs(resizedEye.Latitude - after.Latitude) < 1e-10 &&
                LongitudeError(resizedEye.Longitude, after.Longitude) < 1e-10 &&
                Math.Abs(resizedEye.AltitudeMeters - floor - 10) < 1e-8,
                "An eye-floor correction after viewport resize moved the physical eye or retained the old lens height");
            cases++;
        }
        var overhead = new MapCamera(0, 0, 16);
        Assert(overhead.WithMinimumEyeAltitude(1000, height) == overhead,
            "An orthographic map acquired a physical eye altitude");
        Console.WriteLine($"Passed {cases} chase-spring ridge clearance cases with signed terrain, unchanged eye orientation and viewport resize.");

        (double Latitude, double Longitude) Point(double x)
        {
            double worldX = (id.X + x) / (1 << id.Z), worldY = (id.Y + .5) / (1 << id.Z);
            return (Math.Atan(Math.Sinh((1 - 2 * worldY) * Math.PI)) * 180 / Math.PI, worldX * 360 - 180);
        }
    }

    private static void AimInsets()
    {
        const int width = 900, height = 600;
        int cases = 0;
        double worst = 0;
        foreach (double latitude in new[] { -70d, 0, 70 })
        foreach (double bearing in new[] { -179d, 0, 87 })
        foreach (double pitch in new[] { 0d, 55, 89.999, 90, 120, 179.5 })
        foreach (double x in new[] { 135d, 450, 765 })
        foreach (double y in new[] { 90d, 300, 510 })
        {
            var expected = MapCamera.FromEye(latitude, 179.9999, 1234.5, bearing, pitch, width, height,
                groundAltitudeMeters: 1200);
            var target = AlongRay(expected, width, height, x, y, 35);
            var original = MapCamera.FromEye(latitude, 179.9999, 1234.5, bearing + 87,
                Math.Clamp(pitch + 20, 0, MapCamera.MaxPitch), width, height, groundAltitudeMeters: 1200);
            Assert(original.TryAimAt(target.Latitude, target.Longitude, target.Altitude,
                x, y, width, height, out var aimed),
                $"A reachable inset aim was rejected at latitude {latitude}, bearing {bearing}, pitch {pitch}, screen ({x}, {y})");
            var eye = aimed.GetEye(height);
            Assert(Math.Abs(eye.Latitude - latitude) < 1e-10 && LongitudeError(eye.Longitude, 179.9999) < 1e-10 &&
                Math.Abs(eye.AltitudeMeters - 1234.5) < 1e-8 && aimed.ViewDistanceMeters == original.ViewDistanceMeters &&
                aimed.Zoom == original.Zoom && aimed.GroundAltitudeMeters == original.GroundAltitudeMeters,
                "Aiming into the safe viewport moved the eye or changed its lens or terrain datum");
            Assert(aimed.TryProject(target.Latitude, target.Longitude, target.Altitude, width, height, out var screen),
                "An analytically aimed target left the physical clipping planes");
            double error = Math.Abs(screen.X - x) + Math.Abs(screen.Y - y);
            worst = Math.Max(worst, error);
            Assert(error < .001, "An analytically aimed target missed its requested screen position");
            cases++;
        }
        var overhead = MapCamera.FromEye(0, 0, 100, 0, 0, width, height);
        Assert(!overhead.TryAimAt(0, 0, 0, 650, 300, width, height, out var unchanged) && unchanged == overhead,
            "An unreachable horizontal inset at nadir changed the eye or camera orientation");
        var distant = Forward(overhead, height, 7000);
        Assert(!overhead.TryAimAt(distant.Latitude, distant.Longitude, distant.Altitude,
            450, 300, width, height, out unchanged) && unchanged == overhead,
            "An aim outside the physical far plane changed the camera");
        Console.WriteLine($"Passed {cases} fixed-eye inset aim cases, worst screen error {worst:F6}px.");
    }

    private static void AimStablePoses()
    {
        int cases = 0;
        // North-up follow + pinch in Manhattan: the target already lies on the
        // optical axis, but a Mercator round trip can leave a nonzero horizontal
        // residue and make the analytic yaw solver choose a perpendicular heading.
        for (int i = 0; i < 100; i++)
        for (int j = 0; j < 100; j++)
        {
            double latitude = 40.7069 + i * .0000003, longitude = -74.0089 + j * .0000003;
            const double ground = 43.7, focus = ground + .6;
            var camera = MapCamera.FromEye(latitude, longitude, focus + 18 / Math.Sqrt(1.5),
                0, 0, 1200, 684, groundAltitudeMeters: ground);
            Stable(camera, latitude, longitude, focus, 600, 342, 1200, 684);
        }

        foreach (double latitude in new[] { -MapCamera.MaxLatitude, -70, -40.7069, 0, 40.7069, 70, MapCamera.MaxLatitude })
        foreach (double longitude in new[] { -180d, -74.0089, 0, 179.999999, 540 })
        foreach (double bearing in new[] { -179d, 0, 45, 90, 179 })
        foreach (double ground in new[] { -300d, 1200 })
        foreach (double clearance in new[] { 1.7, 1000 })
        foreach (var viewport in new[] { (Width: 1200d, Height: 684d), (Width: 390d, Height: 844d), (Width: 1900d, Height: 420d) })
        {
            double focus = ground + .6;
            var camera = MapCamera.FromEye(latitude, longitude, focus + clearance, bearing, 0,
                1200, 684, groundAltitudeMeters: ground);
            Stable(camera, latitude, longitude, focus, viewport.Width * .5, viewport.Height * .5,
                viewport.Width, viewport.Height);
        }

        // The same no-op contract applies away from nadir, at inset screen
        // positions and after re-encoding a physical eye for a different viewport.
        foreach (double latitude in new[] { -70d, 0, 70 })
        foreach (double bearing in new[] { -179d, 0, 87 })
        foreach (double pitch in new[] { 0d, 55, 89.999, 90, 120, 179.5 })
        foreach (var viewport in new[] { (Width: 900d, Height: 600d), (Width: 390d, Height: 844d) })
        foreach (var inset in new[] { (X: .15, Y: .2), (X: .5, Y: .5), (X: .8, Y: .75) })
        {
            var camera = MapCamera.FromEye(latitude, 179.9999, 1234.5, bearing, pitch,
                900, 600, groundAltitudeMeters: 1200);
            double x = viewport.Width * inset.X, y = viewport.Height * inset.Y;
            var target = AlongRay(camera.ForViewport(viewport.Width, viewport.Height),
                viewport.Width, viewport.Height, x, y, 35);
            Stable(camera, target.Latitude, target.Longitude, target.Altitude,
                x, y, viewport.Width, viewport.Height);
        }

        var overhead = MapCamera.FromEye(40.7069, -74.0089, 100, 0, 0, 900, 600);
        foreach (double distance in new[] { 0d, .04, -1, 7000 })
        {
            var target = Forward(overhead, 600, distance);
            Assert(!overhead.TryAimAt(target.Latitude, target.Longitude, target.Altitude,
                450, 300, 900, 600, out var unchanged) && unchanged == overhead,
                $"An already centered target at clipped or unsupported distance {distance}m changed the camera");
        }
        Console.WriteLine($"Passed {cases} already-aligned aim poses with exact yaw/pitch preservation, repeated aims, resized viewports and clipped-target rejection.");

        void Stable(MapCamera camera, double latitude, double longitude, double altitude,
            double x, double y, double width, double height)
        {
            var expected = camera.ForViewport(width, height);
            Assert(expected.TryProject(latitude, longitude, altitude, width, height, out var before) &&
                Math.Abs(before.X - x) + Math.Abs(before.Y - y) < .0001,
                "The orientation-preservation fixture did not start within the existing projection tolerance");
            Assert(camera.TryAimAt(latitude, longitude, altitude, x, y, width, height, out var aimed) && aimed == expected,
                $"An already-aligned aim changed the camera at ({latitude:R}, {longitude:R}), bearing {camera.Bearing:R}, pitch {camera.Pitch:R}, viewport {width}x{height}");
            for (int repeat = 0; repeat < 3; repeat++)
                Assert(aimed.TryAimAt(latitude, longitude, altitude, x, y, width, height, out aimed) && aimed == expected,
                    "Repeated aiming accumulated orientation or physical-eye drift");
            cases++;
        }
    }

    private static (double Latitude, double Longitude, double Altitude) AlongRay(MapCamera camera,
        double width, double height, double x, double y, double distance)
    {
        var eye = camera.GetEye(height);
        double scale = Circumference * Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180);
        double focal = height / (2 * Math.Tan(MapCamera.VerticalFieldOfView * Math.PI / 360));
        double u = (x - width * .5) / focal, v = (y - height * .5) / focal;
        var (sb, cb) = Math.SinCos(camera.Bearing * Math.PI / 180);
        var (sp, cp) = Math.SinCos(camera.Pitch * Math.PI / 180);
        double wx = (eye.Longitude + 180) / 360 + distance * (sp * sb + u * cb - v * cp * sb) / scale;
        double wy = (1 - Math.Asinh(Math.Tan(eye.Latitude * Math.PI / 180)) / Math.PI) * .5 +
            distance * (-sp * cb + u * sb + v * cp * cb) / scale;
        return (Math.Atan(Math.Sinh((1 - 2 * wy) * Math.PI)) * 180 / Math.PI,
            (wx - Math.Floor(wx)) * 360 - 180, eye.AltitudeMeters - distance * (cp + v * sp));
    }

    private static void MercatorEdges()
    {
        var page = new Uri("https://wieslawsoltes.github.io/UnoPixelMap/");
        var terrain = new TerrainOptions(new ElevationSnapshot([
            new ElevationTile(new(0, 0, 0), 2, [1200, 1200, 1200, 1200])]));
        foreach (double latitude in new[] { -MapCamera.MaxLatitude, MapCamera.MaxLatitude })
        foreach (double longitude in new[] { -179.999999, 179.999999 })
        foreach (double bearing in new[] { 0d, 90, 180, -90 })
        foreach (double pitch in new[] { 0d, 15, 90, 120, 179.5 })
        {
            var camera = MapCamera.FromEye(latitude, longitude, 1201.7, bearing, pitch, 900, 600,
                groundAltitudeMeters: 1200);
            var rotated = MapCamera.FromEye(latitude, longitude, 1201.7, bearing + 170, 35, 900, 600,
                groundAltitudeMeters: 1200);
            var interpolated = camera.Interpolate(rotated, .5);
            foreach (var view in new[]
            {
                camera, camera.ForViewport(390, 844), interpolated, camera.PanBy(0, 0),
                camera.PanFromTo(450, 450, 450, 450, 900, 600),
                camera.TransformAt(0, 0, 0, 450, 450, 900, 600),
                camera.PanFromTo(450, 450, 450, 450, 900, 600, terrain),
                camera.TransformAt(0, 0, 0, 450, 450, 900, 600, terrain)
            })
            {
                var eye = view.GetEye(view.ViewportHeight);
                Assert(Math.Abs(eye.Latitude - latitude) < 1e-10 && LongitudeError(eye.Longitude, longitude) < 1e-10 &&
                    Math.Abs(eye.AltitudeMeters - 1201.7) < 1e-9,
                    "A free virtual focus moved its physical eye at the Mercator or longitude boundary");
                var tiles = view.VisibleTiles(900, view.ViewportHeight);
                Assert(tiles.Length is > 0 and <= 64 && tiles.All(t => t.X >= 0 && t.X < 1 << t.Z && t.Y >= 0 && t.Y < 1 << t.Z),
                    "An out-of-coverage virtual focus requested invalid Mercator tiles");
            }
            var link = MapViewLink.Create(page, camera, PixelStyle.Day, true, false, true, 1, MapRenderStyle.Pixel);
            Assert(MapViewLink.TryRead(link, out var shared, out _, out _, out _, out _, out _, out _),
                "A free-focus share link was rejected at the Mercator boundary");
            var restored = shared.ForViewport(390, 844).GetEye(844);
            Assert(new RoutePoint(latitude, longitude).DistanceTo(new(restored.Latitude, restored.Longitude)) < .001 &&
                Math.Abs(restored.AltitudeMeters - 1201.7) < .0011,
                "A shared edge view failed to preserve the physical eye through a viewport resize");
        }
        foreach (double latitude in new[] { -MapCamera.MaxLatitude, MapCamera.MaxLatitude })
        {
            const int width = 320, height = 240, extent = 1 << 24, count = 1 << 14;
            double targetLatitude = latitude - Math.CopySign(.0000001, latitude), longitude = 179.99999;
            double wx = (longitude + 180) / 360, wy = (1 - Math.Asinh(Math.Tan(targetLatitude * Math.PI / 180)) / Math.PI) * .5;
            var id = new TileId(14, (int)Math.Floor(wx * count), (int)Math.Floor(wy * count));
            var point = new TilePoint((int)Math.Round((wx * count - id.X) * extent), (int)Math.Round((wy * count - id.Y) * extent));
            double unitsPerMeter = count * (double)extent / (Circumference * Math.Cos(latitude * Math.PI / 180));
            int halfWidth = (int)Math.Round(.05 * unitsPerMeter), halfDepth = (int)Math.Round(.005 * unitsPerMeter);
            TilePoint[] ring = [new(point.X - halfWidth, point.Y - halfDepth), new(point.X + halfWidth, point.Y - halfDepth),
                new(point.X + halfWidth, point.Y + halfDepth), new(point.X - halfWidth, point.Y + halfDepth)];
            var feature = new MapFeature("building", "building", "", "Polar edge", 900, extent, GeometryType.Polygon,
                [ring], .5, 0, false, 0, 0, "");
            var camera = MapCamera.FromEye(latitude, longitude, 1.7, latitude > 0 ? 0 : 180, 15, width, height);
            Assert(camera.TryProject(targetLatitude, longitude, .5, width, height, out var screen) &&
                screen.X >= 0 && screen.X < width && screen.Y >= 0 && screen.Y < height,
                "The physical eye lost a visible ground point inside the Mercator data edge");
            using var renderer = new PixelMapRenderer();
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            renderer.Draw(canvas, width, height, camera, [new(id, [])], pixelSize: 1);
            var empty = bitmap.GetPixelSpan().ToArray();
            var tile = new MapTile(id, [feature]);
            renderer.Draw(canvas, width, height, camera, [tile], pixelSize: 1);
            bool rendered = !empty.AsSpan().SequenceEqual(bitmap.GetPixelSpan());
            var picked = renderer.Pick(screen.X, screen.Y, width, height, camera, [tile]);
            Assert(rendered && picked == feature,
                $"The edge feature at {latitude} was not visible or selectable. Rendered {rendered}, picked {picked?.Id}");
        }
    }

    private static void NearGeometry()
    {
        const int width = 420, height = 280;
        var id = new TileId(14, 8192, 8192);
        double count = 1 << id.Z;
        double latitude = Math.Atan(Math.Sinh((1 - 2 * (id.Y + .5) / count) * Math.PI)) * 180 / Math.PI;
        double longitude = (id.X + .5) / count * 360 - 180;
        const int extent = 1 << 24, center = extent / 2;
        double unitsPerMeter = count * extent / (Circumference * Math.Cos(latitude * Math.PI / 180));
        int halfWidth = (int)Math.Round(.12 * unitsPerMeter);
        int near = (int)Math.Round(.12 * unitsPerMeter), far = (int)Math.Round(.18 * unitsPerMeter);
        TilePoint[] ring = [new(center - halfWidth, center - far), new(center + halfWidth, center - far),
            new(center + halfWidth, center - near), new(center - halfWidth, center - near)];
        var feature = new MapFeature("building", "building", "residential", "Close wall", 810,
            extent, GeometryType.Polygon, [ring], 3, 0, false, 0, 0, "");
        var camera = MapCamera.FromEye(latitude, longitude, 1.7, 0, 90, width, height);
        var tile = new MapTile(id, [feature]);
        using var renderer = new PixelMapRenderer();
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        renderer.Draw(canvas, width, height, camera, [new(id, [])], pixelSize: 2);
        var empty = bitmap.GetPixelSpan().ToArray();
        renderer.Draw(canvas, width, height, camera, [tile], pixelSize: 2);
        Assert(!empty.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "A model twelve centimetres from the eye was clipped away");
        Assert(renderer.Pick(width * .5, height * .5, width, height, camera, [tile])?.Id == feature.Id,
            "Close-range model picking still used the map near plane");
    }

    private static void LensBounds()
    {
        const int width = 900, height = 600;
        foreach (double pitch in new[] { 79d, 89.999, 90, 90.001, 120, 179.5 })
        foreach (double zoom in new[] { 0d, 14, 26 })
        {
            var camera = new MapCamera(70, 179.99, zoom, -37, pitch, true, 25000);
            var tiles = camera.VisibleTiles(width, height, 24);
            Assert(tiles.Length is > 0 and <= 256, "A grazing map lens enumerated an unbounded source region");
            foreach (var point in new[] { (0d, 0d), (width * .5, height * .5), ((double)width, (double)height) })
            {
                var location = camera.Unproject(point.Item1, point.Item2, width, height);
                Assert(double.IsFinite(location.Latitude) && double.IsFinite(location.Longitude),
                    "A horizontal or upward map ray produced nonfinite coordinates");
            }
        }
        var orbit = new MapCamera(0, 0, 20, 0, 72, true).TransformAt(4, 3600, 45, 450, 450, width, height);
        Assert(orbit.Pitch == 117 && orbit.Zoom == 24 && Math.Abs(orbit.Bearing) < 1e-9,
            "Map orbit retained the old pitch or zoom ceiling");
        var orthographic = new MapCamera(0, 0, 20, 0, 55).TransformAt(0, 0, 100, 450, 300, width, height);
        Assert(orthographic.Pitch == MapCamera.MaxOrthographicPitch && double.IsFinite(orthographic.Longitude),
            "Orthographic orbit crossed its singular projection plane");
        RoutePoint[] route = [new(45.001, 179.98), new(45.004, -179.99), new(44.996, -179.97)];
        foreach (double pitch in new[] { 0d, 90, 120, 179.5 })
        {
            var eye = MapCamera.FromEye(45, 179.99, 1.7, 28, pitch, width, height);
            var overview = eye.FitRoute(route, width, height, 45);
            Assert(!overview.IsFreeLook && overview.TargetAltitudeMeters == 0, "Route overview retained a first-person lens");
            foreach (var point in route)
                Assert(overview.TryProject(point.Latitude, point.Longitude, 0, width, height, out var screen) &&
                    screen.X >= 44.9 && screen.X <= width - 44.9 && screen.Y >= 44.9 && screen.Y <= height - 44.9,
                    "A route could not be framed after looking above the horizon");
        }
    }

    private static void Links()
    {
        var page = new Uri("https://wieslawsoltes.github.io/UnoPixelMap/?source=game");
        var camera = new MapCamera(45.875, 10.85, 24, -25, 125, true, -120.25)
        { IsFreeLook = true, ViewDistanceMeters = 6000, GroundAltitudeMeters = 1200.5, ViewportHeight = 600 };
        foreach (var renderStyle in Enum.GetValues<MapRenderStyle>())
        {
            var link = MapViewLink.Create(page, camera, PixelStyle.Dusk, false, true, true, 3, renderStyle);
            Assert(MapViewLink.TryRead(link, out var read, out var palette, out var earth, out var elevation,
                out var terrain, out var scale, out var style) && read == camera && palette == PixelStyle.Dusk &&
                !earth && elevation && terrain && scale == 3 && style == renderStyle,
                "A v5 free-camera/render-style link failed to round-trip");
        }
        foreach (double latitude in new[] { -70.123456789, 0.123456789, 70.123456789 })
        foreach (double pitch in new[] { 0d, 89.74382, 120.78961, 179.5 })
        {
            var original = MapCamera.FromEye(latitude, 179.999999123, 1234.56789, -179.29382, pitch,
                900, 600.374, groundAltitudeMeters: 1233.91273);
            var link = MapViewLink.Create(page, original, PixelStyle.Day, true, false, true, 2, MapRenderStyle.Pixel);
            Assert(MapViewLink.TryRead(link, out var shared, out _, out _, out _, out _, out _, out _),
                "A factory-created free-camera share link was rejected");
            var expected = original.GetEye(original.ViewportHeight);
            var actual = shared.ForViewport(390, 844).GetEye(844);
            double horizontalError = new RoutePoint(expected.Latitude, expected.Longitude)
                .DistanceTo(new(actual.Latitude, actual.Longitude));
            Assert(horizontalError < .001 && Math.Abs(expected.AltitudeMeters - actual.AltitudeMeters) < .0011,
                "Sharing a free-camera view changed its physical eye beyond millimetre precision");
        }
        string valid = "v5/45/10/24/0/125/Day/Elevation/Perspective/-120/Relief/1/Normal/FreeLook/6000/1200/600";
        foreach (var invalid in new[] {
            valid.Replace("/6000/", "/NaN/"), valid.Replace("/6000/", "/Infinity/"),
            valid.Replace("/6000/", "/-1/"), valid.Replace("/6000/", "/0/"),
            valid.Replace("/1200", "/Infinity"), valid.Replace("/1200", "/40000"),
            valid.Replace("/125/", "/180/"), valid.Replace("/FreeLook/", "/Orbit/"),
            valid.Replace("/Perspective/", "/Orthographic/"), valid.Replace("/Normal/", "/Unknown/"),
            valid[..^3] + "0", valid[..^3] + "NaN", valid[..^3] + "Infinity" })
            Assert(!MapViewLink.TryRead(new UriBuilder(page) { Fragment = invalid }.Uri,
                out _, out _, out _, out _, out _, out _, out _), "An invalid v5 camera/style was accepted");
    }

    private static (double Latitude, double Longitude, double Altitude) Forward(MapCamera camera, double height, double distance)
    {
        var eye = camera.GetEye(height);
        double scale = Circumference * Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180);
        double bearing = camera.Bearing * Math.PI / 180, pitch = camera.Pitch * Math.PI / 180;
        double x = (eye.Longitude + 180) / 360 + distance * Math.Sin(pitch) * Math.Sin(bearing) / scale;
        double y = (1 - Math.Asinh(Math.Tan(eye.Latitude * Math.PI / 180)) / Math.PI) * .5 -
            distance * Math.Sin(pitch) * Math.Cos(bearing) / scale;
        return (Math.Atan(Math.Sinh((1 - 2 * y) * Math.PI)) * 180 / Math.PI,
            (x - Math.Floor(x)) * 360 - 180, eye.AltitudeMeters - distance * Math.Cos(pitch));
    }

    private static double LongitudeError(double first, double second) => Math.Abs(Math.IEEERemainder(first - second, 360));
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
