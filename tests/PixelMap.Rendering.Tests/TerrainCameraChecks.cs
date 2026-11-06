using PixelMap.App;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

internal static class TerrainCameraChecks
{
    internal static void Run()
    {
        const int width = 900, height = 600, size = 64;
        var id = new TileId(12, 2171, 1459);
        var values = new float[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) values[y * size + x] = 650 + x * 3 + y * 2;
        var elevation = new ElevationSnapshot([new ElevationTile(id, size, values)]);
        var center = Geographic((id.X + .5) / (1 << id.Z), (id.Y + .5) / (1 << id.Z));
        var datum = new MapCamera(center.Latitude, center.Longitude, 14, 0, 55) { GroundAltitudeMeters = 1800 };
        var reference = datum.Project(center.Latitude, center.Longitude, width, height);
        Assert(Math.Abs(reference.X - width / 2d) + Math.Abs(reference.Y - height / 2d) < 1e-8,
            "The local terrain datum moved the reference-plane center");
        Assert(datum.OnTerrain(null).GroundAltitudeMeters == 0, "Flat mode retained a terrain datum");
        Assert(datum.Interpolate(datum with { GroundAltitudeMeters = -200 }, .5).GroundAltitudeMeters == 800,
            "Camera interpolation lost the signed local datum");

        foreach (bool perspective in new[] { false, true })
        foreach (double bearing in new[] { 0d, 67, -145 })
        foreach (double pitch in new[] { 0d, 42, 67 })
        foreach (double exaggeration in new[] { 1d, 3 })
        {
            var terrain = new TerrainOptions(elevation, Exaggeration: exaggeration);
            var camera = new MapCamera(center.Latitude, center.Longitude, 14, bearing, pitch, perspective).OnTerrain(terrain, height);
            var location = Geographic((id.X + .53) / (1 << id.Z), (id.Y + .47) / (1 << id.Z));
            double ground = elevation.SampleSurfaceMeters(location.Latitude, location.Longitude);
            double altitude = terrain.RenderAltitude(camera, location.Latitude, location.Longitude, ground);
            Assert(camera.TryProject(location.Latitude, location.Longitude, altitude, width, height, out var screen),
                "A nearby terrain anchor left the frustum");
            Assert(camera.TryUnprojectTerrain(screen.X, screen.Y, width, height, terrain, out var hit), "Terrain inverse missed a visible surface");
            Assert(Math.Abs(hit.Latitude - location.Latitude) + Math.Abs(hit.Longitude - location.Longitude) < 2e-6 &&
                Math.Abs(hit.ElevationMeters - ground) < .02, "Terrain inverse disagreed with forward elevation projection");
            var changed = camera.TransformAt(.4, 18, pitch > 60 ? -5 : 5, screen.X, screen.Y, width, height, terrain);
            double afterAltitude = terrain.RenderAltitude(changed, location.Latitude, location.Longitude, ground);
            var after = changed.ProjectAtAltitude(location.Latitude, location.Longitude, afterAltitude, width, height);
            Assert(Math.Abs(after.X - screen.X) + Math.Abs(after.Y - screen.Y) < 1,
                $"Terrain orbit/zoom lost its anchor ({perspective}, {bearing}, {pitch}, {exaggeration})");
            var dragged = camera.PanFromTo(screen.X, screen.Y, screen.X + 40, screen.Y - 25, width, height, terrain);
            afterAltitude = terrain.RenderAltitude(dragged, location.Latitude, location.Longitude, ground);
            after = dragged.ProjectAtAltitude(location.Latitude, location.Longitude, afterAltitude, width, height);
            Assert(Math.Abs(after.X - screen.X - 40) + Math.Abs(after.Y - screen.Y + 25) < 1, "Terrain drag lost its anchor");
            var following = (camera with { TargetAltitudeMeters = 900 }).AnchorAltitude(location.Latitude,
                location.Longitude, ground + 900, 620, 340, width, height, terrain);
            afterAltitude = terrain.RenderAltitude(following, location.Latitude, location.Longitude, ground + 900);
            bool inFrustum = following.TryProject(location.Latitude, location.Longitude, afterAltitude, width, height, out after);
            Assert(inFrustum && Math.Abs(after.X - 620) + Math.Abs(after.Y - 340) < 1,
                "Altitude-aware follow lost its inset anchor over terrain");
        }

        var underwater = new ElevationSnapshot([new ElevationTile(new(0, 0, 0), 2, new float[] { -300, -300, -300, -300 })]);
        var below = new TerrainOptions(underwater);
        var belowCamera = new MapCamera(0, 179.99, 8, 120, 58, true).OnTerrain(below, height);
        Assert(belowCamera.GroundAltitudeMeters == -300 &&
            belowCamera.TryUnprojectTerrain(450, 300, width, height, below, out var belowHit) &&
            Math.Abs(belowHit.ElevationMeters + 300) < 1e-6, "Below-sea-level terrain lost its signed height");
        var flatTerrain = new TerrainOptions(elevation, Relief: false, Hypsometric: true, Exaggeration: 4);
        Assert(flatTerrain.RenderAltitude(datum.OnTerrain(flatTerrain), center.Latitude, center.Longitude,
            elevation.SampleSurfaceMeters(center.Latitude, center.Longitude) + 100) == 100,
            "Flat elevation mode failed to preserve flight clearance");

        SteepAnchors();
        InsetRouteFits();
        DenseRouteFit();
        MountainRays();
        Links();
        Console.WriteLine("Passed terrain camera datum, height-field inverse, orbit/zoom/drag and flight-follow anchors, signed heights and v1–v4 view links.");
    }

    private static void SteepAnchors()
    {
        const int width = 900, height = 600, size = 64;
        var id = new TileId(14, 8192, 8192);
        var values = new float[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) values[y * size + x] = 1000 + x * 15 + y * 12;
        var elevation = new ElevationSnapshot([
            new ElevationTile(new(0, 0, 0), 2, new float[] { 1000, 1000, 1000, 1000 }),
            new ElevationTile(id, size, values)]);
        var center = Geographic((id.X + .5) / (1 << id.Z), (id.Y + .5) / (1 << id.Z));
        var focus = new RoutePoint(center.Latitude, center.Longitude);
        double ground = elevation.SampleSurfaceMeters(focus.Latitude, focus.Longitude), worst = 0;
        int cases = 0;
        foreach (bool perspective in new[] { false, true })
        foreach (double zoom in new[] { 14d, 16, 19 })
        foreach (double bearing in new[] { 0d, 67, -145 })
        foreach (double pitch in new[] { 0d, 42, 67, 72 })
        foreach (double exaggeration in new[] { 1d, 4 })
        foreach (double clearance in new[] { 0d, 100, 900 })
        {
            var terrain = new TerrainOptions(elevation, Exaggeration: exaggeration);
            var location = perspective ? focus : focus.Move(bearing, clearance * Math.Tan(pitch * Math.PI / 180));
            var camera = new MapCamera(location.Latitude, location.Longitude, zoom, bearing, pitch, perspective,
                perspective ? clearance : 0).OnTerrain(terrain, height);
            var shifted = (camera with { TargetAltitudeMeters = 0 }).PanFromTo(width * .5, height * .5, 620, 340, width, height);
            camera = (camera with { Latitude = shifted.Latitude, Longitude = shifted.Longitude }).OnTerrain(terrain, height);
            var following = camera.AnchorAltitude(focus.Latitude, focus.Longitude, ground + clearance,
                620, 340, width, height, terrain);
            double altitude = terrain.RenderAltitude(following, focus.Latitude, focus.Longitude, ground + clearance);
            bool visible = following.TryProject(focus.Latitude, focus.Longitude, altitude, width, height, out var screen);
            double error = Math.Abs(screen.X - 620) + Math.Abs(screen.Y - 340);
            worst = Math.Max(worst, error);
            Assert(visible && error < 1,
                $"Steep terrain anchor failed at perspective={perspective}, zoom={zoom}, bearing={bearing}, pitch={pitch}, exaggeration={exaggeration}, AGL={clearance}, error={error:F3}");
            Assert(Math.Abs(following.OnTerrain(terrain, height).TargetAltitudeMeters - following.TargetAltitudeMeters) < 1e-6,
                "Applying the terrain datum again changed an anchored camera's eye clearance");
            cases++;
        }
        Console.WriteLine($"Passed {cases} steep-slope follow anchors with {worst:F4}px maximum projection error.");
    }

    private static void InsetRouteFits()
    {
        const int width = 900, height = 600, size = 64;
        var id = new TileId(14, 8192, 8192);
        var values = new float[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) values[y * size + x] = 1000 + x * 15 + y * 12;
        var terrain = new TerrainOptions(new ElevationSnapshot([
            new ElevationTile(new(0, 0, 0), 2, [1000, 1000, 1000, 1000]),
            new ElevationTile(id, size, values)]), Exaggeration: 4);
        RoutePoint[] route = [Point(.25, .25), Point(.5, .6), Point(.75, .75)];
        var origin = Point(.5, .5);
        int cases = 0;
        foreach (bool perspective in new[] { false, true })
        foreach (double bearing in new[] { 0d, 67, -145 })
        foreach (double pitch in new[] { 0d, 42, 67 })
        foreach (var viewport in new[]
        {
            new SKRect(0, 0, 900, 240), new SKRect(0, 360, 900, 600), new SKRect(600, 0, 900, 600)
        })
        {
            var camera = new MapCamera(origin.Latitude, origin.Longitude, 15, bearing, pitch, perspective);
            var fitted = camera.FitRoute(route, width, height, viewport, 20, terrain);
            Assert(fitted.Zoom > MapCamera.MinZoom,
                "Terrain route fitting exhausted the lens range behind an asymmetric inset");
            if (!perspective && pitch == 0)
                Assert(Math.Abs(fitted.Zoom - camera.FitRoute(route, width, height, viewport, 20).Zoom) < 1e-9,
                    "Subpixel rounding changed the scale of an already-fitted overhead route");
            foreach (var point in route)
            {
                double surface = terrain.Elevation.SampleSurfaceMeters(point.Latitude, point.Longitude);
                double altitude = terrain.RenderAltitude(fitted, point.Latitude, point.Longitude, surface);
                Assert(fitted.TryProject(point.Latitude, point.Longitude, altitude, width, height, out var screen) &&
                    screen.X >= viewport.Left + 19.99 && screen.X <= viewport.Right - 19.99 &&
                    screen.Y >= viewport.Top + 19.99 && screen.Y <= viewport.Bottom - 19.99,
                    $"Terrain fit left the safe viewport at perspective={perspective}, bearing={bearing}, pitch={pitch}, viewport={viewport}");
            }
            cases++;
        }
        Console.WriteLine($"Passed {cases} elevated route fits with top, bottom and side UI insets.");

        RoutePoint Point(double x, double y)
        {
            var point = Geographic((id.X + x) / (1 << id.Z), (id.Y + y) / (1 << id.Z));
            return new(point.Latitude, point.Longitude);
        }
    }

    private static void DenseRouteFit()
    {
        const int width = 900, height = 600, size = 64;
        var id = new TileId(14, 8192, 8192);
        var values = new float[size * size];
        for (int y = 23; y <= 27; y++)
        for (int x = 17; x <= 21; x++) values[y * size + x] = 3000;
        var terrain = new TerrainOptions(new ElevationSnapshot([
            new ElevationTile(new(0, 0, 0), 2, [0, 0, 0, 0]),
            new ElevationTile(id, size, values)]), Exaggeration: 3);
        var route = new RoutePoint[1025];
        for (int i = 0; i < route.Length; i++) route[i] = Point(.1 + .8 * i / (route.Length - 1), .6);
        // Providers need not sample by arc length. The summit is the first bend,
        // followed by many detailed points on the long descent and valley road.
        // The old 513-point subset skipped this vertex entirely.
        route[1] = Point(.3, .4);
        var origin = Point(.5, .5);
        foreach (bool perspective in new[] { false, true })
        {
            var camera = new MapCamera(origin.Latitude, origin.Longitude, 15, 0, 55, perspective);
            var viewport = new SKRect(200, 0, width, height);
            var fitted = camera.FitRoute(route, width, height, viewport, 30, terrain);
            Assert(fitted.Zoom > MapCamera.MinZoom, "A dense elevated route exhausted the map lens range");
            foreach (var point in route)
            {
                double surface = terrain.Elevation.SampleSurfaceMeters(point.Latitude, point.Longitude);
                double altitude = terrain.RenderAltitude(fitted, point.Latitude, point.Longitude, surface);
                Assert(fitted.TryProject(point.Latitude, point.Longitude, altitude, width, height, out var screen) &&
                    screen.X >= viewport.Left + 29.99 && screen.X <= viewport.Right - 29.99 &&
                    screen.Y >= viewport.Top + 29.99 && screen.Y <= viewport.Bottom - 29.99,
                    $"Dense route fitting omitted a terrain peak in the {(perspective ? "perspective" : "orthographic")} view");
            }
        }
        Console.WriteLine("Passed full-vertex terrain fitting for dense routes with unevenly sampled mountain peaks.");

        RoutePoint Point(double x, double y)
        {
            var point = Geographic((id.X + x) / (1 << id.Z), (id.Y + y) / (1 << id.Z));
            return new(point.Latitude, point.Longitude);
        }
    }

    private static void MountainRays()
    {
        const int width = 900, height = 600, size = 64;
        var id = new TileId(14, 8192, 8192);
        var values = new float[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) values[y * size + x] = (float)(500 * Math.Clamp((.6 - (y + .5) / size) / .35, 0, 1));
        var elevation = new ElevationSnapshot([
            new ElevationTile(new(0, 0, 0), 2, new float[] { 0, 0, 0, 0 }), new ElevationTile(id, size, values)]);
        var terrain = new TerrainOptions(elevation);
        var center = Geographic((id.X + .5) / (1 << id.Z), (id.Y + .8) / (1 << id.Z));
        var camera = new MapCamera(center.Latitude, center.Longitude, 15.5, 0, 72, true).OnTerrain(terrain, height);
        double pitch = camera.Pitch * Math.PI / 180, focal = height * 1.2071067811865475;
        double horizon = height * .5 - focal * Math.Cos(pitch) / Math.Sin(pitch);
        double pixelsPerMeter = 512 * Math.Pow(2, camera.Zoom) /
            (40075016.68557849 * Math.Cos(camera.Latitude * Math.PI / 180));
        double eye = camera.GroundAltitudeMeters + camera.TargetAltitudeMeters + focal * Math.Cos(pitch) / pixelsPerMeter;
        foreach (double y in new[] { 35d, horizon })
        {
            Assert(camera.TryUnprojectTerrain(width * .5, y, width, height, terrain, out var hit),
                "A horizontal or rising screen ray missed the mountain in front of the camera");
            Assert(hit.ElevationMeters >= eye - .001, "An upward-looking terrain ray returned ground below the eye");
            double altitude = terrain.RenderAltitude(camera, hit.Latitude, hit.Longitude, hit.ElevationMeters);
            Assert(camera.TryProject(hit.Latitude, hit.Longitude, altitude, width, height, out var screen) &&
                Math.Abs(screen.X - width * .5) + Math.Abs(screen.Y - y) < .01,
                "An upward-looking mountain intersection failed forward projection");
        }
    }

    private static void Links()
    {
        var page = new Uri("https://wieslawsoltes.github.io/UnoPixelMap/?source=test");
        var camera = new MapCamera(45.875, 10.85, 12.3, -25, 58, true, 140);
        var link = MapViewLink.Create(page, camera, PixelStyle.Dusk, false, true, true, 2.25);
        Assert(link.AbsolutePath == "/UnoPixelMap/" && link.Query == "?source=test", "Sharing lost the Pages base path or query");
        Assert(MapViewLink.TryRead(link, out var read, out var style, out var earth, out var elevation, out var relief, out var scale) &&
            read == camera && style == PixelStyle.Dusk && !earth && elevation && relief && scale == 2.25,
            "v4 elevation view did not round-trip");
        Assert(MapViewLink.TryRead(MapViewLink.Create(page, camera, PixelStyle.Day, true), out read, out _, out earth,
            out elevation, out relief, out scale) && earth && !elevation && !relief && scale == 1 && read == camera,
            "Existing v3 links acquired terrain relief");
        foreach (var fragment in new[] { "v1/45/10/12/0/55/Day", "v2/45/10/12/0/55/Day/Map",
            "v3/45/10/12/0/55/Day/Map/Perspective" })
            Assert(MapViewLink.TryRead(new UriBuilder(page) { Fragment = fragment }.Uri, out _, out _, out _,
                out elevation, out relief, out scale) && !elevation && !relief && scale == 1, "A legacy shared view was rejected");
        foreach (var tail in new[] { "Relief/NaN", "Relief/0", "Relief/5", "Unknown/1", "Flat/Infinity" })
            Assert(!MapViewLink.TryRead(new UriBuilder(page) { Fragment = "v4/45/10/12/0/55/Day/Elevation/Perspective/0/" + tail }.Uri,
                out _, out _, out _, out _, out _, out _), "Invalid terrain fragment was accepted");
    }

    private static (double Latitude, double Longitude) Geographic(double x, double y) =>
        (Math.Atan(Math.Sinh((1 - 2 * y) * Math.PI)) * 180 / Math.PI, x * 360 - 180);

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
