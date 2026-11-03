using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

/// <summary>
/// Offline CPU-rendering measurements and exact visual comparisons. Run both binaries
/// on the same machine with no competing builds. PNG encoding and hashing are untimed.
/// </summary>
internal static class RenderingBenchmark
{
    private const int Width = 1100;
    private const int Height = 650;
    private const int SequenceFrames = 16;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Options(string Output, string? Compare, string? Filter, int Samples, int Warmup, int ColdSamples, bool VisualOnly);
    private sealed record Projection(string Name, double Pitch, bool Perspective);
    private sealed record Scene(string Name, MapCamera Camera, IReadOnlyList<MapTile> Tiles, IReadOnlyList<ImageryTile> Imagery);
    private sealed record Case(string Id, Scene Scene, Projection Projection, PixelStyle Style, bool Earth, int PixelSize, RoutePlan Route, bool Follow);
    private readonly record struct Frame(MapCamera Camera, VehiclePose Vehicle, RoutePosition Gate);
    private readonly record struct Sample(double TotalMs, double MapMs, double NavigationMs, double GateMs, long AllocatedBytes);
    private sealed record Measurements(double P50Ms, double P95Ms, double MinMs, double MaxMs,
        double AllocatedBytesPerFrame, double MapP50Ms, double NavigationP50Ms, double GateP50Ms, IReadOnlyList<Sample> Samples);
    private sealed record CaseResult(string Id, int Tiles, int Features, Measurements? Cold, Measurements? Still,
        Measurements? Moving, string ColdFrameHash, IReadOnlyList<string> FrameHashes, string SequenceHash);
    private sealed record Report(int Version, int Width, int Height, int SequenceFrames, string InputHash,
        string RendererHash, string Runtime, string OS, string Architecture, int ProcessorCount, bool ServerGC,
        string? TieredCompilation, string Typeface, Options Options, IReadOnlyList<CaseResult> Cases);

    internal static async Task RunAsync(string[] arguments)
    {
        var options = ReadOptions(arguments);
        Directory.CreateDirectory(options.Output);
        string imageDirectory = Path.Combine(options.Output, "images");
        Directory.CreateDirectory(imageDirectory);
        using var http = new HttpClient(new OfflineHandler());
        var vectors = new VectorTileSource(http);
        var photos = new EarthImagerySource(http);
        var tiles = new List<MapTile>();
        var imagery = new List<ImageryTile> { await photos.GetTileAsync(new(0, 0, 0)) };
        for (int y = 6159; y <= 6161; y++)
        for (int x = 4822; x <= 4824; x++)
        {
            var id = new TileId(14, x, y);
            tiles.Add(await vectors.GetTileAsync(id));
            imagery.Add(await photos.GetTileAsync(id));
        }

        var scenes = new[]
        {
            new Scene("downtown", new(40.7087, -74.0104, 16.5, -15, 55), tiles, imagery),
            new Scene("nine-tiles", new(40.7150, -74.0000, 14.8, 31, 55), tiles, imagery)
        };
        var projections = new[]
        {
            new Projection("overhead", 0, false), new Projection("iso55", 55, false),
            new Projection("persp45", 45, true), new Projection("persp65", 65, true), new Projection("persp72", 72, true)
        };
        var routes = Enum.GetValues<TravelMode>().ToDictionary(mode => mode, RouteDemos.ForMode);
        var cases = new List<Case>();
        foreach (var scene in scenes)
        foreach (var projection in projections)
        foreach (bool earth in new[] { false, true })
        foreach (var style in Enum.GetValues<PixelStyle>())
        foreach (int pixel in new[] { 2, 3, 4 })
        {
            string id = $"{scene.Name}-{projection.Name}-{(earth ? "earth" : "map")}-{style.ToString().ToLowerInvariant()}-p{pixel}";
            cases.Add(new(id, scene, projection, style, earth, pixel, routes[TravelMode.Drive], false));
        }
        foreach (var mode in Enum.GetValues<TravelMode>())
        foreach (bool earth in new[] { false, true })
        {
            var projection = earth ? projections[3] : projections[1];
            cases.Add(new($"vehicle-{mode.ToString().ToLowerInvariant()}-{(earth ? "earth" : "map")}",
                scenes[0], projection, earth ? PixelStyle.Dusk : PixelStyle.Day, earth, earth ? 3 : 2, routes[mode], true));
        }
        var edges = EdgeScene(imagery);
        foreach (var projection in projections)
        foreach (bool earth in new[] { false, true })
        foreach (var style in new[] { PixelStyle.Day, PixelStyle.Night })
            cases.Add(new($"edges-{projection.Name}-{(earth ? "earth" : "map")}-{style.ToString().ToLowerInvariant()}-p3",
                edges, projection, style, earth, 3, routes[TravelMode.Drive], false));
        foreach (var projection in projections)
        {
            var camera = RingFixtures.Camera with { Pitch = projection.Pitch, Perspective = projection.Perspective };
            var ringScene = new Scene("rings", camera,
                [RingFixtures.Create(camera, (Width + 2) / 3 * 3, (Height + 2) / 3 * 3)],
                [imagery.First(p => p.Id.Z == 14) with { Id = RingFixtures.Tile }]);
            foreach (bool earth in new[] { false, true })
            foreach (var style in new[] { PixelStyle.Day, PixelStyle.Night })
                cases.Add(new($"rings-{projection.Name}-{(earth ? "earth" : "map")}-{style.ToString().ToLowerInvariant()}-p3",
                    ringScene, projection, style, earth, 3, routes[TravelMode.Drive], false));
        }
        if (options.Filter is { } filter)
            cases.RemoveAll(c => !c.Id.Contains(filter, StringComparison.OrdinalIgnoreCase));
        if (cases.Count == 0) throw new ArgumentException("The benchmark filter selected no cases.");

        string inputHash = ResourceHash(typeof(VectorTileSource).Assembly, typeof(RouteDemos).Assembly);
        string rendererHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(typeof(PixelMapRenderer).Assembly.Location)));
        var results = new List<CaseResult>();
        Report MakeReport() => new(1, Width, Height, SequenceFrames, inputHash, rendererHash,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, GCSettings.IsServerGC,
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), SKTypeface.Default.FamilyName, options, results);

        Console.WriteLine($"Offline renderer benchmark, {Width}×{Height}, {cases.Count} cases, {tiles.Sum(t => t.Features.Count)} real features across nine tiles.");
        Console.WriteLine(options.VisualOnly
            ? $"Each case captures {SequenceFrames} exact visual frames."
            : $"Each case has {options.ColdSamples} fresh-renderer draws, {options.Warmup} warmup frames, {options.Samples} still and moving samples, and {SequenceFrames} exact visual frames.");
        Console.WriteLine("Allocated bytes measure managed allocations on the calling thread. Cold draws exclude renderer construction and decoded input loading.");
        Console.WriteLine($"Renderer SHA-256 {rendererHash}. Output {options.Output}");

        using var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        foreach (var item in cases)
        {
            var result = RunCase(item, options, bitmap, canvas, imageDirectory);
            results.Add(result);
            Console.WriteLine(result.Moving is { } moving
                ? FormattableString.Invariant($"{results.Count,3}/{cases.Count} {item.Id,-46} cold {result.Cold!.P50Ms,7:F2} ms | still {result.Still!.P50Ms,6:F2} ms | moving {moving.P50Ms,7:F2}/{moving.P95Ms,7:F2} ms | {moving.AllocatedBytesPerFrame,9:F0} B/frame")
                : $"{results.Count,3}/{cases.Count} {item.Id,-46} visual {result.SequenceHash}");
            await File.WriteAllTextAsync(Path.Combine(options.Output, "report.partial.json"), JsonSerializer.Serialize(MakeReport(), Json));
        }
        var report = MakeReport();
        string reportFile = Path.Combine(options.Output, "report.json");
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(report, Json));
        await File.WriteAllTextAsync(Path.Combine(options.Output, "summary.csv"), Csv(results));
        if (options.Compare is { } baselineFile)
            Compare(report, JsonSerializer.Deserialize<Report>(await File.ReadAllTextAsync(baselineFile), Json)
                ?? throw new InvalidDataException("The baseline report is empty."));
        Console.WriteLine($"Completed {results.Count} cases. Report {reportFile}");
    }

    private static CaseResult RunCase(Case item, Options options, SKBitmap bitmap, SKCanvas canvas, string imageDirectory)
    {
        var frames = CreateFrames(item);
        var still = frames[0] with { Vehicle = frames[0].Vehicle with { Moving = false } };
        string? coldHash = null;
        Measurements? coldResult = null, stillResult = null, movingResult = null;
        if (!options.VisualOnly)
        {
            var coldSamples = new Sample[options.ColdSamples];
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (int i = 0; i < coldSamples.Length; i++)
            {
                using var coldRenderer = new PixelMapRenderer();
                coldSamples[i] = Measure(coldRenderer, canvas, item, frames[0]);
                string hash = PixelHash(bitmap);
                if (coldHash is not null && hash != coldHash)
                    throw new InvalidOperationException($"Cold output is nondeterministic in {item.Id}.");
                coldHash = hash;
            }

            var stillSamples = new Sample[options.Samples];
            var movingSamples = new Sample[options.Samples];
            using var renderer = new PixelMapRenderer();
            for (int i = 0; i < options.Warmup; i++) Draw(renderer, canvas, item, still);
            for (int i = 0; i < stillSamples.Length; i++) stillSamples[i] = Measure(renderer, canvas, item, still);
            for (int i = 0; i < options.Warmup; i++) Draw(renderer, canvas, item, frames[i % SequenceFrames]);
            for (int i = 0; i < movingSamples.Length; i++)
            {
                var frame = frames[i % SequenceFrames];
                // Each lap has the same bounded effects history, independent of sample count.
                frame = frame with { Vehicle = frame.Vehicle with { Continuity = 1 + i / SequenceFrames } };
                movingSamples[i] = Measure(renderer, canvas, item, frame);
            }
            coldResult = Summarize(coldSamples);
            stillResult = Summarize(stillSamples);
            movingResult = Summarize(movingSamples);
        }

        var hashes = new List<string>(SequenceFrames);
        using (var renderer = new PixelMapRenderer())
        {
            for (int i = 0; i < frames.Length; i++)
            {
                Draw(renderer, canvas, item, frames[i]);
                hashes.Add(PixelHash(bitmap));
                if (i == 0)
                {
                    Draw(renderer, canvas, item, frames[i]);
                    if (PixelHash(bitmap) != hashes[0])
                        throw new InvalidOperationException($"Cached redraw changed pixels in {item.Id}.");
                }
                if (i == 0 || i == SequenceFrames / 2 || i == SequenceFrames - 1)
                {
                    using var image = SKImage.FromBitmap(bitmap);
                    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                    using var file = File.Create(Path.Combine(imageDirectory, $"{item.Id}-{i:D2}.png"));
                    png.SaveTo(file);
                }
            }
        }
        coldHash ??= hashes[0];
        if (hashes[0] != coldHash) throw new InvalidOperationException($"Cold and visual output disagree in {item.Id}.");
        string sequenceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("", hashes))));
        return new(item.Id, item.Scene.Tiles.Count, item.Scene.Tiles.Sum(t => t.Features.Count),
            coldResult, stillResult, movingResult, coldHash, hashes, sequenceHash);
    }

    private static Frame[] CreateFrames(Case item)
    {
        var track = new RouteTrack(item.Route);
        var frames = new Frame[SequenceFrames];
        double speed = item.Route.Mode switch { TravelMode.Walk => 1.4, TravelMode.Bike => 5, TravelMode.Boat => 8, TravelMode.Train => 20, TravelMode.Flight => 80, _ => 12 };
        for (int i = 0; i < frames.Length; i++)
        {
            double phase = i * Math.PI * 2 / SequenceFrames;
            double progress = .35 + speed * (i / 20d) / item.Route.DistanceMeters;
            var position = track.Sample(item.Route.DistanceMeters * progress);
            double altitude = item.Route.Mode == TravelMode.Flight ? 500 : 0;
            var vehicle = new VehiclePose(position.Point, position.Bearing, i / 20d, altitude, true, progress, speed);
            var camera = item.Scene.Camera with
            {
                Latitude = item.Scene.Camera.Latitude + .0006 * Math.Sin(phase),
                Longitude = item.Scene.Camera.Longitude + .0008 * (Math.Cos(phase) - 1),
                Zoom = item.Scene.Camera.Zoom + .12 * Math.Sin(phase / 2),
                Bearing = item.Scene.Camera.Bearing + 24 * Math.Sin(phase),
                Pitch = item.Projection.Pitch == 0 ? 0 : Math.Clamp(item.Projection.Pitch + 2 * Math.Sin(phase), 0, 72),
                Perspective = item.Projection.Perspective
            };
            if (item.Follow)
                camera = camera with { Latitude = position.Point.Latitude, Longitude = position.Point.Longitude,
                    Zoom = item.Route.Mode == TravelMode.Flight ? 14.3 : camera.Zoom, TargetAltitudeMeters = altitude };
            frames[i] = new(camera, vehicle, track.Sample(item.Route.DistanceMeters * Math.Min(.95, progress + .15)));
        }
        return frames;
    }

    private static Scene EdgeScene(IReadOnlyList<ImageryTile> photos)
    {
        var id = new TileId(14, 8192, 8192);
        double longitude = (id.X + .5) / (1 << id.Z) * 360 - 180;
        double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (id.Y + .5) / (1 << id.Z)))) * 180 / Math.PI;
        var features = new List<MapFeature>
        {
            Model(301, 1000, 110, 0, Box(-230, -180, 60, 130), Box(-165, -115, -5, 65).Reverse().ToArray()),
            Model(302, 8192, 63, 8, new TilePoint[] { new(1790, 2210), new(2060, 2210), new(2060, 2290), new(1870, 2290), new(1870, 2470), new(1790, 2470) }),
            Model(303, 4096, 190, 80, Box(40, -180, 290, -105)),
            Model(304, 8192, 620, 0, Box(70, 40, 240, 215)),
            Model(305, 1000, 24, 0, [new(1740, 1870), new(1740, 1870), new(1820, 1870), new(1820, 2010), new(1740, 2010)], []),
            Model(306, 4096, 85, 0, Box(250, -15, 350, 170), Box(250, -15, 350, 170))
        };
        var photo = photos.First(p => p.Id.Z == 14) with { Id = id };
        return new("edges", new(latitude, longitude, 16, 31, 55), [new(id, features)], [photo]);

        static TilePoint[] Box(int x0, int y0, int x1, int y1) =>
            [new(2048 + x0, 2048 + y0), new(2048 + x1, 2048 + y0), new(2048 + x1, 2048 + y1), new(2048 + x0, 2048 + y1)];
        static MapFeature Model(ulong id, int extent, double height, double minHeight, params TilePoint[][] paths) =>
            new("building", "", "", "", id, extent, GeometryType.Polygon,
                paths.Select(ring => ring.Select(p => new TilePoint(
                    (int)Math.Round(p.X * (double)extent / 4096), (int)Math.Round(p.Y * (double)extent / 4096))).ToArray()).ToArray(),
                height, minHeight, false, 0, 0, "");
    }

    private static Sample Measure(PixelMapRenderer renderer, SKCanvas canvas, Case item, Frame frame)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        renderer.Draw(canvas, Width, Height, frame.Camera, item.Scene.Tiles, item.Style, item.PixelSize, item.Earth ? item.Scene.Imagery : null);
        long mapEnd = Stopwatch.GetTimestamp();
        renderer.DrawNavigation(canvas, Width, Height, frame.Camera, item.Route, frame.Vehicle, item.PixelSize);
        long navigationEnd = Stopwatch.GetTimestamp();
        renderer.DrawGate(canvas, frame.Camera, Width, Height, frame.Gate, item.Route.Mode, frame.Vehicle.AltitudeMeters, frame.Vehicle.AnimationSeconds);
        canvas.Flush();
        long end = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return new(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds,
            Stopwatch.GetElapsedTime(start, mapEnd).TotalMilliseconds,
            Stopwatch.GetElapsedTime(mapEnd, navigationEnd).TotalMilliseconds,
            Stopwatch.GetElapsedTime(navigationEnd, end).TotalMilliseconds, bytes);
    }

    private static void Draw(PixelMapRenderer renderer, SKCanvas canvas, Case item, Frame frame)
    {
        renderer.Draw(canvas, Width, Height, frame.Camera, item.Scene.Tiles, item.Style, item.PixelSize, item.Earth ? item.Scene.Imagery : null);
        renderer.DrawNavigation(canvas, Width, Height, frame.Camera, item.Route, frame.Vehicle, item.PixelSize);
        renderer.DrawGate(canvas, frame.Camera, Width, Height, frame.Gate, item.Route.Mode, frame.Vehicle.AltitudeMeters, frame.Vehicle.AnimationSeconds);
        canvas.Flush();
    }

    private static Measurements Summarize(Sample[] samples)
    {
        double[] times = samples.Select(s => s.TotalMs).Order().ToArray();
        return new(Percentile(times, .5), Percentile(times, .95), times[0], times[^1], samples.Average(s => s.AllocatedBytes),
            Percentile(samples.Select(s => s.MapMs).Order().ToArray(), .5),
            Percentile(samples.Select(s => s.NavigationMs).Order().ToArray(), .5),
            Percentile(samples.Select(s => s.GateMs).Order().ToArray(), .5), samples);
    }

    private static double Percentile(double[] sorted, double fraction) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];
    private static string PixelHash(SKBitmap bitmap) => Convert.ToHexStringLower(SHA256.HashData(bitmap.GetPixelSpan()));

    private static string ResourceHash(params Assembly[] assemblies)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var assembly in assemblies)
        foreach (var name in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            using var resource = assembly.GetManifestResourceStream(name)!;
            using var data = new MemoryStream();
            resource.CopyTo(data);
            hash.AppendData(data.GetBuffer().AsSpan(0, checked((int)data.Length)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Compare(Report actual, Report baseline)
    {
        if (actual.Version != baseline.Version || actual.Width != baseline.Width || actual.Height != baseline.Height ||
            actual.SequenceFrames != baseline.SequenceFrames || actual.InputHash != baseline.InputHash || actual.Typeface != baseline.Typeface)
            throw new InvalidOperationException("Baseline fixture, viewport, sequence or typeface differs. Capture comparable reports on the same environment.");
        var expected = baseline.Cases.ToDictionary(c => c.Id);
        var differences = new List<string>();
        foreach (var item in actual.Cases)
        {
            if (!expected.TryGetValue(item.Id, out var other)) { differences.Add(item.Id + " has no baseline"); continue; }
            for (int i = 0; i < item.FrameHashes.Count; i++)
                if (item.FrameHashes[i] != other.FrameHashes[i]) differences.Add($"{item.Id} frame {i:D2}");
            if (item.ColdFrameHash != other.ColdFrameHash) differences.Add(item.Id + " cold frame");
        }
        if (actual.Options.Filter is null && actual.Cases.Count != baseline.Cases.Count)
            differences.Add("The full case counts differ.");
        if (differences.Count > 0)
            throw new InvalidOperationException($"Exact visual comparison failed for {differences.Count} frames.\n" + string.Join("\n", differences));
        Console.WriteLine($"Exact visual equivalence passed for {actual.Cases.Count * SequenceFrames} moving frames and {actual.Cases.Count} cold frames.");
    }

    private static string Csv(IEnumerable<CaseResult> cases)
    {
        var text = new StringBuilder("case,phase,p50_ms,p95_ms,min_ms,max_ms,managed_bytes_per_frame,map_p50_ms,navigation_p50_ms,gate_p50_ms\n");
        foreach (var item in cases)
        foreach (var (phase, value) in new[] { ("cold", item.Cold), ("still", item.Still), ("moving", item.Moving) })
        {
            if (value is null) continue;
            text.AppendLine(FormattableString.Invariant($"{item.Id},{phase},{value.P50Ms:F5},{value.P95Ms:F5},{value.MinMs:F5},{value.MaxMs:F5},{value.AllocatedBytesPerFrame:F2},{value.MapP50Ms:F5},{value.NavigationP50Ms:F5},{value.GateP50Ms:F5}"));
        }
        return text.ToString();
    }

    private static Options ReadOptions(string[] arguments)
    {
        string output = Path.Combine(Path.GetTempPath(), "PixelMap.Rendering.Benchmark");
        string? compare = null, filter = null;
        int samples = 32, warmup = SequenceFrames, cold = 3;
        bool visualOnly = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            string argument = arguments[i];
            if (argument == "--benchmark") continue;
            if (argument == "--visual-only") { visualOnly = true; continue; }
            if (i + 1 >= arguments.Length) throw new ArgumentException($"Missing value for {argument}.");
            string value = arguments[++i];
            switch (argument)
            {
                case "--output": output = Path.GetFullPath(value); break;
                case "--compare": compare = Path.GetFullPath(value); break;
                case "--filter": filter = value; break;
                case "--samples": samples = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--warmup": warmup = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--cold-samples": cold = int.Parse(value, CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Unknown benchmark option {argument}.");
            }
        }
        if (samples is < 1 or > 512 || warmup is < 1 or > 512 || cold is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(arguments), "Samples and warmup must be 1–512, cold samples 1–32.");
        return new(output, compare, filter, samples, warmup, cold, visualOnly);
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The benchmark requires a bundled tile and never makes network requests.");
    }
}
