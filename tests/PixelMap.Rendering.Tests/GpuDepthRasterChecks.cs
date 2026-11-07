using System.Collections;
using System.Reflection;
using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

/// <summary>Isolate projected primitive coverage from mesh generation and clipping.
/// The unchanged eager oracle owns edge coverage, interpolation and source-order semantics.</summary>
internal static class GpuDepthRasterChecks
{
    private const int Width = 34, Height = 34;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly record struct Primitive(SKPoint A, SKPoint B, SKPoint C, bool Terrain = true,
        float PhysicalHeight = 10, float Altitude = 7, int Group = 0);
    private readonly record struct Fixture(string Name, Primitive Triangle, bool Empty = false, bool TighterBins = false);
    private readonly record struct Hit(float Depth, bool HasSurface, bool SurfaceIsTerrain, bool HasTerrain);

    internal static void Run()
    {
        int frames = 0, queries = 0, removed = 0;
        foreach (bool perspective in new[] { false, true })
        foreach (bool terrain in new[] { false, true })
        foreach (bool reverse in new[] { false, true })
        foreach (var fixture in Fixtures())
        {
            var primitive = fixture.Triangle with { Terrain = terrain };
            if (reverse) primitive = primitive with { B = primitive.C, C = primitive.B };
            using var scene = new Scene(Width, Height, perspective);
            scene.Add(primitive);
            if (fixture.Empty)
            {
                Assert(scene.Reference.Count == 1, $"The empty-raster fixture '{fixture.Name}' lost its valid geometry.");
                Assert(scene.Candidate.Count == 0, $"Raster-empty triangle '{fixture.Name}' was retained.");
                removed++;
            }
            if (fixture.TighterBins)
                Assert(scene.Candidate.CellEntriesAt(16, 15) == 0 && scene.Reference.CellEntriesAt(16, 15) > 0,
                    "A primitive ending at a cell edge still entered a cell with no covered pixel center.");
            queries += scene.Compare(AllPixels(Width, Height), $"{fixture.Name}; perspective={perspective}; terrain={terrain}; reverse={reverse}");
            frames++;
        }

        foreach (bool perspective in new[] { false, true })
        foreach (bool model in new[] { false, true })
        foreach (bool reversePreparation in new[] { false, true })
        {
            using var scene = new Scene(Width, Height, perspective);
            Primitive[] layers =
            [
                new(new(2, 2), new(29, 2), new(2, 29), PhysicalHeight: 10, Group: 0),
                new(new(4.05f, 4.05f), new(4.45f, 4.05f), new(4.05f, 4.45f), PhysicalHeight: 20, Group: 1),
                new(new(2, 2), new(29, 2), new(2, 29), PhysicalHeight: 30, Group: 2)
            ];
            foreach (var layer in layers) scene.Reference.Add(layer, perspective);
            foreach (var layer in reversePreparation ? layers.Reverse() : layers) scene.Candidate.Add(layer, perspective);
            if (model)
            {
                scene.Add(new(new(2, 2), new(29, 2), new(2, 29), Terrain: false, Group: 3));
                scene.Add(new(new(8.05f, 8.05f), new(8.45f, 8.05f), new(8.05f, 8.45f), Terrain: false, Group: 4));
            }
            Assert(scene.Candidate.Count < scene.Reference.Count, "Source-order fixture did not remove a non-contributing primitive.");
            queries += scene.Compare(AllPixels(Width, Height),
                $"survivor order; perspective={perspective}; model={model}; reversed preparation={reversePreparation}");
            frames++;
        }

        // Above 2^23, x + .5f no longer represents a half-integer. An oversized
        // synthetic viewport must retain the conservative path rather than rely on
        // the ordinary texture-size lattice. Only a few relevant pixels are queried.
        const int boundary = 1 << 23;
        foreach (bool perspective in new[] { false, true })
        {
            using var scene = new Scene(boundary + 8, 2, perspective);
            scene.Add(new(new(boundary, 0), new(boundary + 6, 0), new(boundary, 2)));
            queries += scene.Compare(Enumerable.Range(boundary - 2, 10).SelectMany(x =>
                Enumerable.Range(0, 2).Select(y => (X: x, Y: y))), $"wide float-center viewport; perspective={perspective}");
            frames++;
        }
        Console.WriteLine($"Projected depth raster checks: {queries:N0} exact comparisons across {frames} frames; " +
            $"{removed} valid raster-empty triangles removed. Half-pixel/cell boundaries, winding, subpixel coverage, " +
            "source-order ties and large-coordinate fallback agree with the frozen eager oracle.");
    }

    private static IEnumerable<Fixture> Fixtures()
    {
        yield return new("subpixel empty box", new(new(.05f, .05f), new(.45f, .05f), new(.05f, .45f)), Empty: true);
        yield return new("empty center column", new(new(10.05f, 0), new(10.45f, 0), new(10.05f, 30)), Empty: true);
        yield return new("empty center row", new(new(0, 10.05f), new(30, 10.05f), new(0, 10.45f)), Empty: true);
        yield return new("sole center misses diagonal", new(new(3.1f, 3.1f), new(3.6f, 3.1f), new(3.1f, 3.6f)), Empty: true);
        yield return new("sole center on exclusive diagonal", new(new(3, 3), new(4, 3), new(3, 4)), Empty: true);
        yield return new("sole center covered", new(new(3.2f, 3.2f), new(3.9f, 3.2f), new(3.2f, 3.9f)));
        yield return new("one column with distant covered rows", new(new(10.1f, 0), new(10.1f, 30), new(10.9f, 30)));
        yield return new("one row with distant covered columns", new(new(0, 10.1f), new(30, 10.1f), new(30, 10.9f)));
        yield return new("exclusive subpixel cell boundary", new(new(15.1f, 15.1f), new(16, 15.1f), new(15.1f, 16)), TighterBins: true);
        foreach (float edge in new[] { .5f, 15.5f, 16f, 16.5f, 33.5f })
        foreach (float value in new[] { MathF.BitDecrement(edge), edge, MathF.BitIncrement(edge) })
        {
            float low = Math.Max(0, edge - 4), high = Math.Min(Width, edge + 4);
            yield return new($"right bound {value:R}", new(new(low, 4), new(value, 4), new(value, 12)));
            yield return new($"left bound {value:R}", new(new(value, 4), new(high, 4), new(value, 12)));
            yield return new($"bottom bound {value:R}", new(new(4, low), new(4, value), new(12, value)));
            yield return new($"top bound {value:R}", new(new(4, value), new(4, high), new(12, value)));
        }
    }

    private static IEnumerable<(int X, int Y)> AllPixels(int width, int height)
    {
        // Backward order exercises the last cell first and avoids relying on a
        // forward query to complete a sparse cell before its boundary is tested.
        for (int y = height - 1; y >= 0; y--)
        for (int x = width - 1; x >= 0; x--) yield return (x, y);
    }

    private sealed class Scene : IDisposable
    {
        private readonly GpuMapFrame frame;
        private readonly EagerGpuSceneDepth eager;
        private readonly bool perspective;
        internal readonly ProjectionAdapter Candidate, Reference;

        internal Scene(int width, int height, bool perspective)
        {
            this.perspective = perspective;
            frame = new(width, height, 1, new(0, 0, 14, 0, 30, perspective),
                new(ElevationSnapshot.Empty, Relief: false), PixelPalette.Day, 1, [], []);
            frame.Depth = new(frame);
            eager = new(frame);
            eager.Retain();
            Candidate = new(frame.Depth);
            Reference = new(eager);
        }

        internal void Add(Primitive primitive)
        {
            Candidate.Add(primitive, perspective);
            Reference.Add(primitive, perspective);
        }

        internal int Compare(IEnumerable<(int X, int Y)> pixels, string context)
        {
            int queries = 0;
            ReadOnlySpan<float> levels = [-10f, 0, 7, 30];
            ReadOnlySpan<SKPoint> offsets = [new(.01f, .99f), new(.49f, .51f), new(.99f, .01f)];
            foreach (var (x, y) in pixels)
            {
                var a = Candidate.Sample(x, y);
                var b = Reference.Sample(x, y);
                if (!Exact(a.Depth, b.Depth) || a.HasSurface != b.HasSurface ||
                    a.SurfaceIsTerrain != b.SurfaceIsTerrain || a.HasTerrain != b.HasTerrain)
                    throw new InvalidOperationException($"Projected sample differs ({context}) at {x},{y}: {a} / {b}.");
                bool found = frame.Depth.TryTerrain(x, y, out float altitude, out float inverse, out float physical);
                bool otherFound = eager.TryTerrain(x, y, out float otherAltitude, out float otherInverse, out float otherPhysical);
                if (found != otherFound || !Exact(altitude, otherAltitude) || !Exact(inverse, otherInverse) || !Exact(physical, otherPhysical))
                    throw new InvalidOperationException($"Projected terrain differs ({context}) at {x},{y}.");
                queries += 2;
                foreach (float level in levels)
                foreach (var offset in offsets)
                {
                    float sx = x + offset.X, sy = y + offset.Y;
                    if (frame.Depth.CoversAt(sx, sy, level) != eager.CoversAt(sx, sy, level))
                        throw new InvalidOperationException($"Projected subpixel coverage differs ({context}) at {sx:R},{sy:R}, level {level}.");
                    queries++;
                }
            }
            return queries;
        }

        public void Dispose()
        {
            eager.Release();
            frame.Dispose();
        }
    }

    private sealed class ProjectionAdapter
    {
        private readonly object depth;
        private readonly MethodInfo project, sample;
        private readonly ConstructorInfo vertex;
        private readonly FieldInfo triangles, cells;
        private readonly FieldInfo? preparingOrder;
        private readonly PropertyInfo nearest, surface, terrainSurface, terrainIndex;
        private readonly int cellSize, columns;

        internal ProjectionAdapter(object depth)
        {
            this.depth = depth;
            Type type = depth.GetType();
            project = type.GetMethod("AddProjectedTriangle", Members)!;
            sample = type.GetMethod("Sample", Members)!;
            vertex = type.GetNestedType("Vertex", BindingFlags.NonPublic)!.GetConstructor(Members, null,
                [typeof(H), typeof(float), typeof(int)], null)!;
            triangles = type.GetField("triangles", Members)!;
            cells = type.GetField("cells", Members)!;
            cellSize = (int?)type.GetField("cellSize", Members)?.GetValue(depth) ?? 16; // Frozen eager grid.
            columns = (int)type.GetField("columns", Members)!.GetValue(depth)!;
            preparingOrder = type.GetField("preparingOrder", Members);
            nearest = sample.ReturnType.GetProperty("Depth", Members)!;
            surface = sample.ReturnType.GetProperty("HasSurface", Members)!;
            terrainSurface = sample.ReturnType.GetProperty("SurfaceIsTerrain", Members)!;
            terrainIndex = sample.ReturnType.GetProperty("TerrainTriangle", Members)!;
        }

        internal int Count => ((ICollection)triangles.GetValue(depth)!).Count;

        internal int CellEntriesAt(int x, int y)
        {
            object? cell = ((Array)cells.GetValue(depth)!).GetValue(y / cellSize * columns + x / cellSize);
            return cell is ICollection entries ? entries.Count : 0;
        }

        internal void Add(Primitive triangle, bool perspective)
        {
            preparingOrder?.SetValue(depth, triangle.Group);
            project.Invoke(depth,
            [
                Vertex(triangle.A, 0, 1), Vertex(triangle.B, 1, perspective ? 2 : 1),
                Vertex(triangle.C, 2, perspective ? .5f : 1), triangle.Terrain
            ]);
            object Vertex(SKPoint p, int index, float w) => vertex.Invoke(
                [new H(p.X * w, p.Y * w, w, triangle.Altitude + index * .25f), triangle.PhysicalHeight, -1]);
        }

        internal Hit Sample(int x, int y)
        {
            object hit = sample.Invoke(depth, [x, y])!;
            return new((float)nearest.GetValue(hit)!, (bool)surface.GetValue(hit)!,
                (bool)terrainSurface.GetValue(hit)!, (int)terrainIndex.GetValue(hit)! >= 0);
        }
    }

    private static bool Exact(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
