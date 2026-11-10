using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

/// <summary>Compare opaque dyadic texture copies against the retained Skia rendering
/// path. The cost mode calls only the unchanged private bake signature, allowing the
/// same compiled harness to measure the previous renderer assembly.</summary>
internal static class TerrainTextureCopyChecks
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private delegate SKRect[] Bake(TileId id, SKBitmap texture, int pixels, PixelPalette palette, bool shadows);

    internal static Task RunAsync(string[]? args = null) =>
        args?.Contains("--terrain-texture-copy-cost", StringComparer.Ordinal) == true ? CostAsync(args) :
        args?.Contains("--terrain-frame-cost", StringComparer.Ordinal) == true ? FrameCostAsync(args) : RunChecksAsync();

    private static async Task RunChecksAsync()
    {
        using var http = new HttpClient(new OfflineHandler());
        var source = new VectorTileSource(http);
        var real = await source.GetTileAsync(new(14, 4823, 6160));
        MapTile[] tiles = [Fixture(new(0, 0, 0)), real];
        int checkedTextures = 0;
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        foreach (var palette in new[] { PixelPalette.Day, PixelPalette.Night })
        foreach (var tile in tiles)
        {
            using var renderer = new PixelMapRenderer();
            typeof(PixelMapRenderer).GetMethod("SetRenderStyle", Members)!.Invoke(renderer, [style]);
            using var geometry = new TileGeometry(tile, style);
            geometry.ShadowSources.Add(new(geometry, 0, 0));
            var cache = new TerrainCache();
            try
            {
                foreach (int delta in new[] { 0, 1, 3, 4, 5, 7, 8, 9, 12, 13, 18, 22 }.Where(z => z + tile.Id.Z <= 22))
                foreach (int position in new[] { 0, 1, (1 << delta) / 2 - 1, (1 << delta) / 2, (1 << delta) - 2, (1 << delta) - 1 }
                    .Where(value => value >= 0 && value < (1 << delta)).Distinct())
                {
                    int count = 1 << delta;
                    var patch = new TileId(tile.Id.Z + delta, tile.Id.X * count + position,
                        tile.Id.Y * count + count - 1 - position);
                    int pixels = checkedTextures % 3 is 0 ? 1 : checkedTextures % 3 is 1 ? 3 : 8;
                    bool shadows = (checkedTextures & 1) == 0;
                    using var expected = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    using var actual = new SKBitmap();
                    Assert(actual.TryAllocPixels(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque), 1056),
                        "Could not allocate the padded-stride terrain fixture.");
                    actual.GetPixelSpan().Fill(0xa5);
                    Reference(cache, expected, patch, geometry, pixels, shadows, palette, renderer);
                    Assert(cache.TryCopyTexture(actual, patch, geometry, pixels, shadows, palette, renderer),
                        "A fully covered opaque dyadic texture did not use the copy path.");
                    for (int row = 0; row < 256; row++)
                    {
                        Assert(actual.GetPixelSpan().Slice(row * actual.RowBytes, 1024)
                            .SequenceEqual(expected.GetPixelSpan().Slice(row * expected.RowBytes, 1024)),
                            $"Dyadic texture pixels differ at {style}/{palette.Style}, source {tile.Id}, patch {patch}, row {row}.");
                        int padding = Math.Min(actual.RowBytes - 1024, actual.GetPixelSpan().Length - row * actual.RowBytes - 1024);
                        Assert(actual.GetPixelSpan().Slice(row * actual.RowBytes + 1024, padding).IndexOfAnyExcept((byte)0xa5) < 0,
                            "The texture copy modified destination row padding.");
                    }
                    checkedTextures++;
                }
                using var transparent = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul);
                transparent.GetPixelSpan().Fill(0x35);
                Assert(!cache.TryCopyTexture(transparent, tile.Id, geometry, 3, true, palette, renderer) &&
                    transparent.GetPixelSpan().IndexOfAnyExcept((byte)0x35) < 0,
                    "The opaque copy path accepted or modified a transparent destination.");
            }
            finally { cache.Clear(); }
        }
        CheckInteriorBoundaries();
        CheckCoverage();
        CheckGroundPlay(real);
        Console.WriteLine($"Passed {checkedTextures:N0} exact terrain copies against Skia across patch zoom 0–22, all render styles, day/night, shadow and pixel-size changes, edge subpixels, BGRA channels and padded strides; mixed/partial/photographic coverage remains composited.");
    }

    private static void Reference(TerrainCache cache, SKBitmap bitmap, TileId patch, TileGeometry tile, int pixels,
        bool shadows, PixelPalette palette, PixelMapRenderer renderer)
    {
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(palette.Ground));
        double relative = Math.Pow(2, patch.Z - tile.Source.Id.Z);
        float size = (float)(256 * relative);
        float left = (float)((tile.Source.Id.X * relative - patch.X) * 256);
        float top = (float)((tile.Source.Id.Y * relative - patch.Y) * 256);
        float scale = size / TileGeometry.Extent;
        var matrix = SKMatrix.CreateScaleTranslation(scale, scale, left, top);
        canvas.SetMatrix(in matrix);
        canvas.ClipRect(new(0, 0, TileGeometry.Extent, TileGeometry.Extent));
        cache.Draw(canvas, tile, Math.Min(1, scale), pixels, shadows, false, palette, renderer);
        canvas.Flush();
    }

    private static void CheckCoverage()
    {
        using var renderer = new PixelMapRenderer();
        var list = (List<TileGeometry>)typeof(PixelMapRenderer).GetField("reliefTiles", Members)!.GetValue(renderer)!;
        var select = typeof(PixelMapRenderer).GetMethod("SingleTerrainSource", Members)!;
        using var parent = new TileGeometry(Fixture(new(1, 0, 0)));
        using var child = new TileGeometry(Fixture(new(3, 1, 1)));
        using var sibling = new TileGeometry(Fixture(new(3, 0, 1)));
        using var duplicate = new TileGeometry(Fixture(child.Source.Id));
        list.Add(parent);
        Assert(ReferenceEquals(select.Invoke(renderer, [child.Source.Id]), parent), "A fully covering ancestor was not selected.");
        list.Add(child);
        Assert(select.Invoke(renderer, [parent.Source.Id]) is null, "A partially overlapping child was incorrectly treated as one complete source.");
        Assert(ReferenceEquals(select.Invoke(renderer, [child.Source.Id]), child), "The most detailed full source did not replace its ancestor.");
        list.Add(sibling);
        Assert(ReferenceEquals(select.Invoke(renderer, [child.Source.Id]), child), "An adjacent source changed a nonoverlapping patch.");
        list.Add(duplicate);
        Assert(select.Invoke(renderer, [child.Source.Id]) is null, "Distinct duplicate sources bypassed ordered compositing.");
        list.Remove(duplicate);
        using (var photograph = new SKBitmap(8, 8, SKColorType.Bgra8888, SKAlphaType.Opaque))
        {
            photograph.Erase(new SKColor(37, 108, 211));
            using var image = SKImage.FromBitmap(photograph);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            ((List<ImageryTile>)typeof(PixelMapRenderer).GetField("imageryOrder", Members)!.GetValue(renderer)!)
                .Add(new(new(0, 0, 0), encoded.ToArray()));
            var bake = typeof(PixelMapRenderer).GetMethod("BakeTerrainTexture", Members)!.CreateDelegate<Bake>(renderer);
            using var output = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque);
            Assert(bake(child.Source.Id, output, 3, PixelPalette.Day, true).Length == 1,
                "A photographic source bypassed the normal compositor and lost its Earth coverage mask.");
        }
        list.Clear();
    }

    private static void CheckInteriorBoundaries()
    {
        using var renderer = new PixelMapRenderer();
        using var geometry = new TileGeometry(new(new(0, 0, 0), []));
        var cache = new TerrainCache();
        using var actual = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var expected = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque);
        try
        {
            Assert(cache.TryCopyTexture(actual, new(22, 0, 0), geometry, 3, true, PixelPalette.Day, renderer),
                "Could not initialize the retained block for the interior-boundary fixture.");
            var index = (IDictionary)typeof(TerrainCache).GetField("index", Members)!.GetValue(cache)!;
            var node = index.Values.Cast<object>().Single();
            var value = node.GetType().GetProperty("Value")!;
            var entry = value.GetValue(node)!;
            var key = entry.GetType().GetProperty("Key")!.GetValue(entry)!;
            var oldImage = (SKImage)entry.GetType().GetProperty("Image")!.GetValue(entry)!;
            using (var bitmap = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque))
            {
                var pixels = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
                for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                    pixels[y * 256 + x] = 0xff000000 | (uint)(x << 16) | (uint)(y << 8) | (uint)((x + y) & 255);
                bitmap.SetImmutable();
                // Adjacent pixels must differ: a real terrain block can be flat at
                // an interior boundary and conceal a nearest-sampling rounding error.
                var image = SKImage.FromBitmap(bitmap);
                try { value.SetValue(node, Activator.CreateInstance(entry.GetType(), Members, null, [key, image], null)); }
                catch { image.Dispose(); throw; }
                oldImage.Dispose();
            }
            int checks = 0;
            foreach (int difference in new[] { 16, 17, 18, 19, 20, 21, 22 })
            foreach (int boundary in new[] { 1, 31, 63, 127, 191, 254 })
            {
                int shift = difference - 4;
                int local = ((boundary + 1) << (shift - 8)) - 1;
                var patch = new TileId(difference, local, local);
                Assert(cache.TryCopyTexture(actual, patch, geometry, 3, true, PixelPalette.Day, renderer),
                    "An interior-boundary fixture did not take the exact-copy path.");
                Reference(cache, expected, patch, geometry, 3, true, PixelPalette.Day, renderer);
                Assert(actual.GetPixelSpan().SequenceEqual(expected.GetPixelSpan()),
                    $"Skia nearest-sampling differs at zoom {difference}, interior source-pixel boundary {boundary + 1}.");
                checks++;
            }
            Console.WriteLine($"Passed {checks} asymmetric cached-BGRA interior-boundary comparisons at patch zooms 16–22, including zoom22/local131071.");
        }
        finally { cache.Clear(); }
    }

    private static void CheckGroundPlay(MapTile tile)
    {
        double n = 1 << tile.Id.Z;
        double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (tile.Id.Y + .5) / n))) * 180 / Math.PI;
        double longitude = (tile.Id.X + .5) / n * 360 - 180;
        using var renderer = new PixelMapRenderer();
        TestLabelFont.Apply(renderer);
        var camera = MapCamera.FromEye(latitude, longitude, 1.7, 23, 90, 1100, 650);
        using var frame = renderer.PrepareGpuFrame(1100, 650, camera, [tile], PixelStyle.Day, 3);
        var select = typeof(PixelMapRenderer).GetMethod("SingleTerrainSource", Members)!;
        int eligible = frame.Terrain.Count(draw => select.Invoke(renderer, [draw.Mesh.Id]) is TileGeometry);
        Assert(eligible > 0, "The first-person ground fixture did not exercise complete-source terrain texture copies.");
        Console.WriteLine($"Terrain copy eligibility: {eligible}/{frame.Terrain.Length} cold first-person patches have one complete source.");
    }

    private static async Task CostAsync(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault(value => value.StartsWith("--terrain-copy-output=", StringComparison.Ordinal))?
            ["--terrain-copy-output=".Length..] ?? Path.Combine(Path.GetTempPath(), "PixelMap.TerrainCopy"));
        int samples = int.Parse(args.FirstOrDefault(value => value.StartsWith("--terrain-copy-samples=", StringComparison.Ordinal))?
            ["--terrain-copy-samples=".Length..] ?? "64");
        const int warmup = 16;
        using var http = new HttpClient(new OfflineHandler());
        var source = new VectorTileSource(http);
        var city = await source.GetTileAsync(new(14, 4823, 6160));
        var reports = new List<object>();
        foreach (var (name, tile, delta) in new[] { ("identity", city, 4), ("magnify-2", city, 5),
            ("magnify-16", city, 8), ("magnify-256", Fixture(new(0, 0, 0)), 12) })
        {
            using var renderer = new PixelMapRenderer();
            using var geometry = new TileGeometry(tile);
            geometry.ShadowSources.Add(new(geometry, 0, 0));
            ((List<TileGeometry>)typeof(PixelMapRenderer).GetField("reliefTiles", Members)!.GetValue(renderer)!).Add(geometry);
            var bake = typeof(PixelMapRenderer).GetMethod("BakeTerrainTexture", Members)!.CreateDelegate<Bake>(renderer);
            using var bitmap = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque);
            int count = 1 << delta;
            var patches = Enumerable.Range(0, 16).Select(i => new TileId(tile.Id.Z + delta,
                tile.Id.X * count + count / 2 + i % 4, tile.Id.Y * count + count / 2 + i / 4)).ToArray();
            var elapsed = new List<double>();
            var allocated = new List<long>();
            var hashes = new List<string[]>();
            for (int sample = -warmup; sample < samples; sample++)
            {
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                foreach (var patch in patches) _ = bake(patch, bitmap, 3, PixelPalette.Day, true);
                double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long allocation = GC.GetAllocatedBytesForCurrentThread() - bytes;
                if (sample < 0) continue;
                elapsed.Add(milliseconds);
                allocated.Add(allocation);
                var control = new string[patches.Length];
                for (int i = 0; i < patches.Length; i++)
                {
                    _ = bake(patches[i], bitmap, 3, PixelPalette.Day, true);
                    control[i] = Convert.ToHexStringLower(SHA256.HashData(bitmap.GetPixelSpan()));
                }
                hashes.Add(control);
            }
            double[] ordered = elapsed.Order().ToArray();
            reports.Add(new { Name = name, Source = tile.Id, ZoomDelta = delta, Patches = patches, MedianMs = ordered[samples / 2],
                P95Ms = ordered[(int)Math.Ceiling(samples * .95) - 1], SamplesMs = elapsed, AllocatedBytes = allocated, Hashes = hashes });
            Console.WriteLine($"Terrain texture {name}: {ordered[samples / 2]:F4} ms per 16 patches, {allocated.Average():F0} managed bytes.");
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "terrain-copy.json"), JsonSerializer.Serialize(new
        {
            Runtime = RuntimeInformation.FrameworkDescription, Platform = RuntimeInformation.OSDescription,
            Processors = Environment.ProcessorCount, Warmup = warmup, Samples = samples, Width = 256, Height = 256,
            Scope = "Native BakeTerrainTexture into a reusable bitmap over 16 deterministic patches per sample; source block cache warm; fixture preparation, bitmap allocation and control hashing excluded. Exact same private bake delegate signature for baseline/candidate.",
            RendererSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(PixelMapRenderer).Assembly.Location))),
            HarnessSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(TerrainTextureCopyChecks).Assembly.Location))),
            Reports = reports
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task FrameCostAsync(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault(value => value.StartsWith("--terrain-frame-output=", StringComparison.Ordinal))?
            ["--terrain-frame-output=".Length..] ?? Path.Combine(Path.GetTempPath(), "PixelMap.TerrainFrames"));
        string? filter = args.FirstOrDefault(value => value.StartsWith("--terrain-frame-filter=", StringComparison.Ordinal))?
            ["--terrain-frame-filter=".Length..];
        int samples = int.Parse(args.FirstOrDefault(value => value.StartsWith("--terrain-frame-samples=", StringComparison.Ordinal))?
            ["--terrain-frame-samples=".Length..] ?? "64");
        if (samples is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(args));
        const int warmup = 16, width = 1100, height = 650, pixels = 3;
        using var http = new HttpClient(new OfflineHandler());
        var source = new VectorTileSource(http);
        var tiles = new List<MapTile>();
        for (int y = 6159; y <= 6161; y++)
        for (int x = 4822; x <= 4824; x++) tiles.Add(await source.GetTileAsync(new(14, x, y)));
        var tileIds = tiles.Select(tile => tile.Id).ToHashSet();
        var route = RouteDemos.ForMode(TravelMode.Drive);
        var track = new RouteTrack(route);
        var reports = new List<object>();
        string labelFont = "";
        foreach (var (name, distanceStep, eyeHeight, pitch) in new[]
        {
            ("walk-first-person", 1.5, 1.7, 90d),
            ("drive-first-person", 6d, 1.7, 90d),
            ("drive-chase", 6d, 7d, 82d),
            ("retained-control", 0d, 7d, 82d)
        })
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            if (route.DistanceMeters * .35 + (samples + warmup - 1) * distanceStep >= route.DistanceMeters)
                throw new ArgumentOutOfRangeException(nameof(args), "Reduce --terrain-frame-samples so the deterministic trace never clamps at the route end.");
            using var renderer = new PixelMapRenderer();
            labelFont = TestLabelFont.Apply(renderer);
            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            var times = new List<double>();
            var allocations = new List<long>();
            var values = new List<object>();
            int totalMisses = 0, eligibleMisses = 0, totalPatches = 0;
            for (int sample = -warmup; sample < samples; sample++)
            {
                int step = sample + warmup;
                var position = track.Sample(route.DistanceMeters * .35 + step * distanceStep);
                // Unique trace positions are fixed by sample index, never by elapsed
                // runtime. The retained control changes bearing without changing its
                // footprint enough to need new texture patches.
                var camera = MapCamera.FromEye(position.Point.Latitude, position.Point.Longitude, eyeHeight,
                    position.Bearing + (distanceStep == 0 ? step % 16 * .00001 : 0), pitch, width, height,
                    viewDistanceMeters: 6000);
                var before = CacheEntries(renderer);
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                using var frame = renderer.PrepareGpuFrame(width, height, camera, tiles, PixelStyle.Day, pixels);
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                var after = CacheEntries(renderer);
                var ids = frame.Terrain.Select(draw => draw.Mesh.Id).Distinct().ToArray();
                var misses = ids.Where(id => !before.TryGetValue(id, out var old) || !ReferenceEquals(old, after[id])).ToArray();
                int eligible = misses.Count(id => id.Z >= 14 && tileIds.Contains(new TileId(14, id.X >> (id.Z - 14), id.Y >> (id.Z - 14))));
                if (sample < 0) continue;
                canvas.Clear(SKColors.Transparent);
                renderer.DrawGpuOverlay(canvas, frame);
                canvas.Flush();
                using var textureHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var draw in frame.Terrain)
                {
                    int[] identity = [draw.Mesh.Id.Z, draw.Mesh.Id.X, draw.Mesh.Id.Y, draw.Mesh.Copy];
                    textureHash.AppendData(MemoryMarshal.AsBytes(identity.AsSpan()));
                    textureHash.AppendData(MemoryMarshal.AsBytes(draw.Texture.Texels.AsSpan()));
                }
                times.Add(elapsed);
                allocations.Add(allocated);
                totalMisses += misses.Length;
                eligibleMisses += eligible;
                totalPatches += ids.Length;
                values.Add(new
                {
                    Frame = sample, RouteDistanceMeters = route.DistanceMeters * .35 + step * distanceStep,
                    Camera = camera, PrepareMs = elapsed, AllocatedBytes = allocated,
                    TerrainPatches = ids.Length, TextureMisses = misses.Length, EligibleMisses = eligible,
                    MissedPatches = misses, TextureHash = Convert.ToHexStringLower(textureHash.GetHashAndReset()),
                    OverlayHash = Convert.ToHexStringLower(SHA256.HashData(bitmap.GetPixelSpan()))
                });
            }
            double[] ordered = times.Order().ToArray();
            reports.Add(new
            {
                Name = name, DistanceStepMeters = distanceStep, EyeHeightMeters = eyeHeight, Pitch = pitch,
                MedianMs = ordered[samples / 2], P95Ms = ordered[(int)Math.Ceiling(samples * .95) - 1],
                MeanAllocatedBytes = allocations.Average(), TotalTerrainPatches = totalPatches, TotalTextureMisses = totalMisses,
                EligibleTextureMisses = eligibleMisses, EligibleMissPercent = totalMisses == 0 ? 0 : eligibleMisses * 100d / totalMisses,
                Samples = values
            });
            Console.WriteLine($"Terrain frames {name}: {ordered[samples / 2]:F4} ms prepare, {allocations.Average():F0} managed bytes, " +
                $"{totalMisses}/{totalPatches} texture misses, {eligibleMisses} eligible for opaque copy.");
        }
        if (reports.Count == 0) throw new ArgumentException("No matching terrain-frame workload.");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "terrain-frame-cost.json"), JsonSerializer.Serialize(new
        {
            Runtime = RuntimeInformation.FrameworkDescription, Platform = RuntimeInformation.OSDescription,
            Processors = Environment.ProcessorCount, Warmup = warmup, Samples = samples, Width = width, Height = height, PixelSize = pixels,
            Scope = "Native public PrepareGpuFrame, including terrain texture misses, meshes, models, sparse depth and label preparation; fixed index-driven camera traces over nine bundled Manhattan tiles. Fixtures, cache diagnostics, CPU overlay playback, pixel hashing, GL submission and frame disposal excluded from timing and allocation. Source eligibility diagnostic assumes these unique same-zoom Map-only tiles.",
            LabelFont = labelFont,
            RendererSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(PixelMapRenderer).Assembly.Location))),
            HarnessSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(TerrainTextureCopyChecks).Assembly.Location))),
            Reports = reports
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<TileId, object> CacheEntries(PixelMapRenderer renderer)
    {
        if (typeof(PixelMapRenderer).GetField("reliefSurface", Members)!.GetValue(renderer) is not { } surface) return [];
        var entries = (IDictionary)surface.GetType().GetField("textures", Members)!.GetValue(surface)!;
        return entries.Keys.Cast<TileId>().ToDictionary(id => id, id => entries[id]!);
    }

    private static MapTile Fixture(TileId id)
    {
        var features = new List<MapFeature>();
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            string layer = (x + y) % 3 == 0 ? "water" : "landcover";
            string kind = (x + y) % 3 == 1 ? "wood" : "grass";
            int left = x * 512 + 7, top = y * 512 + 11;
            features.Add(new(layer, kind, "", "", (ulong)(y * 8 + x), 4096, GeometryType.Polygon,
                [[new(left, top), new(left + 483, top + 13), new(left + 497, top + 489), new(left + 3, top + 501)]],
                null, null, false, 0, 0, ""));
        }
        features.Add(new("transportation", "primary", "", "", 100, 4096, GeometryType.LineString,
            [[new(-100, 150), new(2057, 1871), new(4100, 4049)]], null, null, false, 0, 0, ""));
        features.Add(new("building", "", "", "", 101, 4096, GeometryType.Polygon,
            [[new(2021, 2075), new(2213, 2075), new(2213, 2291), new(2021, 2291)]], 60, null, false, 0, 0, ""));
        return new(id, features);
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Terrain texture checks use bundled data only.");
    }
}
