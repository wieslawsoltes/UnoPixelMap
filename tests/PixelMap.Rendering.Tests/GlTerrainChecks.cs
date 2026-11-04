using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using Silk.NET.OpenGL;
using SkiaSharp;

/// <summary>Opt-in real GLES shader, framebuffer, depth and fixed-resolution checks.</summary>
internal static class GlTerrainChecks
{
    private static readonly TileId Tile = new(14, 8192, 8192);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private sealed record Difference(int Pixels, int ChangedPixels, int MaximumChannelDifference,
        double MeanChannelDifference, int UnmatchedPixels, int StableInteriorPixels, int ChangedInteriorPixels);
    private sealed record CaseResult(string Name, int Width, int Height, int PixelSize, Difference Difference,
        Difference ArtworkDifference, string CpuHash, string GpuHash, string ArtworkHash, string RgbaArtworkHash,
        long UploadBytes, int DrawCalls, long ResidentBytes);
    private sealed record Statistics(double Median, double P95, double Minimum, double Maximum,
        IReadOnlyList<double> Samples);
    private sealed record GardaFixture(MapTile[] Tiles, ImageryTile[] Imagery, ElevationSnapshot Elevation);

    internal static void Run(string[] arguments)
    {
        string output = Path.GetFullPath(arguments.FirstOrDefault(a => a.StartsWith("--gpu-output=", StringComparison.Ordinal))?
            ["--gpu-output=".Length..] ?? Path.Combine(Path.GetTempPath(), "PixelMap.GpuTerrain"));
        Directory.CreateDirectory(output);
        using var context = new HeadlessGlContext(1200, 800);
        Console.WriteLine(context.Description);
        var gl = context.Gl;
        RenderStyleChecks.RunGl(gl);
        GpuVisibilityChecks.Run(gl);
        var results = new List<CaseResult>();
        var failures = new List<string>();
        using var cpu = new PixelMapRenderer();
        using var scene = new PixelMapRenderer();
        using var renderer = new GlMapRenderer(gl);
        var plateau = new ElevationSnapshot([Dem(new(0, 0, 0), static (_, _) => 1500)]);
        var hills = new ElevationSnapshot([Dem(new(0, 0, 0), static (_, _) => 1500),
            Dem(Tile, static (u, v) => 1500 + 600 * Math.Sin(u * Math.PI) * Math.Sin(v * Math.PI) + 180 * u)]);
        var center = Geo(Tile, .5, .5);
        var camera = new MapCamera(center.Latitude, center.Longitude, 15.4, 23, 55);
        MapTile[] empty = [new(Tile, [])];
        MapTile[] models = [new(Tile, [
            Building(701, 90, 0, Box(1750, 1750, 2070, 2180), Box(1820, 1840, 1980, 2080).Reverse().ToArray()),
            Building(702, 125, 25, Box(2160, 1900, 2330, 2130)),
            Building(703, 48, 0, new TilePoint[] { new(1800, 2250), new(2000, 2350), new(1900, 2250), new(1800, 2350) })])];
        ImageryTile[] imagery = [Image(Tile), Image(new(0, 0, 0))];

        foreach (var (width, height, pixels) in new[] { (640, 420, 2), (391, 233, 3), (641, 421, 4) })
        foreach (var style in Enum.GetValues<PixelStyle>())
        foreach (var (pitch, perspective) in new[] { (0d, false), (55d, false), (58d, true), (72d, true) })
        {
            string name = $"models-{width}-{height}-p{pixels}-{style}-{pitch}-{perspective}";
            var options = new TerrainOptions(hills, Hypsometric: style == PixelStyle.Dusk);
            var photos = style == PixelStyle.Night ? imagery : null;
            var view = (camera with { Pitch = pitch, Perspective = perspective }).OnTerrain(options, height);
            Check(name, width, height, pixels, view, models, style, photos, options);
        }

        foreach (bool relief in new[] { false, true })
        foreach (bool tint in new[] { false, true })
        foreach (double exaggeration in new[] { 1d, 4d })
        {
            var options = new TerrainOptions(hills, relief, tint, exaggeration);
            var view = (camera with { Perspective = true }).OnTerrain(options, 420);
            Check($"surface-relief{relief}-tint{tint}-x{exaggeration}", 640, 420, 2,
                view, empty, PixelStyle.Day, null, options);
        }

        foreach (double pitch in new[] { 0d, 15d, 29d, 30d })
        {
            var options = new TerrainOptions(plateau);
            var view = (camera with { Pitch = pitch }).OnTerrain(options, 420);
            Check($"earth-overhead-blend-{pitch}", 640, 420, 2, view, models, PixelStyle.Day, imagery, options);
        }

        CheckPicking(scene, renderer, gl, camera, empty, new(hills));
        string recreatedHash = CheckRecreation(gl, camera.OnTerrain(new(plateau), 420), empty, new(plateau));
        CheckRetainedFrames(gl, renderer, camera, models, new(hills));
        CheckRetainedNavigation(camera, new(plateau));
        CheckHiDpi(gl, renderer, camera, empty, new(hills), output);
        var navigation = CheckNavigation(gl, renderer, output, failures);
        var sourceOrdering = CheckSourceOrdering(gl);
        var duplicateImagery = CheckDuplicateImagery(gl, renderer, camera, new(plateau));
        File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new
        {
            Context = context.Description,
            Comparison = "Exact color deltas are reported. Structural validation permits a one-art-pixel rasterization boundary and at most two byte levels per color channel. Both image directions are checked.",
            Cases = results,
            Navigation = navigation,
            SourceOrdering = sourceOrdering,
            DuplicateImagery = duplicateImagery,
            Failures = failures
        }, Json));
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        Console.WriteLine($"Passed {results.Count} native GLES terrain, model, tint, Earth, projection, style, resize and exact redraw checks. Reports {output}");
        renderer.Dispose();
        scene.Dispose();
        cpu.Dispose();
        if (arguments.Contains("--gpu-benchmark", StringComparer.Ordinal)) RunBenchmark(context, output);
        context.Dispose();
        using (var recreatedContext = new HeadlessGlContext(1200, 800))
        {
            Assert(CheckRecreation(recreatedContext.Gl, camera.OnTerrain(new(plateau), 420), empty, new(plateau)) == recreatedHash,
                "Destroying and recreating the EGL context changed terrain pixels.");
        }
        Console.WriteLine("Passed complete EGL context destruction and recreation with identical terrain pixels.");

        void Check(string name, int width, int height, int pixels, MapCamera view, IReadOnlyList<MapTile> tiles,
            PixelStyle style, IReadOnlyList<ImageryTile>? photos, TerrainOptions options)
        {
            using var expected = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(expected);
            cpu.Draw(canvas, width, height, view, tiles, style, pixels, photos, false, false, options);
            using var actual = Render(gl, scene, renderer, width, height, view, tiles, style, pixels, photos, options);
            var difference = Compare(expected, actual, pixels);
            string expectedHash = Hash(expected), actualHash = Hash(actual);
            long uploads = renderer.LastUploadBytes;
            int draws = renderer.LastDrawCalls;
            using var repeated = Render(gl, scene, renderer, width, height, view, tiles, style, pixels, photos, options);
            Assert(actual.GetPixelSpan().SequenceEqual(repeated.GetPixelSpan()), $"{name} changed on a retained GPU redraw.");
            canvas.Clear(SKColors.Magenta);
            scene.Draw(canvas, width, height, view, tiles, style, pixels, photos, false, false, options);
            Assert(Hash(expected) == expectedHash, $"{name} changed CPU pixels after GPU preparation.");
            using var artworkFrame = scene.PrepareGpuFrame(width, height, view, tiles, style, pixels, photos, options);
            using var artwork = CaptureArtwork(gl, scene, renderer, artworkFrame);
            using var rgbaArtwork = CaptureArtwork(gl, scene, renderer, artworkFrame, bottomUpRgba: true);
            Assert(artwork.GetPixelSpan().SequenceEqual(rgbaArtwork.GetPixelSpan()),
                $"{name} changed pixels when presenting an immutable bottom-up RGBA image.");
            var artworkDifference = Compare(expected, artwork, pixels);
            Assert(Compare(actual, artwork, pixels).MaximumChannelDifference <= 2,
                $"{name} changed geometry or photograph composition during art-sized capture.");
            results.Add(new(name, width, height, pixels, difference, artworkDifference, expectedHash, actualHash, Hash(artwork), Hash(rgbaArtwork),
                uploads, draws, renderer.ResidentBytes));
            Console.WriteLine($"GL {name} changed {difference.ChangedPixels}/{difference.Pixels}, " +
                $"unmatched {difference.UnmatchedPixels}, mean {difference.MeanChannelDifference:F3}, draws {draws}, upload {uploads}");
            if (difference.UnmatchedPixels > Math.Max(pixels * pixels * 2, difference.Pixels / 1000))
            {
                Save(expected, Path.Combine(output, name + "-cpu.png"));
                Save(actual, Path.Combine(output, name + "-gpu.png"));
                failures.Add($"{name} has {difference.UnmatchedPixels}/{difference.Pixels} structurally different pixels.");
            }
        }
    }

    private static SKBitmap Render(GL gl, PixelMapRenderer scene, GlMapRenderer renderer,
        int width, int height, MapCamera camera, IReadOnlyList<MapTile> tiles, PixelStyle style,
        int pixels, IReadOnlyList<ImageryTile>? imagery, TerrainOptions options)
    {
        gl.Viewport(0, 0, (uint)width, (uint)height);
        using var overlay = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var overlayCanvas = new SKCanvas(overlay);
        overlayCanvas.Clear(SKColors.Transparent);
        using var frame = scene.PrepareGpuFrame(width, height, camera, tiles, style, pixels, imagery, options);
        scene.DrawGpuOverlay(overlayCanvas, frame);
        renderer.Draw(frame, overlay);
        Assert(gl.GetInteger(GLEnum.DrawFramebufferBinding) == 0 && gl.GetInteger(GLEnum.ReadFramebufferBinding) == 0,
            "The map changed the caller's framebuffer bindings.");
        return ReadBitmap(gl, width, height);
    }

    private static SKBitmap ReadBitmap(GL gl, int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        gl.ReadPixels<byte>(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, rgba.AsSpan());
        CheckError(gl);
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var destination = bitmap.GetPixelSpan();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int source = ((height - 1 - y) * width + x) * 4, target = (y * width + x) * 4;
            destination[target] = rgba[source + 2];
            destination[target + 1] = rgba[source + 1];
            destination[target + 2] = rgba[source];
            destination[target + 3] = 255;
        }
        return bitmap;
    }

    private static SKBitmap CaptureArtwork(GL gl, PixelMapRenderer scene, GlMapRenderer renderer,
        GpuMapFrame frame, Action<SKCanvas>? drawOverlay = null, int scale = 1, bool bottomUpRgba = false)
    {
        int width = (frame.Width + frame.PixelSize - 1) / frame.PixelSize;
        int height = (frame.Height + frame.PixelSize - 1) / frame.PixelSize;
        gl.Viewport(0, 0, (uint)width, (uint)height);
        renderer.DrawArtwork(frame);
        byte[] readback = new byte[width * height * 4];
        gl.ReadPixels<byte>(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, readback.AsSpan());
        CheckError(gl);
        if (!bottomUpRgba) SwapRedBlue(readback);
        // Uno exposes its readback through IBuffer. Model the public DataReader copy
        // before the immutable, top-down Skia snapshot used by the foreground canvas.
        byte[] copy = new byte[readback.Length];
        readback.CopyTo(copy, 0);
        using var image = SnapshotArtwork(copy, width, height, bottomUpRgba);
        var result = new SKBitmap(frame.Width * scale, frame.Height * scale, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(result);
        canvas.Scale(scale);
        canvas.ClipRect(new SKRect(0, 0, frame.Width, frame.Height));
        canvas.Save();
        if (bottomUpRgba)
        {
            canvas.Translate(0, height * frame.PixelSize);
            canvas.Scale(1, -1);
        }
        canvas.DrawImage(image, new SKRect(0, 0, width * frame.PixelSize, height * frame.PixelSize), Nearest);
        canvas.Restore();
        scene.DrawGpuOverlay(canvas, frame);
        drawOverlay?.Invoke(canvas);
        return result;
    }

    private static void SwapRedBlue(Span<byte> pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }

    private static SKImage SnapshotArtwork(ReadOnlySpan<byte> pixels, int width, int height, bool bottomUpRgba = false)
    {
        using var bitmap = new SKBitmap(width, height,
            bottomUpRgba ? SKColorType.Rgba8888 : SKColorType.Bgra8888, SKAlphaType.Opaque);
        var destination = bitmap.GetPixelSpan();
        int rowBytes = width * 4;
        if (bottomUpRgba) pixels.CopyTo(destination);
        else
            for (int y = 0; y < height; y++)
                pixels.Slice((height - 1 - y) * rowBytes, rowBytes).CopyTo(destination.Slice(y * bitmap.RowBytes, rowBytes));
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    private static List<object> CheckNavigation(GL gl, GlMapRenderer renderer, string output, List<string> failures)
    {
        const int width = 680, height = 460;
        var center = Point(.5, .5);
        var start = Point(.34, .45);
        var finish = Point(.66, .55);
        var elevation = new ElevationSnapshot([Dem(new(0, 0, 0), static (_, _) => 1300),
            Dem(Tile, static (u, v) => 1300 + 350 * u + 200 * v + 140 * Math.Sin(u * 4 * Math.PI) * Math.Sin(v * 2 * Math.PI))]);
        MapTile[] tiles = [new(Tile, [])];
        var results = new List<object>();
        var cases = new[]
        {
            (Zoom: 14.3, Bearing: 21d, Pitch: 55d, Perspective: true, Pixels: 2, Style: PixelStyle.Day, Relief: true, Scale: 1d),
            (Zoom: 14.85, Bearing: 39d, Pitch: 64d, Perspective: true, Pixels: 3, Style: PixelStyle.Dusk, Relief: true, Scale: 2d),
            (Zoom: 14.6, Bearing: -32d, Pitch: 0d, Perspective: false, Pixels: 2, Style: PixelStyle.Night, Relief: false, Scale: 1d),
            (Zoom: 14.6, Bearing: 67d, Pitch: 67d, Perspective: true, Pixels: 4, Style: PixelStyle.Night, Relief: true, Scale: 4d)
        };
        foreach (var item in cases)
        {
            var terrain = new TerrainOptions(elevation, item.Relief, !item.Relief, item.Scale);
            var camera = new MapCamera(center.Latitude, center.Longitude, item.Zoom, item.Bearing, item.Pitch,
                item.Perspective).OnTerrain(terrain, height);
            using var cpu = new PixelMapRenderer();
            using var scene = new PixelMapRenderer();
            using var artworkScene = new PixelMapRenderer();
            foreach (var mode in Enum.GetValues<TravelMode>())
            {
                var plan = Plan(mode, [start, center, finish]);
                double clearance = mode == TravelMode.Flight ? 150 : 0;
                var pose = new VehiclePose(center, 18, AltitudeMeters: elevation.SampleSurfaceMeters(center.Latitude, center.Longitude) + clearance,
                    Moving: false, SpeedMetersPerSecond: 30);
                string name = $"navigation-{mode}-{item.Style}-p{item.Pixels}-x{item.Scale}";
                string? paused = null;
                string? pausedArtwork = null;
                int changedOverlay = 0, maxCompositeError = 0, unmatched = 0;
                int artworkUnmatched = 0, artworkMaximum = 0;
                for (int step = 0; step < 7; step++)
                {
                    var point = center.Move(90, Math.Min(step, 3) * 4);
                    var sample = pose with
                    {
                        Point = point,
                        AltitudeMeters = elevation.SampleSurfaceMeters(point.Latitude, point.Longitude) + clearance,
                        AnimationSeconds = step == 5 ? 16 : Math.Min(step, 3) * .3,
                        Moving = step is > 0 and < 4,
                        Bearing = step == 0 ? 18 : 113,
                        Continuity = step == 6 ? 1 : 0
                    };
                    using var expected = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    using var canvas = new SKCanvas(expected);
                    cpu.Draw(canvas, width, height, camera, tiles, item.Style, item.Pixels, null, false, false, terrain);
                    using var cpuOverlay = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var cpuOverlayCanvas = new SKCanvas(cpuOverlay);
                    cpuOverlayCanvas.Clear(SKColors.Transparent);
                    DrawJourney(cpu, cpuOverlayCanvas);
                    canvas.DrawBitmap(cpuOverlay, 0, 0);
                    using var frame = scene.PrepareGpuFrame(width, height, camera, tiles, item.Style, item.Pixels, null, terrain);
                    using var gpuOverlay = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var gpuOverlayCanvas = new SKCanvas(gpuOverlay);
                    gpuOverlayCanvas.Clear(SKColors.Transparent);
                    scene.DrawGpuOverlay(gpuOverlayCanvas, frame);
                    DrawJourney(scene, gpuOverlayCanvas);
                    int overlayDifference = ChangedPixels(cpuOverlay, gpuOverlay);
                    changedOverlay += overlayDifference;
                    if (overlayDifference > 0)
                    {
                        Save(cpuOverlay, Path.Combine(output, name + $"-{step}-cpu-overlay.png"));
                        Save(gpuOverlay, Path.Combine(output, name + $"-{step}-gpu-overlay.png"));
                    }
                    if (step == 4) paused = Hash(gpuOverlay);
                    if (step == 5 && paused != Hash(gpuOverlay)) failures.Add(name + " advanced GPU overlay effects while paused.");
                    gl.Viewport(0, 0, width, height);
                    renderer.Draw(frame, gpuOverlay);
                    using var actual = ReadBitmap(gl, width, height);
                    var difference = Compare(expected, actual, item.Pixels);
                    maxCompositeError = Math.Max(maxCompositeError, difference.MaximumChannelDifference);
                    unmatched += difference.UnmatchedPixels;
                    if (difference.UnmatchedPixels > width * height / 1000)
                    {
                        Save(expected, Path.Combine(output, name + $"-{step}-cpu.png"));
                        Save(actual, Path.Combine(output, name + $"-{step}-gpu.png"));
                    }
                    using var artworkFrame = artworkScene.PrepareGpuFrame(width, height, camera, tiles,
                        item.Style, item.Pixels, null, terrain);
                    using var artwork = CaptureArtwork(gl, artworkScene, renderer, artworkFrame,
                        target => DrawJourney(artworkScene, target));
                    using var rgbaArtwork = CaptureArtwork(gl, artworkScene, renderer, artworkFrame,
                        target => DrawJourney(artworkScene, target), bottomUpRgba: true);
                    Assert(artwork.GetPixelSpan().SequenceEqual(rgbaArtwork.GetPixelSpan()),
                        $"{name} changed journey overlays when presenting bottom-up RGBA artwork.");
                    var artworkDifference = Compare(expected, artwork, item.Pixels);
                    artworkUnmatched += artworkDifference.UnmatchedPixels;
                    artworkMaximum = Math.Max(artworkMaximum, artworkDifference.MaximumChannelDifference);
                    if (step == 4) pausedArtwork = Hash(artwork);
                    if (step == 5 && pausedArtwork != Hash(artwork)) failures.Add(name + " advanced captured artwork effects while paused.");
                    if (artworkDifference.UnmatchedPixels > width * height / 1000)
                    {
                        Save(expected, Path.Combine(output, name + $"-{step}-artwork-cpu.png"));
                        Save(artwork, Path.Combine(output, name + $"-{step}-artwork-gpu.png"));
                    }

                    void DrawJourney(PixelMapRenderer map, SKCanvas target)
                    {
                        map.DrawGate(target, camera, width, height, new(center, 45, 0), mode, pose.AltitudeMeters, .25);
                        map.DrawNavigation(target, width, height, camera, plan, sample, item.Pixels);
                    }
                }
                results.Add(new { Name = name, Frames = 7, ChangedOverlayPixels = changedOverlay,
                    MaximumCompositeChannelDifference = maxCompositeError, UnmatchedCompositePixels = unmatched,
                    ArtworkMaximumChannelDifference = artworkMaximum, ArtworkUnmatchedPixels = artworkUnmatched });
                if (changedOverlay > 0) failures.Add($"{name} changed {changedOverlay} CPU/GPU overlay pixels across seven effect frames.");
                if (unmatched > width * height * 7 / 1000) failures.Add($"{name} changed {unmatched} composite pixels away from raster boundaries.");
                if (artworkUnmatched > width * height * 7 / 1000) failures.Add($"{name} changed {artworkUnmatched} captured artwork pixels away from raster boundaries.");
                Console.WriteLine($"GL {name} overlay differences {changedOverlay}, composite unmatched {unmatched}, " +
                    $"artwork unmatched {artworkUnmatched}, maximum delta {Math.Max(maxCompositeError, artworkMaximum)}");
            }
        }

        var ridge = new ElevationSnapshot([Dem(new(0, 0, 0), static (_, _) => 0),
            Dem(Tile, static (_, v) => 900 * Math.Exp(-Math.Pow((v - .63) / .07, 2)))]);
        var ridgeOptions = new TerrainOptions(ridge);
        var ridgeCamera = new MapCamera(center.Latitude, center.Longitude, 13.8, 0, 55, true).OnTerrain(ridgeOptions, height);
        var hidden = Point(.5, .25);
        using (var cpu = new PixelMapRenderer())
        using (var scene = new PixelMapRenderer())
        using (var map = new SKBitmap(width, height))
        using (var mapCanvas = new SKCanvas(map))
        {
            cpu.Draw(mapCanvas, width, height, ridgeCamera, tiles, PixelStyle.Day, 2, null, false, false, ridgeOptions);
            using var frame = scene.PrepareGpuFrame(width, height, ridgeCamera, tiles, PixelStyle.Day, 2, null, ridgeOptions);
            foreach (var mode in Enum.GetValues<TravelMode>())
            {
                using var a = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var b = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var ca = new SKCanvas(a);
                using var cb = new SKCanvas(b);
                ca.Clear(SKColors.Transparent);
                cb.Clear(SKColors.Transparent);
                double altitude = ridge.SampleSurfaceMeters(hidden.Latitude, hidden.Longitude) + (mode == TravelMode.Flight ? 150 : 0);
                cpu.DrawGate(ca, ridgeCamera, width, height, new(hidden, 45, 0), mode, altitude, .25);
                scene.DrawGpuOverlay(cb, frame);
                scene.DrawGate(cb, ridgeCamera, width, height, new(hidden, 45, 0), mode, altitude, .25);
                Assert(a.Pixels.All(pixel => pixel.Alpha == 0), "The reference ridge gate is not fully occluded.");
                Assert(a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()), $"The {mode} GPU gate shows through the foreground ridge.");
                var plan = Plan(mode, [Point(.4, .25), hidden, Point(.6, .25)]);
                var pose = new VehiclePose(hidden, 45, AltitudeMeters: altitude, Moving: false);
                cpu.DrawNavigation(ca, width, height, ridgeCamera, plan, pose, 2);
                scene.DrawNavigation(cb, width, height, ridgeCamera, plan, pose, 2);
                Assert(a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()), $"The {mode} GPU route or vehicle changed ridge occlusion.");
                gl.Viewport(0, 0, width, height);
                renderer.Draw(frame, b);
                using var actual = ReadBitmap(gl, width, height);
                CheckError(gl);
            }
        }
        Console.WriteLine("Passed six GPU ridge-occluded gate, route and vehicle overlays through full-resolution GL composition.");
        return results;

        static RoutePoint Point(double u, double v)
        {
            var point = Geo(Tile, u, v);
            return new(point.Latitude, point.Longitude);
        }
    }

    private static RoutePlan Plan(TravelMode mode, RoutePoint[] geometry)
    {
        var start = new RouteStop("Start", geometry[0]);
        var finish = new RouteStop("Finish", geometry[^1]);
        double distance = 0;
        for (int i = 1; i < geometry.Length; i++) distance += geometry[i - 1].DistanceTo(geometry[i]);
        return new(mode, [start, finish], [new(start, finish, geometry, distance, TimeSpan.FromMinutes(5), [])], "GPU terrain fixture", false);
    }

    private static void CheckPicking(PixelMapRenderer scene, GlMapRenderer renderer, GL gl,
        MapCamera camera, IReadOnlyList<MapTile> tiles, TerrainOptions options)
    {
        using var cpu = new PixelMapRenderer();
        using var expected = new SKBitmap(640, 420);
        using var canvas = new SKCanvas(expected);
        int hits = 0;
        foreach (bool perspective in new[] { false, true })
        {
            var view = (camera with { Perspective = perspective }).OnTerrain(options, 420);
            cpu.Draw(canvas, 640, 420, view, tiles, PixelStyle.Day, 2, null, false, false, options);
            using var actual = Render(gl, scene, renderer, 640, 420, view, tiles, PixelStyle.Day, 2, null, options);
            for (int y = 80; y < 400; y += 29)
            for (int x = 80; x < 600; x += 31)
            {
                bool cpuHit = cpu.TryPickTerrain(x, y, 640, 420, view, out var a);
                bool gpuHit = scene.TryPickTerrain(x, y, 640, 420, view, out var b);
                Assert(cpuHit == gpuHit, "GPU terrain picking changed the visible mesh coverage.");
                if (!cpuHit) continue;
                Assert(Math.Abs(a.ElevationMeters - b.ElevationMeters) < .2 &&
                    Math.Abs(a.Latitude - b.Latitude) + Math.Abs(a.Longitude - b.Longitude) < .000002,
                    "GPU terrain picking changed the retained mesh intersection.");
                hits++;
            }
            Assert(!scene.TryPickTerrain(320, 250, 640, 420, view with { Bearing = view.Bearing + 1 }, out _),
                "GPU terrain picking accepted a stale camera.");
        }
        Assert(hits > 100, "The GPU terrain picking fixtures did not exercise enough visible mesh samples.");
        Console.WriteLine($"Passed {hits} native GPU/CPU mesh intersection checks and stale-camera rejection.");
    }

    private static string CheckRecreation(GL gl, MapCamera camera, IReadOnlyList<MapTile> tiles, TerrainOptions options)
    {
        string? first = null;
        for (int generation = 0; generation < 3; generation++)
        {
            using var scene = new PixelMapRenderer();
            using var renderer = new GlMapRenderer(gl);
            using var frame = Render(gl, scene, renderer, 640, 420, camera, tiles, PixelStyle.Day, 2, null, options);
            string hash = Hash(frame);
            Assert(first is null || first == hash, "Recreating GL resources changed terrain pixels.");
            first = hash;
        }
        CheckError(gl);
        return first!;
    }

    private static void CheckRetainedFrames(GL gl, GlMapRenderer renderer, MapCamera camera,
        IReadOnlyList<MapTile> tiles, TerrainOptions options)
    {
        const int width = 640, height = 420;
        using var scene = new PixelMapRenderer();
        using var overlay = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(overlay);
        var view = camera.OnTerrain(options, height);
        using var first = scene.PrepareGpuFrame(width, height, view, tiles, PixelStyle.Day, 2, null, options);
        string expected = Draw(first);
        using var second = scene.PrepareGpuFrame(width, height, view with { Bearing = view.Bearing + 31 },
            tiles, PixelStyle.Night, 3, null, options with { Exaggeration = 4 });
        Assert(Draw(second) != expected, "The retained-frame lifetime fixture did not change its camera and style.");
        scene.ActivateGpuFrame(first);
        Assert(scene.TryPickTerrain(width / 2, height / 2, width, height, view, out _),
            "Activating a retained GPU frame did not restore picking before overlay drawing.");
        Assert(Draw(first) == expected, "Preparing another scene mutated a retained GPU frame.");
        Assert(scene.TryPickTerrain(width / 2, height / 2, width, height, view, out _),
            "Reactivating a retained GPU frame did not restore its terrain picking.");
        using var repeated = scene.PrepareGpuFrame(width, height, view, tiles, PixelStyle.Day, 2, null, options);
        Assert(Draw(repeated) == expected, "Preparing the reactivated camera reused another frame's geometry.");
        repeated.Dispose();
        try
        {
            scene.ActivateGpuFrame(repeated);
            throw new InvalidOperationException("A disposed GPU frame could be reactivated.");
        }
        catch (ObjectDisposedException) { }
        Console.WriteLine("Passed retained GPU frame mutation, reactivation, picking and cache identity checks.");

        string Draw(GpuMapFrame frame)
        {
            canvas.Clear(SKColors.Transparent);
            scene.DrawGpuOverlay(canvas, frame);
            gl.Viewport(0, 0, width, height);
            renderer.Draw(frame, overlay);
            using var bitmap = ReadBitmap(gl, width, height);
            return Hash(bitmap);
        }
    }

    private static void CheckHiDpi(GL gl, GlMapRenderer renderer, MapCamera camera,
        IReadOnlyList<MapTile> tiles, TerrainOptions options, string output)
    {
        const int width = 391, height = 233, scale = 2;
        const int physicalWidth = width * scale, physicalHeight = height * scale;
        using var cpu = new PixelMapRenderer();
        using var scene = new PixelMapRenderer();
        using var logical = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var logicalCanvas = new SKCanvas(logical);
        var view = camera.OnTerrain(options, height);
        cpu.Draw(logicalCanvas, width, height, view, tiles, PixelStyle.Day, 3, null, false, false, options);
        using var expected = new SKBitmap(physicalWidth, physicalHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(expected);
        using var mapImage = SKImage.FromBitmap(logical);
        canvas.DrawImage(mapImage, new SKRect(0, 0, physicalWidth, physicalHeight),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        using var overlay = new SKBitmap(physicalWidth, physicalHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var overlayCanvas = new SKCanvas(overlay);
        overlayCanvas.Clear(SKColors.Transparent);
        overlayCanvas.Scale(scale);
        using var frame = scene.PrepareGpuFrame(width, height, view, tiles, PixelStyle.Day, 3, null, options);
        scene.DrawGpuOverlay(overlayCanvas, frame);
        using var paint = new SKPaint { Color = new SKColor(18, 37, 58, 170), IsAntialias = false };
        overlayCanvas.DrawRect(11, 7, 250, 34, paint);
        paint.Color = new SKColor(255, 218, 125);
        paint.IsAntialias = true;
        using var font = new SKFont(SKTypeface.Default, 15);
        overlayCanvas.DrawText("GPU terrain · 2× display", 17, 30, font, paint);
        paint.IsAntialias = false;
        paint.Color = SKColors.Red;
        overlayCanvas.DrawRect(0, 0, 3, 4, paint);
        paint.Color = SKColors.Cyan;
        overlayCanvas.DrawRect(width - 5, height - 3, 5, 3, paint);
        canvas.DrawBitmap(overlay, 0, 0);

        uint framebuffer = gl.GenFramebuffer();
        uint color = gl.GenRenderbuffer();
        try
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, color);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, physicalWidth, physicalHeight);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                RenderbufferTarget.Renderbuffer, color);
            Assert(gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == GLEnum.FramebufferComplete,
                "The high-DPI test target is incomplete.");
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            gl.Viewport(0, 0, physicalWidth, physicalHeight);
            renderer.Draw(frame, overlay);
            Assert(gl.GetInteger(GLEnum.DrawFramebufferBinding) == framebuffer &&
                gl.GetInteger(GLEnum.ReadFramebufferBinding) == 0,
                "High-DPI presentation changed the caller's independent framebuffer bindings.");
            Span<int> viewport = stackalloc int[4];
            gl.GetInteger(GLEnum.Viewport, viewport);
            Assert(viewport.SequenceEqual([0, 0, physicalWidth, physicalHeight]), "High-DPI presentation changed the caller's viewport.");
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
            using var actual = ReadBitmap(gl, physicalWidth, physicalHeight);
            var difference = Compare(expected, actual, 3 * scale);
            if (difference.MaximumChannelDifference > 2)
            {
                Save(expected, Path.Combine(output, "hidpi-cpu.png"));
                Save(actual, Path.Combine(output, "hidpi-gpu.png"));
            }
            Assert(difference.MaximumChannelDifference <= 2,
                $"High-DPI geometry or physical-resolution overlay changed by {difference.MaximumChannelDifference} byte levels.");
            Action<SKCanvas> drawOverlay = target =>
            {
                target.Save();
                target.ResetMatrix();
                target.DrawBitmap(overlay, 0, 0);
                target.Restore();
            };
            using var artwork = CaptureArtwork(gl, scene, renderer, frame, drawOverlay, scale);
            using var rgbaArtwork = CaptureArtwork(gl, scene, renderer, frame, drawOverlay, scale, bottomUpRgba: true);
            Assert(artwork.GetPixelSpan().SequenceEqual(rgbaArtwork.GetPixelSpan()),
                "Bottom-up RGBA presentation changed high-DPI artwork, clipping or full-resolution overlays.");
            var artworkDifference = Compare(expected, artwork, 3 * scale);
            Assert(artworkDifference.MaximumChannelDifference <= 2,
                $"Art-sized capture changed high-DPI pixels by {artworkDifference.MaximumChannelDifference} byte levels.");
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteRenderbuffer(color);
        }
        CheckError(gl);
        Console.WriteLine("Passed 2× destination framebuffer, nearest art pixels, physical-resolution text and independent read/draw target restoration.");
    }

    private static void CheckRetainedNavigation(MapCamera camera, TerrainOptions options)
    {
        const int width = 680, height = 460, pixels = 2;
        var center = Geo(Tile, .5, .5);
        var start = Geo(Tile, .34, .45);
        var finish = Geo(Tile, .66, .55);
        var plan = Plan(TravelMode.Drive,
            [new(start.Latitude, start.Longitude), new(center.Latitude, center.Longitude), new(finish.Latitude, finish.Longitude)]);
        var vehicle = new VehiclePose(plan.Geometry[1], 18, AltitudeMeters: 1500, Moving: false);
        var view = (camera with { Zoom = 14.6, Perspective = true }).OnTerrain(options, height);
        MapTile[] clear = [new(Tile, [])];
        MapTile[] blocked = [new(Tile, [Building(704, 180, 0, Box(1700, 1700, 2396, 2396))])];
        using var scene = new PixelMapRenderer();
        using var first = scene.PrepareGpuFrame(width, height, view, clear, PixelStyle.Day, pixels, null, options);
        using var second = scene.PrepareGpuFrame(width, height, view, blocked, PixelStyle.Day, pixels, null, options);
        // Camera and terrain options stay identical while the displayed geometry trails
        // the newly prepared tiles. Each retained frame needs its own route depth key.
        using var a = Overlay(scene, first);
        using var b = Overlay(scene, second);
        using var fresh = new PixelMapRenderer();
        using var referenceFrame = fresh.PrepareGpuFrame(width, height, view, blocked, PixelStyle.Day, pixels, null, options);
        using var reference = Overlay(fresh, referenceFrame);
        Assert(ChangedPixels(a, reference) > 100, "The stationary route-cache fixture did not occlude its route and vehicle.");
        Assert(b.GetPixelSpan().SequenceEqual(reference.GetPixelSpan()),
            "Publishing a prepared blocker reused route occlusion from the previously displayed clear frame.");
        using var restored = Overlay(scene, first);
        Assert(restored.GetPixelSpan().SequenceEqual(a.GetPixelSpan()),
            "Reactivating the earlier stationary frame retained a later building's route occlusion.");
        Console.WriteLine("Passed stationary A/B/A route-cache invalidation with delayed building publication.");

        SKBitmap Overlay(PixelMapRenderer map, GpuMapFrame frame)
        {
            var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            map.ActivateGpuFrame(frame);
            map.DrawNavigation(canvas, width, height, view, plan, vehicle, pixels);
            return bitmap;
        }
    }

    private static Difference Compare(SKBitmap expected, SKBitmap actual, int pixels)
    {
        var a = expected.GetPixelSpan();
        var b = actual.GetPixelSpan();
        int changed = 0, maximum = 0, unmatched = 0, stable = 0, changedStable = 0;
        long total = 0;
        for (int y = 0; y < expected.Height; y++)
        for (int x = 0; x < expected.Width; x++)
        {
            int offset = (y * expected.Width + x) * 4, delta = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                int difference = Math.Abs(a[offset + channel] - b[offset + channel]);
                total += difference;
                delta = Math.Max(delta, difference);
            }
            if (delta > 0) changed++;
            maximum = Math.Max(maximum, delta);
            bool interior = MatchesNeighborhood(a, a, x, y, expected.Width, expected.Height, pixels, requireEvery: true);
            if (interior) { stable++; if (delta > 2) changedStable++; }
            if (delta > 2 && (!MatchesNeighborhood(a, b, x, y, expected.Width, expected.Height, pixels, false) ||
                !MatchesNeighborhood(b, a, x, y, expected.Width, expected.Height, pixels, false))) unmatched++;
        }
        return new(expected.Width * expected.Height, changed, maximum,
            total / (expected.Width * expected.Height * 3d), unmatched, stable, changedStable);
    }

    private static int ChangedPixels(SKBitmap expected, SKBitmap actual)
    {
        var a = expected.GetPixelSpan();
        var b = actual.GetPixelSpan();
        int changed = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (!a.Slice(i, 4).SequenceEqual(b.Slice(i, 4))) changed++;
        return changed;
    }

    private static bool MatchesNeighborhood(ReadOnlySpan<byte> source, ReadOnlySpan<byte> target,
        int x, int y, int width, int height, int pixels, bool requireEvery)
    {
        int reference = (y * width + x) * 4;
        for (int dy = -pixels; dy <= pixels; dy += pixels)
        for (int dx = -pixels; dx <= pixels; dx += pixels)
        {
            int sample = (Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1)) * 4;
            bool match = Math.Abs(source[reference] - target[sample]) <= 2 &&
                Math.Abs(source[reference + 1] - target[sample + 1]) <= 2 &&
                Math.Abs(source[reference + 2] - target[sample + 2]) <= 2;
            if (requireEvery && !match) return false;
            if (!requireEvery && match) return true;
        }
        return requireEvery;
    }

    private static GardaFixture LoadGarda()
    {
        using var client = new HttpClient(new OfflineHandler());
        var vectors = new VectorTileSource(client);
        var photos = new EarthImagerySource(client);
        var dem = new ElevationTileSource(client);
        List<MapTile> tiles = [vectors.GetTileAsync(new(0, 0, 0)).GetAwaiter().GetResult()];
        List<ImageryTile> imagery = [photos.GetTileAsync(new(0, 0, 0)).GetAwaiter().GetResult()];
        List<ElevationTile> elevation = [dem.GetTileAsync(new(0, 0, 0)).GetAwaiter().GetResult()];
        for (int y = 1458; y <= 1460; y++)
        for (int x = 2170; x <= 2172; x++)
        {
            var id = new TileId(12, x, y);
            tiles.Add(vectors.GetTileAsync(id).GetAwaiter().GetResult());
            imagery.Add(photos.GetTileAsync(id).GetAwaiter().GetResult());
            elevation.Add(dem.GetTileAsync(id).GetAwaiter().GetResult());
        }
        return new(tiles.ToArray(), imagery.ToArray(), new ElevationSnapshot(elevation));
    }

    private static List<object> CheckSourceOrdering(GL gl)
    {
        const int width = 1100, height = 650, pixels = 4;
        const int textureBytes = 256 * 256 * 4;
        var fixture = LoadGarda();
        var reports = new List<object>();
        foreach (string layer in new[] { "Map", "Earth", "Elevation" })
        {
            using var scene = new PixelMapRenderer();
            using var engine = new GlMapRenderer(gl);
            var options = new TerrainOptions(fixture.Elevation, Hypsometric: layer == "Elevation");
            var camera = new MapCamera(45.875, 10.85, 12.3, -25, 58, true).OnTerrain(options, height);
            var images = layer == "Earth" ? fixture.Imagery : null;
            var initial = Draw(fixture.Tiles, images);
            var warm = Draw(fixture.Tiles, images);
            Assert(warm.Hash == initial.Hash && warm.UploadBytes < textureBytes,
                $"{layer} did not retain its warm terrain resources.");
            var orderings = new HashSet<string>();
            var reorderedUploads = new List<long>();
            for (int step = 0; step < 16; step++)
            {
                // TileViewport publishes camera-distance priority order. Change that
                // priority independently of the view to isolate identical-source reuse.
                double angle = step * Math.PI / 8;
                double focusX = (camera.Longitude + 180) / 360 + .0003 * Math.Sin(angle);
                double focusY = (1 - Math.Asinh(Math.Tan(camera.Latitude * Math.PI / 180)) / Math.PI) * .5
                    + .0003 * Math.Cos(angle);
                var tiles = fixture.Tiles.OrderBy(tile => Distance(tile.Id)).ToArray();
                var photos = images?.OrderBy(tile => Distance(tile.Id)).ToArray();
                orderings.Add(string.Join('|', tiles.Select(tile => tile.Id)));
                var result = Draw(tiles, photos);
                Assert(result.Hash == initial.Hash, $"{layer} terrain pixels changed after source-priority reordering.");
                Assert(result.UploadBytes == warm.UploadBytes,
                    $"{layer} uploaded {result.UploadBytes} bytes after an order-only source change, expected {warm.UploadBytes}.");
                reorderedUploads.Add(result.UploadBytes);

                double Distance(TileId id)
                {
                    double count = 1 << id.Z;
                    double dx = (id.X + .5) / count - focusX;
                    dx -= Math.Floor(dx + .5);
                    double dy = (id.Y + .5) / count - focusY;
                    return dx * dx + dy * dy;
                }
            }
            Assert(orderings.Count > 1, "The source-priority fixture did not change tile ordering.");
            var replacementTiles = fixture.Tiles.ToArray();
            replacementTiles[1] = new(replacementTiles[1].Id, replacementTiles[1].Features);
            var replacement = Draw(replacementTiles, images);
            Assert(replacement.Hash == initial.Hash && replacement.UploadBytes >= textureBytes,
                $"{layer} failed to invalidate a replacement vector source with equal contents.");
            long? replacementImageUploads = null;
            if (images is not null)
            {
                var replacementImages = images.ToArray();
                replacementImages[1] = new(replacementImages[1].Id, replacementImages[1].Encoded);
                var replacementImage = Draw(replacementTiles, replacementImages);
                Assert(replacementImage.Hash == initial.Hash && replacementImage.UploadBytes >= textureBytes,
                    "Earth failed to invalidate a replacement photograph with equal contents.");
                replacementImageUploads = replacementImage.UploadBytes;
            }
            var removed = Draw(fixture.Tiles.Skip(1).ToArray(), images);
            Assert(removed.UploadBytes >= textureBytes, $"{layer} failed to invalidate removed source membership.");
            reports.Add(new { Layer = layer, Permutations = reorderedUploads.Count, DistinctOrderings = orderings.Count,
                WarmUploadBytes = warm.UploadBytes, ReorderedUploadBytes = reorderedUploads,
                ReplacementVectorUploadBytes = replacement.UploadBytes,
                ReplacementImageryUploadBytes = replacementImageUploads, RemovedSourceUploadBytes = removed.UploadBytes });
            Console.WriteLine($"GL source ordering {layer}, {orderings.Count} priority orders, " +
                $"warm/reordered uploads {warm.UploadBytes}/{reorderedUploads.Max()} bytes, exact terrain pixels.");

            (string Hash, long UploadBytes) Draw(IReadOnlyList<MapTile> tiles, IReadOnlyList<ImageryTile>? photos)
            {
                using var frame = scene.PrepareGpuFrame(width, height, camera, tiles, PixelStyle.Day, pixels, photos, options);
                int artWidth = (width + pixels - 1) / pixels, artHeight = (height + pixels - 1) / pixels;
                gl.Viewport(0, 0, (uint)artWidth, (uint)artHeight);
                engine.DrawArtwork(frame);
                using var bitmap = ReadBitmap(gl, artWidth, artHeight);
                return (Hash(bitmap), engine.LastUploadBytes);
            }
        }
        return reports;
    }

    private static object CheckDuplicateImagery(GL gl, GlMapRenderer renderer, MapCamera camera, TerrainOptions options)
    {
        const int width = 640, height = 420, pixels = 3;
        const int textureBytes = 256 * 256 * 4;
        using var scene = new PixelMapRenderer();
        using var fresh = new PixelMapRenderer();
        MapTile[] tiles = [new(Tile, [])];
        var view = (camera with { Pitch = 0, Perspective = false }).OnTerrain(options, height);
        var firstSource = SolidImage(new SKColor(197, 177, 124));
        var secondSource = SolidImage(new SKColor(20, 69, 103));
        using var original = Draw(scene, [firstSource, secondSource]);
        using var repeated = Draw(scene, [firstSource, secondSource]);
        long repeatedUploads = renderer.LastUploadBytes;
        Assert(original.GetPixelSpan().SequenceEqual(repeated.GetPixelSpan()) && repeatedUploads < textureBytes,
            "An unchanged duplicate-imagery sequence lost its retained artwork.");
        using var reversed = Draw(scene, [secondSource, firstSource]);
        long reversedUploads = renderer.LastUploadBytes;
        using var freshReversed = Draw(fresh, [secondSource, firstSource]);
        using var firstOnly = Draw(fresh, [firstSource]);
        using var secondOnly = Draw(fresh, [secondSource]);
        int changed = ChangedPixels(original, reversed);
        Assert(changed > width * height / 10, "The duplicate-imagery fixture did not visibly change its last source.");
        Assert(reversedUploads >= textureBytes, "Reordering distinct same-TileId photographs reused stale artwork.");
        Assert(reversed.GetPixelSpan().SequenceEqual(freshReversed.GetPixelSpan()) &&
            reversed.GetPixelSpan().SequenceEqual(firstOnly.GetPixelSpan()) &&
            original.GetPixelSpan().SequenceEqual(secondOnly.GetPixelSpan()),
            "Duplicate-imagery ordering no longer renders the last source or differs from a fresh renderer.");
        Console.WriteLine($"GL duplicate imagery preserves last-source order, changed {changed} pixels, " +
            $"retained/reordered uploads {repeatedUploads}/{reversedUploads} bytes.");
        return new { OriginalHash = Hash(original), ReversedHash = Hash(reversed), FreshReversedHash = Hash(freshReversed),
            ChangedPixels = changed, RetainedUploadBytes = repeatedUploads, ReorderedUploadBytes = reversedUploads };

        SKBitmap Draw(PixelMapRenderer map, IReadOnlyList<ImageryTile> imagery)
        {
            using var frame = map.PrepareGpuFrame(width, height, view, tiles, PixelStyle.Day, pixels, imagery, options);
            return CaptureArtwork(gl, map, renderer, frame, bottomUpRgba: true);
        }

        ImageryTile SolidImage(SKColor color)
        {
            using var bitmap = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque);
            bitmap.Erase(color);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return new(Tile, png.ToArray());
        }
    }

    private static void RunBenchmark(HeadlessGlContext context, string output)
    {
        const int width = 1100, height = 650, samples = 32;
        var fixture = LoadGarda();
        var tiles = fixture.Tiles;
        var imagery = fixture.Imagery;
        var snapshot = fixture.Elevation;
        string rendererHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(PixelMapRenderer).Assembly.Location)));
        string harnessHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(GlTerrainChecks).Assembly.Location)));
        string dataHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(ElevationTile).Assembly.Location)));
        var report = new List<object>();
        foreach (int pixels in new[] { 2, 3, 4 })
        foreach (string layer in new[] { "Map", "Earth", "Elevation" })
        {
            var options = new TerrainOptions(snapshot, Hypsometric: layer == "Elevation");
            var images = layer == "Earth" ? imagery : null;
            using var cpu = new PixelMapRenderer();
            using var scene = new PixelMapRenderer();
            using var renderer = new GlMapRenderer(context.Gl);
            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(bitmap);
            using var overlay = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var overlayCanvas = new SKCanvas(overlay);
            byte[] bytes = new byte[width * height * 4];
            var camera = new MapCamera(45.875, 10.85, 12.3, -25, 58, true).OnTerrain(options, height);
            var views = Enumerable.Range(0, 16).Select(i => camera with
            {
                Bearing = camera.Bearing + 12 * Math.Sin(i * Math.PI / 8),
                Latitude = camera.Latitude + .001 * Math.Sin(i * Math.PI / 8),
                Longitude = camera.Longitude + .002 * (Math.Cos(i * Math.PI / 8) - 1)
            }).ToArray();
            var cpuMs = new List<double>();
            var prepareMs = new List<double>();
            var submitMs = new List<double>();
            var readbackMs = new List<double>();
            var totalMs = new List<double>();
            var uploadBytes = new List<long>();
            for (int i = -16; i < samples; i++)
            {
                var view = views[(i + 16) % views.Length];
                long start = Stopwatch.GetTimestamp();
                cpu.Draw(canvas, width, height, view, tiles, PixelStyle.Day, pixels, images, false, false, options);
                if (i >= 0) cpuMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            context.Gl.Viewport(0, 0, width, height);
            for (int i = -16; i < samples; i++)
            {
                var view = views[(i + 16) % views.Length];
                long start = Stopwatch.GetTimestamp();
                using var frame = scene.PrepareGpuFrame(width, height, view, tiles, PixelStyle.Day, pixels, images, options);
                overlayCanvas.Clear(SKColors.Transparent);
                scene.DrawGpuOverlay(overlayCanvas, frame);
                double prepare = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                renderer.Draw(frame, overlay);
                long readStart = Stopwatch.GetTimestamp();
                context.Gl.ReadPixels<byte>(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, bytes.AsSpan());
                double readback = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                double complete = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (i < 0) continue;
                prepareMs.Add(prepare);
                submitMs.Add(renderer.LastSubmissionMilliseconds);
                readbackMs.Add(readback);
                totalMs.Add(complete);
                uploadBytes.Add(renderer.LastUploadBytes);
            }
            CheckError(context.Gl);
            int artWidth = (width + pixels - 1) / pixels, artHeight = (height + pixels - 1) / pixels;
            byte[] artReadback = new byte[artWidth * artHeight * 4];
            byte[] artCopy = new byte[artReadback.Length];
            var artPrepareMs = new List<double>();
            var artSubmitMs = new List<double>();
            var artReadbackMs = new List<double>();
            var artCopyMs = new List<double>();
            var artSnapshotMs = new List<double>();
            var artPresentMs = new List<double>();
            var artTotalMs = new List<double>();
            var artUploadBytes = new List<long>();
            context.Gl.Viewport(0, 0, (uint)artWidth, (uint)artHeight);
            for (int i = -16; i < samples; i++)
            {
                var view = views[(i + 16) % views.Length];
                long start = Stopwatch.GetTimestamp();
                using var frame = scene.PrepareGpuFrame(width, height, view, tiles, PixelStyle.Day, pixels, images, options);
                double prepare = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                renderer.DrawArtwork(frame);
                long readStart = Stopwatch.GetTimestamp();
                context.Gl.ReadPixels<byte>(0, 0, (uint)artWidth, (uint)artHeight,
                    PixelFormat.Rgba, PixelType.UnsignedByte, artReadback.AsSpan());
                double readback = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                long copyStart = Stopwatch.GetTimestamp();
                SwapRedBlue(artReadback);
                artReadback.CopyTo(artCopy, 0);
                double copy = Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
                long snapshotStart = Stopwatch.GetTimestamp();
                using var image = SnapshotArtwork(artCopy, artWidth, artHeight);
                double snapshotMs = Stopwatch.GetElapsedTime(snapshotStart).TotalMilliseconds;
                long presentStart = Stopwatch.GetTimestamp();
                canvas.Save();
                canvas.ClipRect(new SKRect(0, 0, width, height));
                canvas.DrawImage(image, new SKRect(0, 0, artWidth * pixels, artHeight * pixels), Nearest);
                scene.DrawGpuOverlay(canvas, frame);
                canvas.Restore();
                image.Dispose();
                frame.Dispose();
                double present = Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds;
                double complete = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (i < 0) continue;
                artPrepareMs.Add(prepare);
                artSubmitMs.Add(renderer.LastSubmissionMilliseconds);
                artReadbackMs.Add(readback);
                artCopyMs.Add(copy);
                artSnapshotMs.Add(snapshotMs);
                artPresentMs.Add(present);
                artTotalMs.Add(complete);
                artUploadBytes.Add(renderer.LastUploadBytes);
            }
            CheckError(context.Gl);
            report.Add(new { Layer = layer, PixelSize = pixels, Width = width, Height = height, Samples = samples,
                CpuMs = Summary(cpuMs), GpuPrepareMs = Summary(prepareMs), GpuSubmitMs = Summary(submitMs),
                GpuCompletionAndReadbackMs = Summary(readbackMs), GpuTotalMs = Summary(totalMs),
                MeanUploadBytes = uploadBytes.Average(), renderer.ResidentBytes,
                ArtworkCapture = new
                {
                    Width = artWidth, Height = artHeight, ReadbackBytes = artReadback.Length,
                    PrepareMs = Summary(artPrepareMs), SubmitMs = Summary(artSubmitMs),
                    CompletionAndReadbackMs = Summary(artReadbackMs), ChannelSwapAndBufferCopyMs = Summary(artCopyMs),
                    RowFlipAndSnapshotMs = Summary(artSnapshotMs), SkiaPresentationAndOverlayMs = Summary(artPresentMs),
                    TotalMs = Summary(artTotalMs), MeanUploadBytes = artUploadBytes.Average()
                } });
            Console.WriteLine($"GL benchmark {layer} p{pixels} CPU {Summary(cpuMs).Median:F2} ms, " +
                $"prepare {Summary(prepareMs).Median:F2}, submit {Summary(submitMs).Median:F2}, " +
                $"complete/readback {Summary(readbackMs).Median:F2}, GPU total {Summary(totalMs).Median:F2} ms | " +
                $"art capture {Summary(artTotalMs).Median:F2} ms " +
                $"(prepare {Summary(artPrepareMs).Median:F2}, submit {Summary(artSubmitMs).Median:F2}, " +
                $"readback {Summary(artReadbackMs).Median:F2}, copy {Summary(artCopyMs).Median:F2}, " +
                $"snapshot {Summary(artSnapshotMs).Median:F2}, present {Summary(artPresentMs).Median:F2})");
        }
        File.WriteAllText(Path.Combine(output, "benchmark.json"), JsonSerializer.Serialize(new
        {
            Context = context.Description,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessorCount = Environment.ProcessorCount,
            MesaWorkerThreads = Environment.GetEnvironmentVariable("LP_NUM_THREADS"),
            TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default",
            RendererAssemblySha256 = rendererHash,
            HarnessAssemblySha256 = harnessHash,
            DataAssemblySha256 = dataHash,
            WarmupFrames = 16,
            Workload = "Bundled Garda 3×3 vectors, photographs and DEM plus world parents, 16-view pan/orbit loop, fixed pixel size. GpuTotalMs measures full-resolution GL overlay composition and synchronous RGBA readback, excluding subsequent row conversion and UI composition. ArtworkCapture measures art-resolution GL output, synchronous RGBA readback including GPU completion, R/B swap, equivalent IBuffer byte copy, row reversal into a new immutable Skia image, nearest logical-resolution presentation, and native labels. Both paths include scene preparation. Input loading, hashing, PNG encoding and the final Uno UI compositor are untimed.",
            Cases = report
        }, Json));
    }

    private static Statistics Summary(List<double> samples)
    {
        var values = samples.Order().ToArray();
        return new(values[values.Length / 2], values[Math.Min(values.Length - 1, (int)(values.Length * .95))],
            values[0], values[^1], samples);
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
        paint.Color = new(132, 103, 54);
        canvas.DrawRect(64, 160, 96, 64, paint);
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

    private static TilePoint[] Box(int left, int top, int right, int bottom) =>
        [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];

    private static MapFeature Building(ulong id, double height, double minimum, params TilePoint[][] paths) =>
        new("building", "", "", "", id, 4096, GeometryType.Polygon, paths, height, minimum, false, 0, 0, "");

    private static string Hash(SKBitmap bitmap) => Convert.ToHexStringLower(SHA256.HashData(bitmap.GetPixelSpan()));

    private static void Save(SKBitmap bitmap, string file)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(file);
        png.SaveTo(stream);
    }

    private static void CheckError(GL gl)
    {
        uint error = (uint)gl.GetError();
        Assert(error == 0, $"The native GPU checks encountered GL error 0x{error:X4}.");
    }

    private static void Assert(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The GPU benchmark requires bundled input and never makes network requests.");
    }
}
