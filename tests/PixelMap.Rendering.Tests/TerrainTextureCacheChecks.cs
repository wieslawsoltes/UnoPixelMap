using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

/// <summary>GPU artwork must retain one immutable copy while CPU fallback remains
/// byte-exact. Exercise the moving working set that exceeded the old entry limit.</summary>
internal static class TerrainTextureCacheChecks
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const long Budget = 48L * 1024 * 1024;
    private const int Width = 1100, Height = 650, Pixels = 3;

    internal static async Task RunAsync()
    {
        RoundTrip(opaque: true);
        RoundTrip(opaque: false);
        CheckBudget();
        await CheckMovingCacheAsync();
        await TerrainTextureCopyChecks.RunAsync();
        Console.WriteLine("Passed opaque/nonopaque terrain texture round trips, 48 MiB mixed-cache budget, CPU fallback, retained frames and source invalidation.");
    }

    private static void RoundTrip(bool opaque)
    {
        var bitmap = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var pixels = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 0xff000000 | ((uint)i * 1664525 & 0xffffff);
        if (!opaque) pixels[73] &= 0x7fffffff;
        var expected = bitmap.GetPixelSpan().ToArray();
        var type = typeof(TerrainSurface).GetNestedType("Texture", BindingFlags.NonPublic)!;
        var texture = type.GetConstructors(Members).Single().Invoke([
            bitmap, new SKRect[] { new(10.3f, 5.4f, 90.2f, 130.9f), new(150, 70, 250, 210) }]);
        using var lifetime = (IDisposable)texture;
        var gpu = Call(texture, "PrepareGpu")!;
        var originalGpuHash = Hash((uint[])Field(gpu, "Texels")!);
        Assert((Field(texture, "bitmap") is null) == opaque,
            "GPU promotion discarded nonopaque data or retained an unnecessary opaque bitmap.");
        var restored = (SKBitmap)Property(texture, "Bitmap")!;
        Assert(restored.GetPixelSpan().SequenceEqual(expected), "CPU fallback did not reconstruct the exact original bitmap bytes.");
        Assert(opaque || ReferenceEquals(bitmap, restored), "A nonopaque original was replaced by an opaque reconstruction.");
        Assert(ReferenceEquals(gpu, Call(texture, "PrepareGpu")), "Returning to GPU rendering replaced unchanged immutable texels.");
        Assert(Hash((uint[])Field(gpu, "Texels")!) == originalGpuHash, "CPU restoration mutated a pending GPU texture.");
    }

    private static void CheckBudget()
    {
        using var surface = new TerrainSurface();
        var get = typeof(TerrainSurface).GetMethod("GetTexture", Members)!;
        var trim = typeof(TerrainSurface).GetMethod("TrimCaches", Members)!;
        Func<TileId, SKBitmap, SKRect[]> bake = (id, bitmap) =>
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor((byte)id.X, (byte)(id.X * 7), 93));
            if (id.X % 17 == 0) MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan())[0] &= 0x7fffffff;
            return [];
        };
        object? retainedGpu = null;
        string? retainedHash = null;
        for (int i = 0; i < 212; i++)
        {
            typeof(TerrainSurface).GetField("frame", Members)!.SetValue(surface, (long)i);
            var texture = get.Invoke(surface, [new TileId(8, i, 100), bake])!;
            var gpu = Call(texture, "PrepareGpu")!;
            if (i == 0) { retainedGpu = gpu; retainedHash = Hash((uint[])Field(gpu, "Texels")!); }
            trim.Invoke(surface, [true]);
            CheckCache(surface, gpu: true);
        }
        Assert(Cache(surface).Count > 96, "GPU-only entries did not use the available byte budget.");
        Assert(Cache(surface).Count < 192, "The byte budget ignored retained nonopaque native bitmaps.");
        var restoredIds = Cache(surface).Keys.TakeLast(64).ToArray();
        foreach (var id in restoredIds)
        {
            typeof(TerrainSurface).GetField("frame", Members)!.SetValue(surface, 300L);
            var texture = get.Invoke(surface, [id, bake])!;
            _ = (SKBitmap)Property(texture, "Bitmap")!;
        }
        trim.Invoke(surface, [false]);
        CheckCache(surface, gpu: false);
        Assert(Hash((uint[])Field(retainedGpu!, "Texels")!) == retainedHash,
            "Eviction or CPU fallback changed an immutable texture retained by an older frame.");
    }

    private static async Task CheckMovingCacheAsync()
    {
        using var http = new HttpClient(new OfflineHandler());
        var source = new VectorTileSource(http);
        var tiles = new List<MapTile>();
        for (int y = 6159; y <= 6161; y++)
        for (int x = 4822; x <= 4824; x++) tiles.Add(await source.GetTileAsync(new(14, x, y)));
        var route = RouteDemos.ForMode(TravelMode.Drive);
        var track = new RouteTrack(route);
        using var renderer = new PixelMapRenderer();
        TestLabelFont.Apply(renderer);
        var workingSet = new HashSet<TileId>();
        var created = new int[3];
        GpuMapFrame? retained = null;
        string? retainedHash = null;
        MapCamera camera = default;
        try
        {
            for (int i = 0; i < 48; i++)
            {
                int phase = i % 16;
                var position = track.Sample(route.DistanceMeters * .35 + phase * 1.5);
                camera = MapCamera.FromEye(position.Point.Latitude, position.Point.Longitude, 7,
                    position.Bearing + Math.Sin(phase * Math.PI / 8) * 5, 82, Width, Height);
                var before = Field(renderer, "reliefSurface") is { } oldSurface ? Cache(oldSurface) : [];
                var frame = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, Pixels);
                if (retained is null) { retained = frame; retainedHash = FrameHash(frame); }
                var surface = Field(renderer, "reliefSurface")!;
                var after = Cache(surface);
                foreach (var draw in frame.Terrain)
                {
                    workingSet.Add(draw.Mesh.Id);
                    if (!before.TryGetValue(draw.Mesh.Id, out var previous) || !ReferenceEquals(previous, after[draw.Mesh.Id]))
                        created[i / 16]++;
                }
                CheckCache(surface, gpu: true);
                if (!ReferenceEquals(frame, retained)) frame.Dispose();
            }
            Assert(workingSet.Count > 96 && workingSet.Count <= 192,
                "The moving fixture no longer exercises a working set beyond the former 96-entry limit.");
            Assert(created[1] == 0 && created[2] == 0,
                $"Moving chase rebaked cached terrain after warmup: {string.Join('/', created)} textures per cycle.");
            Assert(FrameHash(retained!) == retainedHash, "Moving the camera mutated a retained frame's GPU textures.");

            // A DEM update changes geometry without invalidating camera-independent artwork.
            var previousCache = Cache(Field(renderer, "reliefSurface")!);
            var terrain = new TerrainOptions(new([new(new(0, 0, 0), 2, new float[4])]));
            using (var dem = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, Pixels, null, terrain))
            {
                var updated = Cache(Field(renderer, "reliefSurface")!);
                Assert(previousCache.Keys.Intersect(updated.Keys).Any(), "The DEM fixture has no shared artwork to verify.");
                Assert(previousCache.Keys.Intersect(updated.Keys).All(id => ReferenceEquals(previousCache[id], updated[id])),
                    "A DEM-only change discarded unchanged artwork.");
            }

            // Render through the real CPU fallback after GPU bitmaps have been released.
            using var expected = new PixelMapRenderer();
            TestLabelFont.Apply(expected);
            using var a = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var b = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var ca = new SKCanvas(a);
            using var cb = new SKCanvas(b);
            renderer.Draw(ca, Width, Height, camera, tiles, PixelStyle.Day, Pixels, null, false, false, terrain);
            expected.Draw(cb, Width, Height, camera, tiles, PixelStyle.Day, Pixels, null, false, false, terrain);
            Assert(a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()), "GPU→CPU terrain restoration changed rendered pixels.");
            CheckCache(Field(renderer, "reliefSurface")!, gpu: false);
            using (var resumed = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, Pixels, null, terrain))
                CheckCache(Field(renderer, "reliefSurface")!, gpu: true);

            var old = Cache(Field(renderer, "reliefSurface")!);
            using (var styled = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Dusk, Pixels, null, terrain))
                AssertReplaced(old, Cache(Field(renderer, "reliefSurface")!), "palette");
            old = Cache(Field(renderer, "reliefSurface")!);
            using (var styled = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Dusk, Pixels, null, terrain, MapRenderStyle.Normal))
                AssertReplaced(old, Cache(Field(renderer, "reliefSurface")!), "render style");
            var imagery = new[] { Image(SKColors.DarkOliveGreen) };
            using (var photographed = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Dusk, Pixels, imagery, terrain)) { }
            old = Cache(Field(renderer, "reliefSurface")!);
            imagery[0] = Image(SKColors.MidnightBlue);
            using (var updated = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Dusk, Pixels, imagery, terrain))
                AssertReplaced(old, Cache(Field(renderer, "reliefSurface")!), "imagery source");
            renderer.ActivateGpuFrame(retained!);
            Assert(FrameHash(retained!) == retainedHash, "A→B→A frame activation lost old artwork after invalidation.");
            Console.WriteLine($"Terrain cache: {workingSet.Count} moving-chase tiles; new textures per 16-view cycle {string.Join('/', created)}; exact CPU fallback and retained-frame hashes.");
        }
        finally { retained?.Dispose(); }
    }

    private static void CheckCache(object surface, bool gpu)
    {
        var cache = Cache(surface);
        long bytes = 0;
        foreach (var texture in cache.Values)
        {
            if (Field(texture, "bitmap") is SKBitmap bitmap) bytes += (long)bitmap.RowBytes * bitmap.Height;
            if (Field(texture, "Gpu") is { } image) bytes += ((uint[])Field(image, "Texels")!).LongLength * sizeof(uint);
        }
        Assert(bytes <= Budget, $"Terrain cache retained {bytes} bytes, exceeding 48 MiB.");
        Assert(cache.Count <= (gpu ? 192 : 96), "Terrain cache exceeded the GPU/CPU entry bound.");
    }

    private static void AssertReplaced(Dictionary<TileId, object> previous, Dictionary<TileId, object> next, string reason)
    {
        Assert(next.Count > 0 && !previous.Values.Intersect(next.Values, ReferenceEqualityComparer.Instance).Any(),
            $"Changing {reason} reused obsolete terrain artwork.");
    }

    private static ImageryTile Image(SKColor color)
    {
        using var bitmap = new SKBitmap(8, 8, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(new(0, 0, 0), encoded.ToArray());
    }

    private static string FrameHash(GpuMapFrame frame)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var draw in frame.Terrain) hash.AppendData(MemoryMarshal.AsBytes(draw.Texture.Texels.AsSpan()));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Hash(uint[] values) => Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));
    private static Dictionary<TileId, object> Cache(object surface)
    {
        var dictionary = (IDictionary)Field(surface, "textures")!;
        return dictionary.Keys.Cast<TileId>().ToDictionary(id => id, id => dictionary[id]!);
    }
    private static object? Field(object value, string name) => value.GetType().GetField(name, Members)!.GetValue(value);
    private static object? Property(object value, string name) => value.GetType().GetProperty(name, Members)!.GetValue(value);
    private static object? Call(object value, string name) => value.GetType().GetMethod(name, Members)!.Invoke(value, null);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Terrain cache checks require bundled tiles and never use the network.");
    }
}
