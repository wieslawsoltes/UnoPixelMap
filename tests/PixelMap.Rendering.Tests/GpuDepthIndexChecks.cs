using System.Collections;
using System.Reflection;
using PixelMap.Data;
using PixelMap.Rendering;

/// <summary>Compare demand-prepared depth with the unchanged eager implementation.
/// Query permutation deliberately prepares shared cells/groups in different orders.</summary>
internal static class GpuDepthIndexChecks
{
    private const int Width = 245, Height = 147, Pixels = 3;
    private static readonly TileId Tile = new(14, 8192, 8192);
    private static readonly BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    internal static void Run()
    {
        var tiles = Tiles();
        var options = new TerrainOptions(Elevation());
        var center = Geo(.5, .5);
        var origin = new MapCamera(center.Latitude, center.Longitude, 15.4, 23, 55);
        double ground = options.Elevation.SampleSurfaceMeters(center.Latitude, center.Longitude);
        var cameras = new[]
        {
            origin with { Pitch = 0 },
            origin,
            origin with { Perspective = true },
            origin with { Perspective = true, Pitch = 82, Bearing = 131 },
            MapCamera.FromEye(center.Latitude, center.Longitude, ground + 1.7, 23, 90, Width, Height),
            MapCamera.FromEye(center.Latitude, center.Longitude, ground + 7, 203, 82, Width, Height),
            MapCamera.FromEye(center.Latitude, center.Longitude, ground + 180, 93, 65, Width, Height),
            MapCamera.FromEye(center.Latitude, center.Longitude, ground - 35, 13, 150, Width, Height),
            MapCamera.FromEye(center.Latitude, center.Longitude, ground + 32, 273, 179.5, Width, Height),
            origin with { Longitude = origin.Longitude + 360, Bearing = 300, Perspective = true },
        };
        int frames = 0, queries = 0;
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        {
            using var renderer = new PixelMapRenderer();
            foreach (var source in cameras)
            {
                var view = source.OnTerrain(options, Height);
                using var frame = renderer.PrepareGpuFrame(Width, Height, view, tiles, PixelStyle.Day, Pixels, null, options, style);
                Assert(Count(frame.Depth, "triangles") == 0 && Count(frame.Depth, "edges") == 0,
                    "A frame without labels eagerly projected depth primitives.");
                queries += Compare(frame, frames++);
            }

            // Full-viewport near-plane proxies exercise bounded overflow storage.
            var eye = MapCamera.FromEye(center.Latitude, center.Longitude, ground + 20, 37, 89.9, Width * 2, Height * 2).OnTerrain(options, Height * 2);
            MapTile[] huge = [new(Tile, [Building(9001, 600, -100,
                new TilePoint[] { new(-80000, 2000), new(2000, -80000), new(84000, 2000), new(2000, 84000) })])];
            using var large = renderer.PrepareGpuFrame(Width * 2, Height * 2, eye, huge, PixelStyle.Day, Pixels, null, options, style);
            queries += Compare(large, frames++);
            Assert(Count(large.Depth, "largeProxies") > 0, "The full-screen fixture did not exercise bounded overflow proxies.");
            int indexed = ((IEnumerable)Field(large.Depth, "pendingCells")).Cast<object?>().Sum(value => value is ICollection list ? list.Count : 0);
            Assert(indexed <= Count(large.Depth, "groups") * 64, "Coarse depth storage grew with screen-filling proxy area.");
        }

        // A pending frame retains its own meshes, projections, query caches and
        // reference plane after another camera/DEM/style disposes shared geometry.
        using (var renderer = new PixelMapRenderer())
        {
            var view = cameras[4].OnTerrain(options, Height);
            using var first = renderer.PrepareGpuFrame(Width, Height, view, tiles, PixelStyle.Night, Pixels, null, options, MapRenderStyle.Pixel);
            using (var next = renderer.PrepareGpuFrame(Width + 31, Height + 17,
                cameras[6].OnTerrain(new(Elevation(70)), Height + 17), tiles, PixelStyle.Day, 2,
                null, new(Elevation(70)), MapRenderStyle.Roblox))
                queries += Compare(next, frames++);
            renderer.ActivateGpuFrame(first);
            queries += Compare(first, frames++);
            queries += Compare(first, frames++);
        }
        TerrainOrderAndNonfinite(ref frames, ref queries);
        OcclusionBeforePicking(ref frames, ref queries);
        Console.WriteLine($"Passed {queries:N0} exact sparse/eager depth queries across {frames} frames: all styles, projection modes, near-plane crossings, raised/courtyard roofs, signed terrain, wrapped worlds, bounded overflow and retained A→B→A frames.");
    }

    private static void OcclusionBeforePicking(ref int frames, ref int queries)
    {
        var center = Geo(.5, .5);
        var camera = new MapCamera(center.Latitude, center.Longitude, 16, 0, 0);
        MapTile[] tiles = [new(Tile, Enumerable.Range(0, 48).Select(index =>
            Building((ulong)(index + 1), 12 + index * 3, 0, Box(800, 800, 3300, 3300))).ToArray())];
        using var renderer = new PixelMapRenderer();
        using var frame = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, Pixels);
        var reference = new EagerGpuSceneDepth(frame);
        reference.Retain();
        try
        {
            int x = frame.ArtWidth / 2, y = frame.ArtHeight / 2;
            Assert(frame.Depth.CoversAt(x + .23f, y + .71f, 4) && reference.CoversAt(x + .23f, y + .71f, 4),
                "The layered-building fixture failed to cover its first label.");
            var groups = ((IEnumerable)Field(frame.Depth, "groups")).Cast<object>().ToArray();
            int models = groups.Count(group => (int)Field(group, "Model") >= 0);
            int prepared = groups.Count(group => (int)Field(group, "Model") >= 0 && (int)Field(group, "FirstTriangle") >= 0);
            Assert(models >= 40 && prepared > 0 && prepared < models,
                "An already-covered label still prepared every overlapping building.");
            // The first successful Boolean query leaves higher layers unprepared.
            // A different altitude and a subsequent terrain pick must still see
            // those omitted layers; neither a partial hit nor a complete-cell flag
            // may escape the early-return path.
            foreach (float altitude in new[] { 4f, 65f, 170f })
            {
                Assert(frame.Depth.CoversAt(x + .99f, y + .01f, altitude) == reference.CoversAt(x + .99f, y + .01f, altitude),
                    "An early occlusion result hid an unprepared higher layer.");
                queries++;
            }
            queries += Compare(frame, frames++);
        }
        finally { reference.Release(); }
    }

    private static void TerrainOrderAndNonfinite(ref int frames, ref int queries)
    {
        const int width = 192, height = 96;
        var center = Geo(.5, .5);
        var texture = new GpuTerrainTexture(new uint[GpuTerrainTexture.Size * GpuTerrainTexture.Size]);
        foreach (bool perspective in new[] { false, true })
        {
            var camera = new MapCamera(center.Latitude, center.Longitude, 15.4, 0, 0, perspective);
            var transform = new TileProjection(camera, width, height, 1).Get(Tile, 0).Transform;
            float meters = (float)(camera.WorldSize / (MapCamera.EarthCircumference * Math.Cos(camera.Latitude * Math.PI / 180)));
            // Relief off makes all layers exactly coplanar, but their physical
            // heights differ. Small layers enter ordinary cells before the first
            // layer's overflow proxy: preparation order opposes source draw order.
            TerrainGpuDraw[] layers = [
                new(new(new object(), Tile, 0, Vertices(1, 10), 1), texture, transform),
                new(new(new object(), Tile, 0, Vertices(.04f, 20), 1), texture, transform),
                new(new(new object(), Tile, 0, Vertices(.04f, 30), 1), texture, transform)
            ];
            using var frame = new GpuMapFrame(width, height, 1, camera,
                new(ElevationSnapshot.Empty, Relief: false), PixelPalette.Day, meters, layers, []);
            frame.Depth = new(frame);
            Assert(Count(frame.Depth, "largeProxies") > 0, "Terrain tie fixture did not exercise an overflow proxy.");
            Assert(frame.Depth.TryTerrain(width / 2, height / 2, out _, out _, out float physical) && Exact(physical, 30),
                "A late-prepared earlier terrain layer won an equal-depth physical-height tie.");
            queries += Compare(frame, frames++);
        }

        // Preserve the eager finite-depth gate even when a source mesh contains
        // non-finite coordinates/heights and its box must use conservative overflow.
        foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
        foreach (int component in new[] { 0, 2 })
        {
            var camera = new MapCamera(center.Latitude, center.Longitude, 15.4, 0, 55, true);
            var transform = new TileProjection(camera, width, height, 1).Get(Tile, 0).Transform;
            var vertices = Vertices(1, 10);
            for (int i = component; i < vertices.Length; i += GpuTerrainMesh.VertexStride) vertices[i] = invalid;
            TerrainGpuDraw[] draws = [new(new(new object(), Tile, 0, vertices, 1), texture, transform)];
            using var frame = new GpuMapFrame(width, height, 1, camera,
                new(ElevationSnapshot.Empty), PixelPalette.Day, 1, draws, []);
            frame.Depth = new(frame);
            queries += Compare(frame, frames++);
        }

        static float[] Vertices(float scale, float physicalHeight)
        {
            var values = new float[GpuTerrainMesh.Side * GpuTerrainMesh.Side * GpuTerrainMesh.VertexStride];
            for (int y = 0; y < GpuTerrainMesh.Side; y++)
            for (int x = 0; x < GpuTerrainMesh.Side; x++)
            {
                int offset = (y * GpuTerrainMesh.Side + x) * GpuTerrainMesh.VertexStride;
                values[offset] = ((x / (float)GpuTerrainMesh.Segments - .5f) * scale + .5f) * TileGeometry.Extent;
                values[offset + 1] = ((y / (float)GpuTerrainMesh.Segments - .5f) * scale + .5f) * TileGeometry.Extent;
                values[offset + 2] = physicalHeight;
                values[offset + 3] = 1;
            }
            return values;
        }
    }

    private static int Compare(GpuMapFrame frame, int sequence)
    {
        float low = frame.MinimumAltitude, high = frame.MaximumAltitude;
        var reference = new EagerGpuSceneDepth(frame);
        reference.Retain();
        try
        {
            Assert(Exact(low, frame.MinimumAltitude) && Exact(high, frame.MaximumAltitude),
                "Sparse bounds changed the GL altitude normalization range.");
            int width = frame.ArtWidth, height = frame.ArtHeight;
            var pixels = Enumerable.Range(0, width * height).ToArray();
            var random = new Random(0x9e37 + sequence);
            random.Shuffle(pixels);
            int queries = 0;
            foreach (int pixel in pixels)
            {
                int x = pixel % width, y = pixel / width;
                // Boolean visibility is normally queried by labels before picking.
                // Exercise that order at distinct subpixel positions and heights,
                // then demand the complete terrain result from the same pixel.
                ReadOnlySpan<float> firstLevels = [-50f, 4f, 35f, 160f];
                foreach (float firstLevel in firstLevels)
                {
                    float firstX = x + (firstLevel == 4 ? .99f : .01f);
                    float firstY = y + (firstLevel == 4 ? .01f : .99f);
                    if (frame.Depth.CoversAt(firstX, firstY, firstLevel) != reference.CoversAt(firstX, firstY, firstLevel))
                        throw new InvalidOperationException($"First occlusion differs at frame {sequence}, pixel {x},{y}, altitude {firstLevel}.");
                    queries++;
                }
                bool a = frame.Depth.TryTerrain(x, y, out float altitude, out float inverse, out float physical);
                bool b = reference.TryTerrain(x, y, out float otherAltitude, out float otherInverse, out float otherPhysical);
                if (a != b || !Exact(altitude, otherAltitude) || !Exact(inverse, otherInverse) || !Exact(physical, otherPhysical))
                    throw new InvalidOperationException($"Sparse terrain depth differs at frame {sequence}, pixel {x},{y}: {a}/{b}, altitude {altitude:R}/{otherAltitude:R}, inverse {inverse:R}/{otherInverse:R}, physical {physical:R}/{otherPhysical:R}.");
                ReadOnlySpan<float> levels = [-50f, 4f, 35f, 160f];
                foreach (float z in levels)
                {
                    float sx = x + .23f, sy = y + .71f;
                    bool actual = frame.Depth.CoversAt(sx, sy, z), expected = reference.CoversAt(sx, sy, z);
                    if (actual != expected)
                        throw new InvalidOperationException($"Sparse occlusion differs at frame {sequence}, pixel {x},{y}, altitude {z}: {actual}/{expected}.");
                    queries++;
                }
                queries++;
            }
            // Query again after every group has been demanded, now in reverse order.
            foreach (int pixel in pixels.Reverse().Take(71))
            {
                int x = pixel % width, y = pixel / width;
                Assert(frame.Depth.CoversAt(x + .5f, y + .5f, 27) == reference.CoversAt(x + .5f, y + .5f, 27),
                    "Depth query order changed a retained sample.");
                queries++;
            }
            return queries;
        }
        finally { reference.Release(); }
    }

    internal static MapTile[] Tiles()
    {
        var tiles = new List<MapTile>();
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            var features = new List<MapFeature>();
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                int left = 400 + column * 850, top = 400 + row * 850;
                ulong id = (ulong)(1 + (y + 1) * 1000 + (x + 1) * 100 + row * 4 + column);
                var outline = Box(left, top, left + 580, top + 530);
                var hole = Box(left + 130, top + 130, left + 410, top + 390).Reverse().ToArray();
                features.Add(Building(id, 12 + (id % 7) * 23, id % 3 == 0 ? 9 : 0,
                    id % 2 == 0 ? [outline, hole] : [outline]));
            }
            // Coplanar and intersecting parts exercise order-dependent ties and rims.
            features.Add(Building((ulong)(9000 + (y + 1) * 3 + x + 1), 45, 7, Box(1800, 1600, 2500, 2300)));
            features.Add(Building((ulong)(9100 + (y + 1) * 3 + x + 1), 45, 7, Box(2000, 1900, 2650, 2400)));
            tiles.Add(new(Tile with { X = Tile.X + x, Y = Tile.Y + y }, features));
        }
        return tiles.ToArray();
    }

    internal static ElevationSnapshot Elevation(double offset = 0)
    {
        var tiles = new List<ElevationTile>();
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            const int size = 32;
            var values = new float[size * size];
            for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
                values[row * size + column] = (float)(offset - 20 + 65 * Math.Sin((column / (double)size + x) * Math.PI) *
                    Math.Cos((row / (double)size + y) * Math.PI) + column);
            tiles.Add(new(Tile with { X = Tile.X + x, Y = Tile.Y + y }, size, values));
        }
        return new(tiles);
    }

    internal static (double Latitude, double Longitude) Geo(double x, double y) =>
        (Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (Tile.Y + y) / (1 << Tile.Z)))) * 180 / Math.PI,
            (Tile.X + x) * 360 / (1 << Tile.Z) - 180);

    private static TilePoint[] Box(int left, int top, int right, int bottom) =>
        [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];

    private static MapFeature Building(ulong id, double top, double bottom, params TilePoint[][] rings) =>
        new("building", "", "", "", id, 4096, GeometryType.Polygon, rings, top, bottom, false, 0, 0, "");

    private static bool Exact(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    private static object Field(object value, string name) => value.GetType().GetField(name, Members)!.GetValue(value)!;
    private static int Count(object value, string name) => ((ICollection)Field(value, name)).Count;
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
