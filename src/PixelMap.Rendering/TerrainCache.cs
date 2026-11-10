using PixelMap.Data;
using SkiaSharp;
using System.Runtime.InteropServices;

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
        if (GetBlock(tile, lod, x, y, bounds, pixelSize, shadows, overlay, palette, renderer, canvas) is { } image)
            canvas.DrawImage(image, bounds, Nearest);
    }

    /// <summary>Copy a fully covered opaque terrain patch from its world-aligned cached
    /// block. Both grids are dyadic: nearest sampling is an integer shift, and identical
    /// destination rows can be copied without invoking Skia's transformed-image pipeline.</summary>
    internal bool TryCopyTexture(SKBitmap destination, TileId patch, TileGeometry tile, int pixelSize,
        bool shadows, PixelPalette palette, PixelMapRenderer renderer)
    {
        int difference = patch.Z - tile.Source.Id.Z;
        if (difference is < 0 or > 22 || destination.Width != BlockSize || destination.Height != BlockSize ||
            destination.ColorType != SKColorType.Bgra8888 || destination.AlphaType != SKAlphaType.Opaque) return false;
        int count = 1 << difference;
        int localX = patch.X - tile.Source.Id.X * count, localY = patch.Y - tile.Source.Id.Y * count;
        if ((uint)localX >= (uint)count || (uint)localY >= (uint)count) return false;

        // BakeTerrainTexture caps the source density at one pixel per MVT unit.
        // Preserve that cap and its LOD-dependent vector styling exactly.
        int lod = Math.Min(difference, 4), shift = difference - lod;
        int x = localX >> shift, y = localY >> shift;
        float units = TileGeometry.Extent / (1 << lod);
        var bounds = new SKRect(x * units, y * units, (x + 1) * units, (y + 1) * units);
        var image = GetBlock(tile, lod, x, y, bounds, pixelSize, shadows, false, palette, renderer)!;
        using var pixels = image.PeekPixels();
        if (pixels is null || pixels.ColorType != destination.ColorType || pixels.AlphaType != SKAlphaType.Opaque) return false;

        var source = MemoryMarshal.Cast<byte, uint>(pixels.GetPixelSpan());
        var target = MemoryMarshal.Cast<byte, uint>(destination.GetPixelSpan());
        int sourceStride = pixels.RowBytes / sizeof(uint), targetStride = destination.RowBytes / sizeof(uint);
        int mask = (1 << shift) - 1;
        int offsetX = (localX & mask) * BlockSize, offsetY = (localY & mask) * BlockSize;
        int previousY = -1;
        for (int row = 0; row < BlockSize; row++)
        {
            int sourceY = (offsetY + row) >> shift;
            var output = target.Slice(row * targetStride, BlockSize);
            if (sourceY == previousY)
            {
                target.Slice((row - 1) * targetStride, BlockSize).CopyTo(output);
                continue;
            }
            previousY = sourceY;
            var input = source.Slice(sourceY * sourceStride, BlockSize);
            if (shift == 0) input.CopyTo(output);
            else if (shift >= 8) output.Fill(input[offsetX >> shift]);
            else
            {
                int repeat = 1 << shift;
                for (int column = 0; column < BlockSize; column += repeat)
                    output.Slice(column, repeat).Fill(input[(offsetX + column) >> shift]);
            }
        }
        destination.NotifyPixelsChanged();
        return true;
    }

    private SKImage? GetBlock(TileGeometry tile, int lod, int x, int y, SKRect bounds,
        int pixelSize, bool shadows, bool overlay, PixelPalette palette, PixelMapRenderer renderer, SKCanvas? canvas = null)
    {
        var key = new Key(tile.Source.Id, lod, pixelSize, shadows, overlay, x, y);
        if (!index.TryGetValue(key, out var node))
        {
            if (canvas?.QuickReject(bounds) == true) return null;
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
        return node.Value.Image;
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
