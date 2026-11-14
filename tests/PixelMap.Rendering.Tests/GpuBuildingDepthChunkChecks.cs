using System.Collections;
using System.Reflection;
using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

/// <summary>Compare large, partly visible building preparation with the frozen
/// eager projector. Every fixture deliberately exceeds the chunking threshold.</summary>
internal static class GpuBuildingDepthChunkChecks
{
    private const int Width = 192, Height = 128;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly TileId Tile = new(14, 8192, 8192);
    private static readonly GpuTerrainTexture Texture = new(new uint[GpuTerrainTexture.Size * GpuTerrainTexture.Size]);

    internal static void Run()
    {
        int frames = 0, comparisons = 0;
        var center = GpuDepthIndexChecks.Geo(.5, .5);
        var origin = new MapCamera(center.Latitude, center.Longitude, 16, 23, 55);
        var cameras = new[]
        {
            origin with { Pitch = 0 }, origin,
            origin with { Perspective = true, Pitch = 58 },
            MapCamera.FromEye(center.Latitude, center.Longitude, 40, 23, 90, Width, Height),
            MapCamera.FromEye(center.Latitude, center.Longitude, 40, 203, 150, Width, Height),
            MapCamera.FromEye(center.Latitude, center.Longitude, -12, 273, 179.5, Width, Height)
        };
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        {
            using var geometry = ComplexGeometry(style);
            var mesh = new GpuBuildingMesh(geometry);
            Assert(mesh.Ranges[0].VertexCount >= GpuBuildingDepthChunks.MinimumVertices,
                $"The {style} fixture did not exceed the chunk threshold.");
            foreach (var camera in cameras)
            {
                using var frame = Frame(mesh, camera, style);
                comparisons += Compare(frame, $"{style}, pitch{camera.Pitch}, perspective{camera.Perspective}");
                frames++;
            }
            var models = (float[])mesh.Models.Clone();
            for (int index = 0; index < models.Length; index += GpuBuildingMesh.ModelStride) models[index] = -27;
            var lowered = mesh.WithFoundations(models);
            Assert(ReferenceEquals(mesh.DepthChunks, lowered.DepthChunks) &&
                ReferenceEquals(mesh.DepthChunks.Get(mesh, 0), lowered.DepthChunks.Get(lowered, 0)),
                "A foundation-only snapshot rebuilt or changed immutable chunk bounds.");
            using var retained = Frame(mesh, cameras[2], style);
            comparisons += Compare(retained, $"{style} retained A");
            using (var next = Frame(lowered, cameras[3], style)) comparisons += Compare(next, $"{style} lowered B");
            comparisons += Compare(retained, $"{style} retained A after B");
            frames += 3;
        }
        using (var geometry = ComplexGeometry(MapRenderStyle.Pixel))
        {
            var mesh = new GpuBuildingMesh(geometry);
            // Constant homogeneous weights isolate closed near/far boundaries,
            // including the adjacent representable values on either side.
            foreach (float weight in new[] { MathF.BitDecrement(.1f), .1f, MathF.BitIncrement(.1f),
                         MathF.BitDecrement(6f), 6f, MathF.BitIncrement(6f) })
            {
                var camera = cameras[2];
                var transform = new TileProjection(camera, Width, Height, 1).Get(Tile, 0).Transform with
                {
                    PX = 0, PY = 0, PW = weight, InverseFocal = .001f,
                    CosPitch = 0, SinPitch = 1, TargetZ = 1000, Near = .1f, Far = 6
                };
                using var frame = Frame(mesh, camera, MapRenderStyle.Pixel, transform);
                comparisons += Compare(frame, $"constant W={weight:R}");
                frames++;
            }
            foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
            {
                var models = (float[])mesh.Models.Clone();
                models[0] = invalid;
                using var frame = Frame(mesh.WithFoundations(models), cameras[2], MapRenderStyle.Pixel);
                comparisons += Compare(frame, $"nonfinite foundation {invalid}");
                frames++;
            }
        }
        foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
        {
            // Corrupt just one source vertex before metadata exists. Other chunks
            // remain finite and useful; the affected chunk must stay conservative.
            using var geometry = ComplexGeometry(MapRenderStyle.Pixel);
            var mesh = new GpuBuildingMesh(geometry);
            int index = mesh.Ranges[0].FirstVertex + 170;
            var old = mesh.Vertices[index];
            mesh.Vertices[index] = new(invalid, old.Y, old.Length, old.Light,
                old.PerspectiveLight, old.Dx, old.Dy, old.Packed);
            using var frame = Frame(mesh, cameras[2], MapRenderStyle.Pixel);
            comparisons += Compare(frame, $"mixed finite/nonfinite vertex {invalid}");
            frames++;
        }
        StampAndRejectionChecks(cameras[0], ref frames, ref comparisons);
        Console.WriteLine($"Passed {comparisons:N0} exact large-building depth checks across {frames} frames: all styles, " +
            "disconnected concave roofs/holes, clipping boundaries, signed foundations, nonfinite chunks, " +
            "retained frames and pooled projection-stamp rollover.");
    }

    private static void StampAndRejectionChecks(MapCamera camera, ref int frames, ref int comparisons)
    {
        using var geometry = ComplexGeometry(MapRenderStyle.Pixel);
        var mesh = new GpuBuildingMesh(geometry);
        var transform = new TileProjection(camera, Width, Height, 1).Get(Tile, 0).Transform;
        object previousStorage;
        using (var frame = Frame(mesh, camera, MapRenderStyle.Pixel, transform, secondCopy: true))
        {
            var groups = (IList)Field(frame.Depth, "groups");
            var prepare = typeof(GpuSceneDepth).GetMethod("PrepareGroup", Members)!.CreateDelegate<Action<int>>(frame.Depth);
            int Find(int draw, int model) => Enumerable.Range(0, groups.Count).First(index =>
                (int)Field(groups[index]!, "Draw") == draw && (int)Field(groups[index]!, "Model") == model);
            int first = Find(0, 0);
            prepare(first);
            previousStorage = Field(frame.Depth, "storage");
            int stamp = (int)Field(previousStorage, "ProjectionStamp");
            int projected = ((int[])Field(previousStorage, "ProjectedVertexStamps"))
                .Take(mesh.Ranges[0].VertexCount).Count(value => value == stamp);
            Assert(stamp > 0 && projected > 0 && projected * 4 < mesh.Ranges[0].VertexCount * 3,
                "The large-building fixture did not skip substantial offscreen projection work.");
            var group = groups[first]!;
            float bottom = (float)Field(group, "Bottom"), top = (float)Field(group, "Top");
            var chunks = mesh.DepthChunks.Get(mesh, 0);
            var bounds = typeof(GpuSceneDepth).GetMethod("Bounds", Members)!;
            int rejectedTriangles = chunks.Triangles.Count(chunk => !(bool)bounds.Invoke(frame.Depth,
                [chunk.Bounds, chunk.HasBottom ? bottom : top, chunk.HasTop ? top : bottom, transform, false, null, null])!);
            int rejectedEdges = chunks.Edges.Count(chunk => !(bool)bounds.Invoke(frame.Depth,
                [chunk.Bounds, top, top, transform, false, null, null])!);
            Assert(rejectedTriangles > 0 && rejectedEdges > 0,
                $"The chunk fixture did not reject triangle and roof-edge chunks ({rejectedTriangles}/{chunks.Triangles.Length}, {rejectedEdges}/{chunks.Edges.Length}).");
            // The ordinary small-model path overwrites the same local vertex slots.
            // A second transformed copy must clear stale epoch-1 entries on wrap.
            prepare(Find(0, 1));
            previousStorage.GetType().GetField("ProjectionStamp", Members)!.SetValue(previousStorage, int.MaxValue);
            prepare(Find(1, 0));
            Assert((int)Field(previousStorage, "ProjectionStamp") == 1, "Projection-stamp rollover was not exercised.");
            comparisons += Compare(frame, "large→small→large, translated copy and stamp rollover");
            frames++;
            Console.WriteLine($"Chunk fixture projected {projected:N0}/{mesh.Ranges[0].VertexCount:N0} vertices; " +
                $"rejected {rejectedTriangles}/{chunks.Triangles.Length} triangle and {rejectedEdges}/{chunks.Edges.Length} edge chunks.");
        }
        var models = (float[])mesh.Models.Clone();
        for (int index = 0; index < models.Length; index += GpuBuildingMesh.ModelStride) models[index] = 35;
        using var reused = Frame(mesh.WithFoundations(models), camera with { Bearing = 131, Perspective = true }, MapRenderStyle.Pixel);
        Assert(ReferenceEquals(previousStorage, Field(reused.Depth, "storage")), "The scratch-reuse fixture did not reclaim its pooled storage.");
        comparisons += Compare(reused, "pooled scratch with changed foundation and projection");
        Assert((int)Field(previousStorage, "ProjectionStamp") > 1, "Pooled storage reused an old projection epoch.");
        frames++;
    }

    private static int Compare(GpuMapFrame frame, string context)
    {
        var eager = new EagerGpuSceneDepth(frame);
        eager.Retain();
        int comparisons = 0;
        try
        {
            var points = Queries(frame).ToArray();
            foreach (var (x, y) in points)
            {
                foreach (float altitude in new[] { -50f, 4f, 35f, 180f })
                {
                    Assert(frame.Depth.CoversAt(x + .01f, y + .99f, altitude) == eager.CoversAt(x + .01f, y + .99f, altitude),
                        $"First large-building occlusion changed at {x},{y}, h{altitude}: {context}.");
                    comparisons++;
                }
                bool actual = frame.Depth.TryTerrain(x, y, out float z, out float w, out float physical);
                bool expected = eager.TryTerrain(x, y, out float ez, out float ew, out float ep);
                Assert(actual == expected && Exact(z, ez) && Exact(w, ew) && Exact(physical, ep),
                    $"Large-building terrain picking changed at {x},{y}: {context}.");
                Assert(frame.Depth.CoversAt(x + .99f, y + .01f, 65) == eager.CoversAt(x + .99f, y + .01f, 65),
                    $"Large-building subpixel occlusion changed after picking at {x},{y}: {context}.");
                comparisons += 2;
            }
            foreach (var (x, y) in points.Reverse().Take(71))
            {
                Assert(frame.Depth.CoversAt(x + .5f, y + .5f, 17) == eager.CoversAt(x + .5f, y + .5f, 17),
                    "Demand order changed a retained large-building sample.");
                comparisons++;
            }
            return comparisons;
        }
        finally { eager.Release(); }
    }

    private static IEnumerable<(int X, int Y)> Queries(GpuMapFrame frame)
    {
        var points = new HashSet<(int, int)>();
        void Add(int x, int y) { if ((uint)x < Width && (uint)y < Height) points.Add((x, y)); }
        for (int x = 0; x < Width; x++) { Add(x, 0); Add(x, 1); Add(x, Height - 2); Add(x, Height - 1); }
        for (int y = 0; y < Height; y++) { Add(0, y); Add(1, y); Add(Width - 2, y); Add(Width - 1, y); }
        for (int index = 0; index < 1024; index++) Add((index * 97 + 13) % Width, (index * 67 + 5) % Height);
        foreach (int x in new[] { 15, 16, 31, 32, 63, 64, Width / 2 })
        for (int y = 0; y < Height; y += 3) Add(x, y);
        foreach (var draw in frame.Buildings)
        foreach (var range in draw.Mesh.Ranges)
        {
            float foundation = (float)frame.TerrainOptions.LocalAltitude(frame.Camera,
                draw.Mesh.Models[draw.Mesh.Vertices[range.FirstVertex].Model * GpuBuildingMesh.ModelStride]);
            float top = (range.Model.Top + foundation) * frame.PixelsPerMeter;
            // Adjacent roof segments straddle every chunk boundary. Include DDA
            // endpoints and midpoints around their projected raster pixels.
            for (int edge = range.FirstEdge; edge < range.FirstEdge + range.EdgeCount; edge += 64 * 2)
            foreach (int offset in new[] { -2, 0, 2 })
            {
                int index = edge + offset;
                if (index < range.FirstEdge || index + 1 >= range.FirstEdge + range.EdgeCount) continue;
                int first = index * GpuBuildingMesh.EdgeStride, second = first + GpuBuildingMesh.EdgeStride;
                var a = draw.Transform.Vertex(draw.Mesh.RoofEdges[first], draw.Mesh.RoofEdges[first + 1], top).Point;
                var b = draw.Transform.Vertex(draw.Mesh.RoofEdges[second], draw.Mesh.RoofEdges[second + 1], top).Point;
                foreach (float t in new[] { 0f, .5f, 1f })
                {
                    float px = a.X + (b.X - a.X) * t, py = a.Y + (b.Y - a.Y) * t;
                    if (!float.IsFinite(px) || !float.IsFinite(py) || px < -2 || py < -2 || px > Width + 2 || py > Height + 2) continue;
                    int x = (int)MathF.Floor(px), y = (int)MathF.Floor(py);
                    for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) Add(x + dx, y + dy);
                }
            }
        }
        return points.OrderBy(point => ((point.Item1 * 73) ^ (point.Item2 * 151)) % 257).Select(point => (point.Item1, point.Item2));
    }

    private static GpuMapFrame Frame(GpuBuildingMesh mesh, MapCamera camera, MapRenderStyle style,
        TileTransform? supplied = null, bool secondCopy = false)
    {
        var transform = supplied ?? new TileProjection(camera, Width, Height, 1).Get(Tile, 0).Transform;
        float meters = (float)(camera.WorldSize / (MapCamera.EarthCircumference * Math.Cos(camera.Latitude * Math.PI / 180)));
        var vertices = new float[GpuTerrainMesh.Side * GpuTerrainMesh.Side * GpuTerrainMesh.VertexStride];
        for (int y = 0; y < GpuTerrainMesh.Side; y++)
        for (int x = 0; x < GpuTerrainMesh.Side; x++)
        {
            int index = (y * GpuTerrainMesh.Side + x) * GpuTerrainMesh.VertexStride;
            vertices[index] = x * TileGeometry.Extent / GpuTerrainMesh.Segments;
            vertices[index + 1] = y * TileGeometry.Extent / GpuTerrainMesh.Segments;
            vertices[index + 3] = 1;
        }
        TerrainGpuDraw terrain = new(new(new object(), Tile, 0, vertices, 1), Texture, transform);
        GpuBuildingDraw building = new(mesh, transform, 1);
        var frame = new GpuMapFrame(Width, Height, 1, camera, new(ElevationSnapshot.Empty) { RenderStyle = style },
            PixelPalette.Day, meters, [terrain], secondCopy ? [building, new(mesh, transform.Translate(31, -27), 1)] : [building]);
        frame.Depth = new(frame);
        return frame;
    }

    private static TileGeometry ComplexGeometry(MapRenderStyle style)
    {
        var rings = new List<TilePoint[]>();
        for (int row = -4; row <= 4; row++)
        for (int column = -4; column <= 4; column++)
        {
            int left = 2048 + column * 960 - 180, top = 2048 + row * 960 - 160;
            rings.Add(Dense([new(left, top), new(left + 100, top), new(left + 100, top + 40), new(left + 360, top + 40),
                new(left + 360, top + 320), new(left + 140, top + 320), new(left + 140, top + 280), new(left, top + 280)]));
            if ((row + column) % 3 == 0)
                rings.Add([new(left + 40, top + 80), new(left + 40, top + 140), new(left + 80, top + 140), new(left + 80, top + 80)]);
        }
        var large = new MapFeature("building", "", "", "", 1, 4096, GeometryType.Polygon, rings.ToArray(), 120, 17, false, 0, 0, "");
        var small = new MapFeature("building", "", "", "", 2, 4096, GeometryType.Polygon,
            [new TilePoint[] { new(1980, 1980), new(2120, 1980), new(2120, 2120), new(1980, 2120) }], 40, 0, false, 0, 0, "");
        return new(new(Tile, [large, small]), style);

        static TilePoint[] Dense(TilePoint[] ring)
        {
            var result = new TilePoint[ring.Length * 8];
            for (int index = 0; index < ring.Length; index++)
            {
                var from = ring[index]; var to = ring[(index + 1) % ring.Length];
                for (int step = 0; step < 8; step++) result[index * 8 + step] = new(
                    (int)Math.Round(from.X + (to.X - from.X) * step / 8d),
                    (int)Math.Round(from.Y + (to.Y - from.Y) * step / 8d));
            }
            return result;
        }
    }

    private static bool Exact(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    private static object Field(object value, string name) => value.GetType().GetField(name, Members)!.GetValue(value)!;
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
