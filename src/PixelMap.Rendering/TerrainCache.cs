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
    private readonly record struct Key(TileId Id, int Lod, int PixelSize, bool Shadow, bool Overlay, int X, int Y);
    private readonly record struct Entry(Key Key, SKImage Image);
    private readonly Dictionary<Key, LinkedListNode<Entry>> index = [];
    private readonly LinkedList<Entry> usage = [];

    internal void Draw(SKCanvas canvas, TileGeometry tile, float scale, int pixelSize, bool shadows, bool overlay,
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
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            var bounds = new SKRect(x * units, y * units, (x + 1) * units, (y + 1) * units);
            DrawBlock(canvas, tile, lod, x, y, bounds, pixelSize, shadows, overlay, palette, renderer);
        }
    }

    internal void DrawPerspective(SKCanvas canvas, TileGeometry tile, TileTransform transform, int width, int height,
        int pixelSize, bool shadows, bool overlay, PixelPalette palette, PixelMapRenderer renderer)
    {
        Visit(0, 0, 0);

        void Visit(int lod, int x, int y)
        {
            float units = TileGeometry.Extent / (1 << lod);
            var bounds = new SKRect(x * units, y * units, (x + 1) * units, (y + 1) * units);
            Span<SKPoint> polygon = stackalloc SKPoint[16];
            int count = transform.VisiblePolygon(width, height, polygon, bounds);
            if (count < 3) return;
            // A geographic quadtree follows perspective scale across deep tiles. Only
            // visible children receive texture storage, keeping far-ground detail bounded.
            if (lod < 11 && units * transform.MaximumScale(polygon[..count]) > BlockSize * 1.35f)
            {
                Visit(lod + 1, x * 2, y * 2);
                Visit(lod + 1, x * 2 + 1, y * 2);
                Visit(lod + 1, x * 2, y * 2 + 1);
                Visit(lod + 1, x * 2 + 1, y * 2 + 1);
                return;
            }
            DrawBlock(canvas, tile, lod, x, y, bounds, pixelSize, shadows, overlay, palette, renderer);
        }
    }

    private void DrawBlock(SKCanvas canvas, TileGeometry tile, int lod, int x, int y, SKRect bounds,
        int pixelSize, bool shadows, bool overlay, PixelPalette palette, PixelMapRenderer renderer)
    {
        var key = new Key(tile.Source.Id, lod, pixelSize, shadows, overlay, x, y);
        if (!index.TryGetValue(key, out var node))
        {
            if (canvas.QuickReject(bounds)) return;
            float density = BlockSize / bounds.Width;
            double zoom = tile.Source.Id.Z + lod + Math.Log2(pixelSize) - 1;
            using var bitmap = new SKBitmap(BlockSize, BlockSize, SKColorType.Bgra8888, overlay ? SKAlphaType.Premul : SKAlphaType.Opaque);
            using (var block = new SKCanvas(bitmap))
            {
                block.Clear(overlay ? SKColors.Transparent : new SKColor(palette.Ground));
                block.Scale(density);
                block.Translate(-bounds.Left, -bounds.Top);
                renderer.DrawTerrainPatch(block, tile, density, zoom, pixelSize, shadows, overlay, palette, bounds);
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
