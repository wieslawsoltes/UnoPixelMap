using System.Runtime.InteropServices;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using Silk.NET.OpenGL;
using SkiaSharp;

internal static class RenderStyleChecks
{
    private const int Width = 360, Height = 260;
    private static readonly TileId Tile = new(14, 8192, 8192);
    private static readonly ElevationSnapshot Elevation = new([
        new(new(0, 0, 0), 2, new float[] { 1501.3f, 1501.3f, 1501.3f, 1501.3f })]);
    private static readonly TerrainOptions Terrain = new(Elevation);
    private static readonly MapCamera Camera = new MapCamera(-.010986328057677, .010986328125, 15.4, 23, 55, true)
        .OnTerrain(Terrain, Height);
    private static readonly MapFeature Building = new("building", "", "", "", 810, 4096, GeometryType.Polygon,
        [[new(1800, 2048), new(2048, 1790), new(2296, 2048), new(2048, 2306)]], 37.3, 0, false, 0, 0, "");
    private static readonly MapTile[] Tiles = [new(Tile, [Building])];
    private static readonly MapTile[] Empty = [new(Tile, [])];

    internal static void Run()
    {
        using var renderer = new PixelMapRenderer();
        using var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        renderer.Draw(canvas, Width, Height, Camera, Tiles, PixelStyle.Day, 3, null, false, false, Terrain);
        byte[] original = bitmap.GetPixelSpan().ToArray();
        var hashes = new HashSet<ulong>();
        using var pixelFrame = renderer.PrepareGpuFrame(Width, Height, Camera, Tiles, PixelStyle.Day, 3, null, Terrain);
        int pixelModels = pixelFrame.BuildingTriangleCount;
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        {
            renderer.Draw(canvas, Width, Height, Camera, Tiles, PixelStyle.Day, 3, null, false, false, Terrain, style);
            hashes.Add(Hash(bitmap));
            byte[] first = bitmap.GetPixelSpan().ToArray();
            renderer.Draw(canvas, Width, Height, Camera, Tiles, PixelStyle.Day, 3, null, false, false, Terrain, style);
            Assert(first.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), $"{style} changed on a retained CPU redraw");
            using var frame = renderer.PrepareGpuFrame(Width, Height, Camera, Tiles, PixelStyle.Day, 3, null, Terrain, style);
            Assert(frame.RenderStyle == style, $"{style} was lost from the retained frame");
            Assert(frame.PixelSize == (style is MapRenderStyle.Normal or MapRenderStyle.Roblox ? 1 : 3),
                $"{style} has the wrong artwork resolution");
            int triangles = style switch { MapRenderStyle.Minecraft => 640, MapRenderStyle.Roblox => 1152, _ => 512 };
            Assert(frame.TerrainPatchCount > 0 && frame.TerrainTriangleCount == frame.TerrainPatchCount * triangles,
                $"{style} did not retain its actual terrain topology");
            if (style == MapRenderStyle.Minecraft)
                Assert(frame.BuildingTriangleCount > pixelModels, "Minecraft retained a diagonal building footprint");
            Assert(renderer.TryGetTerrainElevation(Camera.Latitude, Camera.Longitude, out double surface),
                $"{style} did not expose the rendered surface for collision cameras");
            CheckHeight(style, surface);
            // A held frame must retain its geometry after another style rebuilds the renderer.
            renderer.ActivateGpuFrame(pixelFrame);
            Assert(pixelFrame.RenderStyle == MapRenderStyle.Pixel &&
                renderer.TryGetTerrainElevation(Camera.Latitude, Camera.Longitude, out double oldHeight) &&
                Math.Abs(oldHeight - 1501.3) < .02, "Switching styles mutated a held Pixel terrain frame");
            renderer.ActivateGpuFrame(frame);
            Assert(renderer.TryGetTerrainElevation(Camera.Latitude, Camera.Longitude, out double restored) &&
                Math.Abs(restored - surface) < .001, $"{style} failed to restore its retained terrain height");
        }
        Assert(hashes.Count == 4, "The four render styles collapsed to identical artwork");
        renderer.Draw(canvas, Width, Height, Camera, Tiles, PixelStyle.Day, 3, null, false, false, Terrain, MapRenderStyle.Pixel);
        Assert(original.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "Style switching changed the existing Pixel appearance");
        CheckSurfacePicking(renderer, canvas);
        CheckVehicles(renderer, bitmap, canvas);
        CheckCollisionTiles();
        CheckFlatNavigation(renderer, bitmap, canvas);
        Console.WriteLine("Passed four render styles, bounded voxel/bevel topology, immutable frame switching, terrain height/picking, Pixel restoration and world-scale standalone vehicles.");
    }

    private static void CheckCollisionTiles()
    {
        var road = Building with { Layer = "transportation", Type = GeometryType.LineString };
        var courtyard = Building with { Paths = [Building.Paths[0],
            [new(1984, 1984), new(1984, 2112), new(2112, 2112), new(2112, 1984)]], MinHeight = 12.3 };
        var tile = new MapTile(Tile, [courtyard, road]);
        Assert(ReferenceEquals(tile, PixelMapRenderer.PrepareCollisionTile(tile, MapRenderStyle.Pixel)) &&
            ReferenceEquals(tile, PixelMapRenderer.PrepareCollisionTile(tile, MapRenderStyle.Normal)),
            "Continuous styles replaced immutable source collision geometry");
        foreach (var style in new[] { MapRenderStyle.Minecraft, MapRenderStyle.Roblox })
        {
            var prepared = PixelMapRenderer.PrepareCollisionTile(tile, style);
            var building = prepared.Features[0];
            Assert(ReferenceEquals(prepared, PixelMapRenderer.PrepareCollisionTile(tile, style)) &&
                ReferenceEquals(prepared.Features[1], road), "Block collision tiles lost source identity caching or non-building features");
            Assert(building.Id == courtyard.Id && building.Paths.Length == 2 && building.Height == 38 && building.MinHeight == 12,
                "Block collision geometry lost courtyards, raised bases or source metadata");
            if (style == MapRenderStyle.Minecraft)
                foreach (var ring in building.Paths)
                    for (int i = 0; i < ring.Length; i++)
                    {
                        var a = ring[i]; var b = ring[(i + 1) % ring.Length];
                        Assert(a.X == b.X || a.Y == b.Y, "Minecraft collision retained a diagonal footprint edge");
                    }
        }
        Assert(courtyard.Height == 37.3 && courtyard.MinHeight == 12.3, "Preparing style collision geometry mutated source features");
    }

    private static void CheckFlatNavigation(PixelMapRenderer renderer, SKBitmap bitmap, SKCanvas canvas)
    {
        var point = new RoutePoint(Camera.Latitude, Camera.Longitude);
        var eye = point.Move(180, 26);
        var view = MapCamera.FromEye(eye.Latitude, eye.Longitude, 13, 0, 62, Width, Height);
        var flat = Terrain with { Relief = false };
        double physicalGround = Elevation.SampleSurfaceMeters(point.Latitude, point.Longitude);
        var pose = new VehiclePose(point, 28, AltitudeMeters: physicalGround, Moving: false);
        using var reference = new PixelMapRenderer();
        canvas.Clear(SKColors.Transparent);
        reference.DrawVehicle(canvas, Width, Height, view, TravelMode.Drive, pose with { AltitudeMeters = 0 }, 1);
        byte[] expected = bitmap.GetPixelSpan().ToArray();
        renderer.Draw(canvas, Width, Height, view, [], PixelStyle.Day, 1, null, false, false, flat, MapRenderStyle.Pixel);
        canvas.Clear(SKColors.Transparent);
        renderer.DrawVehicle(canvas, Width, Height, view, TravelMode.Drive, pose, 1);
        Assert(expected.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "Flat-mode standalone vehicles retained absolute DEM altitude");
        using var frame = renderer.PrepareGpuFrame(Width, Height, view, [], PixelStyle.Day, 1, null, flat, MapRenderStyle.Pixel);
        using var other = renderer.PrepareGpuFrame(Width, Height, view, [], PixelStyle.Day, 1, null, null, MapRenderStyle.Normal);
        renderer.ActivateGpuFrame(frame);
        canvas.Clear(SKColors.Transparent);
        renderer.DrawVehicle(canvas, Width, Height, view, TravelMode.Drive, pose, 1);
        Assert(expected.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "A held flat GPU frame lost its physical navigation datum");
    }

    private static void CheckSurfacePicking(PixelMapRenderer renderer, SKCanvas canvas)
    {
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        {
            renderer.Draw(canvas, Width, Height, Camera, Empty, PixelStyle.Day, 2, null, false, false, Terrain, style);
            Assert(renderer.TryPickTerrain(180, 170, Width, Height, Camera, out var cpu), $"{style} CPU surface lost picking");
            using var frame = renderer.PrepareGpuFrame(Width, Height, Camera, Empty, PixelStyle.Day, 2, null, Terrain, style);
            Assert(renderer.TryPickTerrain(180, 170, Width, Height, Camera, out var gpu), $"{style} retained surface lost picking");
            Assert(Math.Abs(cpu.ElevationMeters - gpu.ElevationMeters) < .03,
                $"{style} CPU and GPU depth disagree on terrain height");
            CheckHeight(style, gpu.ElevationMeters);
            double altitude = Camera.GroundAltitudeMeters + gpu.ElevationMeters - 1501.3;
            var projected = Camera.ProjectAtAltitude(gpu.Latitude, gpu.Longitude, altitude, Width, Height);
            int pixel = frame.PixelSize;
            double expectedX = Math.Floor(180d / pixel) * pixel + pixel * .5;
            double expectedY = Math.Floor(170d / pixel) * pixel + pixel * .5;
            Assert(Math.Abs(projected.X - expectedX) + Math.Abs(projected.Y - expectedY) < .1,
                $"{style} terrain projection disagrees with the rendered triangle");
        }
    }

    private static void CheckHeight(MapRenderStyle style, double height)
    {
        bool valid = style switch
        {
            MapRenderStyle.Minecraft => Math.Abs(height - 1502) < .03,
            MapRenderStyle.Roblox => height is > 1500.8 and < 1501.03,
            _ => Math.Abs(height - 1501.3) < .03
        };
        Assert(valid, $"{style} returned the wrong rendered surface height {height}");
    }

    private static void CheckVehicles(PixelMapRenderer renderer, SKBitmap bitmap, SKCanvas canvas)
    {
        var point = new RoutePoint(Camera.Latitude, Camera.Longitude);
        var eye = point.Move(180, 26);
        var view = MapCamera.FromEye(eye.Latitude, eye.Longitude, 13, 0, 62, Width, Height);
        var shapes = new HashSet<ulong>();
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        {
            renderer.Draw(canvas, Width, Height, view, [], PixelStyle.Day, 2, null, false, false, null, style);
            foreach (var mode in Enum.GetValues<TravelMode>())
            {
                canvas.Clear(SKColors.Transparent);
                renderer.DrawVehicle(canvas, Width, Height, view, mode, new(point, 28, Moving: false), 2);
                Assert(CountVisible(bitmap) > 3, $"{style} {mode} did not render a standalone world-scale model");
                shapes.Add(Hash(bitmap));
            }
        }
        Assert(shapes.Count >= 12, "Standalone models lost their vehicle or geometry-style identity");
        using var fresh = new PixelMapRenderer();
        canvas.Clear(SKColors.Transparent);
        fresh.DrawVehicle(canvas, Width, Height, view, TravelMode.Drive, new(point, 28, Moving: false), 1);
        int close = CountVisible(bitmap);
        eye = point.Move(180, 52);
        view = MapCamera.FromEye(eye.Latitude, eye.Longitude, 26, 0, 62, Width, Height);
        canvas.Clear(SKColors.Transparent);
        fresh.DrawVehicle(canvas, Width, Height, view, TravelMode.Drive, new(point, 28, Moving: false), 1);
        Assert(CountVisible(bitmap) < close * .55, "A free-roaming vehicle retained fixed icon size as the eye moved away");
    }

    internal static void RunGl(GL gl)
    {
        using var scene = new PixelMapRenderer();
        using var renderer = new GlMapRenderer(gl);
        using var overlay = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(overlay);
        using var image = Image();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        ImageryTile[] photos = [new(Tile, encoded.ToArray())];
        var hashes = new HashSet<ulong>();
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        foreach (int layer in new[] { 0, 1, 2 })
        {
            var options = Terrain with { Hypsometric = layer == 2 };
            using var frame = scene.PrepareGpuFrame(Width, Height, Camera, Tiles, PixelStyle.Day, 3,
                layer == 1 ? photos : null, options, style);
            canvas.Clear(SKColors.Transparent);
            scene.DrawGpuOverlay(canvas, frame);
            gl.Viewport(0, 0, Width, Height);
            renderer.Draw(frame, overlay);
            byte[] first = Read(gl);
            renderer.Draw(frame, overlay);
            Assert(first.AsSpan().SequenceEqual(Read(gl)), $"{style} layer {layer} changed on a retained GLES redraw");
            Assert(renderer.LastUploadBytes == frame.TerrainPatchCount * 14 * sizeof(float) + Width * Height * sizeof(uint), $"{style} layer {layer} uploaded immutable meshes on redraw");
            Assert(gl.GetError() == GLEnum.NoError, $"{style} layer {layer} generated a GLES error");
            hashes.Add(Hash(first));
            if (Environment.GetEnvironmentVariable("PIXELMAP_SCREENSHOT_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                using var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                for (int y = 0; y < Height; y++)
                    first.AsSpan((Height - 1 - y) * Width * 4, Width * 4).CopyTo(bitmap.GetPixelSpan().Slice(y * bitmap.RowBytes));
                using var snapshot = SKImage.FromBitmap(bitmap);
                using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(Path.Combine(output, $"style-{style}-layer{layer}.png"));
                png.SaveTo(file);
            }
        }
        Assert(hashes.Count == 12, "Map, Earth, elevation or geometry style reused stale GLES artwork");
        Console.WriteLine("Passed native GLES rendering, immutable redraw and upload reuse for all four render styles across Map, Earth and Elevation.");
    }

    private static SKImage Image()
    {
        using var bitmap = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var colors = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++) colors[y * 64 + x] = (uint)new SKColor((byte)(60 + x * 2), (byte)(90 + y * 2), 91);
        return SKImage.FromBitmap(bitmap);
    }

    private static byte[] Read(GL gl)
    {
        var rgba = new byte[Width * Height * 4];
        gl.ReadPixels<byte>(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, rgba.AsSpan());
        return rgba;
    }

    private static int CountVisible(SKBitmap bitmap)
    {
        int count = 0;
        foreach (uint color in MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan())) if ((color >> 24) != 0) count++;
        return count;
    }
    private static ulong Hash(SKBitmap bitmap) => Hash(bitmap.GetPixelSpan());
    private static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037;
        foreach (byte value in bytes) hash = unchecked((hash ^ value) * 1099511628211);
        return hash;
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
