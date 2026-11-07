using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

/// <summary>Moving-frame CPU preparation and sparse depth costs. This entry point uses
/// only public APIs/reflection so the identical harness can load the unchanged ab765d2
/// renderer. Hashing, delegate creation and fixture loading are outside measurements.</summary>
internal static class DepthPreparationBenchmark
{
    private const int Width = 1100, Height = 650, Pixels = 3;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private delegate bool TerrainQuery(int x, int y, out float altitude, out float inverse, out float physical);
    private delegate bool CoverQuery(float x, float y, float altitude);
    private readonly record struct Probe(int X, int Y, float Altitude);
    private readonly record struct Metric(double WallMs, double ThreadCpuMs, long Bytes);
    private sealed record Sample(int Frame, Metric Prepare, Metric Navigation, Metric DepthConstruction,
        Metric SparseQueries, Metric RepeatedQueries, int TerrainPatches, int BuildingTriangles,
        int FramePreparedTriangles, int ConstructedTriangles, int PreparedTriangles, int PreparedEdges,
        int? PreparedGroups, int? PreparedCells, string QueryHash, string OverlayHash, float MinimumAltitude, float MaximumAltitude);
    private sealed record Workload(string Name, MapTile[] Tiles, TerrainOptions? Terrain, MapRenderStyle Style,
        MapCamera[] Cameras, RoutePlan Route, int QueryStride = 0);

    internal static async Task RunAsync(string[] arguments)
    {
        string output = Path.GetFullPath(Value(arguments, "--depth-output=") ?? Path.Combine(Path.GetTempPath(), "PixelMap.DepthBenchmark"));
        string? filter = Value(arguments, "--depth-filter=");
        int warmup = int.Parse(Value(arguments, "--depth-warmup=") ?? "16");
        int samples = int.Parse(Value(arguments, "--depth-samples=") ?? "64");
        if (warmup < 0 || samples < 1 || samples > 512) throw new ArgumentOutOfRangeException(nameof(arguments));
        Directory.CreateDirectory(output);
        using var http = new HttpClient(new OfflineHandler());
        var vectors = new VectorTileSource(http);
        var real = new List<MapTile>();
        for (int y = 6159; y <= 6161; y++)
        for (int x = 4822; x <= 4824; x++) real.Add(await vectors.GetTileAsync(new(14, x, y)));
        var route = RouteDemos.ForMode(TravelMode.Drive);
        var track = new RouteTrack(route);
        var workloads = new List<Workload>();
        foreach (string name in new[] { "walk", "chase", "freeflight", "orbit", "freeflight-normal" })
        {
            var cameras = new MapCamera[16];
            for (int i = 0; i < cameras.Length; i++)
            {
                var position = track.Sample(route.DistanceMeters * .35 + i * (name == "walk" ? .35 : 1.5));
                double pitch = name == "walk" ? 90 : name == "chase" ? 82 : 64;
                double altitude = name == "walk" ? 1.7 : name == "chase" ? 7 : 180;
                double bearing = position.Bearing + Math.Sin(i * Math.PI / 8) * 5;
                cameras[i] = name == "orbit"
                    ? new(40.7087 + i * .00001, -74.0104, 16.5, 23 + i * .6, 58, true)
                    : MapCamera.FromEye(position.Point.Latitude, position.Point.Longitude, altitude, bearing, pitch,
                        Width, Height, viewDistanceMeters: 6000);
            }
            workloads.Add(new("manhattan-" + name, real.ToArray(), null,
                name.EndsWith("normal", StringComparison.Ordinal) ? MapRenderStyle.Normal : MapRenderStyle.Pixel, cameras, route));
        }
        var terrain = new TerrainOptions(GpuDepthIndexChecks.Elevation());
        var center = GpuDepthIndexChecks.Geo(.5, .5);
        double ground = terrain.Elevation.SampleSurfaceMeters(center.Latitude, center.Longitude);
        foreach (var style in new[] { MapRenderStyle.Pixel, MapRenderStyle.Minecraft, MapRenderStyle.Roblox })
        {
            var cameras = Enumerable.Range(0, 16).Select(i => MapCamera.FromEye(center.Latitude + i * .00001,
                center.Longitude, ground + 180, 23 + i * .6, 65, Width, Height).OnTerrain(terrain, Height)).ToArray();
            workloads.Add(new("hills-" + style.ToString().ToLowerInvariant(), GpuDepthIndexChecks.Tiles(), terrain, style, cameras, route));
        }
        var physical = workloads.Single(value => value.Name == "manhattan-freeflight");
        workloads.Add(physical with { Name = "manhattan-zero-query", QueryStride = -1 });
        workloads.Add(physical with { Name = "manhattan-full-query", QueryStride = 1 });
        if (filter is not null) workloads.RemoveAll(value => !value.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        if (workloads.Count == 0) throw new ArgumentException("No matching depth benchmark workload.");

        var depthType = typeof(PixelMapRenderer).Assembly.GetType("PixelMap.Rendering.GpuSceneDepth")!;
        var constructor = depthType.GetConstructors(Members).Single();
        var retain = depthType.GetMethod("Retain", Members)!;
        var release = depthType.GetMethod("Release", Members)!;
        var terrainQuery = depthType.GetMethod("TryTerrain", Members)!;
        var coverQuery = depthType.GetMethod("CoversAt", Members)!;
        var reports = new List<object>();
        string labelFont = "";
        foreach (var workload in workloads)
        {
            using var renderer = new PixelMapRenderer();
            labelFont = TestLabelFont.Apply(renderer);
            using var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            var values = new List<Sample>();
            int caseSamples = workload.QueryStride != 0 ? Math.Min(samples, 8) : samples;
            int caseWarmup = workload.QueryStride != 0 ? Math.Min(warmup, 4) : warmup;
            var probes = Probes(workload.Style is MapRenderStyle.Normal or MapRenderStyle.Roblox ? 1 : Pixels, workload.QueryStride);
            var bits = new int[probes.Length * 5];
            var repeatedBits = new int[bits.Length];
            for (int i = -caseWarmup; i < caseSamples; i++)
            {
                int phase = (i + caseWarmup) % workload.Cameras.Length;
                var camera = workload.Cameras[phase];
                GpuMapFrame? frame = null;
                var prepare = Measure(() => frame = renderer.PrepareGpuFrame(Width, Height, camera, workload.Tiles,
                    PixelStyle.Day, Pixels, null, workload.Terrain, workload.Style));
                using var current = frame!;
                canvas.Clear(SKColors.Transparent);
                var position = track.Sample(route.DistanceMeters * .35 + phase * 1.5);
                var gate = track.Sample(route.DistanceMeters * .35 + phase * 1.5 + 100);
                var vehicle = new VehiclePose(position.Point, position.Bearing, phase / 20d, 0, true,
                    .35 + phase * 1.5 / route.DistanceMeters, 12);
                var navigation = Measure(() =>
                {
                    renderer.DrawGpuOverlay(canvas, current);
                    renderer.DrawNavigation(canvas, Width, Height, camera, route, vehicle, Pixels);
                    renderer.DrawGate(canvas, camera, Width, Height, gate, TravelMode.Drive, 0, phase / 20d);
                    renderer.TryPickTerrain(Width * .5, Height * .72, Width, Height, camera, out _);
                });

                object? depth = null;
                var construction = Measure(() => depth = constructor.Invoke([current]));
                retain.Invoke(depth, null);
                try
                {
                    int constructedTriangles = Count(depth!, "triangles");
                    var sampleTerrain = terrainQuery.CreateDelegate<TerrainQuery>(depth);
                    var covers = coverQuery.CreateDelegate<CoverQuery>(depth);
                    var sparse = Measure(() => Query(probes, sampleTerrain, covers, bits));
                    var repeated = Measure(() => Query(probes, sampleTerrain, covers, repeatedBits));
                    if (!bits.AsSpan().SequenceEqual(repeatedBits)) throw new InvalidOperationException("Retained depth samples changed.");
                    if (i < 0) continue;
                    values.Add(new(i, prepare, navigation, construction, sparse, repeated,
                        current.TerrainPatchCount, current.BuildingTriangleCount, Count(Property<object>(current, "Depth"), "triangles"),
                        constructedTriangles, Count(depth!, "triangles"), Count(depth!, "edges"),
                        PreparedGroups(depth!), PreparedCells(depth!),
                        Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(bits.AsSpan()))),
                        Convert.ToHexStringLower(SHA256.HashData(bitmap.GetPixelSpan())),
                        Property<float>(current, "MinimumAltitude"), Property<float>(current, "MaximumAltitude")));
                }
                finally { release.Invoke(depth, null); }
            }
            object summary = new
            {
                Prepare = Summarize(values.Select(value => value.Prepare)),
                Navigation = Summarize(values.Select(value => value.Navigation)),
                DepthConstruction = Summarize(values.Select(value => value.DepthConstruction)),
                SparseQueries = Summarize(values.Select(value => value.SparseQueries)),
                RepeatedQueries = Summarize(values.Select(value => value.RepeatedQueries)),
                PreparationAndNavigation = Summarize(values.Select(value => Combine(value.Prepare, value.Navigation))),
                DepthConstructionAndQueries = Summarize(values.Select(value => Combine(value.DepthConstruction, value.SparseQueries)))
            };
            reports.Add(new { workload.Name, Features = workload.Tiles.Sum(tile => tile.Features.Count), workload.Style,
                Probes = probes.Length, QueryCells = probes.Select(value => (value.X / 16, value.Y / 16)).Distinct().Count(),
                Warmup = caseWarmup, MeasuredSamples = caseSamples, Summary = summary, Samples = values });
            Console.WriteLine($"Depth {workload.Name}: prepare {Median(values.Select(value => value.Prepare.WallMs)):F3} ms, " +
                $"navigation {Median(values.Select(value => value.Navigation.WallMs)):F3} ms, construction {Median(values.Select(value => value.DepthConstruction.WallMs)):F3} ms, " +
                $"queries {Median(values.Select(value => value.SparseQueries.WallMs)):F3} ms; " +
                $"{Median(values.Select(value => (double)value.PreparedTriangles)):F0} demand-prepared triangles.");
        }
        File.WriteAllText(Path.Combine(output, "depth-preparation.json"), JsonSerializer.Serialize(new
        {
            Baseline = "ab765d24c3063c1745867772426720fd47152805", Runtime = RuntimeInformation.FrameworkDescription,
            Platform = RuntimeInformation.OSDescription, Processors = Environment.ProcessorCount, LabelFont = labelFont,
            RendererSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(PixelMapRenderer).Assembly.Location))),
            HarnessSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(DepthPreparationBenchmark).Assembly.Location))),
            Warmup = warmup, Samples = samples, Width, Height, RequestedPixelSize = Pixels,
            Scope = "CPU-only moving PrepareGpuFrame including labels, followed by DrawGpuOverlay/Drive route/vehicle/gate/picking for every camera: navigation overlay stress, not a free-roam simulation. Independent depth construction plus 144 sparse first/repeated query pairs, or zero/full-pixel stress. Query percentiles describe whole batches. GL/presentation excluded; hashes/delegate creation untimed. Thread CPU uses CLOCK_THREAD_CPUTIME_ID.",
            Results = reports
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Metric Measure(Action operation)
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        double cpu = ThreadCpu();
        long started = Stopwatch.GetTimestamp();
        operation();
        double wall = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        cpu = ThreadCpu() - cpu;
        return new(wall, cpu, GC.GetAllocatedBytesForCurrentThread() - bytes);
    }

    private static Probe[] Probes(int pixelSize, int stride)
    {
        int width = (Width + pixelSize - 1) / pixelSize, height = (Height + pixelSize - 1) / pixelSize;
        if (stride < 0) return [];
        var result = new List<Probe>();
        if (stride > 0)
        {
            for (int y = 0; y < height; y += stride)
            for (int x = 0; x < width; x += stride) result.Add(new(x, y, 4));
            return result.ToArray();
        }
        for (int i = 0; i < 96; i++) result.Add(new((i * 97 + 13) % width, (i * 67 + 5) % height, i % 3 == 0 ? 35 : 4));
        for (int i = 0; i < 48; i++) result.Add(new((int)(width * (.5 + .25 * Math.Sin(i * .07))),
            (int)(height * (.30 + i * .012)), 2));
        return result.ToArray();
    }

    private static void Query(Probe[] probes, TerrainQuery terrain, CoverQuery covers, int[] output)
    {
        for (int i = 0; i < probes.Length; i++)
        {
            var probe = probes[i];
            bool hit = terrain(probe.X, probe.Y, out float altitude, out float inverse, out float physical);
            output[i * 5] = hit ? 1 : 0;
            output[i * 5 + 1] = BitConverter.SingleToInt32Bits(altitude);
            output[i * 5 + 2] = BitConverter.SingleToInt32Bits(inverse);
            output[i * 5 + 3] = BitConverter.SingleToInt32Bits(physical);
            output[i * 5 + 4] = covers(probe.X + .23f, probe.Y + .71f, probe.Altitude) ? 1 : 0;
        }
    }

    private static object Summarize(IEnumerable<Metric> metrics)
    {
        var array = metrics.ToArray();
        return new { MedianMs = Median(array.Select(value => value.WallMs)),
            P95Ms = Percentile(array.Select(value => value.WallMs), .95),
            MedianThreadCpuMs = Median(array.Select(value => value.ThreadCpuMs)),
            MeanAllocatedBytes = array.Average(value => value.Bytes) };
    }
    private static Metric Combine(Metric a, Metric b) => new(a.WallMs + b.WallMs, a.ThreadCpuMs + b.ThreadCpuMs, a.Bytes + b.Bytes);
    private static double Median(IEnumerable<double> values) => Percentile(values, .5);
    private static double Percentile(IEnumerable<double> values, double p) { var sorted = values.Order().ToArray(); return sorted[(int)Math.Ceiling(sorted.Length * p) - 1]; }
    private static string? Value(string[] args, string prefix) => args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Members)!.GetValue(value)!;
    private static int Count(object value, string name) => ((System.Collections.ICollection)value.GetType().GetField(name, Members)!.GetValue(value)!).Count;
    private static int? PreparedGroups(object depth) => depth.GetType().GetField("groups", Members)?.GetValue(depth) is System.Collections.IEnumerable groups
        ? groups.Cast<object>().Count(group => (int)group.GetType().GetField("FirstTriangle", Members)!.GetValue(group)! >= 0) : null;
    private static int? PreparedCells(object depth) => depth.GetType().GetField("preparedCells", Members)?.GetValue(depth) is bool[] cells
        ? cells.Count(value => value) : null;
    private static double ThreadCpu()
    {
        if (!OperatingSystem.IsLinux() || clock_gettime(3, out var time) != 0)
            throw new PlatformNotSupportedException("Thread CPU measurement requires Linux CLOCK_THREAD_CPUTIME_ID.");
        return time.Seconds * 1000d + time.Nanoseconds / 1e6;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Timespec { internal long Seconds, Nanoseconds; }
    [DllImport("libc")] private static extern int clock_gettime(int id, out Timespec result);
    internal sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The depth benchmark requires bundled tiles and never makes network requests.");
    }
}
