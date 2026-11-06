using System.Reflection;
using PixelMap.Data;
using PixelMap.Rendering;
using Silk.NET.OpenGL;

internal static class GpuCacheChecks
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Width = 384, Height = 264;
    private static readonly TileId Center = new(14, 8192, 8192);

    internal static void Run(GL gl)
    {
        var tiles = Tiles();
        var elevation = new ElevationSnapshot([new(new(0, 0, 0), 2, [1000, 1000, 1000, 1000])]);
        var terrain = new TerrainOptions(elevation);
        var center = Point(.5, .5);
        var camera = new MapCamera(center.Latitude, center.Longitude, 13.4, 23, 55).OnTerrain(terrain, Height);
        int cases = 0;
        long greatestExcess = 0;
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        {
            using var scene = new PixelMapRenderer();
            using var first = scene.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null, terrain, style);
            using var moved = scene.PrepareGpuFrame(Width, Height, camera with { Bearing = 61, Perspective = true },
                tiles, PixelStyle.Dusk, 3, null, terrain, style);
            var eye = MapCamera.FromEye(center.Latitude, center.Longitude, 1080, 15, 82, Width, Height,
                viewDistanceMeters: 6000, groundAltitudeMeters: 1000);
            using var physical = scene.PrepareGpuFrame(Width, Height, eye, tiles, PixelStyle.Night, 2, null, terrain, style);
            var footprints = MeshBytes(first, gl.GetInteger(GLEnum.MaxTextureSize));
            Assert(footprints.Length >= 4, "The pressure fixture needs multiple independent tile meshes.");
            using var reference = new GlMapRenderer(gl);
            long baseBytes = reference.ResidentBytes;
            long allowance = footprints.Max() + 1024;
            Assert(footprints.Sum() > allowance * 2, "The pressure fixture fits its complete building working set.");
            long budget = baseBytes + allowance;
            using var bounded = Bounded(gl, budget);

            // Submitted GL primitives may outlive the retained objects. The two passes
            // must still paint every face before the ordered, depth-tested roof rims.
            foreach (var frame in new[] { first, first, moved, physical, first, physical, moved, first })
            {
                byte[] expected = Capture(gl, reference, frame);
                Assert(expected.AsSpan().SequenceEqual(Capture(gl, bounded, frame)),
                    $"Streaming a constrained GPU cache changed {style} terrain, faces or roof rims at case {cases}.");
                Assert(bounded.ResidentBytes <= budget, "The streaming building cache exceeded its configured byte budget.");
                Assert(reference.LastDrawCalls == bounded.LastDrawCalls, "Evicting a submitted tile omitted a face or roof-rim draw.");
                greatestExcess = Math.Max(greatestExcess, reference.ResidentBytes - budget);
                Assert(gl.GetError() == GLEnum.NoError, "Streaming building resources left an OpenGL error.");
                cases++;
            }
            Capture(gl, reference, first);
            Capture(gl, reference, first);
            long transformsOnly = first.TerrainPatchCount * 14L * sizeof(float);
            Assert(reference.LastUploadBytes == transformsOnly,
                "The no-pressure renderer re-uploaded retained face, rim, model or terrain resources.");
            Capture(gl, bounded, first);
            Assert(bounded.LastUploadBytes > transformsOnly,
                "The constrained renderer did not exercise reconstruction of evicted tile buffers.");
        }
        Assert(greatestExcess > 0, "The default cache never exceeded the pressure fixture's smaller budget.");
        CheckOversizedRecovery(gl, camera, tiles, terrain);
        Console.WriteLine($"Passed {cases} exact bounded GPU-cache frames across all styles, motion, physical-camera culling and held-frame replay; retained uploads and oversized-tile recovery also pass.");
    }

    private static void CheckOversizedRecovery(GL gl, MapCamera camera, MapTile[] tiles, TerrainOptions terrain)
    {
        using var scene = new PixelMapRenderer();
        using var frame = scene.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null, terrain);
        using var reference = new GlMapRenderer(gl);
        long budget = reference.ResidentBytes + MeshBytes(frame, gl.GetInteger(GLEnum.MaxTextureSize)).Min() / 2;
        using var bounded = Bounded(gl, budget);
        bool rejected = false;
        try { Capture(gl, bounded, frame); }
        catch (NotSupportedException exception) when (exception.Message.Contains("single map tile", StringComparison.Ordinal))
        {
            rejected = true;
        }
        Assert(rejected && bounded.ResidentBytes <= budget,
            "A genuinely oversized single tile was not rejected before exceeding the cache bound.");
        MapTile[] small = [new(Center, [Building(301, 1880, 1840, 320, 340, 75, 10)])];
        using var recovery = scene.PrepareGpuFrame(Width, Height, camera, small, PixelStyle.Day, 2, null, terrain);
        Assert(Capture(gl, reference, recovery).AsSpan().SequenceEqual(Capture(gl, bounded, recovery)),
            "A rejected oversized tile poisoned later compatible GPU draws.");
        Assert(bounded.ResidentBytes <= budget && gl.GetError() == GLEnum.NoError,
            "Recovering from an oversized tile left excess storage or GL errors.");
    }

    private static GlMapRenderer Bounded(GL gl, long bytes) =>
        (GlMapRenderer)typeof(GlMapRenderer).GetConstructor(Members, binder: null, [typeof(GL), typeof(long)], modifiers: null)!
            .Invoke([gl, bytes]);

    private static long[] MeshBytes(GpuMapFrame frame, int textureLimit)
    {
        var draws = (Array)Property(frame, "Buildings");
        return draws.Cast<object>().Select(draw =>
        {
            object mesh = Property(draw, "Mesh");
            int texels = Math.Max(2, (Field<float[]>(mesh, "Models").Length + 3) / 4);
            int width = Math.Min(textureLimit, texels), height = (texels + width - 1) / width;
            return Field<float[]>(mesh, "Vertices").LongLength * sizeof(float) +
                Field<uint[]>(mesh, "Indices").LongLength * sizeof(uint) +
                Field<float[]>(mesh, "RoofEdges").LongLength * sizeof(float) + (long)width * height * 4 * sizeof(float);
        }).ToArray();
    }

    private static byte[] Capture(GL gl, GlMapRenderer renderer, GpuMapFrame frame)
    {
        int width = (frame.Width + frame.PixelSize - 1) / frame.PixelSize;
        int height = (frame.Height + frame.PixelSize - 1) / frame.PixelSize;
        gl.Viewport(0, 0, (uint)width, (uint)height);
        renderer.DrawArtwork(frame);
        var pixels = new byte[width * height * 4];
        gl.ReadPixels<byte>(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        return pixels;
    }

    private static MapTile[] Tiles()
    {
        var tiles = new List<MapTile>();
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            var features = new List<MapFeature>();
            for (int row = 0; row < 5; row++)
            for (int column = 0; column < 5; column++)
            {
                int index = row * 5 + column;
                features.Add(Building((ulong)((y + 1) * 300 + (x + 1) * 30 + index + 1),
                    80 + column * 800, 100 + row * 800, 560, 540,
                    60 + (index % 5) * 22, index % 4 == 0 ? 18 : 0));
            }
            tiles.Add(new(Center with { X = Center.X + x, Y = Center.Y + y }, features));
        }
        return tiles.ToArray();
    }

    private static MapFeature Building(ulong id, int x, int y, int width, int height, double top, double bottom) =>
        new("building", "", "", "", id, 4096, GeometryType.Polygon,
            [[new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)]],
            top, bottom, false, 0, 0, "");

    private static (double Latitude, double Longitude) Point(double x, double y) =>
        (Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (Center.Y + y) / (1 << Center.Z)))) * 180 / Math.PI,
            (Center.X + x) * 360 / (1 << Center.Z) - 180);

    private static object Property(object target, string name) => target.GetType().GetProperty(name, Members)!.GetValue(target)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static void Assert(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
}
