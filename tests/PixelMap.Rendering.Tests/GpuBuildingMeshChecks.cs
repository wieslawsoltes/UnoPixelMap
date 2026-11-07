using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Rendering.Tests.Reference;
using SkiaSharp;

/// <summary>Independent float-stream reconstruction and allocation checks for the
/// compact mesh. Source contours, triangle/rim order and CPU edge math stay exact.</summary>
internal static class GpuBuildingMeshChecks
{
    private static readonly FieldInfo Prepared = typeof(Building).GetField("vertices", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private sealed record Cost(long OldAllocated, long NewAllocated, long OldVertexBytes, long NewVertexBytes, int Vertices);

    internal static async Task RunAsync()
    {
        Assert(Unsafe.SizeOf<GpuBuildingVertex>() == 32 && Marshal.OffsetOf<GpuBuildingVertex>("Packed").ToInt32() == 28,
            "The GL vertex layout is not seven float values followed by one uint.");
        foreach (int model in new[] { 0, 1, 65535, 65536, 1048577, 0x0fffffff })
        foreach (float length in new[] { 17.25f, 0f, -0f })
        for (uint flags = 0; flags < 16; flags++)
        {
            var vertex = new GpuBuildingVertex(0, 0, length, 0, 0, 0, 0, (uint)model << 4 | flags);
            Assert(vertex.Model == model && vertex.Face == (int)(flags & 3) && vertex.Bottom == ((flags & 4) != 0) &&
                Bits(vertex.Along) == Bits((flags & 8) != 0 ? length : 0), "Packed integer model/endpoint flags changed value.");
        }

        long oldAllocated = 0, newAllocated = 0, oldVertexBytes = 0, newVertexBytes = 0;
        int cases = 0, vertices = 0;
        foreach (MapRenderStyle style in Enum.GetValues<MapRenderStyle>())
        foreach (int extent in new[] { 4096, 3000, 8191 })
        {
            var cost = Check(Synthetic(extent), style, $"synthetic/{style}/{extent}");
            cases++; vertices += cost.Vertices;
        }
        Check(new(new(14, 8192, 8192), []), MapRenderStyle.Pixel, "empty");
        cases++;
        using var client = new HttpClient(new OfflineHandler());
        var source = new VectorTileSource(client);
        for (int y = 6159; y <= 6161; y++)
        for (int x = 4822; x <= 4824; x++)
        {
            var tile = await source.GetTileAsync(new(14, x, y));
            var cost = Check(tile, MapRenderStyle.Pixel, $"Manhattan/{x}/{y}");
            oldAllocated += cost.OldAllocated; newAllocated += cost.NewAllocated;
            oldVertexBytes += cost.OldVertexBytes; newVertexBytes += cost.NewVertexBytes;
            vertices += cost.Vertices; cases++;
        }
        Assert(newVertexBytes * 3 == oldVertexBytes * 2, "Compact retained vertex payload did not fall by exactly one third.");
        // Warm synthetic cases precede this real-data allocation gate. It includes
        // tessellation/staging and the old prepared edges, not merely final arrays.
        Assert(newAllocated < oldAllocated * .8,
            $"Exact construction did not materially reduce real-tile allocations: {oldAllocated:N0} -> {newAllocated:N0} bytes.");
        Console.WriteLine($"Passed {cases} building mesh cases / {vertices:N0} vertices: every old logical float bit, index, rim and range matches; " +
            $"real-tile constructor allocation {oldAllocated:N0} -> {newAllocated:N0} bytes; vertex payload {oldVertexBytes:N0} -> {newVertexBytes:N0} bytes; GPU leaves CPU edge tables lazy.");
    }

    private static Cost Check(MapTile tile, MapRenderStyle style, string name)
    {
        using var oldGeometry = new TileGeometry(tile, style);
        using var geometry = new TileGeometry(tile, style);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var expected = new LegacyGpuBuildingMesh(oldGeometry);
        long oldAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var actual = new GpuBuildingMesh(geometry);
        long newAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(geometry.Buildings.All(model => Prepared.GetValue(model) is null), name + ": GPU construction eagerly retained CPU edge tables.");
        Assert(expected.Vertices.Length == actual.Vertices.Length * 12, name + ": vertex count changed.");
        Assert(expected.Indices.AsSpan().SequenceEqual(actual.Indices), name + ": index values/order changed.");
        Assert(BytesEqual(expected.RoofEdges, actual.RoofEdges), name + ": rim attributes/order changed.");
        Assert(BytesEqual(expected.Models, actual.Models), name + ": model metadata changed.");
        Assert(expected.Ranges.Length == actual.Ranges.Length, name + ": model/range count changed.");
        Span<float> values = stackalloc float[12];
        for (int i = 0; i < actual.Vertices.Length; i++)
        {
            ref readonly var vertex = ref actual.Vertices[i];
            int offset = vertex.Model * GpuBuildingMesh.ModelStride;
            float top = actual.Models[offset + 3], bottom = actual.Models[offset + 6];
            values[0] = vertex.X; values[1] = vertex.Y; values[2] = vertex.Bottom ? bottom : top;
            values[3] = vertex.Model; values[4] = vertex.Along; values[5] = vertex.Length;
            values[6] = vertex.Light; values[7] = vertex.PerspectiveLight;
            values[8] = vertex.Dx; values[9] = vertex.Dy; values[10] = top - bottom; values[11] = vertex.Face;
            if (!MemoryMarshal.AsBytes(values).SequenceEqual(MemoryMarshal.AsBytes(expected.Vertices.AsSpan(i * 12, 12))))
                throw new InvalidOperationException($"{name}: logical vertex {i} does not reproduce the original IEEE754 attribute bits.");
        }
        for (int i = 0; i < actual.Ranges.Length; i++)
        {
            var a = actual.Ranges[i]; var e = expected.Ranges[i];
            Assert(ReferenceEquals(a.Model, geometry.Buildings[i]) && a.FirstIndex == e.FirstIndex && a.IndexCount == e.IndexCount &&
                a.FirstEdge == e.FirstEdge && a.EdgeCount == e.EdgeCount && a.FirstVertex == e.FirstVertex &&
                a.VertexCount == e.VertexCount && a.WallIndexCount == e.WallIndexCount, name + ": source model/range order changed.");
            // Independently retained original arithmetic also checks the helper
            // extraction used by both CPU tables and streaming GPU construction.
            var original = OriginalVertices(a.Model.Feature, a.Model.Unit);
            var cpu = a.Model.Vertices;
            Assert(cpu.Length == original.Length, name + ": CPU edge count changed.");
            for (int v = 0; v < cpu.Length; v++)
            {
                var x = cpu[v]; var y = original[v];
                if (Bits(x.Point.X) != Bits(y.Point.X) || Bits(x.Point.Y) != Bits(y.Point.Y) || Bits(x.Length) != Bits(y.Length) ||
                    Bits(x.PerspectiveLength) != Bits(y.PerspectiveLength) || Bits(x.Light) != Bits(y.Light) ||
                    Bits(x.PerspectiveLight) != Bits(y.PerspectiveLight) || x.TileCut != y.TileCut)
                    throw new InvalidOperationException($"{name}: CPU edge {i}/{v} changed its original coordinate-space arithmetic.");
            }
        }
        var models = (float[])actual.Models.Clone();
        for (int i = 0; i < models.Length; i += GpuBuildingMesh.ModelStride) models[i] += 13.75f;
        var foundations = actual.WithFoundations(models);
        Assert(ReferenceEquals(actual.Vertices, foundations.Vertices) && ReferenceEquals(actual.Indices, foundations.Indices) &&
            ReferenceEquals(actual.RoofEdges, foundations.RoofEdges) && ReferenceEquals(actual.Ranges, foundations.Ranges) &&
            ReferenceEquals(actual.ResourceKey, foundations.ResourceKey) && !ReferenceEquals(actual.Models, foundations.Models),
            name + ": a foundation snapshot stopped sharing immutable geometry.");
        return new(oldAllocated, newAllocated, expected.Vertices.LongLength * sizeof(float),
            actual.Vertices.LongLength * Unsafe.SizeOf<GpuBuildingVertex>(), actual.Vertices.Length);
    }

    private static BuildingVertex[] OriginalVertices(MapFeature feature, float unit)
    {
        int count = 0;
        foreach (var ring in feature.Paths) count += ring.Length;
        var vertices = new BuildingVertex[count];
        int index = 0;
        foreach (var ring in feature.Paths)
        {
            if (ring.Length == 0) continue;
            var previous = ring[^1];
            foreach (var point in ring)
            {
                float x = point.X * unit, y = point.Y * unit;
                float dx = point.X - previous.X, dy = point.Y - previous.Y;
                float length = MathF.Sqrt(dx * dx + dy * dy);
                float light = Math.Clamp(.5f + (dx * .75f - dy * .55f) / (length * 2), 0, 1);
                float normalizedDx = x - previous.X * unit, normalizedDy = y - previous.Y * unit;
                float normalizedLength = MathF.Sqrt(normalizedDx * normalizedDx + normalizedDy * normalizedDy);
                float normalizedLight = Math.Clamp(.5f + (normalizedDx * .75f - normalizedDy * .55f) / (2 * normalizedLength), 0, 1);
                bool tileCut = previous.X == point.X && (previous.X <= 0 || previous.X >= feature.Extent) ||
                    previous.Y == point.Y && (previous.Y <= 0 || previous.Y >= feature.Extent);
                vertices[index++] = new(new(x, y), length * unit, normalizedLength, light, normalizedLight, tileCut);
                previous = point;
            }
        }
        return vertices;
    }

    private static MapTile Synthetic(int extent)
    {
        var features = new List<MapFeature>();
        Add([Ring((100, 100), (700, 100), (700, 600), (100, 600))], 37, 0);
        Add([Ring((900, 100), (1700, 100), (1700, 900), (900, 900)),
             Ring((1100, 300), (1100, 700), (1500, 700), (1500, 300))], 90.125, 13.375);
        Add([Ring((100, 1100), (900, 1100), (900, 1300), (400, 1300), (400, 1800), (100, 1800))], 19.75, 19.75);
        Add([Ring((1100, 1100), (1800, 1800), (1800, 1100), (1100, 1800))], 58.5, 1.25);
        Add([Ring((2000, 100), (2400, 100), (2400, 500), (2000, 500)),
             Ring((2500, 100), (2800, 100), (2800, 700), (2500, 700))], null, null);
        Add([Ring((0, 1900), (500, 1900), (500, 2300), (0, 2300)),
             Ring((extent - 300, 900), (extent, 900), (extent, 1500), (extent - 300, 1500))], 61, 0);
        Add([Ring((100, 2500), (100, 2500), (500, 2500), (500, 2900), (100, 2900)),
             [], Ring((0, 0)), Ring((10, 10), (20, 20))], 7.5, 0);
        Add([Ring((1800, 2300), (2200, 2400), (2100, 2800), (1700, 2700))], 1500, 15.0625);
        Add([Ring((2300, 2300), (2700, 2300), (2700, 2700), (2300, 2700))], 23.5, -0d);
        return new(new(14, 4823, 6160), features);
        void Add(TilePoint[][] paths, double? height, double? bottom) => features.Add(new("building", "", "", "", (ulong)features.Count + 1,
            extent, GeometryType.Polygon, paths, height, bottom, false, 0, 0, ""));
        static TilePoint[] Ring(params (int X, int Y)[] points) => points.Select(p => new TilePoint(p.X, p.Y)).ToArray();
    }

    private static bool BytesEqual(float[] a, float[] b) => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class OfflineHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("Unexpected network request: " + request.RequestUri); }
}
