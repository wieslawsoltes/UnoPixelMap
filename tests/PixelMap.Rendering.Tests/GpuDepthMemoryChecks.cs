using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

internal static class GpuDepthMemoryChecks
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const long IndexBudget = 16 * 1024 * 1024;
    private static readonly TileId TestTile = new(0, 0, 0);
    private static readonly GpuTerrainTexture TestTexture = new(new uint[GpuTerrainTexture.Size * GpuTerrainTexture.Size]);

    internal static void RunPressureChecks()
    {
        int comparisons = 0;
        foreach (bool perspective in new[] { false, true })
        {
            const int width = 2048, height = 2048;
            var transform = Transform(width, height, perspective);
            var feature = new MapFeature("building", "building", "", "", 717, 4096, GeometryType.Polygon,
                [new TilePoint[] { new(1500, 1200), new(1800, 1200), new(1800, 1500), new(1500, 1500) }],
                80, 10, false, 0, 0, "");
            using var geometry = new TileGeometry(new(TestTile, [feature]));
            var building = new GpuBuildingMesh(geometry);
            using var frame = Frame(width, height, perspective,
                [Draw(Vertices(0, 0, width, height, 5), transform),
                 Draw(Vertices(0, 0, 1056, height, 10, folded: true), transform),
                 Draw(Vertices(256, 256, 320, 320, 800), transform)], [new(building, transform, 1)]);

            // A right-hand pixel prepares only the ordinary background. The folded
            // left patch then generates enough real clipped triangle memberships
            // to exceed the production budget after this answer has been cached.
            frame.Depth.TryTerrain(1900, 500, out float beforeZ, out float beforeW, out float beforePhysical);
            Assert(DepthPreparationBenchmark.IndexCapacityBytes(frame.Depth) < IndexBudget,
                "The pressure fixture did not begin on the indexed path.");
            frame.Depth.TryTerrain(288, 288, out _, out _, out float physical);
            CheckCapacity(frame.Depth);
            Assert(!Flag(frame.Depth, "hasExactIndex"), "The exact-membership fixture did not exhaust its active budget.");
            Assert(DepthPreparationBenchmark.IndexCapacityBytes(frame.Depth) == IndexBudget,
                "The exact-membership fixture entered fallback before using the available budget.");
            Assert(Exact(physical, 800), "A later-materialized earlier group replaced the source-order terrain winner under pressure.");
            frame.Depth.TryTerrain(1900, 500, out float afterZ, out float afterW, out float afterPhysical);
            Assert(Exact(beforeZ, afterZ) && Exact(beforeW, afterW) && Exact(beforePhysical, afterPhysical),
                "Switching to group ranges changed an already cached terrain sample.");
            ((IDictionary)Field(frame.Depth, "samples")).Clear();
            frame.Depth.TryTerrain(1900, 500, out afterZ, out afterW, out afterPhysical);
            Assert(Exact(beforeZ, afterZ) && Exact(beforeW, afterW) && Exact(beforePhysical, afterPhysical),
                "Re-evaluating a pre-transition pixel through group ranges changed its terrain sample.");

            var queries = Queries(width, height).Concat(new[] { (1900, 500), (288, 288), (1600, 1300) }).ToList();
            // Include roof-rim DDA endpoints/interior pixels outside the half-open
            // roof fill; the model is first demanded after index exhaustion.
            for (int i = 0; i < building.RoofEdges.Length; i += GpuBuildingMesh.EdgeStride * 2)
            {
                var a = transform.Vertex(building.RoofEdges[i], building.RoofEdges[i + 1], 80).Point;
                var b = transform.Vertex(building.RoofEdges[i + GpuBuildingMesh.EdgeStride], building.RoofEdges[i + GpuBuildingMesh.EdgeStride + 1], 80).Point;
                for (int step = 0; step <= 8; step++)
                    queries.Add(((int)MathF.Floor(a.X + (b.X - a.X) * step / 8), (int)MathF.Floor(a.Y + (b.Y - a.Y) * step / 8)));
            }
            comparisons += Compare(frame, queries);
            Assert(Count(frame.Depth, "edges") > 0, "The pressure fixture did not prepare model rims after fallback.");
            using (var other = Frame(480, 320, false, [Draw(Vertices(0, 0, 480, 320, 123), Transform(480, 320, false))], []))
                comparisons += Compare(other, Queries(480, 320));
            comparisons += Compare(frame, queries.AsEnumerable().Reverse());
            CheckCapacity(frame.Depth);
        }

        // 65,537 identical 8x8-cell bounds require more than 16 MiB of coarse
        // memberships. Their triangles are degenerate, so preparation stays small.
        // A valid final group is deliberately added after coarse indexing stops.
        var identity = Transform(2048, 2048, false);
        var degenerate = Vertices(3, 3, 122, 122, 0, folded: true, diagonal: true);
        var shared = Draw(degenerate, identity);
        var draws = new TerrainGpuDraw[65538];
        Array.Fill(draws, shared, 0, draws.Length - 1);
        draws[^1] = Draw(Vertices(500, 500, 564, 564, 777), identity);
        using (var coarse = Frame(2048, 2048, false, draws, []))
        {
            CheckCapacity(coarse.Depth);
            Assert(!Flag(coarse.Depth, "hasCoarseIndex"), "The coarse-membership fixture did not exhaust its active budget.");
            Assert(DepthPreparationBenchmark.IndexCapacityBytes(coarse.Depth) == IndexBudget,
                "The coarse-membership fixture entered fallback before using the available budget.");
            coarse.Depth.TryTerrain(532, 532, out _, out _, out float physical);
            Assert(Exact(physical, 777), "Coarse fallback skipped a valid group added after index exhaustion.");
            comparisons += Compare(coarse, new[] { (532, 532), (500, 500), (563, 563), (700, 700), (1700, 1200) });
            CheckCapacity(coarse.Depth);
        }

        object? previous = null;
        foreach (var size in new[] { (Width: 512, Height: 384), (Width: 4096, Height: 2160), (Width: 384, Height: 640) })
        {
            using var frame = Frame(size.Width, size.Height, false,
                [Draw(Vertices(0, 0, size.Width, size.Height, 91), Transform(size.Width, size.Height, false))], []);
            object storage = Field(frame.Depth, "storage");
            if (previous is not null) Assert(ReferenceEquals(previous, storage), "The capacity test did not reuse its preceding workspace.");
            comparisons += Compare(frame, Queries(size.Width, size.Height));
            CheckCapacity(frame.Depth);
            Assert(((Array)Field(frame.Depth, "cells")).Length <= 16384, "An oversized viewport expanded the coarse grid without a bound.");
            if (size.Width == 4096) Assert((int)Field(frame.Depth, "cellSize") > 16, "The large viewport did not exercise adaptive cell size.");
            previous = storage;
        }
        Console.WriteLine($"Passed {comparisons:N0} exact pressure queries: active exact/coarse budgets, source-order terrain ties, model rims, cached/held frames, pooled capacity reuse and adaptive large grids.");
    }

    internal static async Task RunAsync(string[] arguments)
    {
        string output = Path.GetFullPath(arguments.FirstOrDefault(a => a.StartsWith("--depth-memory-output=", StringComparison.Ordinal))?
            ["--depth-memory-output=".Length..] ?? Path.Combine(Path.GetTempPath(), "PixelMap.DepthMemory"));
        Directory.CreateDirectory(output);
        using var http = new HttpClient(new DepthPreparationBenchmark.OfflineHandler());
        var source = new VectorTileSource(http);
        var tiles = new List<MapTile>();
        for (int y = 6159; y <= 6161; y++)
        for (int x = 4822; x <= 4824; x++) tiles.Add(await source.GetTileAsync(new(14, x, y)));
        var reports = new List<object>();
        using var renderer = new PixelMapRenderer();
        string font = TestLabelFont.Apply(renderer);
        foreach (var viewport in new[]
        {
            (Width: 1800, Height: 807, Pixels: 2, Style: MapRenderStyle.Pixel),
            (Width: 1800, Height: 924, Pixels: 2, Style: MapRenderStyle.Pixel),
            (Width: 3600, Height: 1848, Pixels: 2, Style: MapRenderStyle.Pixel),
            (Width: 1800, Height: 807, Pixels: 1, Style: MapRenderStyle.Normal)
        })
        {
            var first = Pose(90, viewport.Width, viewport.Height);
            var last = Pose(176.4, viewport.Width, viewport.Height);
            foreach (double alpha in new[] { 0, .4091174, .5554795, .6432968, .7018416744, .7603865, .8482038, 1 })
            {
                var camera = first.Interpolate(last, alpha);
                long allocation = GC.GetAllocatedBytesForCurrentThread();
                using var frame = renderer.PrepareGpuFrame(viewport.Width, viewport.Height, camera, tiles.ToArray(),
                    PixelStyle.Night, viewport.Pixels, null, null, viewport.Style);
                long afterLabels = DepthPreparationBenchmark.IndexCapacityBytes(frame.Depth);
                int[] bits = new int[256 * 5];
                for (int i = 0; i < 256; i++)
                {
                    int x = (i * 97 + 13) % frame.ArtWidth, y = (i * 67 + 5) % frame.ArtHeight;
                    bool hit = frame.Depth.TryTerrain(x, y, out float altitude, out float inverse, out float physical);
                    bits[i * 5] = hit ? 1 : 0;
                    bits[i * 5 + 1] = BitConverter.SingleToInt32Bits(altitude);
                    bits[i * 5 + 2] = BitConverter.SingleToInt32Bits(inverse);
                    bits[i * 5 + 3] = BitConverter.SingleToInt32Bits(physical);
                    bits[i * 5 + 4] = frame.Depth.CoversAt(x + .23f, y + .71f, i % 3 == 0 ? 35 : 4) ? 1 : 0;
                }
                long afterQueries = DepthPreparationBenchmark.IndexCapacityBytes(frame.Depth);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocation;
                int triangles = Count(frame.Depth, "triangles"), edges = Count(frame.Depth, "edges");
                reports.Add(new { viewport.Width, viewport.Height, PixelSize = frame.PixelSize, viewport.Style, Alpha = alpha,
                    Camera = camera, frame.ArtWidth, frame.ArtHeight, frame.TerrainPatchCount, frame.BuildingTriangleCount,
                    Groups = Count(frame.Depth, "groups"), Triangles = triangles, Edges = edges,
                    IndexCapacityAfterLabels = afterLabels, IndexCapacityAfterQueries = afterQueries,
                    CoarseIndex = DepthPreparationBenchmark.IndexFlag(frame.Depth, "hasCoarseIndex"),
                    ExactIndex = DepthPreparationBenchmark.IndexFlag(frame.Depth, "hasExactIndex"),
                    ExactMemberships = Memberships(frame.Depth, "cells"), CoarseMemberships = Memberships(frame.Depth, "pendingCells"),
                    AllocatedBytes = allocated, QueryHash = Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(bits.AsSpan()))) });
                Console.WriteLine($"Depth memory {viewport.Width}x{viewport.Height} p{frame.PixelSize} {viewport.Style} pitch{camera.Pitch:F4}: " +
                    $"labels {afterLabels / 1048576d:F3} MiB, queried {afterQueries / 1048576d:F3} MiB; {triangles:N0} triangles, {edges:N0} rims.");
            }
        }
        File.WriteAllText(Path.Combine(output, "depth-memory.json"), JsonSerializer.Serialize(new
        {
            ScenarioOrigin = "Camera path derived from the e086f47 live NorthUp-to-Orbit failure; this native subset did not itself reproduce the OOM",
            Scope = "Nine bundled Manhattan tiles with flat visual terrain and a constant-ground reference pose, whereas the live failure had relief enabled. Observed logical map 1800x807, alternate logical 1800x924, device-sized stress 3600x1848, and Normal pixel size1. Pixel size2 is a diagnostic choice; the live artwork resolution was not captured. Observed compositor DPR2 alone does not enlarge depth dimensions. The live vector cache can contain more than these nine tiles.",
            Features = tiles.Sum(t => t.Features.Count), Font = font,
            RendererSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(PixelMapRenderer).Assembly.Location))),
            Results = reports
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static MapCamera Pose(double lookPitch, int width, int height)
    {
        var body = new RoutePoint(40.7087, -74.0104);
        double angle = (66 + lookPitch - 90) * Math.PI / 180;
        double horizontal = 6 * Math.Sin(angle), altitude = Math.Max(.35, .85 + 6 * Math.Cos(angle));
        var eye = body.Move(165, horizontal);
        var camera = MapCamera.FromEye(eye.Latitude, eye.Longitude, altitude, -15,
            Math.Atan2(horizontal, altitude - .85) * 180 / Math.PI, width, height);
        if (!camera.TryAimAt(body.Latitude, body.Longitude, .85, width * .5, height * .5, width, height, out var aimed))
            throw new InvalidOperationException("The NorthUp-to-Orbit memory fixture could not aim at the avatar.");
        return aimed;
    }

    private static int Count(object value, string field) => ((ICollection)value.GetType().GetField(field, Members)!.GetValue(value)!).Count;
    private static long Memberships(object value, string field) => ((IEnumerable)value.GetType().GetField(field, Members)!.GetValue(value)!)
        .Cast<object?>().Sum(cell => cell is ICollection indices ? (long)indices.Count : 0);

    private static GpuMapFrame Frame(int width, int height, bool perspective, TerrainGpuDraw[] terrain, GpuBuildingDraw[] buildings)
    {
        var frame = new GpuMapFrame(width, height, 1, new(0, 0, 16, 0, 0, perspective),
            new(ElevationSnapshot.Empty, Relief: false), PixelPalette.Night, 1, terrain, buildings);
        frame.Depth = new(frame);
        return frame;
    }

    private static TileTransform Transform(int width, int height, bool perspective) => new(0, 0, 1, 0, 0, 1,
        CX: width * .5f, CY: height * .5f, InverseFocal: perspective ? (float)(1 / MapCamera.Focal(height)) : 0);
    private static TerrainGpuDraw Draw(float[] vertices, TileTransform transform) =>
        new(new(new object(), TestTile, 0, vertices, 1), TestTexture, transform);

    private static float[] Vertices(float left, float top, float right, float bottom, float physical, bool folded = false, bool diagonal = false)
    {
        var values = new float[GpuTerrainMesh.Side * GpuTerrainMesh.Side * GpuTerrainMesh.VertexStride];
        for (int y = 0; y < GpuTerrainMesh.Side; y++)
        for (int x = 0; x < GpuTerrainMesh.Side; x++)
        {
            int i = (y * GpuTerrainMesh.Side + x) * GpuTerrainMesh.VertexStride;
            float u = folded ? x & 1 : x / (float)GpuTerrainMesh.Segments;
            float v = diagonal ? u : folded ? y & 1 : y / (float)GpuTerrainMesh.Segments;
            values[i] = left + (right - left) * u;
            values[i + 1] = top + (bottom - top) * v;
            values[i + 2] = physical + (folded && !diagonal ? y * GpuTerrainMesh.Side + x : 0);
            values[i + 3] = 1;
        }
        return values;
    }

    private static IEnumerable<(int X, int Y)> Queries(int width, int height)
    {
        yield return (0, 0); yield return (width - 1, height - 1);
        foreach (int x in new[] { 15, 16, 31, 32, width / 2, width - 2 })
        foreach (int y in new[] { 15, 16, 31, 32, height / 2, height - 2 }) yield return (x, y);
        for (int i = 0; i < 96; i++) yield return ((i * 97 + 13) % width, (i * 67 + 5) % height);
    }

    private static int Compare(GpuMapFrame frame, IEnumerable<(int X, int Y)> queries)
    {
        var reference = new EagerGpuSceneDepth(frame);
        reference.Retain();
        int comparisons = 0;
        try
        {
            foreach (var (x, y) in queries)
            {
                bool actual = frame.Depth.TryTerrain(x, y, out float z, out float w, out float physical);
                bool expected = reference.TryTerrain(x, y, out float ez, out float ew, out float ep);
                Assert(actual == expected && Exact(z, ez) && Exact(w, ew) && Exact(physical, ep),
                    $"Pressure terrain query differs from eager at {x},{y}.");
                foreach (float altitude in new[] { -5f, 0, 10, 35, 80 })
                    Assert(frame.Depth.CoversAt(x + .23f, y + .71f, altitude) == reference.CoversAt(x + .23f, y + .71f, altitude),
                        $"Pressure occlusion query differs from eager at {x},{y}, altitude {altitude}.");
                comparisons += 6;
            }
        }
        finally { reference.Release(); }
        return comparisons;
    }

    private static void CheckCapacity(GpuSceneDepth depth)
    {
        long actual = DepthPreparationBenchmark.IndexCapacityBytes(depth);
        Assert(actual <= IndexBudget, $"Active index buffers exceeded their 16 MiB budget: {actual:N0} bytes.");
        Assert(actual == (int)Field(depth, "indexCapacityBytes"), "Active index accounting omitted retained or replaced buffer capacity.");
    }
    private static object Field(object value, string field) => value.GetType().GetField(field, Members)!.GetValue(value)!;
    private static bool Flag(object value, string field) => (bool)Field(value, field);
    private static bool Exact(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
