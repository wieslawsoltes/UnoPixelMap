using System.Security.Cryptography;
using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

internal static class EarthRasterChecks
{
    internal static void Run()
    {
        int images = 0;
        long pixels = 0;
        using var current = new EarthRaster();
        using var reference = new LegacyEarthRaster();
        var assembly = typeof(ImageryTile).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            const string prefix = "PixelMap.Imagery.";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".jpg", StringComparison.Ordinal)) continue;
            var parts = name[prefix.Length..^4].Split('-').Select(int.Parse).ToArray();
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var encoded = new MemoryStream();
            stream.CopyTo(encoded);
            Check(name, new(new(parts[0], parts[1], parts[2]), encoded.ToArray()));
        }

        foreach (var (kind, width, height) in new[]
        {
            ("smooth", 256, 256), ("checker", 256, 256), ("gradient", 256, 256), ("noise", 256, 256),
            ("irregular", 197, 341), ("tiny", 1, 1), ("large", 512, 512), ("thin", 2048, 1),
            ("non-square", 257, 129)
        })
        {
            byte[] encoded = Encode(kind, width, height);
            foreach (int zoom in new[] { 12, 14 })
            {
                // Different payloads intentionally reuse each key: replacing a decoded
                // tile must still invalidate both natural and art cache entries.
                Check($"{kind}/{zoom}", new(new(zoom, 5000, 3000), encoded));
            }
        }
        foreach (byte[] invalid in new byte[][] { [], [1, 2, 3], [0xff, 0xd8, 0xff, 0xd9], Encode("gradient", 2049, 1) })
        foreach (bool natural in new[] { false, true })
        {
            var tile = new ImageryTile(new(14, 1, 1), invalid);
            Assert(current.Get(tile, natural) is null && reference.Get(tile, natural) is null,
                "Undecodable or oversized imagery bypassed the codec guards.");
        }

        int clusters = CheckClusters();
        Console.WriteLine($"Passed {images} exact Earth textures ({pixels:N0} pixels), natural imagery, replacement/cache reuse, " +
            $"codec guards and {clusters:N0} palette/Bayer clusters against the frozen raster.");

        void Check(string name, ImageryTile tile)
        {
            foreach (bool natural in new[] { false, true })
            {
                var actual = current.Get(tile, natural);
                var expected = reference.Get(tile, natural);
                Assert(actual is not null && expected is not null, $"Fixture {name} did not decode.");
                Assert(ReferenceEquals(actual, current.Get(tile, natural)), $"Fixture {name} lost its decoded cache entry.");
                using var a = SKBitmap.FromImage(actual!);
                using var b = SKBitmap.FromImage(expected!);
                Assert(a.Width == b.Width && a.Height == b.Height && a.GetPixelSpan().SequenceEqual(b.GetPixelSpan()),
                    $"Earth preprocessing changed {name}, natural={natural}: " +
                    $"{Convert.ToHexString(SHA256.HashData(a.GetPixelSpan()))}/{Convert.ToHexString(SHA256.HashData(b.GetPixelSpan()))}.");
                images++;
                pixels += a.Width * (long)a.Height;
            }
        }
    }

    private static int CheckClusters()
    {
        Span<uint> pixels = stackalloc uint[20];
        int count = 0;
        for (int key = 0; key < 32768; key++)
        {
            int r = EarthInks.Linear[((key >> 10) & 31) * 255 / 31];
            int g = EarthInks.Linear[((key >> 5) & 31) * 255 / 31];
            int b = EarthInks.Linear[(key & 31) * 255 / 31];
            int pair = EarthInks.Resolve(r, g, b);
            if (pair != LegacyEarthInks.Resolve(r, g, b))
                throw new InvalidOperationException($"Earth palette resolution changed at key {key}.");
            for (int y = 0; y <= 2; y += 2)
            for (int x = 0; x <= 2; x += 2)
            {
                const uint sentinel = 0x12345678;
                pixels.Fill(sentinel);
                EarthInks.WriteCluster(pixels, 6, 5, x, y, pair);
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint expected = i switch
                    {
                        6 => LegacyEarthInks.Sample(pair, x, y),
                        7 => LegacyEarthInks.Sample(pair, x + 1, y),
                        11 => LegacyEarthInks.Sample(pair, x, y + 1),
                        12 => LegacyEarthInks.Sample(pair, x + 1, y + 1),
                        _ => sentinel
                    };
                    if (pixels[i] != expected)
                        throw new InvalidOperationException($"Earth cluster changed key {key}, phase {x}/{y}, offset {i}.");
                }
                count++;
            }
        }
        return count;
    }

    private static byte[] Encode(string kind, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var random = new Random(7257);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            bitmap.SetPixel(x, y, kind switch
            {
                "smooth" => new((byte)(60 + x / 8), (byte)(75 + y / 8), (byte)(80 + (x + y) / 16)),
                "checker" => (x + y) % 2 == 0 ? new(13, 27, 51) : new(194, 233, 255),
                "noise" => new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)),
                _ => new((byte)x, (byte)y, (byte)(x ^ y))
            });
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
