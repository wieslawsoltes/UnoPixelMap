using System.Reflection;
using PixelMap.Data;
using PixelMap.Rendering;
using Silk.NET.OpenGL;
using SkiaSharp;

internal static class GpuVisibilityChecks
{
    private const int Width = 360, Height = 240;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly TileId Tile = new(14, 8192, 8192);

    internal static void Run(GL gl)
    {
        var features = new List<MapFeature>();
        for (int y = -3; y <= 3; y++)
        for (int x = -3; x <= 3; x++)
        {
            if (x == 0 && y == 0) continue;
            int left = 2048 + x * 140 - 36, top = 2048 + y * 140 - 36;
            TilePoint[] box = [new(left, top), new(left + 72, top), new(left + 72, top + 72), new(left, top + 72)];
            features.Add(new("building", "", "", "", (ulong)(features.Count + 1), 4096,
                GeometryType.Polygon, [box], 16 + (Math.Abs(x) + Math.Abs(y)) * 12,
                (x + y) % 3 == 0 ? 12 : 0, false, 0, 0, ""));
        }
        MapTile[] tiles = [new(Tile, features)];
        var elevation = new ElevationSnapshot([new(new(0, 0, 0), 2, new float[] { 1501.3f, 1501.3f, 1501.3f, 1501.3f })]);
        double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (Tile.Y + .5) / (1 << Tile.Z)))) * 180 / Math.PI;
        double longitude = (Tile.X + .5) / (1 << Tile.Z) * 360 - 180;
        double ground = elevation.SampleSurfaceMeters(latitude, longitude);
        using var image = new SKBitmap(32, 32);
        using (var canvas = new SKCanvas(image)) canvas.Clear(new SKColor(94, 136, 72));
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        ImageryTile[] imagery = [new(Tile, encoded.ToArray())];
        (double Height, double Bearing, double Pitch)[] poses =
        {
            (Height: 1.7, Bearing: 0d, Pitch: 90d), (1.7, 89d, 90d),
            (1.7, 180d, 89.999), (1.7, 271d, 90.001),
            (16d, 23d, 45d), (16d, 23d, 110d),
            (80d, -47d, 70d), (1.7, 0d, 0d), (1.7, 0d, 179.5)
        };
        using var scene = new PixelMapRenderer();
        using var renderer = new GlMapRenderer(gl);
        int cases = 0, reduced = 0, zeroRims = 0;
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        foreach (int layer in new[] { 0, 1, 2 })
        foreach (var (clearance, bearing, pitch) in poses)
        {
            var terrain = new TerrainOptions(elevation, Relief: layer != 0, Hypsometric: layer == 2);
            double datum = terrain.Relief ? ground : 0;
            var camera = MapCamera.FromEye(latitude, longitude, datum + clearance, bearing, pitch,
                Width, Height, viewDistanceMeters: 400, groundAltitudeMeters: datum);
            using var frame = scene.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 3,
                layer == 1 ? imagery : null, terrain, style);
            using var complete = CompleteFrame(frame);
            byte[] actual = Capture(gl, renderer, frame);
            byte[] expected = Capture(gl, renderer, complete);
            string name = $"{style} layer {layer} eye {clearance} bearing {bearing} pitch {pitch}";
            Assert(actual.AsSpan().SequenceEqual(expected), $"Visibility culling changed exact GLES artwork for {name}.");
            CheckDepth(frame, complete, name);
            Assert(actual.AsSpan().SequenceEqual(Capture(gl, renderer, frame)),
                $"Restoring a held compact model stream changed GLES artwork for {name}.");
            renderer.DrawArtwork(frame);
            Assert(renderer.LastUploadBytes == frame.TerrainPatchCount * 14 * sizeof(float),
                $"An unchanged model selection uploaded its indices or edges again for {name}.");
            Assert(gl.GetError() == GLEnum.NoError, $"Visibility selection generated a GLES error for {name}.");

            var buildings = (Array)Get(frame, "Buildings")!;
            long fullIndices = 0, selectedIndices = 0, selectedEdges = 0;
            foreach (object draw in buildings)
            {
                object mesh = Get(draw, "Mesh")!, selection = Get(draw, "Selection")!;
                CheckRoofStorage(mesh);
                fullIndices += ((Array)Field(mesh, "Indices")!).Length;
                selectedIndices += ((Array)Field(selection, "Indices")!).Length;
                selectedEdges += ((Array)Field(selection, "RoofEdges")!).Length;
            }
            if (selectedIndices < fullIndices) reduced++;
            if (selectedEdges == 0) zeroRims++;
            using var nudged = scene.PrepareGpuFrame(Width, Height, camera with { Bearing = camera.Bearing + 1e-10 },
                tiles, PixelStyle.Day, 3, layer == 1 ? imagery : null, terrain, style);
            var nextBuildings = (Array)Get(nudged, "Buildings")!;
            Assert(buildings.Length == nextBuildings.Length, "An infinitesimal rotation changed model tile coverage.");
            for (int i = 0; i < buildings.Length; i++)
                Assert(ReferenceEquals(Get(buildings.GetValue(i)!, "Selection"), Get(nextBuildings.GetValue(i)!, "Selection")),
                    "Unchanged per-model visibility rebuilt the retained compact streams.");
            cases++;
        }
        Assert(reduced >= cases / 2 && zeroRims >= 12, "Physical-camera fixtures did not exercise model and roof-edge rejection.");
        Console.WriteLine($"Passed {cases} exact physical-camera GLES visibility comparisons, depth queries, retained stream restoration and upload reuse across all styles and terrain layers.");
    }

    private static void CheckRoofStorage(object mesh)
    {
        var edges = (float[])Field(mesh, "RoofEdges")!;
        var ranges = (Array)Field(mesh, "Ranges")!;
        int expectedLength = 0, modelIndex = 0;
        foreach (object range in ranges)
        {
            int first = (int)Get(range, "FirstEdge")!, count = (int)Get(range, "EdgeCount")!;
            float top = (float)Field(Get(range, "Model")!, "Top")!;
            Assert(first * 4 == expectedLength && count % 2 == 0,
                "A compact roof range lost its paired endpoint offsets.");
            for (int point = first; point < first + count; point++)
            {
                int offset = point * 4;
                Assert(float.IsFinite(edges[offset]) && float.IsFinite(edges[offset + 1]) &&
                    edges[offset + 2] == top && edges[offset + 3] == modelIndex,
                    "A compact roof vertex changed its position, height or model identity.");
            }
            expectedLength += count * 4;
            modelIndex++;
        }
        Assert(expectedLength == edges.Length, "Roof geometry retained unused per-vertex attributes.");
    }

    private static GpuMapFrame CompleteFrame(GpuMapFrame source)
    {
        var selected = (Array)Get(source, "Buildings")!;
        var type = selected.GetType().GetElementType()!;
        var complete = Array.CreateInstance(type, selected.Length);
        var constructor = type.GetConstructors(Members).Single(c => c.GetParameters().Length == 4);
        for (int i = 0; i < selected.Length; i++)
        {
            object draw = selected.GetValue(i)!;
            complete.SetValue(constructor.Invoke([Get(draw, "Mesh"), Get(draw, "Transform"), Get(draw, "TileUnitsToMeters"), null]), i);
        }
        var frame = (GpuMapFrame)typeof(GpuMapFrame).GetConstructors(Members).Single().Invoke([
            source.Width, source.Height, source.PixelSize, source.Camera, Get(source, "TerrainOptions"),
            Get(source, "Palette"), Get(source, "PixelsPerMeter"), Get(source, "Terrain"), complete]);
        typeof(GpuMapFrame).GetProperty("PreservePhotograph", Members)!.SetValue(frame, Get(source, "PreservePhotograph"));
        var depthType = typeof(PixelMapRenderer).Assembly.GetType("PixelMap.Rendering.GpuSceneDepth")!;
        var depth = depthType.GetConstructors(Members).Single().Invoke([frame]);
        typeof(GpuMapFrame).GetProperty("Depth", Members)!.SetValue(frame, depth);
        return frame;
    }

    private static void CheckDepth(GpuMapFrame actual, GpuMapFrame expected, string name)
    {
        object a = Get(actual, "Depth")!, b = Get(expected, "Depth")!;
        var covers = a.GetType().GetMethod("CoversAt", Members)!;
        var terrain = a.GetType().GetMethod("TryTerrain", Members)!;
        int width = (actual.Width + actual.PixelSize - 1) / actual.PixelSize;
        int height = (actual.Height + actual.PixelSize - 1) / actual.PixelSize;
        for (int y = 1; y < 9; y++)
        for (int x = 1; x < 11; x++)
        {
            int px = x * width / 11, py = y * height / 9;
            object[] pa = [px, py, 0f, 0f, 0f], pb = [px, py, 0f, 0f, 0f];
            Assert(Equals(terrain.Invoke(a, pa), terrain.Invoke(b, pb)) && pa.SequenceEqual(pb),
                $"Visibility culling changed terrain intersection for {name}.");
            foreach (float altitude in new[] { 0f, 1.7f, 20f, 90f })
            {
                object[] point = [(float)px + .5f, (float)py + .5f, altitude];
                Assert(Equals(covers.Invoke(a, point), covers.Invoke(b, point)),
                    $"Visibility culling changed overlay depth for {name}.");
            }
        }
    }

    private static byte[] Capture(GL gl, GlMapRenderer renderer, GpuMapFrame frame)
    {
        int width = (frame.Width + frame.PixelSize - 1) / frame.PixelSize;
        int height = (frame.Height + frame.PixelSize - 1) / frame.PixelSize;
        gl.Viewport(0, 0, (uint)width, (uint)height);
        renderer.DrawArtwork(frame);
        byte[] pixels = new byte[width * height * 4];
        gl.ReadPixels<byte>(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        return pixels;
    }

    private static object? Get(object instance, string name) => instance.GetType().GetProperty(name, Members)!.GetValue(instance);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Members)!.GetValue(instance);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
