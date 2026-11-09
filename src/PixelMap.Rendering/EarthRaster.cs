using System.Runtime.CompilerServices;
using PixelMap.Data;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>Decoded, art-directed Earth textures. The global palette and world pixel grid are
/// shared by all tiles. Panning, rotation and request order cannot change a tile's colors.</summary>
internal sealed class EarthRaster : IDisposable
{
    private const long Budget = 24 * 1024 * 1024;
    private long bytes;
    private sealed record Entry(ImageryTile Source, bool Natural, SKImage? Image, long Bytes);
    private readonly Dictionary<(TileId Id, bool Natural), LinkedListNode<Entry>> index = [];
    private readonly LinkedList<Entry> usage = [];

    public SKImage? Get(ImageryTile tile, bool natural = false)
    {
        if (index.TryGetValue((tile.Id, natural), out var node))
        {
            if (ReferenceEquals(node.Value.Source.Encoded, tile.Encoded))
            {
                usage.Remove(node); usage.AddFirst(node);
                return node.Value.Image;
            }
            Drop(node);
        }
        var result = Decode(tile, natural);
        long cost = tile.Encoded.Length + 128L + (result is null ? 0 : result.Width * result.Height * 4L);
        index.Add((tile.Id, natural), usage.AddFirst(new Entry(tile, natural, result, cost)));
        bytes += cost;
        while ((bytes > Budget || usage.Count > 384) && usage.Count > 1) Drop(usage.Last!);
        return result;
    }

    private static unsafe SKImage? Decode(ImageryTile tile, bool natural)
    {
        int size = tile.Id.Z >= 13 ? 256 : 128;
        using var data = SKData.CreateCopy(tile.Encoded);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width is < 1 or > 2048 || codec.Info.Height is < 1 or > 2048) return null;
        using var decoded = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success) return null;
        if (natural)
        {
            decoded.SetImmutable();
            return SKImage.FromBitmap(decoded);
        }
        using var art = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Opaque);
        int width = decoded.Width, height = decoded.Height;
        // Keep the common source grids explicit. Integer 2×2 sums are exactly the
        // generic box integral, including its truncation after linear-light averaging.
        bool direct = width == size && height == size;
        bool half = width == size * 2 && height == size * 2;
        var src = new ReadOnlySpan<uint>((void*)decoded.GetPixels(), width * height);
        var dest = new Span<uint>((void*)art.GetPixels(), size * size);
        Span<LinearRgb> cluster = stackalloc LinearRgb[4];
        for (int y = 0; y < size; y += 2)
        {
            for (int x = 0; x < size; x += 2)
            {
                int r = 0, g = 0, b = 0;
                for (int i = 0; i < 4; i++)
                {
                    int sx = x + (i & 1), sy = y + (i >> 1);
                    cluster[i] = direct ? LinearRgb.From(src[sy * width + sx]) : half
                        ? Box2x2(src, width, sx, sy) : Box(src, width, height, sx, sy, size);
                    r += cluster[i].R; g += cluster[i].G; b += cluster[i].B;
                }
                var mean = new LinearRgb((r + 2) / 4, (g + 2) / 4, (b + 2) / 4);
                // Merge smooth regions into 2×2 clusters without sacrificing the original
                // 256px detail at a coastline, city block, glacier margin or field boundary.
                bool smooth = true;
                foreach (var p in cluster)
                {
                    if (Math.Abs(p.R - mean.R) > 22 + mean.R / 6 || Math.Abs(p.G - mean.G) > 22 + mean.G / 6 ||
                        Math.Abs(p.B - mean.B) > 22 + mean.B / 6) { smooth = false; break; }
                }
                if (smooth)
                {
                    EarthInks.WriteCluster(dest, y * size + x, size, x, y,
                        EarthInks.Resolve(mean.R, mean.G, mean.B));
                }
                else
                {
                    for (int i = 0; i < 4; i++)
                    {
                        var sample = cluster[i];
                        int pair = EarthInks.Resolve(sample.R, sample.G, sample.B);
                        int sx = x + (i & 1), sy = y + (i >> 1);
                        // Both artwork sizes are multiples of the 4×4 Bayer period,
                        // so a tile origin contributes zero to the dither phase.
                        dest[sy * size + sx] = EarthInks.Sample(pair, sx, sy);
                    }
                }
            }
        }
        art.SetImmutable();
        return SKImage.FromBitmap(art);
    }

    private readonly record struct LinearRgb(int R, int G, int B)
    {
        internal static LinearRgb From(uint p) => new(EarthInks.Linear[(p >> 16) & 255],
            EarthInks.Linear[(p >> 8) & 255], EarthInks.Linear[p & 255]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LinearRgb Box2x2(ReadOnlySpan<uint> src, int width, int x, int y)
    {
        int offset = y * 2 * width + x * 2;
        var a = LinearRgb.From(src[offset]);
        var b = LinearRgb.From(src[offset + 1]);
        var c = LinearRgb.From(src[offset + width]);
        var d = LinearRgb.From(src[offset + width + 1]);
        return new((a.R + b.R + c.R + d.R) / 4,
            (a.G + b.G + c.G + d.G) / 4, (a.B + b.B + c.B + d.B) / 4);
    }

    private static LinearRgb Box(ReadOnlySpan<uint> src, int width, int height, int x, int y, int size)
    {
        int top = y * height / size, bottom = Math.Max(top + 1, (y + 1) * height / size);
        int left = x * width / size, right = Math.Max(left + 1, (x + 1) * width / size);
        int r = 0, g = 0, b = 0;
        for (int sy = top; sy < bottom; sy++)
        for (int sx = left; sx < right; sx++)
        {
            var p = LinearRgb.From(src[sy * width + sx]);
            r += p.R; g += p.G; b += p.B;
        }
        int area = (right - left) * (bottom - top);
        return new(r / area, g / area, b / area);
    }

    private void Drop(LinkedListNode<Entry> node)
    {
        bytes -= node.Value.Bytes;
        index.Remove((node.Value.Source.Id, node.Value.Natural));
        usage.Remove(node);
        node.Value.Image?.Dispose();
    }

    public void Dispose()
    {
        foreach (var e in usage) e.Image?.Dispose();
        usage.Clear(); index.Clear(); bytes = 0;
    }
}

internal static class EarthInks
{
    // Deep ocean, shelf, wetland, conifer, grassland, cultivated land, desert, rock, snow, built land.
    private static readonly uint[] Colors = [
        0x0c1528,0x102541,0x153555,0x1a4968,0x235e80,0x30759b,0x478caa,0x68a8bf,0x98c7cf,
        0x0c2933,0x133c45,0x1b5056,0x23696b,0x348781,0x4ca09a,0x6dbcb0,0x9ad2be,0xbadccb,
        0x17251d,0x263a28,0x334c32,0x415e3a,0x537448,0x658957,0x81a169,0x9bb57e,0xb7c793,
        0x273027,0x394531,0x505a39,0x646d43,0x7b8250,0x93975e,0xabb174,0xc3c589,0xdad9aa,
        0x363127,0x514b35,0x68603f,0x847649,0x9e8d56,0xb7a367,0xcaba82,0xdfcc99,0xefdfb5,
        0x44352e,0x624b3b,0x7a5b43,0x956e49,0xb1875d,0xcba477,0xddba90,0xe9cfab,0xf2dfc3,
        0x3e3031,0x614442,0x7d514a,0x9a6355,0xb47c66,0xc99a85,0xdbb5a0,0xe9d1bd,
        0x171b20,0x2b3036,0x42494c,0x596063,0x72797a,0x8b9190,0xa7abaa,0xc0c4be,0xd8dcd3,0xecede4,
        0x26303c,0x3a485a,0x52637a,0x708297,0x94a4b6,0xb5c3d0,0xd1dce3,0xe8edf0,0xf8f8ef,
        0x332f2c,0x494641,0x655f55,0x80796b,0x9b9384,0xb5ae9d,0xcfc8b7,0xe5dfcf
    ];
    internal static readonly int[] Linear = Enumerable.Range(0, 256).Select(i =>
    {
        double v = i / 255.0;
        return (int)Math.Round(4095 * (v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4)));
    }).ToArray();
    private static readonly byte[] Gamma5 = BuildGamma();
    private static byte[] BuildGamma()
    {
        var table = new byte[4096];
        int previous = 0;
        for (int s = 0; s < 31; s++)
        {
            double v = (s + .5) / 31;
            int end = (int)Math.Ceiling(4095 * (v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4)));
            Array.Fill(table, (byte)s, previous, end - previous);
            previous = end;
        }
        Array.Fill(table, (byte)31, previous, table.Length - previous);
        return table;
    }
    private static readonly byte[] Bayer = [0,8,2,10,12,4,14,6,3,11,1,9,15,7,13,5];
    private record struct Lab(float L, float A, float B)
    {
        public static Lab operator -(Lab a, Lab b) => new(a.L - b.L, a.A - b.A, a.B - b.B);
        public float Dot(Lab b) => L * b.L + A * b.A + B * b.B;
    }
    private static readonly Lab[] Swatches = Colors.Select(c => ToLab((int)(c >> 16), (int)((c >> 8) & 255), (int)(c & 255))).ToArray();
    // Most satellite tiles occupy a narrow part of the RGB cube. Memoize only encountered
    // 15-bit colors. Concurrent renderers can safely compute and publish the same packed value.
    private static readonly int[] Lut = new int[32 * 32 * 32];

    // Quantize in OKLab after averaging source samples in linear light. A small ordered mix
    // along the closest palette segment retains low-contrast shorelines and elevation gradients.
    internal static int Resolve(int r, int g, int b)
    {
        int key = (Gamma5[r] << 10) | (Gamma5[g] << 5) | Gamma5[b];
        int pair = Volatile.Read(ref Lut[key]);
        if (pair == 0)
        {
            pair = Match(key);
            Volatile.Write(ref Lut[key], pair);
        }
        return pair;
    }

    internal static uint Sample(int pair, int x, int y)
    {
        int choice = Bayer[((y & 3) << 2) | (x & 3)] < ((pair >> 16) & 15) ? (pair >> 8) & 255 : pair & 255;
        return 0xff000000 | Colors[choice];
    }

    // The decoder advances by two pixels on both axes. For these even origins,
    // all four Bayer positions stay within the same 4×4 period. A smooth region
    // shares its two inks and mixture threshold without changing any pixel choice.
    internal static void WriteCluster(Span<uint> pixels, int offset, int width, int x, int y, int pair)
    {
        uint first = 0xff000000 | Colors[pair & 255];
        uint second = 0xff000000 | Colors[(pair >> 8) & 255];
        int fraction = (pair >> 16) & 15;
        int phase = ((y & 3) << 2) | (x & 3);
        pixels[offset] = Bayer[phase] < fraction ? second : first;
        pixels[offset + 1] = Bayer[phase + 1] < fraction ? second : first;
        pixels[offset + width] = Bayer[phase + 4] < fraction ? second : first;
        pixels[offset + width + 1] = Bayer[phase + 5] < fraction ? second : first;
    }

    private static Lab ToLab(int r, int g, int b)
    {
        float lr = Linear[r] / 4095f, lg = Linear[g] / 4095f, lb = Linear[b] / 4095f;
        float l = MathF.Cbrt(.41222147f * lr + .53633254f * lg + .05144599f * lb);
        float m = MathF.Cbrt(.21190350f * lr + .68069955f * lg + .10739696f * lb);
        float s = MathF.Cbrt(.08830246f * lr + .28171884f * lg + .62997870f * lb);
        return new(.21045426f * l + .79361779f * m - .00407205f * s,
            1.9779985f * l - 2.4285922f * m + .45059371f * s,
            .02590404f * l + .78277177f * m - .80867577f * s);
    }

    private static int Match(int key)
    {
        var sample = ToLab(((key >> 10) & 31) * 255 / 31, ((key >> 5) & 31) * 255 / 31, (key & 31) * 255 / 31);
        int first = 0, second = 0;
        float min = float.MaxValue, next = float.MaxValue;
        for (int j = 0; j < Swatches.Length; j++)
        {
            var diff = sample - Swatches[j];
            float d = diff.Dot(diff);
            if (d < min) { next = min; second = first; min = d; first = j; }
            else if (d < next) { next = d; second = j; }
        }
        var axis = Swatches[second] - Swatches[first];
        float blend = (sample - Swatches[first]).Dot(axis) / Math.Max(.00001f, axis.Dot(axis));
        return 1 << 24 | (int)Math.Clamp(Math.Round(blend * 14), 0, 7) << 16 | second << 8 | first;
    }
}
