using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

internal static class TerrainNavigationChecks
{
    private const int Width = 680, Height = 460;

    internal static void Run()
    {
        var id = new TileId(14, 8192, 8192);
        const int size = 64;
        var heights = new float[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double u = (x + .5) / size, v = (y + .5) / size;
            heights[y * size + x] = (float)(1300 + 350 * u + 200 * v + 140 * Math.Sin(u * 4 * Math.PI) * Math.Sin(v * 2 * Math.PI));
        }
        var fallback = Enumerable.Repeat(1300f, size * size).ToArray();
        var elevation = new ElevationSnapshot([new(new(0, 0, 0), size, fallback), new(id, size, heights)]);
        MapTile[] tiles = [new(id, [])];
        var center = Geo(id, .5, .5);
        var start = Geo(id, .34, .45);
        var finish = Geo(id, .66, .55);
        RoutePoint[] geometry = [start, center, finish];
        var plans = Enum.GetValues<TravelMode>().ToDictionary(mode => mode, mode => Plan(mode, geometry));
        using var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new PixelMapRenderer();
        var initial = new MapCamera(center.Latitude, center.Longitude, 14.3, 21, 55, true);
        Assert(!renderer.TryProjectTerrain(center.Latitude, center.Longitude, Width, Height, initial, out _),
            "A renderer without terrain returned a mesh projection");
        var cases = new[]
        {
            (Zoom: 14.3, Bearing: 21d, Pitch: 55d, Perspective: true, Pixel: 2, Style: PixelStyle.Day, Relief: true, Scale: 1d),
            (Zoom: 14.85, Bearing: 39d, Pitch: 64d, Perspective: true, Pixel: 3, Style: PixelStyle.Dusk, Relief: true, Scale: 2d),
            (Zoom: 14.6, Bearing: -32d, Pitch: 0d, Perspective: false, Pixel: 2, Style: PixelStyle.Night, Relief: false, Scale: 1d),
            (Zoom: 14.6, Bearing: 67d, Pitch: 67d, Perspective: true, Pixel: 4, Style: PixelStyle.Night, Relief: true, Scale: 4d)
        };
        int checks = 0;
        foreach (var item in cases)
        {
            var terrain = new TerrainOptions(elevation, item.Relief, Hypsometric: !item.Relief, Exaggeration: item.Scale);
            var camera = (initial with { Zoom = item.Zoom, Bearing = item.Bearing, Pitch = item.Pitch, Perspective = item.Perspective })
                .OnTerrain(terrain, Height);
            DrawMap(renderer, camera, terrain, item.Style, item.Pixel);
            Assert(renderer.TryProjectTerrain(center.Latitude, center.Longitude, Width, Height, camera, out var anchor),
                "The current terrain could not project its center");
            Assert(!renderer.TryProjectTerrain(center.Latitude, center.Longitude, Width + 1, Height, camera, out _) &&
                !renderer.TryProjectTerrain(center.Latitude, center.Longitude, Width, Height, camera with { Bearing = camera.Bearing + 1 }, out _),
                "A stale camera or viewport reused terrain mesh projection");
            Assert(renderer.TryPickTerrain(anchor.X, anchor.Y, Width, Height, camera, out var picked) &&
                renderer.TryProjectTerrain(picked.Latitude, picked.Longitude, Width, Height, camera, out var roundTrip) &&
                Math.Abs(roundTrip.X - (Math.Floor(anchor.X / item.Pixel) + .5) * item.Pixel) +
                Math.Abs(roundTrip.Y - (Math.Floor(anchor.Y / item.Pixel) + .5) * item.Pixel) < .12,
                "Public terrain projection disagreed with the rendered art-pixel surface");

            var shapes = new HashSet<ulong>();
            foreach (var mode in Enum.GetValues<TravelMode>())
            {
                var plan = plans[mode];
                DrawOverlay(null);
                uint routeColor = mode switch
                {
                    TravelMode.Walk => 0xffb4df79, TravelMode.Bike => 0xff67dba8, TravelMode.Drive => 0xff65cfee,
                    TravelMode.Train => 0xffe9a2e1, TravelMode.Boat => 0xff85dcea, _ => 0xffffc37a
                };
                foreach (double station in new[] { .35, .5, .65 })
                {
                    var point = station <= .5 ? start.InterpolateTo(center, station * 2) : center.InterpolateTo(finish, station * 2 - 1);
                    Assert(renderer.TryProjectTerrain(point.Latitude, point.Longitude, Width, Height, camera, out var screen) &&
                        NearColor(screen, routeColor, item.Pixel * 2 + 3), $"{mode} route missed its displayed terrain mesh after an LOD/palette change");
                }
                Assert(renderer.TryProjectTerrain(start.Latitude, start.Longitude, Width, Height, camera, out var origin) &&
                    NearColor(origin, 0xff26393c, item.Pixel * 4), $"{mode} origin flag detached from terrain");

                var baseline = bitmap.Pixels;
                double clearance = mode == TravelMode.Flight ? 150 : 0;
                double physical = elevation.SampleSurfaceMeters(center.Latitude, center.Longitude);
                var pose = new VehiclePose(center, 18, AltitudeMeters: physical + clearance, Moving: false, SpeedMetersPerSecond: 30);
                double altitude = terrain.RenderAltitude(camera, center.Latitude, center.Longitude, pose.AltitudeMeters);
                Assert(camera.TryProject(center.Latitude, center.Longitude, altitude, Width, Height, out var vehicleScreen),
                    "The terrain vehicle fixture left the camera");
                DrawOverlay(pose);
                var first = VehicleDifference(baseline, vehicleScreen, item.Pixel * 25);
                Assert(first.Changed > 12 && first.Opaque > 4, $"{mode} sprite disappeared or was screened by its own terrain");
                shapes.Add(first.Hash);
                DrawOverlay(pose with { Bearing = 113 });
                Assert(VehicleDifference(baseline, vehicleScreen, item.Pixel * 25).Hash != first.Hash,
                    $"{mode} lost world-relative heading on terrain");

                // Trails remain geographic and pause/seek semantics survive terrain framing.
                for (int step = 1; step <= 5; step++)
                {
                    var point = center.Move(90, step * 4);
                    DrawOverlay(pose with { Point = point, AltitudeMeters = elevation.SampleSurfaceMeters(point.Latitude, point.Longitude) + clearance,
                        AnimationSeconds = step * .3, Moving = true });
                }
                var stoppedPoint = center.Move(90, 20);
                var stopped = pose with { Point = stoppedPoint, AltitudeMeters = elevation.SampleSurfaceMeters(stoppedPoint.Latitude, stoppedPoint.Longitude) + clearance,
                    AnimationSeconds = 1.5, Moving = false };
                DrawOverlay(stopped);
                var paused = bitmap.GetPixelSpan().ToArray();
                DrawOverlay(stopped with { AnimationSeconds = 16 });
                Assert(paused.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), $"{mode} terrain effects advanced while paused");
                var seek = stopped with { AnimationSeconds = 1.5, Continuity = 1 };
                DrawOverlay(seek);
                var reset = bitmap.GetPixelSpan().ToArray();
                using (var fresh = new PixelMapRenderer())
                {
                    DrawMap(fresh, camera, terrain, item.Style, item.Pixel);
                    canvas.Clear(SKColors.Transparent);
                    fresh.DrawNavigation(canvas, Width, Height, camera, plan, seek, item.Pixel);
                    Assert(reset.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), $"{mode} terrain navigation retained stale view or effect state after seeking");
                }

                canvas.Clear(SKColors.Transparent);
                var gate = new RoutePosition(center, 45, 0);
                renderer.DrawGate(canvas, camera, Width, Height, gate, mode, physical + clearance, .25);
                double halfWidth = mode switch { TravelMode.Flight => 46, TravelMode.Boat => 9, TravelMode.Train => 3, _ => 4 };
                foreach (int side in new[] { -1, 1 })
                {
                    var foot = center.Move(gate.Bearing + side * 90, halfWidth);
                    (double X, double Y) flag;
                    bool projected = mode == TravelMode.Flight
                        ? camera.TryProject(foot.Latitude, foot.Longitude,
                            terrain.RenderAltitude(camera, foot.Latitude, foot.Longitude, physical + clearance), Width, Height, out flag)
                        : renderer.TryProjectTerrain(foot.Latitude, foot.Longitude, Width, Height, camera, out flag);
                    bool foundPole = projected && NearColor((flag.X, flag.Y - 5), 0xff193039, 3);
                    Assert(foundPole, $"{mode} gate pole did not follow terrain/flight altitude at zoom {item.Zoom}, side {side}");
                }
                checks++;

                void DrawOverlay(VehiclePose? vehicle)
                {
                    canvas.Clear(SKColors.Transparent);
                    renderer.DrawNavigation(canvas, Width, Height, camera, plan, vehicle, item.Pixel);
                }
            }
            Assert(shapes.Count == 6, "Terrain navigation collapsed the six vehicle symbols");
        }

        // A continuous planar slope has no hidden ridges. At a grazing camera angle, an
        // art pixel spans appreciable height, so markers must survive subpixel placement.
        var planeTiles = new List<ElevationTile> { new(new(0, 0, 0), size, fallback) };
        for (int row = -1; row <= 1; row++)
        for (int column = -1; column <= 1; column++)
        {
            var plane = new float[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) plane[y * size + x] = (float)(1000 + 500 * (column + (x + .5) / size) + 200 * (row + (y + .5) / size));
            planeTiles.Add(new(new(id.Z, id.X + column, id.Y + row), size, plane));
        }
        var steepTerrain = new TerrainOptions(new(planeTiles), Exaggeration: 4);
        var steepCamera = (initial with { Zoom = 14.6, Bearing = 0, Pitch = 67 }).OnTerrain(steepTerrain, Height);
        DrawMap(renderer, steepCamera, steepTerrain, PixelStyle.Day, 4);
        for (int row = 0; row < 5; row++)
        for (int column = 0; column < 5; column++)
        {
            var point = Geo(id, .44 + column * .03, .44 + row * .03);
            canvas.Clear(SKColors.Transparent);
            renderer.DrawGate(canvas, steepCamera, Width, Height, new(point, 45, 0), TravelMode.Drive, 0, .25);
            foreach (int side in new[] { -1, 1 })
            {
                var foot = point.Move(45 + side * 90, 4);
                Assert(renderer.TryProjectTerrain(foot.Latitude, foot.Longitude, Width, Height, steepCamera, out var flag) &&
                    NearColor((flag.X, flag.Y - 5), 0xff193039, 3),
                    $"A visible gate was self-occluded on a grazing 4× terrain slope at cell {column},{row}");
            }
        }

        var blocker = new MapFeature("building", "", "", "", 907, 4096, GeometryType.Polygon,
            [[new(1850, 1850), new(2246, 1850), new(2246, 2246), new(1850, 2246)]], 180, 0, false, 0, 0, "");
        renderer.Draw(canvas, Width, Height, steepCamera, [new(id, [blocker])], PixelStyle.Day, 4, null, false, false, steepTerrain);
        canvas.Clear(SKColors.Transparent);
        renderer.DrawGate(canvas, steepCamera, Width, Height, new(center, 45, 0), TravelMode.Drive, 0, .25);
        Assert(bitmap.Pixels.All(pixel => pixel.Alpha == 0), "Subpixel terrain visibility exposed a gate through an occluding building");

        renderer.Draw(canvas, Width, Height, initial, tiles, pixelSize: 2);
        Assert(!renderer.TryProjectTerrain(center.Latitude, center.Longitude, Width, Height, initial, out _),
            "Disabling terrain left its mesh projection active");
        Console.WriteLine($"Passed {checks} terrain navigation cases covering routes, six vehicle types, heading, paused/seeked effects, gates, LOD, palettes, exaggeration, flat tint and public mesh projection, plus 25 grazing-slope gates and building occlusion.");

        void DrawMap(PixelMapRenderer target, MapCamera camera, TerrainOptions terrain, PixelStyle style, int pixel) =>
            target.Draw(canvas, Width, Height, camera, tiles, style, pixel, null, false, false, terrain);

        bool NearColor((double X, double Y) point, uint color, int radius)
        {
            for (int y = (int)point.Y - radius; y <= point.Y + radius; y++)
            for (int x = (int)point.X - radius; x <= point.X + radius; x++)
                if ((uint)x < Width && (uint)y < Height && (uint)bitmap.GetPixel(x, y) == color) return true;
            return false;
        }

        (int Changed, int Opaque, ulong Hash) VehicleDifference(SKColor[] baseline, (double X, double Y) center, int radius)
        {
            int changed = 0, opaque = 0;
            ulong hash = 14695981039346656037;
            for (int y = Math.Max(0, (int)center.Y - radius); y < Math.Min(Height, (int)center.Y + radius); y++)
            for (int x = Math.Max(0, (int)center.X - radius); x < Math.Min(Width, (int)center.X + radius); x++)
            {
                var current = bitmap.GetPixel(x, y);
                var original = baseline[y * Width + x];
                if (current == original) continue;
                changed++;
                if (current.Alpha == 255 && original.Alpha == 0) opaque++;
                hash = unchecked((hash ^ (uint)current ^ (uint)(y * Width + x)) * 1099511628211);
            }
            return (changed, opaque, hash);
        }
    }

    private static RoutePlan Plan(TravelMode mode, RoutePoint[] geometry)
    {
        var start = new RouteStop("Start", geometry[0]);
        var finish = new RouteStop("Finish", geometry[^1]);
        double distance = 0;
        for (int i = 1; i < geometry.Length; i++) distance += geometry[i - 1].DistanceTo(geometry[i]);
        return new(mode, [start, finish], [new(start, finish, geometry, distance, TimeSpan.FromMinutes(5), [])], "Terrain fixture", false);
    }

    private static RoutePoint Geo(TileId id, double u, double v)
    {
        int count = 1 << id.Z;
        return new(Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (id.Y + v) / count))) * 180 / Math.PI,
            (id.X + u) * 360 / count - 180);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
