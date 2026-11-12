using System.Collections;
using System.Reflection;
using PixelMap.Data;
using PixelMap.Rendering;

/// <summary>Terrain-only demand must preserve the eager terrain winner without
/// publishing an incomplete model-occlusion sample or preparing unseen models.</summary>
internal static class GpuTerrainPickingChecks
{
    private const int Width = 192, Height = 128;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly TileId Tile = new(14, 8192, 8192);
    private static readonly GpuTerrainTexture Texture = new(new uint[GpuTerrainTexture.Size * GpuTerrainTexture.Size]);

    internal static void Run()
    {
        int frames = 0, comparisons = 0;
        DemandIsolation(ref frames, ref comparisons);
        TerrainOrderAndFallbacks(ref frames, ref comparisons);
        var center = GpuDepthIndexChecks.Geo(.5, .5);
        var terrain = new TerrainOptions(GpuDepthIndexChecks.Elevation(), Exaggeration: 1.7);
        double ground = terrain.Elevation.SampleSurfaceMeters(center.Latitude, center.Longitude);
        var origin = new MapCamera(center.Latitude, center.Longitude, 15.4, 23, 55);
        var cameras = new[]
        {
            origin with { Pitch = 0 }, origin, origin with { Perspective = true },
            MapCamera.FromEye(center.Latitude, center.Longitude, ground + 1.7, 23, 90, Width, Height),
            MapCamera.FromEye(center.Latitude, center.Longitude, ground - 35, 203, 150, Width, Height),
            origin with { Longitude = origin.Longitude + 360, Bearing = 300, Perspective = true }
        };
        var tiles = GpuDepthIndexChecks.Tiles();
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        {
            using var renderer = new PixelMapRenderer();
            foreach (var camera in cameras)
            {
                using var frame = renderer.PrepareGpuFrame(Width, Height, camera.OnTerrain(terrain, Height),
                    tiles, PixelStyle.Day, 2, null, terrain, style);
                comparisons += CompareMixed(frame, renderer, $"{style}, pitch {camera.Pitch}, wrapped {camera.Longitude}", fresh: true);
                frames++;
            }
        }
        using (var renderer = new PixelMapRenderer())
        {
            using var retained = renderer.PrepareGpuFrame(Width, Height, cameras[2].OnTerrain(terrain, Height),
                tiles, PixelStyle.Night, 2, null, terrain, MapRenderStyle.Pixel);
            comparisons += CompareMixed(retained, renderer, "retained A", fresh: true);
            var changed = new TerrainOptions(GpuDepthIndexChecks.Elevation(70), Exaggeration: 2.3);
            using (var next = renderer.PrepareGpuFrame(Width + 31, Height + 17, cameras[4].OnTerrain(changed, Height + 17),
                tiles, PixelStyle.Day, 1, null, changed, MapRenderStyle.Roblox))
                comparisons += CompareMixed(next, renderer, "retained B", fresh: true);
            renderer.ActivateGpuFrame(retained);
            comparisons += CompareMixed(retained, renderer, "retained A after B", fresh: false);
            frames += 3;
        }
        NonfiniteTerrain(ref frames, ref comparisons);
        Console.WriteLine($"Passed {comparisons:N0} exact terrain-picking and mixed-occlusion checks across {frames} frames: " +
            "terrain-only model isolation, full-cache/cell-budget isolation, public geographic picks, all styles, " +
            "source-order ties, index fallback, nonfinite terrain, retained frames and pooled storage reuse.");
    }

    private static void DemandIsolation(ref int frames, ref int comparisons)
    {
        using var geometry = LayeredBuildings();
        var mesh = new GpuBuildingMesh(geometry);
        object previousStorage;
        using (var frame = LayeredFrame(mesh))
        {
            previousStorage = Field(frame.Depth, "storage");
            var eager = new EagerGpuSceneDepth(frame);
            eager.Retain();
            try
            {
                int x = frame.ArtWidth / 2, y = frame.ArtHeight / 2;
                int models = ModelCount(frame.Depth, prepared: false);
                Assert(models == 48, "The pick-isolation fixture did not retain all overlapping models.");
                for (int index = 0; index < 32; index++)
                    comparisons += ComparePick(frame, eager, x + index % 4, y + index / 4, "pick-only repeated cell");
                Assert(ModelCount(frame.Depth, prepared: true) == 0 && Count(frame.Depth, "edges") == 0,
                    "A terrain-only query projected a building or roof rim.");
                Assert(Count(frame.Depth, "samples") == 0 && !((bool[])Field(frame.Depth, "preparedCells")).Any(value => value) &&
                    !((byte[])Field(frame.Depth, "cellQueries")).Any(value => value != 0),
                    "Terrain-only queries changed complete occlusion samples or the whole-cell query budget.");
                Assert(frame.Depth.CoversAt(x + .23f, y + .71f, 4) && eager.CoversAt(x + .23f, y + .71f, 4),
                    "A partial terrain sample hid an overlapping model from the first occlusion query.");
                comparisons++;
                int first = ModelCount(frame.Depth, prepared: true);
                Assert(first > 0 && first < models, "The first occlusion query did not leave unseen models for later demand.");
                comparisons += ComparePick(frame, eager, x, y, "pick after early opaque cover");
                Assert(ModelCount(frame.Depth, prepared: true) == first,
                    "A pick following early opaque cover demanded the remaining models.");
                Assert(frame.Depth.CoversAt(x + .99f, y + .01f, 80) == eager.CoversAt(x + .99f, y + .01f, 80),
                    "Picking suppressed a later higher-altitude model query.");
                comparisons++;
                Assert(ModelCount(frame.Depth, prepared: true) > first,
                    "The altitude-change fixture did not demand a higher building.");
                Assert(!frame.Depth.CoversAt(x + .01f, y + .99f, 180) && !eager.CoversAt(x + .01f, y + .99f, 180),
                    "The complete-sample fixture did not resolve an uncovered altitude.");
                comparisons++;
                Assert(ModelCount(frame.Depth, prepared: true) == models && Count(frame.Depth, "samples") > 0,
                    "A full occlusion query failed to demand every contributing model and cache its complete answer.");
                int triangles = Count(frame.Depth, "triangles"), samples = Count(frame.Depth, "samples");
                comparisons += ComparePick(frame, eager, x, y, "reuse full cached hit");
                Assert(Count(frame.Depth, "triangles") == triangles && Count(frame.Depth, "samples") == samples,
                    "A terrain pick changed an already complete cached pixel.");
                Console.WriteLine($"Pick-only fixture left all {models} overlapping models unprojected; " +
                    $"the first occlusion query prepared {first}, the complete query prepared {models}.");
            }
            finally { eager.Release(); }
            frames++;
        }
        // Reclaim the just-released workspace with different source heights. No
        // stale sample, complete-cell bit or query count may survive its return.
        using (var reused = LayeredFrame(mesh, physicalHeight: 110))
        {
            Assert(ReferenceEquals(previousStorage, Field(reused.Depth, "storage")),
                "The picking fixture did not exercise actual pooled storage reuse.");
            comparisons += CompareMixed(reused, null, "pooled terrain-only frame", fresh: true);
            Assert(reused.Depth.TryTerrain(Width / 2, Height / 2, out _, out _, out float physical) && Exact(physical, 110),
                "A pooled workspace retained an earlier terrain answer.");
            comparisons++;
            frames++;
        }
        using (var empty = LayeredFrame(mesh, noTerrain: true))
        {
            Assert(!empty.Depth.TryTerrain(Width / 2, Height / 2, out float z, out float inverse, out float physical) &&
                Exact(z, 0) && Exact(inverse, 0) && Exact(physical, 0), "A model-only frame returned terrain.");
            Assert(ModelCount(empty.Depth, prepared: true) == 0 && Count(empty.Depth, "samples") == 0,
                "An absent-terrain answer projected models or poisoned full occlusion samples.");
            comparisons += CompareMixed(empty, null, "models without terrain", fresh: true);
            frames++;
        }
    }

    private static void TerrainOrderAndFallbacks(ref int frames, ref int comparisons)
    {
        using var geometry = LayeredBuildings();
        var mesh = new GpuBuildingMesh(geometry);
        foreach (bool perspective in new[] { false, true })
        foreach (int fallback in new[] { 0, 1, 2, 3 })
        {
            using var frame = LayeredFrame(mesh, perspective);
            Assert(Count(frame.Depth, "largeProxies") > 0 &&
                ((IEnumerable)Field(frame.Depth, "pendingCells")).Cast<object?>().Any(value => value is ICollection { Count: > 0 }),
                "The terrain-order fixture did not combine local and overflow groups.");
            if ((fallback & 1) != 0)
            {
                SetField(frame.Depth, "hasExactIndex", false);
                ClearLists(Field(frame.Depth, "cells"));
            }
            if ((fallback & 2) != 0)
            {
                // Simulate a budget-exhausted prefix by removing all coarse
                // memberships. The valid later layer must still be found.
                SetField(frame.Depth, "hasCoarseIndex", false);
                ClearLists(Field(frame.Depth, "pendingCells"));
                ((IList)Field(frame.Depth, "largeProxies")).Clear();
            }
            Assert(frame.Depth.TryTerrain(Width / 2, Height / 2, out _, out _, out float physical) && Exact(physical, 30),
                $"Terrain source order changed with perspective={perspective}, fallback={fallback}.");
            comparisons += CompareMixed(frame, null, $"terrain ties, perspective={perspective}, fallback={fallback}", fresh: true);
            frames++;
        }
    }

    private static void NonfiniteTerrain(ref int frames, ref int comparisons)
    {
        using var geometry = LayeredBuildings();
        var mesh = new GpuBuildingMesh(geometry);
        foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
        foreach (int component in new[] { 0, 2 })
        {
            var vertices = Vertices(.04f, 30);
            for (int index = component; index < vertices.Length; index += GpuTerrainMesh.VertexStride) vertices[index] = invalid;
            using var frame = LayeredFrame(mesh, perspective: true, lastVertices: vertices);
            comparisons += CompareMixed(frame, null, $"nonfinite terrain {invalid}, component={component}", fresh: true);
            frames++;
        }
    }

    private static int CompareMixed(GpuMapFrame frame, PixelMapRenderer? renderer, string context, bool fresh)
    {
        var eager = new EagerGpuSceneDepth(frame);
        eager.Retain();
        using var surface = new TerrainSurface();
        surface.ActivateGpu(frame);
        try
        {
            int comparisons = 0;
            var points = Queries(frame).ToArray();
            if (fresh)
            {
                int models = ModelCount(frame.Depth, prepared: true), samples = Count(frame.Depth, "samples");
                var complete = (bool[])((bool[])Field(frame.Depth, "preparedCells")).Clone();
                var queries = (byte[])((byte[])Field(frame.Depth, "cellQueries")).Clone();
                foreach (var (x, y) in points.Take(64)) comparisons += ComparePick(frame, eager, x, y, context);
                Assert(ModelCount(frame.Depth, prepared: true) == models && Count(frame.Depth, "samples") == samples &&
                    complete.SequenceEqual((bool[])Field(frame.Depth, "preparedCells")) &&
                    queries.SequenceEqual((byte[])Field(frame.Depth, "cellQueries")),
                    $"Terrain picking changed model demand or full-sample state: {context}.");
            }
            for (int index = 0; index < points.Length; index++)
            {
                var (x, y) = points[index];
                if ((index & 1) == 0) comparisons += ComparePick(frame, eager, x, y, context);
                foreach (float altitude in new[] { -50f, 4f, 35f, 180f })
                {
                    float sx = x + ((index & 1) == 0 ? .01f : .99f), sy = y + ((index & 1) == 0 ? .99f : .01f);
                    Assert(frame.Depth.CoversAt(sx, sy, altitude) == eager.CoversAt(sx, sy, altitude),
                        $"Mixed-query occlusion differs at {x},{y}, altitude={altitude}, {context}.");
                    comparisons++;
                    comparisons += ComparePick(frame, eager, x, y, context);
                }
                double logicalX = (x + .37) * frame.PixelSize, logicalY = (y + .83) * frame.PixelSize;
                bool expected = ReferencePick(frame, eager, logicalX, logicalY, out var reference);
                bool actual = surface.TryPick(logicalX, logicalY, frame.Width, frame.Height, frame.PixelSize, out var hit);
                Assert(actual == expected && Exact(hit, reference), $"GPU geographic pick differs at {x},{y}: {context}.");
                comparisons++;
                if (renderer is not null)
                {
                    bool inside = logicalX >= 0 && logicalY >= 0 && logicalX < frame.Width && logicalY < frame.Height;
                    actual = renderer.TryPickTerrain(logicalX, logicalY, frame.Width, frame.Height, frame.Camera, out hit);
                    Assert(actual == (inside && expected) && Exact(hit, inside ? reference : default),
                        $"Public geographic pick differs at {x},{y}: {context}.");
                    comparisons++;
                }
            }
            foreach (var (x, y) in points.Reverse().Take(71)) comparisons += ComparePick(frame, eager, x, y, context);
            return comparisons;
        }
        finally { eager.Release(); }
    }

    private static int ComparePick(GpuMapFrame frame, EagerGpuSceneDepth eager, int x, int y, string context)
    {
        bool actual = frame.Depth.TryTerrain(x, y, out float altitude, out float inverse, out float physical);
        bool expected = eager.TryTerrain(x, y, out float otherAltitude, out float otherInverse, out float otherPhysical);
        Assert(actual == expected && Exact(altitude, otherAltitude) && Exact(inverse, otherInverse) && Exact(physical, otherPhysical),
            $"Terrain pick differs at {x},{y}, {context}: {actual}/{expected}, " +
            $"z={altitude:R}/{otherAltitude:R}, iw={inverse:R}/{otherInverse:R}, physical={physical:R}/{otherPhysical:R}.");
        return 1;
    }

    // Frozen geographic reconstruction from TerrainSurface.TryPick at 16048b7,
    // supplied by the independent eager projector instead of the candidate path.
    private static bool ReferencePick(GpuMapFrame frame, EagerGpuSceneDepth eager, double screenX, double screenY, out TerrainHit hit)
    {
        hit = default;
        int pixel = frame.PixelSize;
        int x = (int)Math.Floor(screenX / pixel), y = (int)Math.Floor(screenY / pixel);
        if (!eager.TryTerrain(x, y, out float altitude, out float inverse, out float physical)) return false;
        double sx = x + .5 - frame.Width * .5 / pixel, sy = y + .5 - frame.Height * .5 / pixel;
        var camera = frame.Camera;
        var (sp, cp) = Math.SinCos(camera.PitchRadians);
        double z = altitude - camera.TargetPixels / pixel;
        double u = sx / inverse;
        double v = !camera.Perspective || Math.Abs(cp) >= Math.Abs(sp) ? (sy / inverse + z * sp) / cp
            : (MapCamera.Focal(frame.Height) / pixel * (1 - 1 / inverse) - z * cp) / sp;
        var (sin, cos) = Math.SinCos(camera.Bearing * Math.PI / 180);
        double world = camera.WorldSize / pixel;
        double wx = camera.CenterX + (u * cos - v * sin) / world, wy = camera.CenterY + (u * sin + v * cos) / world;
        if (wy is < 0 or > 1) return false;
        wx -= Math.Floor(wx);
        hit = new(Math.Atan(Math.Sinh(Math.PI * (1 - 2 * wy))) * 180 / Math.PI, wx * 360 - 180, physical);
        return true;
    }

    private static IEnumerable<(int X, int Y)> Queries(GpuMapFrame frame)
    {
        var points = new HashSet<(int X, int Y)>();
        int width = frame.ArtWidth, height = frame.ArtHeight;
        for (int index = 0; index < 32; index++) points.Add((width / 2 + index % 4, height / 2 + index / 4));
        foreach (int x in new[] { int.MinValue, -1, 0, 1, 15, 16, width - 1, width, int.MaxValue })
        foreach (int y in new[] { -1, 0, 1, 15, 16, height / 2, height - 1, height }) points.Add((x, y));
        for (int index = 0; index < 768; index++) points.Add(((index * 97 + 13) % width, (index * 67 + 5) % height));
        return points;
    }

    private static GpuMapFrame LayeredFrame(GpuBuildingMesh mesh, bool perspective = false,
        float physicalHeight = 30, bool noTerrain = false, float[]? lastVertices = null)
    {
        var center = GpuDepthIndexChecks.Geo(.5, .5);
        var camera = new MapCamera(center.Latitude, center.Longitude, 15.4, 0, 0, perspective);
        var transform = new TileProjection(camera, Width, Height, 1).Get(Tile, 0).Transform;
        TerrainGpuDraw Draw(float[] vertices) => new(new(new object(), Tile, 0, vertices, 1), Texture, transform);
        TerrainGpuDraw[] terrain = noTerrain ? [] :
            [Draw(Vertices(1, 10)), Draw(Vertices(.04f, 20)), Draw(lastVertices ?? Vertices(.04f, physicalHeight))];
        float meters = (float)(camera.WorldSize / (MapCamera.EarthCircumference * Math.Cos(camera.Latitude * Math.PI / 180)));
        var frame = new GpuMapFrame(Width, Height, 1, camera, new(ElevationSnapshot.Empty, Relief: false),
            PixelPalette.Day, meters, terrain, [new(mesh, transform, 1)]);
        frame.Depth = new(frame);
        return frame;
    }

    private static float[] Vertices(float scale, float physicalHeight)
    {
        var vertices = new float[GpuTerrainMesh.Side * GpuTerrainMesh.Side * GpuTerrainMesh.VertexStride];
        for (int y = 0; y < GpuTerrainMesh.Side; y++)
        for (int x = 0; x < GpuTerrainMesh.Side; x++)
        {
            int index = (y * GpuTerrainMesh.Side + x) * GpuTerrainMesh.VertexStride;
            vertices[index] = ((x / (float)GpuTerrainMesh.Segments - .5f) * scale + .5f) * TileGeometry.Extent;
            vertices[index + 1] = ((y / (float)GpuTerrainMesh.Segments - .5f) * scale + .5f) * TileGeometry.Extent;
            vertices[index + 2] = physicalHeight;
            vertices[index + 3] = 1;
        }
        return vertices;
    }

    private static TileGeometry LayeredBuildings() => new(new(Tile, Enumerable.Range(0, 48).Select(index =>
        new MapFeature("building", "", "", "", (ulong)(index + 1), 4096, GeometryType.Polygon,
            [new TilePoint[] { new(800, 800), new(3300, 800), new(3300, 3300), new(800, 3300) }],
            12 + index * 3, 0, false, 0, 0, "")).ToArray()));

    private static int ModelCount(GpuSceneDepth depth, bool prepared) => ((IEnumerable)Field(depth, "groups")).Cast<object>()
        .Count(group => (int)Field(group, "Model") >= 0 && (!prepared || (int)Field(group, "FirstTriangle") >= 0));
    private static void ClearLists(object value)
    {
        foreach (object? entry in (IEnumerable)value) if (entry is IList list) list.Clear();
    }
    private static object Field(object value, string name) => value.GetType().GetField(name, Members)!.GetValue(value)!;
    private static void SetField(object value, string name, object fieldValue) => value.GetType().GetField(name, Members)!.SetValue(value, fieldValue);
    private static int Count(object value, string name) => ((ICollection)Field(value, name)).Count;
    private static bool Exact(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    private static bool Exact(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    private static bool Exact(TerrainHit a, TerrainHit b) => Exact(a.Latitude, b.Latitude) && Exact(a.Longitude, b.Longitude) &&
        Exact(a.ElevationMeters, b.ElevationMeters);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
