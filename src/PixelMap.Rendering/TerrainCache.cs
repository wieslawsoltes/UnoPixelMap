using PixelMap.Data;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>World-aligned terrain blocks amortize vector styling over camera pans and rotations.
/// Quantized LOD limits memory and regeneration during animated fractional zoom.</summary>
internal sealed class TerrainCache
{
    private const int BlockSize = 256;
    private const int MaxBlocks = 128; // 32 MiB of BGRA terrain
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private readonly record struct Key(TileId Id, int Lod, int PixelSize, bool Shadow, int X, int Y);
    private readonly record struct Entry(Key Key, SKImage Image);
    private readonly Dictionary<Key, LinkedListNode<Entry>> index = [];
    private readonly LinkedList<Entry> usage = [];

    internal void Draw(SKCanvas canvas, TileGeometry tile, float scale, int pixelSize, bool shadows,
        PixelPalette palette, PixelMapRenderer renderer)
    {
        var clip = canvas.LocalClipBounds;
        int lod = (int)Math.Clamp(Math.Round(Math.Log2(scale * TileGeometry.Extent / BlockSize)), 0, 11);
        int n = 1 << lod;
        float units = TileGeometry.Extent / n;
        int x0 = Math.Clamp((int)MathF.Floor(clip.Left / units), 0, n);
        int y0 = Math.Clamp((int)MathF.Floor(clip.Top / units), 0, n);
        int x1 = Math.Clamp((int)MathF.Ceiling(clip.Right / units), 0, n);
        int y1 = Math.Clamp((int)MathF.Ceiling(clip.Bottom / units), 0, n);
        float density = BlockSize / units;
        double zoom = tile.Source.Id.Z + lod + Math.Log2(pixelSize) - 1;
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            var key = new Key(tile.Source.Id, lod, pixelSize, shadows, x, y);
            var bounds = new SKRect(x * units, y * units, (x + 1) * units, (y + 1) * units);
            if (!index.TryGetValue(key, out var node))
            {
                using var bitmap = new SKBitmap(BlockSize, BlockSize, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using (var block = new SKCanvas(bitmap))
                {
                    block.Clear(new SKColor(palette.Ground));
                    block.Scale(density);
                    block.Translate(-bounds.Left, -bounds.Top);
                    renderer.DrawTerrainPatch(block, tile, density, zoom, pixelSize, shadows, palette, bounds);
                    block.Flush();
                }
                bitmap.SetImmutable();
                node = usage.AddFirst(new Entry(key, SKImage.FromBitmap(bitmap)));
                index.Add(key, node);
                if (usage.Count > MaxBlocks) Drop(usage.Last!);
            }
            else
            {
                usage.Remove(node);
                usage.AddFirst(node);
            }
            canvas.DrawImage(node.Value.Image, bounds, Nearest);
        }
    }

    private void Drop(LinkedListNode<Entry> node)
    {
        index.Remove(node.Value.Key);
        node.Value.Image.Dispose();
        usage.Remove(node);
    }

    internal void Remove(TileId id)
    {
        for (var node = usage.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.Key.Id == id) Drop(node);
            node = next;
        }
    }

    public void Clear()
    {
        foreach (var entry in usage) entry.Image.Dispose();
        usage.Clear(); index.Clear();
    }
}
