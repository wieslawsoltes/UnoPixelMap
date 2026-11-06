using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

internal static class TerrainMeshChecks
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Segments = 16, Side = Segments + 1;
    private static readonly TileId Hill = new(14, 8192, 8192);

    internal static void Run(string[]? arguments = null)
    {
        ExactStencils();
        LayoutTransitions();
        if (arguments?.FirstOrDefault(value => value.StartsWith("--terrain-output=", StringComparison.Ordinal)) is { } output)
            Benchmark(Path.GetFullPath(output["--terrain-output=".Length..]));
    }

    private static void ExactStencils()
    {
        TileId[] ids = [.. new[] { 0, 1, 3, 12, 14, 22 }.SelectMany(zoom =>
            new[] { 0, (1 << zoom) / 2, (1 << zoom) - 1 }.Distinct().SelectMany(x =>
                new[] { 0, (1 << zoom) / 2, (1 << zoom) - 1 }.Distinct().Select(y => new TileId(zoom, x, y))))];
        int vertices = 0;
        foreach (var elevation in new[] { ElevationSnapshot.Empty, Heights(0), Heights(150, voids: true) })
        {
            using var surface = new Surface(elevation);
            foreach (var id in ids)
            {
                object actual = surface.Mesh(id);
                var reference = Reference(elevation, id);
                Assert(Exact(reference.Heights, Field<float[]>(actual, "Heights")) &&
                    Exact(reference.Light, Field<float[]>(actual, "Light")),
                    $"Terrain sampling changed height or lighting bits at {id}.");
                Assert(ReferenceEquals(actual, surface.Mesh(id)), "A resident terrain patch rebuilt its source mesh.");
                vertices += Side * Side;
            }
        }
        Console.WriteLine($"Passed {vertices:N0} exact terrain height/lighting pairs across zoom 0–22, signed DEMs, void fallback, ocean masking, longitude wrap and polar borders.");
    }

    private static void LayoutTransitions()
    {
        const int width = 317, height = 211;
        var first = new TerrainOptions(Heights(0));
        var second = new TerrainOptions(Heights(150, voids: true));
        var center = Point(Hill, .5, .5);
        var camera = new MapCamera(center.Latitude, center.Longitude, 14.4, 23, 55, true);
        MapTile[] tiles = [new(Hill, [])];
        using var retained = new PixelMapRenderer();
        int cases = 0;
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        {
            Compare(camera, first, style, width, height);
            Compare(camera with { Bearing = camera.Bearing + .00001 }, first, style, width, height);
            Compare(camera with { Zoom = camera.Zoom + .8 }, first, style, width, height);
            Compare(camera with { Pitch = 72 }, first, style, width + 20, height + 10);
            Compare(camera, second, style, width, height);
            Compare(camera, first, style, width, height);
        }

        // A CPU frame updates heights without necessarily touching its older retained
        // GPU meshes. Returning to GPU with an unchanged layout must validate those meshes.
        Compare(camera, first, MapRenderStyle.Pixel, width, height);
        using (var bitmap = new SKBitmap(width, height))
        using (var canvas = new SKCanvas(bitmap))
            retained.Draw(canvas, width, height, camera.OnTerrain(second, height), tiles,
                PixelStyle.Day, 4, null, false, false, second);
        Compare(camera, second, MapRenderStyle.Pixel, width, height);

        foreach (double longitude in new[] { 179.99, -179.99 })
            Compare(new(12, longitude, 1.1, 31, 55, true), first, MapRenderStyle.Pixel, width, height);
        Console.WriteLine($"Passed {cases} exact retained terrain layouts across motion, LOD, style, DEM, CPU/GPU handoffs, resizing and repeated worlds.");

        void Compare(MapCamera source, TerrainOptions options, MapRenderStyle style, int w, int h)
        {
            var view = source.OnTerrain(options, h);
            using var expected = new PixelMapRenderer();
            using var a = retained.PrepareGpuFrame(w, h, view, tiles, PixelStyle.Day, 4, null, options, style);
            using var b = expected.PrepareGpuFrame(w, h, view, tiles, PixelStyle.Day, 4, null, options, style);
            Assert(Hash((Array)Property(a, "Terrain")) == Hash((Array)Property(b, "Terrain")),
                $"A retained terrain layout changed geometry after {style} transition {cases}.");
            for (int row = 1; row < 5; row++)
            for (int column = 1; column < 7; column++)
            {
                double x = column * w / 7d, y = row * h / 5d;
                bool actual = retained.TryPickTerrain(x, y, w, h, view, out var hit);
                bool reference = expected.TryPickTerrain(x, y, w, h, view, out var other);
                Assert(actual == reference && hit == other, "Retained terrain layout changed picking depth or physical height.");
            }
            cases++;
        }
    }

    private static void Benchmark(string output)
    {
        Directory.CreateDirectory(output);
        const int warmup = 16, samples = 64, width = 1200, height = 680, pixels = 4;
        var first = Heights(0);
        var second = Heights(150, voids: true);
        var center = Point(Hill, .5, .5);
        var camera = new MapCamera(center.Latitude, center.Longitude, 14.4, 23, 65, true);
        var results = new List<object>();
        foreach (string workload in new[] { "cold-mesh-grid", "retained-orbit", "moving-lod", "dem-replacement", "retained-block-orbit" })
        {
            using var surface = new Surface(first);
            var timings = new List<double>();
            var allocations = new List<long>();
            var hashes = new List<string>();
            int patches = 0;
            for (int sample = -warmup; sample < samples; sample++)
            {
                int phase = (sample + warmup) % 16;
                var elevation = workload == "dem-replacement" && phase % 2 != 0 ? second : first;
                var terrain = new TerrainOptions(elevation) { RenderStyle = workload == "retained-block-orbit" ? MapRenderStyle.Minecraft : MapRenderStyle.Pixel };
                var view = (camera with
                {
                    Bearing = camera.Bearing + (workload == "moving-lod" ? phase * 2 : phase * .00001),
                    Zoom = camera.Zoom + (workload == "moving-lod" ? Math.Sin(phase * Math.PI / 8) * 1.4 : 0)
                }).OnTerrain(terrain, height);
                if (workload == "cold-mesh-grid") surface.ClearMeshes();
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                Array? draws = null;
                object? mesh = null;
                if (workload == "cold-mesh-grid")
                {
                    for (int y = -1; y <= 1; y++)
                    for (int x = -1; x <= 1; x++) mesh = surface.Mesh(Hill with { X = Hill.X + x, Y = Hill.Y + y });
                    patches = 9;
                }
                else
                {
                    draws = surface.Prepare(view, width, height, pixels, terrain);
                    patches = draws.Length;
                }
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                if (sample < 0) continue;
                timings.Add(elapsed);
                allocations.Add(allocated);
                hashes.Add(draws is not null ? Hash(draws) : Convert.ToHexString(SHA256.HashData(
                    MemoryMarshal.AsBytes(Field<float[]>(mesh!, "Light").AsSpan()))));
            }
            double[] sorted = timings.Order().ToArray();
            results.Add(new { Workload = workload, Patches = patches, MedianMs = sorted[samples / 2],
                P95Ms = sorted[(int)Math.Ceiling(samples * .95) - 1], MeanAllocatedBytes = allocations.Average(),
                SamplesMs = timings, AllocatedBytes = allocations, Hashes = hashes });
            Console.WriteLine($"Terrain {workload}: {sorted[samples / 2]:F4} ms median, {sorted[(int)Math.Ceiling(samples * .95) - 1]:F4} ms p95, {allocations.Average():F0} bytes, {patches} patches.");
        }
        File.WriteAllText(Path.Combine(output, "terrain-preparation.json"), JsonSerializer.Serialize(new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            Platform = RuntimeInformation.OSDescription,
            Processors = Environment.ProcessorCount,
            RendererSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(PixelMapRenderer).Assembly.Location))),
            HarnessSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(TerrainMeshChecks).Assembly.Location))),
            Warmup = warmup, Samples = samples, Width = width, Height = height, PixelSize = pixels,
            Scope = "Actual terrain mesh/LOD/stitch preparation, excluding scene depth, models, labels and GL; deterministic synthetic DEM and blank artwork. Hashing is outside timing.",
            Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static (float[] Heights, float[] Light) Reference(ElevationSnapshot elevation, TileId id)
    {
        // Original five-sample evaluation is deliberately independent of the reused grid.
        var heights = new float[Side * Side];
        var light = new float[Side * Side];
        double size = 1d / (1 << id.Z), left = id.X * size, top = id.Y * size;
        double step = size / Segments;
        for (int row = 0; row < Side; row++)
        for (int column = 0; column < Side; column++)
        {
            double x = left + column * step, y = top + row * step;
            int index = row * Side + column;
            heights[index] = (float)elevation.SampleSurfaceMercator(x, y);
            double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y)));
            double span = 2 * step * 40075016.68557849 * Math.Cos(latitude);
            double dx = (elevation.SampleSurfaceMercator(x + step, y) - elevation.SampleSurfaceMercator(x - step, y)) / Math.Max(1, span);
            double dy = (elevation.SampleSurfaceMercator(x, y + step) - elevation.SampleSurfaceMercator(x, y - step)) / Math.Max(1, span);
            light[index] = (float)((.45 * dx + .55 * dy + .705) / Math.Sqrt(1 + dx * dx + dy * dy));
        }
        return (heights, light);
    }

    private static ElevationSnapshot Heights(double offset, bool voids = false)
    {
        var tiles = new List<ElevationTile> { Dem(new(0, 0, 0), 32, static (x, y) => -700 + x * 1300 - y * 400) };
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            var id = Hill with { X = Hill.X + x, Y = Hill.Y + y };
            int ix = x, iy = y;
            tiles.Add(Dem(id, 64, (u, v) => voids && u is > .2 and < .35 && v is > .6 and < .8
                ? double.NaN : offset + 1000 + 340 * Math.Sin((u + ix) * Math.PI) * Math.Sin((v + iy) * Math.PI) + 85 * (u + ix)));
        }
        MapFeature water = new("water", "ocean", "", "", 1, 4096, GeometryType.Polygon,
            [[new(0, 0), new(1000, 0), new(1000, 1700), new(0, 1700)]], null, null, false, 0, 0, "");
        return new(tiles, [new(Hill, [water])]);
    }

    private static ElevationTile Dem(TileId id, int size, Func<double, double, double> sample)
    {
        var values = new float[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) values[y * size + x] = (float)sample((x + .5) / size, (y + .5) / size);
        return new(id, size, values);
    }

    private static (double Latitude, double Longitude) Point(TileId id, double x, double y) =>
        (Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (id.Y + y) / (1 << id.Z)))) * 180 / Math.PI,
            (id.X + x) * 360 / (1 << id.Z) - 180);

    private static string Hash(Array draws)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (object draw in draws)
        {
            object mesh = Property(draw, "Mesh");
            var id = Field<TileId>(mesh, "Id");
            int[] identity = [id.Z, id.X, id.Y, Field<int>(mesh, "Copy"), (int)Field<MapRenderStyle>(mesh, "RenderStyle")];
            digest.AppendData(MemoryMarshal.AsBytes(identity.AsSpan()));
            digest.AppendData(MemoryMarshal.AsBytes(Field<float[]>(mesh, "Vertices").AsSpan()));
            if (Field<float[]?>(mesh, "HeightLight") is { } heights)
                digest.AppendData(MemoryMarshal.AsBytes(heights.AsSpan()));
            digest.AppendData(MemoryMarshal.AsBytes(((ushort[])Property(mesh, "TriangleIndices")).AsSpan()));
            object transform = Property(draw, "Transform");
            foreach (string name in new[] { "X", "Y", "XX", "XY", "YX", "YY", "PX", "PY", "PW", "CX", "CY",
                "InverseFocal", "SinPitch", "CosPitch", "TargetZ", "Far", "Near" })
                digest.AppendData(BitConverter.GetBytes((float)Property(transform, name)));
        }
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static bool Exact(float[] a, float[] b) =>
        MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));
    private static object Property(object target, string name) => target.GetType().GetProperty(name, Members)!.GetValue(target)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static void Assert(bool success, string message) { if (!success) throw new InvalidOperationException(message); }

    private sealed class Surface : IDisposable
    {
        private static readonly Type Type = typeof(PixelMapRenderer).Assembly.GetType("PixelMap.Rendering.TerrainSurface")!;
        private readonly object surface = Activator.CreateInstance(Type, nonPublic: true)!;
        private readonly Func<TileId, object> getMesh;
        private readonly Func<MapCamera, int, int, int, TerrainOptions, Func<TileId, SKBitmap, SKRect[]>, Array> prepare;
        private readonly IDictionary meshes;

        internal Surface(ElevationSnapshot elevation)
        {
            Type.GetField("elevation", Members)!.SetValue(surface, elevation);
            getMesh = Type.GetMethod("GetMesh", Members)!.CreateDelegate<Func<TileId, object>>(surface);
            prepare = Type.GetMethod("PrepareGpu", Members)!
                .CreateDelegate<Func<MapCamera, int, int, int, TerrainOptions, Func<TileId, SKBitmap, SKRect[]>, Array>>(surface);
            meshes = Field<IDictionary>(surface, "meshes");
        }

        internal object Mesh(TileId id) => getMesh(id);
        internal void ClearMeshes() => meshes.Clear();
        internal Array Prepare(MapCamera camera, int width, int height, int pixels, TerrainOptions options) =>
            prepare(camera, width, height, pixels, options, static (_, bitmap) => { bitmap.Erase(SKColors.White); return []; });
        public void Dispose() => ((IDisposable)surface).Dispose();
    }
}
