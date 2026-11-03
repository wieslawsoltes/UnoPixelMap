using System.Diagnostics;
using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

internal static class TerrainRenderingChecks
{
    private const int Width = 640, Height = 420, PixelSize = 2;

    internal static void Run()
    {
        var id = new TileId(14, 8192, 8192);
        var center = Geo(id, .5, .5);
        var camera = new MapCamera(center.Latitude, center.Longitude, 14.4, 23, 55, true);
        var plateau = new ElevationSnapshot([Dem(new(0, 0, 0), static (_, _) => 2500)]);
        var plateauOptions = new TerrainOptions(plateau);
        using var renderer = new PixelMapRenderer();
        using var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        MapTile[] emptyMap = [new(id, [])];

        renderer.Draw(canvas, Width, Height, camera, emptyMap, pixelSize: PixelSize);
        var flat = bitmap.GetPixelSpan().ToArray();
        Draw(camera, emptyMap, null);
        Assert(flat.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "The terrain overload changed disabled relief pixels");
        Draw(camera, emptyMap, new(ElevationSnapshot.Empty));
        Assert(flat.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "An empty DEM changed flat map pixels");
        Draw(camera, emptyMap, new(plateau, Relief: false));
        Assert(flat.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "Disabling both terrain switches changed flat map pixels");

        double worstProjection = 0;
        foreach (bool perspective in new[] { false, true })
        foreach (double pitch in new[] { 0d, 55, 72 })
        {
            var view = (camera with { Perspective = perspective, Pitch = pitch }).OnTerrain(plateauOptions, Height);
            Draw(view, emptyMap, plateauOptions);
            foreach (var point in new[] { new SKPoint(200, 240), new SKPoint(320, 250), new SKPoint(440, 300) })
            {
                Assert(renderer.TryPickTerrain(point.X, point.Y, Width, Height, view, out var hit), "A visible high-altitude plateau had a picking gap");
                Assert(Math.Abs(hit.ElevationMeters - 2500) < .05, "Terrain picking returned exaggerated or relative height");
                double altitude = plateauOptions.RenderAltitude(view, hit.Latitude, hit.Longitude, hit.ElevationMeters);
                Assert(view.TryProject(hit.Latitude, hit.Longitude, altitude, Width, Height, out var screen), "The picked terrain point fell outside its camera");
                double expectedX = Math.Floor(point.X / PixelSize) * PixelSize + PixelSize * .5;
                double expectedY = Math.Floor(point.Y / PixelSize) * PixelSize + PixelSize * .5;
                worstProjection = Math.Max(worstProjection, Math.Abs(screen.X - expectedX) + Math.Abs(screen.Y - expectedY));
            }
        }
        Assert(worstProjection < .08, $"Terrain pixels and camera rays disagree by {worstProjection:F4}px");

        var hills = new ElevationSnapshot([Dem(new(0, 0, 0), static (_, _) => 1000),
            Dem(id, static (u, v) => 1000 + 1050 * Math.Sin(u * Math.PI) * Math.Sin(v * Math.PI) + 180 * u)]);
        var relief = new TerrainOptions(hills);
        var hillCamera = camera.OnTerrain(relief, Height);
        Draw(hillCamera, emptyMap, relief);
        var natural = bitmap.GetPixelSpan().ToArray();
        Draw(hillCamera, emptyMap, relief);
        Assert(natural.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "Retained terrain frames changed their art pixels");
        Draw(hillCamera, emptyMap, relief with { Hypsometric = true });
        Assert(!natural.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "The elevation layer did not tint terrain");
        var tinted = bitmap.GetPixelSpan().ToArray();
        using (var fresh = new PixelMapRenderer())
        {
            fresh.Draw(canvas, Width, Height, hillCamera, emptyMap, PixelStyle.Day, PixelSize, null, true, false,
                relief with { Hypsometric = true });
            Assert(tinted.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "CPU fallback or renderer lifetime changed the terrain raster");
        }
        Draw(hillCamera, emptyMap, relief with { Exaggeration = 2 });
        Assert(!natural.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "Exaggeration did not invalidate terrain geometry");
        var planar = relief with { Relief = false, Hypsometric = true };
        var planarCamera = camera.OnTerrain(planar, Height);
        Draw(planarCamera, emptyMap, planar);
        Assert(renderer.TryPickTerrain(320, 250, Width, Height, planarCamera, out var planHit), "Flat elevation tint lost picking");
        Assert(planarCamera.TryProject(planHit.Latitude, planHit.Longitude, 0, Width, Height, out var planScreen) &&
            Math.Abs(planScreen.X - 321) + Math.Abs(planScreen.Y - 251) < .08, "Flat elevation tint deformed the ground plane");

        var building = new MapFeature("building", "", "", "Summit lodge", 702, 4096, GeometryType.Polygon,
            [[new(1850, 1850), new(2246, 1850), new(2246, 2246), new(1850, 2246)]], 90, 0, false, 0, 0, "");
        MapTile[] modelMap = [new(id, [building])];
        foreach (bool perspective in new[] { false, true })
        {
            var view = (camera with { Perspective = perspective, Pitch = 55, Zoom = 15.5 }).OnTerrain(relief, Height);
            Draw(view, modelMap, relief);
            double foundation = hills.SampleSurfaceMeters(center.Latitude, center.Longitude);
            double altitude = relief.RenderAltitude(view, center.Latitude, center.Longitude, foundation + 90);
            Assert(view.TryProject(center.Latitude, center.Longitude, altitude, Width, Height, out var roof), "Elevated model roof was outside the view");
            Assert(renderer.Pick(roof.X, roof.Y, Width, Height, view, modelMap)?.Id == building.Id,
                "A building's visible roof and terrain-aware picking disagree");
        }

        // Narrow towers can lie below the camera datum while their physical height remains
        // substantial. Culling must use their height above the foundation.
        var needle = building with { Id = 703, Name = "", Paths = [[new(2049, 2049), new(2051, 2049), new(2051, 2051), new(2049, 2051)]] };
        MapTile[] needleMap = [new(id, [needle])];
        var lowerCamera = camera with { Bearing = 0, Zoom = 16, Perspective = false, Pitch = 55, GroundAltitudeMeters = 2600 };
        Draw(lowerCamera, needleMap, plateauOptions);
        var needleLocation = Geo(id, 2050d / 4096, 2050d / 4096);
        Assert(lowerCamera.TryProject(needleLocation.Latitude, needleLocation.Longitude, 2550, Width, Height, out var wall) &&
            renderer.Pick(wall.X, wall.Y, Width, Height, lowerCamera, needleMap)?.Id == needle.Id,
            "A narrow model below the camera datum lost its visible wall");

        var ridge = new ElevationSnapshot([Dem(new(0, 0, 0), static (_, _) => 0),
            Dem(id, static (_, v) => 900 * Math.Exp(-Math.Pow((v - .63) / .07, 2)))]);
        var ridgeOptions = new TerrainOptions(ridge);
        var ridgeCamera = (camera with { Bearing = 0, Zoom = 13.8, Pitch = 55 }).OnTerrain(ridgeOptions, Height);
        var hiddenPoi = building with { Id = 704, Layer = "poi", Type = GeometryType.Point, Name = "Hidden lookout", Paths = [[new(2048, 1024)]] };
        var hiddenRoad = hiddenPoi with { Id = 705, Layer = "transportation", Type = GeometryType.LineString,
            Name = "Hidden road", Paths = [[new(1800, 1024), new(2300, 1024)]] };
        MapTile[] ridgeMap = [new(id, [hiddenPoi, hiddenRoad])];
        Draw(ridgeCamera, ridgeMap, ridgeOptions);
        var hiddenLocation = Geo(id, .5, .25);
        double hiddenHeight = ridge.SampleSurfaceMeters(hiddenLocation.Latitude, hiddenLocation.Longitude);
        Assert(ridgeCamera.TryProject(hiddenLocation.Latitude, hiddenLocation.Longitude,
            ridgeOptions.RenderAltitude(ridgeCamera, hiddenLocation.Latitude, hiddenLocation.Longitude, hiddenHeight),
            Width, Height, out var hiddenScreen), "Occlusion fixture missed the camera");
        Assert(renderer.TryPickTerrain(hiddenScreen.X, hiddenScreen.Y, Width, Height, ridgeCamera, out var foreground) &&
            foreground.ElevationMeters > hiddenHeight + 100, "Occlusion fixture did not put terrain in front of the POI");
        Assert(renderer.Pick(hiddenScreen.X, hiddenScreen.Y, Width, Height, ridgeCamera, ridgeMap) is null,
            "A hidden point or road was picked through the terrain ridge");
        var staleCamera = ridgeCamera with { Bearing = 1 };
        staleCamera.TryProject(hiddenLocation.Latitude, hiddenLocation.Longitude,
            ridgeOptions.RenderAltitude(staleCamera, hiddenLocation.Latitude, hiddenLocation.Longitude, hiddenHeight),
            Width, Height, out hiddenScreen);
        Assert(renderer.Pick(hiddenScreen.X, hiddenScreen.Y, Width, Height, staleCamera, ridgeMap) is null,
            "Picking used stale terrain depth after the camera moved");

        var wrappedCamera = new MapCamera(12, 179.99, 1, 31, 55, true).OnTerrain(plateauOptions, Height);
        Draw(wrappedCamera, emptyMap, plateauOptions);
        for (int y = Height / 2; y < Height - 12; y += 29)
        for (int x = 12; x < Width - 12; x += 37)
        {
            if (!wrappedCamera.TryUnprojectTerrain(x, y, Width, Height, plateauOptions, out _)) continue;
            Assert(renderer.TryPickTerrain(x, y, Width, Height, wrappedCamera, out var hit) && Math.Abs(hit.ElevationMeters - 2500) < .05,
                "A wrapped world or changing terrain LOD introduced a picking seam");
        }

        // Source images are projected through the same height mesh as the road artwork.
        var imagery = Image(id);
        var photoCamera = (camera with { Pitch = 35, Perspective = true, Zoom = 13.7 }).OnTerrain(plateauOptions, Height);
        Draw(photoCamera, emptyMap, plateauOptions, [imagery]);
        foreach (var sample in new[] { (U: .3, V: .3, Sea: true), (U: .7, V: .3, Sea: false) })
        {
            var location = Geo(id, sample.U, sample.V);
            Assert(photoCamera.TryProject(location.Latitude, location.Longitude, 2500, Width, Height, out var screen), "Draped image fixture missed its camera");
            var color = bitmap.GetPixel((int)screen.X, (int)screen.Y);
            Assert(sample.Sea ? color.Blue > color.Red + 20 : color.Green > color.Red && color.Green > color.Blue,
                "Earth imagery slipped off the elevated terrain");
        }

        // DEM replacements invalidate geometry independently of tile/source identities.
        var replacement = new TerrainOptions(new([Dem(new(0, 0, 0), static (_, _) => -120)]));
        var belowSea = (camera with { Pitch = 0, Perspective = false }).OnTerrain(replacement, Height);
        Draw(belowSea, emptyMap, replacement);
        Assert(renderer.TryPickTerrain(320, 250, Width, Height, belowSea, out var negative) &&
            Math.Abs(negative.ElevationMeters + 120) < .02, "Signed below-sea-level terrain was clipped or cached from the old DEM");
        Draw(camera, emptyMap, null);
        Assert(flat.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "Leaving relief failed to restore the exact flat artwork");

        // Terrain texture identities survive a flat interlude independently of the most
        // recent flat view, including palette, pixel scale, imagery and vector changes.
        renderer.Draw(canvas, Width, Height, hillCamera, ridgeMap, PixelStyle.Night, 3, [imagery], false, false, null);
        renderer.Draw(canvas, Width, Height, hillCamera, ridgeMap, PixelStyle.Night, 3, [imagery], false, false, relief);
        var resumed = bitmap.GetPixelSpan().ToArray();
        using (var fresh = new PixelMapRenderer())
        {
            fresh.Draw(canvas, Width, Height, hillCamera, ridgeMap, PixelStyle.Night, 3, [imagery], false, false, relief);
            Assert(resumed.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "A flat-mode interlude reused stale terrain textures");
        }

        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 8; i++) Draw(hillCamera with { Bearing = hillCamera.Bearing + i }, emptyMap, relief);
        timer.Stop();
        if (Environment.GetEnvironmentVariable("PIXELMAP_SCREENSHOT_DIR") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            Draw(hillCamera, modelMap, relief with { Hypsometric = true }, [imagery]);
            using var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(output, "elevation-terrain.png"));
            png.SaveTo(file);
        }
        Console.WriteLine($"Passed elevation relief, flat compatibility, height-field picking ({worstProjection:F4}px), 2D tint, signed datum, foundations, thin models, ridge occlusion, stale-view picking, wrapped LOD seams, Earth draping and cache invalidation. Warm terrain orbit {timer.Elapsed.TotalMilliseconds / 8:F1} ms/frame at {Width}×{Height} / {PixelSize}.");

        void Draw(MapCamera view, IReadOnlyList<MapTile> tiles, TerrainOptions? terrain, IReadOnlyList<ImageryTile>? images = null) =>
            renderer.Draw(canvas, Width, Height, view, tiles, PixelStyle.Day, PixelSize, images, false, false, terrain);
    }

    private static ElevationTile Dem(TileId id, Func<double, double, double> sample)
    {
        const int size = 64;
        var heights = new float[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) heights[y * size + x] = (float)sample((x + .5) / size, (y + .5) / size);
        return new(id, size, heights);
    }

    private static ImageryTile Image(TileId id)
    {
        using var bitmap = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(36, 95, 128));
        using var paint = new SKPaint { Color = new(60, 104, 48) };
        canvas.DrawRect(128, 0, 128, 256, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(id, png.ToArray());
    }

    private static (double Latitude, double Longitude) Geo(TileId id, double u, double v)
    {
        int count = 1 << id.Z;
        return (Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (id.Y + v) / count))) * 180 / Math.PI,
            (id.X + u) * 360 / count - 180);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
