using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;

/// <summary>Frozen native preparation/overlay/picking benchmark. No graphics device
/// is created. Identical source is compiled against baseline and candidate renderers;
/// output hashes and diagnostics are collected outside each timed region.</summary>
internal static class WebGpuFrameBenchmark
{
    private const int Width = 1200, Height = 700;
    private sealed record Metric(double WallMilliseconds, double? ThreadCpuMilliseconds, long AllocatedBytes);
    private sealed record Summary(double MedianMilliseconds, double P95Milliseconds,
        double? MedianThreadCpuMilliseconds, double MedianAllocatedBytes);
    private sealed record Workload(string Name, MapRenderStyle Style, MapCamera[] Cameras, bool Vehicle);
    private sealed record Sample(int Index, Metric Preparation, Metric Overlay, Metric Picking,
        int TerrainPatches, int BuildingTriangles, int Labels, int OverlayTriangles,
        string GeometryHash, string ArtworkHash, string OverlayHash, string AtlasHash, string PickingHash);

    internal static async Task RunAsync(string[] args)
    {
        string output = Path.GetFullPath(Value(args, "--output=") ?? "artifacts/webgpu-native-performance.json");
        string? filter = Value(args, "--filter=");
        int warmup = int.Parse(Value(args, "--warmup=") ?? "32");
        int count = int.Parse(Value(args, "--samples=") ?? "32");
        if (warmup < 0 || warmup > 256 || count < 1 || count > 1024) throw new ArgumentOutOfRangeException(nameof(args));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var http = new HttpClient(new OfflineHandler());
        var source = new VectorTileSource(http);
        var tiles = new List<MapTile>();
        for (int y = 6159; y <= 6161; y++)
        for (int x = 4822; x <= 4824; x++) tiles.Add(await source.GetTileAsync(new(14, x, y)));
        var route = RouteDemos.ForMode(TravelMode.Drive);
        var track = new RouteTrack(route);
        var workloads = new List<Workload>();
        foreach (var style in new[] { MapRenderStyle.Pixel, MapRenderStyle.Normal, MapRenderStyle.Roblox })
        foreach (string motion in new[] { "pan", "walk", "orbit" })
        {
            var cameras = new MapCamera[32];
            for (int i = 0; i < cameras.Length; i++)
            {
                double phase = i * Math.PI / 16;
                var position = track.Sample(route.DistanceMeters * .35 + i * .35);
                cameras[i] = motion switch
                {
                    "walk" => MapCamera.FromEye(position.Point.Latitude, position.Point.Longitude, 1.7,
                        position.Bearing + Math.Sin(phase) * 5, 90, Width, Height, viewDistanceMeters: 6000),
                    "orbit" => new(40.7087, -74.0104, 16.5, 23 + i * .7, 58, true),
                    _ => new MapCamera(40.7087, -74.0104, 16.5, 23, 58, true)
                        .PanBy(Math.Sin(phase) * 36, Math.Cos(phase) * 24)
                };
            }
            workloads.Add(new(motion + "-" + style.ToString().ToLowerInvariant(), style, cameras, motion == "walk"));
        }
        if (filter is not null) workloads.RemoveAll(x => !x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        if (workloads.Count == 0) throw new ArgumentException("No matching frame workload.");
        var results = new List<object>();
        string font = "";
        foreach (var workload in workloads)
        {
            using var renderer = new PixelMapRenderer();
            using var overlay = new WebGpuSceneOverlay();
            font = TestLabelFont.Apply(renderer);
            var samples = new List<Sample>();
            long atlasRevision = -1;
            string atlasHash = "none";
            for (int i = -warmup; i < count; i++)
            {
                int phase = (i + warmup) % workload.Cameras.Length;
                var camera = workload.Cameras[phase];
                GpuMapFrame? frame = null;
                var prepare = Measure(() => frame = renderer.PrepareGpuFrame(Width, Height, camera, tiles,
                    PixelStyle.Day, 2, null, null, workload.Style, prepareLegacyOverlay: false));
                using var current = frame!;
                var point = track.Sample(route.DistanceMeters * .35 + phase * .35);
                VehiclePose? vehicle = workload.Vehicle ? new(point.Point, point.Bearing,
                    (i + warmup) / 60d, 0, true, .35 + phase * .35 / route.DistanceMeters, 1.4) : null;
                var overlayCost = Measure(() =>
                {
                    renderer.ActivateGpuFrame(current);
                    renderer.PrepareWebGpuOverlay(current, overlay, vehicle: vehicle, mode: TravelMode.Walk);
                });
                var probe = new int[48];
                var picking = Measure(() =>
                {
                    for (int n = 0; n < 12; n++)
                    {
                        int x = (n % 4 + 1) * Width / 5 / current.PixelSize;
                        int y = (n / 4 + 1) * Height / 4 / current.PixelSize;
                        bool hit = current.Depth.TryTerrain(x, y, out float altitude, out float inverse, out float physical);
                        probe[n * 4] = hit ? 1 : 0;
                        probe[n * 4 + 1] = BitConverter.SingleToInt32Bits(altitude);
                        probe[n * 4 + 2] = BitConverter.SingleToInt32Bits(inverse);
                        probe[n * 4 + 3] = BitConverter.SingleToInt32Bits(physical);
                    }
                });
                if (i < 0) continue;
                using var geometry = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var artwork = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var annotations = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Append(geometry, current.PixelsPerMeter);
                Append(geometry, current.MinimumAltitude);
                Append(geometry, current.MaximumAltitude);
                Append(geometry, current.Terrain.Length);
                foreach (var draw in current.Terrain)
                {
                    Append(geometry, draw.Mesh.Id);
                    Append(geometry, draw.Mesh.Copy);
                    Append(geometry, draw.Transform);
                    Append(geometry, draw.Mesh.Vertices.Length);
                    geometry.AppendData(MemoryMarshal.AsBytes(draw.Mesh.Vertices.AsSpan()));
                    Append(geometry, draw.Mesh.TriangleIndices.Length);
                    geometry.AppendData(MemoryMarshal.AsBytes(draw.Mesh.TriangleIndices.AsSpan()));
                    artwork.AppendData(MemoryMarshal.AsBytes(draw.Texture.Texels.AsSpan()));
                }
                Append(geometry, current.Buildings.Length);
                foreach (var draw in current.Buildings)
                {
                    Append(geometry, draw.Transform);
                    Append(geometry, draw.TileUnitsToMeters);
                    Append(geometry, draw.Mesh.Vertices.Length);
                    geometry.AppendData(MemoryMarshal.AsBytes(draw.Mesh.Vertices.AsSpan()));
                    Append(geometry, draw.Mesh.Indices.Length);
                    geometry.AppendData(MemoryMarshal.AsBytes(draw.Mesh.Indices.AsSpan()));
                    Append(geometry, draw.Mesh.Models.Length);
                    geometry.AppendData(MemoryMarshal.AsBytes(draw.Mesh.Models.AsSpan()));
                    Append(geometry, draw.Selection?.Models.Length ?? -1);
                    if (draw.Selection is { } selection) geometry.AppendData(selection.Models);
                }
                annotations.AppendData(MemoryMarshal.AsBytes(overlay.WorldVertices));
                annotations.AppendData(MemoryMarshal.AsBytes(overlay.WorldIndices));
                annotations.AppendData(MemoryMarshal.AsBytes(overlay.OccludedVertices));
                annotations.AppendData(MemoryMarshal.AsBytes(overlay.OccludedIndices));
                annotations.AppendData(MemoryMarshal.AsBytes(overlay.ScreenVertices));
                annotations.AppendData(MemoryMarshal.AsBytes(overlay.ScreenIndices));
                if (overlay.AtlasRevision != atlasRevision)
                {
                    atlasRevision = overlay.AtlasRevision;
                    atlasHash = overlay.Atlas is { } atlas ? Convert.ToHexStringLower(SHA256.HashData(atlas.GetPixelSpan())) : "none";
                }
                samples.Add(new(i, prepare, overlayCost, picking, current.TerrainPatchCount,
                    current.BuildingTriangleCount, overlay.LabelCount, overlay.WorldTriangleCount + overlay.ScreenTriangleCount,
                    Convert.ToHexStringLower(geometry.GetHashAndReset()), Convert.ToHexStringLower(artwork.GetHashAndReset()),
                    Convert.ToHexStringLower(annotations.GetHashAndReset()), atlasHash,
                    Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(probe.AsSpan())))));
            }
            var combined = samples.Select(x => new Metric(x.Preparation.WallMilliseconds + x.Overlay.WallMilliseconds,
                x.Preparation.ThreadCpuMilliseconds + x.Overlay.ThreadCpuMilliseconds,
                x.Preparation.AllocatedBytes + x.Overlay.AllocatedBytes)).ToArray();
            results.Add(new { workload.Name, workload.Style, Preparation = Summarize(samples.Select(x => x.Preparation)),
                Overlay = Summarize(samples.Select(x => x.Overlay)), Picking = Summarize(samples.Select(x => x.Picking)),
                PreparationAndOverlay = Summarize(combined), Samples = samples });
            Console.WriteLine($"{workload.Name}: prepare+overlay median {Median(combined.Select(x => x.WallMilliseconds)):F3} ms; " +
                $"prepare {Median(samples.Select(x => x.Preparation.WallMilliseconds)):F3}, overlay {Median(samples.Select(x => x.Overlay.WallMilliseconds)):F3} ms.");
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Kind = "frozen native WebGPU preparation benchmark", Source = Value(args, "--source=") ?? "unspecified",
            Scope = "CPU preparation, WebGPU overlay geometry and sparse picking; no device, rasterization, GPU timing or browser FPS. Hashing and fixture loading are untimed.",
            Runtime = RuntimeInformation.FrameworkDescription, Platform = RuntimeInformation.OSDescription,
            Processors = Environment.ProcessorCount, Width, Height, Warmup = warmup, MeasuredSamples = count,
            Font = font, RendererSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(PixelMapRenderer).Assembly.Location))),
            HarnessSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(WebGpuFrameBenchmark).Assembly.Location))),
            Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Metric Measure(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        double? cpu = ThreadCpu();
        long started = Stopwatch.GetTimestamp();
        action();
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return new(elapsed, ThreadCpu() - cpu, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static Summary Summarize(IEnumerable<Metric> values)
    {
        var samples = values.ToArray();
        return new(Median(samples.Select(x => x.WallMilliseconds)), Percentile(samples.Select(x => x.WallMilliseconds), .95),
            samples.All(x => x.ThreadCpuMilliseconds.HasValue) ? Median(samples.Select(x => x.ThreadCpuMilliseconds!.Value)) : null,
            Median(samples.Select(x => (double)x.AllocatedBytes)));
    }
    private static void Append<T>(IncrementalHash hash, T value) where T : unmanaged
    {
        Span<T> item = stackalloc T[1] { value };
        hash.AppendData(MemoryMarshal.AsBytes(item));
    }
    private static double Median(IEnumerable<double> values) => Percentile(values, .5);
    private static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        double at = (sorted.Length - 1) * fraction;
        return sorted[(int)at] + (sorted[(int)Math.Ceiling(at)] - sorted[(int)at]) * (at - (int)at);
    }
    private static string? Value(string[] args, string prefix) => args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    private static double? ThreadCpu() => OperatingSystem.IsLinux() && clock_gettime(3, out var value) == 0
        ? value.Seconds * 1000d + value.Nanoseconds / 1_000_000d : null;
    [StructLayout(LayoutKind.Sequential)] private struct Timespec { internal long Seconds, Nanoseconds; }
    [DllImport("libc")] private static extern int clock_gettime(int clock, out Timespec value);
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The benchmark uses bundled fixtures only: " + request.RequestUri);
    }
}
