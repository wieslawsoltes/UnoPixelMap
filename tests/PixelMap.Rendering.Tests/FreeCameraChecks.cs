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
        NearGeometry();
        LensBounds();
        Links();
        Console.WriteLine($"Passed {poses} free-camera poses, physical near/far clipping, terrain datum, close model picking, bounded horizon requests and v5 style/view links.");
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
        double scale = Circumference * Math.Cos(camera.Latitude * Math.PI / 180);
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
