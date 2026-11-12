using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

/// <summary>Source streaming must retain unrelated immutable artwork while matching
/// complete recomposition exactly. The benchmark uses unchanged public renderer APIs
/// and reflection-only diagnostics so one frozen harness runs both revisions.</summary>
internal static class TerrainArtworkInvalidationChecks
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Width = 960, Height = 640;
    private readonly record struct Patch(TileId Id, int Copy);
    private sealed record Texture(object Identity, uint[] Pixels);
    private sealed record Input(MapTile[] Tiles, ImageryTile[]? Imagery = null);

    internal static Task RunAsync()
    {
        int comparisons = 0;
        foreach (var style in Enum.GetValues<MapRenderStyle>()) comparisons += CheckSources(style);
        comparisons += CheckOffscreenDependency();
        comparisons += CheckWorldWrap();
        comparisons += CheckNeighborShadows();
        Console.WriteLine($"Passed {comparisons} exact streamed-artwork comparisons: localized vector/imagery arrival, replacement/removal, distant sources, priority order, duplicate IDs, mixed zooms, world copies, neighboring shadows and caster eviction.");
        return Task.CompletedTask;
    }

    private static int CheckSources(MapRenderStyle style)
    {
        var parent = new TileId(14, 8192, 8192);
        var camera = Camera(parent, 16.5);
        var background = Vector(parent, "grass", 1);
        using var renderer = new PixelMapRenderer();
        using var initial = Prepare(renderer, camera, new([background]), style);
        var prior = Textures(initial);
        var childId = prior.Keys.OrderByDescending(p => p.Id.Z).First().Id;
        Assert(childId.Z > parent.Z && prior.Keys.Any(p => !Overlaps(p.Id, childId)),
            "Source fixture did not produce a localized child and unrelated cached patches.");
        var child = Vector(childId, "wood", 2);
        var far = Vector(new(14, 1000, 1000), "sand", 3);
        int comparisons = 0;
        var retainedHash = TextureHash(initial);
        Step(new([background, far]), [], "distant vector arrival", requireAllRetained: true);
        Step(new([far, background]), [], "unique vector priority order", requireAllRetained: true);
        Step(new([background, far, child]), [childId], "local child arrival", requireChange: true);
        child = Vector(childId, "sand", 4);
        Step(new([background, child, far]), [childId], "local vector replacement", requireChange: true);
        Step(new([background, far]), [childId], "local vector removal", requireChange: true);
        Step(new([background]), [], "distant vector removal", requireAllRetained: true);

        var image = Image(childId, SKColors.DarkOliveGreen);
        Step(new([background], [image]), [childId], "local imagery arrival", requireChange: true);
        image = Image(childId, SKColors.CornflowerBlue);
        Step(new([background], [image]), [childId], "imagery payload replacement", requireChange: true);
        var farImage = Image(far.Id, SKColors.Coral);
        Step(new([background], [farImage, image]), [], "distant imagery arrival", requireAllRetained: true);
        Step(new([background], [image, farImage]), [], "unique imagery priority order", requireAllRetained: true);
        Step(new([background], [farImage]), [childId], "local imagery removal", requireChange: true);
        Step(new([background]), [], "distant imagery removal", requireAllRetained: true);

        // With duplicate IDs, painter order is observable. Conservative retirement
        // is required even if the unordered set of source objects is unchanged.
        var otherImage = Image(childId, SKColors.Gold);
        Step(new([background], [image, otherImage]), null, "duplicate imagery IDs");
        Step(new([background], [otherImage, image]), null, "duplicate imagery order", requireChange: true);
        var duplicate = Vector(parent, "sand", 5);
        Step(new([background, duplicate]), null, "duplicate vector IDs");
        Step(new([duplicate, background]), null, "duplicate vector order", requireChange: true);
        Step(new([background, Vector(new(0, 0, 0), "wood", 6)]), null, "lower zoom ancestor arrival");
        Assert(TextureHash(initial) == retainedHash, "Streaming mutated texture bytes retained by an earlier frame.");
        return comparisons;

        void Step(Input input, TileId[]? changed, string name, bool requireAllRetained = false, bool requireChange = false)
        {
            using var frame = Prepare(renderer, camera, input, style);
            var current = Textures(frame);
            using var reference = new PixelMapRenderer();
            using var expected = Prepare(reference, camera, input, style);
            EqualTextures(frame, expected, $"{style}: {name}");
            int retained = 0, modified = 0;
            foreach (var (key, texture) in current)
            {
                if (!prior.TryGetValue(key, out var old)) continue;
                bool same = ReferenceEquals(old.Identity, texture.Identity);
                if (same) retained++;
                if (!old.Pixels.AsSpan().SequenceEqual(texture.Pixels)) modified++;
                if (requireAllRetained || changed is not null && !changed.Any(id => Overlaps(id, key.Id)))
                    Assert(same, $"{style}: {name} discarded unrelated patch {key.Id}.");
            }
            if (requireAllRetained) Assert(retained == current.Count, $"{name} changed the patch layout.");
            if (requireChange) Assert(modified > 0, $"{style}: {name} did not change any expected artwork pixels.");
            prior = current;
            comparisons++;
        }
    }

    private static int CheckOffscreenDependency()
    {
        var camera = Camera(new(14, 8192, 8192), 16.5);
        var otherCamera = camera with { Longitude = camera.Longitude + 1 };
        var background = Vector(new(0, 0, 0), "grass", 1);
        using var renderer = new PixelMapRenderer();
        using var first = Prepare(renderer, camera, new([background]), MapRenderStyle.Normal);
        var firstHash = TextureHash(first);
        var changedId = Textures(first).Keys.OrderByDescending(p => p.Id.Z).First().Id;
        using var distant = Prepare(renderer, otherCamera, new([background]), MapRenderStyle.Normal);
        var before = Textures(distant);
        Assert(Cache(renderer).ContainsKey(changedId), "Offscreen fixture evicted the texture before its dependency changed.");
        var replacement = Vector(changedId, "sand", 2);
        using var streamed = Prepare(renderer, otherCamera, new([background, replacement]), MapRenderStyle.Normal);
        foreach (var (key, texture) in Textures(streamed))
            Assert(before.TryGetValue(key, out var old) && ReferenceEquals(old.Identity, texture.Identity),
                "An offscreen source update replaced an unrelated displayed texture.");
        Assert(!Cache(renderer).ContainsKey(changedId), "An offscreen cached patch kept obsolete source artwork.");
        using var returned = Prepare(renderer, camera, new([background, replacement]), MapRenderStyle.Normal);
        using var cold = new PixelMapRenderer();
        using var expected = Prepare(cold, camera, new([background, replacement]), MapRenderStyle.Normal);
        EqualTextures(returned, expected, "return after offscreen source update");
        Assert(TextureHash(first) == firstHash, "An offscreen update mutated an older retained frame.");
        return 1;
    }

    private static int CheckWorldWrap()
    {
        var west = Vector(new(2, 3, 2), "grass", 1);
        var east = Vector(new(2, 0, 2), "wood", 2);
        int comparisons = 0;
        foreach (double longitude in new[] { -179.999, 179.999 })
        {
            var camera = new MapCamera(-20, longitude, 8, 17, 55, true);
            using var renderer = new PixelMapRenderer();
            using var first = Prepare(renderer, camera, new([west, east]), MapRenderStyle.Normal);
            var before = Textures(first);
            var replacement = Vector(east.Id, "sand", 3);
            using var next = Prepare(renderer, camera, new([west, replacement]), MapRenderStyle.Normal);
            using var cold = new PixelMapRenderer();
            using var expected = Prepare(cold, camera, new([west, replacement]), MapRenderStyle.Normal);
            EqualTextures(next, expected, $"world seam {longitude}");
            int reused = 0, changed = 0;
            foreach (var (key, texture) in Textures(next))
            {
                if (!before.TryGetValue(key, out var old)) continue;
                if (!Overlaps(key.Id, east.Id))
                {
                    Assert(ReferenceEquals(old.Identity, texture.Identity), "A world copy discarded unrelated canonical artwork.");
                    reused++;
                }
                else if (!old.Pixels.AsSpan().SequenceEqual(texture.Pixels)) changed++;
            }
            Assert(reused > 0 && changed > 0, "World-wrap fixture did not cover both sides of the seam.");
            comparisons++;
        }
        return comparisons;
    }

    private static int CheckNeighborShadows()
    {
        var id = new TileId(14, 8192, 8192);
        var east = new MapTile(id with { X = id.X + 1 }, []);
        var building = new MapFeature("building", "", "", "", 90, 4096, GeometryType.Polygon,
            [[new(3910, 1900), new(4010, 1900), new(4010, 2110), new(3910, 2110)]], 300, 0, false, 0, 0, "");
        var west = new MapTile(id, [building]);
        var camera = Camera(id, 16) with { Longitude = (id.X + 1d) / (1 << id.Z) * 360 - 180, Bearing = 0 };
        using var renderer = new PixelMapRenderer();
        using var sunlit = Prepare(renderer, camera, new([east]), MapRenderStyle.Pixel);
        using var shaded = Prepare(renderer, camera, new([east, west]), MapRenderStyle.Pixel);
        using var expectedRenderer = new PixelMapRenderer();
        using var expected = Prepare(expectedRenderer, camera, new([east, west]), MapRenderStyle.Pixel);
        EqualTextures(shaded, expected, "neighboring shadow arrival");
        Assert(ChangedInside(sunlit, shaded, east.Id), "A new neighboring caster did not update the unchanged target tile's shadow.");

        var emptyWest = new MapTile(id, []);
        using var removed = Prepare(renderer, camera, new([east, emptyWest]), MapRenderStyle.Pixel);
        using var removedRenderer = new PixelMapRenderer();
        using var removedReference = Prepare(removedRenderer, camera, new([east, emptyWest]), MapRenderStyle.Pixel);
        EqualTextures(removed, removedReference, "neighboring caster replacement");
        Assert(ChangedInside(shaded, removed, east.Id), "Replacing a neighboring caster left a stale shadow.");

        using var shadedAgain = Prepare(renderer, camera, new([east, west]), MapRenderStyle.Pixel);
        var distant = Enumerable.Range(0, 40).Select(i => new MapTile(new(14, 1000 + i, 1000), [])).ToArray();
        MapTile[] withoutCaster = [east, .. distant];
        using var evicted = Prepare(renderer, camera, new(withoutCaster), MapRenderStyle.Pixel);
        using var evictedRenderer = new PixelMapRenderer();
        using var evictedReference = Prepare(evictedRenderer, camera, new(withoutCaster), MapRenderStyle.Pixel);
        EqualTextures(evicted, evictedReference, "neighboring caster eviction");
        Assert(ChangedInside(shadedAgain, evicted, east.Id), "Evicting a retained neighboring caster left a stale shadow.");
        return 3;
    }

    private static bool ChangedInside(GpuMapFrame a, GpuMapFrame b, TileId source)
    {
        var previous = Textures(a);
        return Textures(b).Any(pair => Overlaps(pair.Key.Id, source) && previous.TryGetValue(pair.Key, out var old) &&
            !old.Pixels.AsSpan().SequenceEqual(pair.Value.Pixels));
    }

    internal static async Task RunBenchmarkAsync(string[] args)
    {
        int samples = int.Parse(Value(args, "--terrain-streaming-samples=") ?? "32");
        int warmup = int.Parse(Value(args, "--terrain-streaming-warmup=") ?? "8");
        string? filter = Value(args, "--terrain-streaming-filter=");
        if (samples is < 1 or > 256 || warmup is < 0 or > 256) throw new ArgumentOutOfRangeException(nameof(args));
        string output = Path.GetFullPath(Value(args, "--terrain-streaming-output=") ?? Path.Combine(Path.GetTempPath(), "PixelMap.TerrainStreaming"));
        using var http = new HttpClient(new OfflineHandler());
        var source = new VectorTileSource(http);
        var tiles = new List<MapTile>();
        for (int y = 6159; y <= 6161; y++)
        for (int x = 4822; x <= 4824; x++) tiles.Add(await source.GetTileAsync(new(14, x, y)));
        MapTile[] background = tiles.ToArray();
        var camera = new MapCamera(40.7087, -74.0104, 16.5, 23, 58, true);
        var reports = new List<object>();
        foreach (var style in new[] { MapRenderStyle.Pixel, MapRenderStyle.Normal })
        foreach (var workload in new[] { "distant-vector-stream", "local-vector-stream", "local-imagery-stream", "source-priority-control" })
        {
            string name = $"{workload}-{style.ToString().ToLowerInvariant()}";
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            using var renderer = new PixelMapRenderer();
            using var initial = Prepare(renderer, camera, new(background), style);
            var ids = Textures(initial).Keys.Select(p => p.Id).Distinct().OrderByDescending(id => id.Z).ToArray();
            var child = ids[ids.Length / 2];
            Assert(child.Z > 14 && ids.Any(id => !Overlaps(id, child)), "Streaming workload has no localized source region.");
            var values = new List<object>();
            var elapsed = new List<double>();
            var allocations = new List<long>();
            int rebakes = 0, presented = 0;
            for (int sample = -warmup; sample < samples; sample++)
            {
                int index = sample + warmup;
                // Inputs are functions of sample index, never elapsed time. Real
                // bundled city tiles supply the background; tiny changing sources
                // isolate invalidation from network/decode and complex tessellation.
                Input input = workload switch
                {
                    "distant-vector-stream" => new([.. background, Vector(new(14, 1000, 1000), index % 2 == 0 ? "grass" : "wood", (ulong)index + 1)]),
                    "local-vector-stream" => new([.. background, Vector(child, index % 2 == 0 ? "grass" : "wood", (ulong)index + 1)]),
                    "local-imagery-stream" => new(background, [Image(child, index % 2 == 0 ? SKColors.DarkOliveGreen : SKColors.CornflowerBlue)]),
                    _ => new(index % 2 == 0 ? background.Reverse().ToArray() : background)
                };
                var before = Cache(renderer);
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                using var frame = Prepare(renderer, camera, input, style);
                double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                var after = Cache(renderer);
                var textures = Textures(frame);
                var currentIds = textures.Keys.Select(p => p.Id).Distinct().ToArray();
                int created = currentIds.Count(id => !before.TryGetValue(id, out var old) || !ReferenceEquals(old, after[id]));
                if (sample < 0) continue;
                string hash = TextureHash(frame);
                elapsed.Add(milliseconds);
                allocations.Add(allocated);
                rebakes += created;
                presented += currentIds.Length;
                values.Add(new { Frame = sample, PrepareMs = milliseconds, AllocatedBytes = allocated,
                    TerrainPatches = currentIds.Length, RebakedTextures = created,
                    RebakedBaseBytes = (long)created * 256 * 256 * sizeof(uint), TextureHash = hash });
            }
            var ordered = elapsed.Order().ToArray();
            reports.Add(new { Name = name, RenderStyle = style.ToString(), Camera = camera, ChangedLocalSource = child,
                MedianMs = ordered[ordered.Length / 2], P95Ms = ordered[(int)Math.Ceiling(ordered.Length * .95) - 1],
                MeanAllocatedBytes = allocations.Average(), TotalRebakedTextures = rebakes,
                TotalPresentedTextures = presented, Samples = values });
            Console.WriteLine($"Terrain streaming {name}: median {ordered[ordered.Length / 2]:F4} ms, {allocations.Average():F0} managed bytes/frame, {rebakes}/{presented} actual presented texture rebakes.");
        }
        if (reports.Count == 0) throw new ArgumentException("No matching terrain streaming workload.");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "terrain-streaming.json"), JsonSerializer.Serialize(new
        {
            Runtime = RuntimeInformation.FrameworkDescription, Platform = RuntimeInformation.OSDescription,
            Processors = Environment.ProcessorCount, Width, Height, Warmup = warmup, Samples = samples,
            Scope = "Native public PrepareGpuFrame with prepareLegacyOverlay:false over nine bundled Manhattan source tiles. Fixed-camera deterministic source churn isolates complete versus localized terrain invalidation. Tiny synthetic vector/imagery sources alternate by frame index. Fixture creation, decoding outside renderer, cache diagnostics, texture hashing, GPU submission and disposal are excluded from timed/allocation regions. Cached texture object identity independently counts real rebakes. This is CPU preparation, not browser or GPU frame time.",
            RendererSha256 = FileHash(typeof(PixelMapRenderer).Assembly.Location),
            HarnessSha256 = FileHash(typeof(TerrainArtworkInvalidationChecks).Assembly.Location), Reports = reports
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static GpuMapFrame Prepare(PixelMapRenderer renderer, MapCamera camera, Input input, MapRenderStyle style) =>
        renderer.PrepareGpuFrame(Width, Height, camera, input.Tiles, PixelStyle.Day, 2, input.Imagery, null, style,
            prepareLegacyOverlay: false);

    private static MapCamera Camera(TileId id, double zoom)
    {
        double n = 1 << id.Z;
        double y = (id.Y + .5) / n;
        return new(Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y))) * 180 / Math.PI,
            (id.X + .5) / n * 360 - 180, zoom, 23, 55, true);
    }

    private static MapTile Vector(TileId id, string kind, ulong key) => new(id,
        [new("landcover", kind, "", "", key, 4096, GeometryType.Polygon,
            [[new(0, 0), new(4096, 0), new(4096, 4096), new(0, 4096)]], null, null, false, 0, 0, "")]);

    private static ImageryTile Image(TileId id, SKColor color)
    {
        using var bitmap = new SKBitmap(8, 8, SKColorType.Bgra8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(id, encoded.ToArray());
    }

    private static bool Overlaps(TileId a, TileId b)
    {
        // Independent geographic rectangle oracle, rather than the production's
        // integer ancestor test. Dyadic endpoints are exactly representable here.
        double scaleA = Math.ScaleB(1, -a.Z), scaleB = Math.ScaleB(1, -b.Z);
        return a.X * scaleA < (b.X + 1d) * scaleB && b.X * scaleB < (a.X + 1d) * scaleA &&
            a.Y * scaleA < (b.Y + 1d) * scaleB && b.Y * scaleB < (a.Y + 1d) * scaleA;
    }

    private static Dictionary<Patch, Texture> Textures(GpuMapFrame frame)
    {
        var values = new Dictionary<Patch, Texture>();
        foreach (object draw in (IEnumerable)Member(frame, "Terrain")!)
        {
            var mesh = Member(draw, "Mesh")!;
            var texture = Member(draw, "Texture")!;
            var key = new Patch((TileId)Member(mesh, "Id")!, (int)Member(mesh, "Copy")!);
            values.Add(key, new(texture, (uint[])Member(texture, "Texels")!));
        }
        return values;
    }

    private static Dictionary<TileId, object> Cache(PixelMapRenderer renderer)
    {
        if (Member(renderer, "reliefSurface") is not { } surface) return [];
        var values = (IDictionary)Member(surface, "textures")!;
        return values.Keys.Cast<TileId>().ToDictionary(id => id, id => values[id]!);
    }

    private static void EqualTextures(GpuMapFrame actual, GpuMapFrame expected, string name)
    {
        var a = Textures(actual);
        var b = Textures(expected);
        Assert(a.Count == b.Count, $"{name}: terrain layout differs from complete recomposition.");
        foreach (var (key, texture) in a)
            Assert(b.TryGetValue(key, out var other) && texture.Pixels.AsSpan().SequenceEqual(other.Pixels),
                $"{name}: patch {key} differs from complete recomposition.");
    }

    private static string TextureHash(GpuMapFrame frame)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (key, texture) in Textures(frame).OrderBy(pair => pair.Key.Id.Z).ThenBy(pair => pair.Key.Id.X)
            .ThenBy(pair => pair.Key.Id.Y).ThenBy(pair => pair.Key.Copy))
        {
            int[] identity = [key.Id.Z, key.Id.X, key.Id.Y, key.Copy];
            hash.AppendData(MemoryMarshal.AsBytes(identity.AsSpan()));
            hash.AppendData(MemoryMarshal.AsBytes(texture.Pixels.AsSpan()));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string FileHash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static object? Member(object value, string name) => value.GetType().GetField(name, Members) is { } field
        ? field.GetValue(value) : value.GetType().GetProperty(name, Members)!.GetValue(value);
    private static string? Value(string[] args, string prefix) => args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Terrain streaming fixtures require bundled tiles and never use the network.");
    }
}
