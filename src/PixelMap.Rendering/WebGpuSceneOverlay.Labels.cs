using System.Numerics;
using SkiaSharp;

namespace PixelMap.Rendering;

public sealed partial class WebGpuSceneOverlay
{
    private const int AtlasSize = 2048;
    private const int AtlasSegmentWidth = 1024;
    private readonly record struct LabelKey(string Text, float Size, float RasterScale, uint Ink, uint Halo, nint Typeface, bool Antialias);
    private readonly record struct LabelRequest(LabelKey Key, float X, float Y, SKTypeface Typeface);
    private readonly record struct LabelSegment(SKRectI Texture, int X, int Y);
    private sealed record LabelAsset(LabelSegment[] Segments);
    private readonly List<LabelRequest> pendingLabels = new(90);
    private readonly Dictionary<LabelKey, LabelAsset> atlasEntries = [];
    private SKBitmap? atlas;
    private SKCanvas? atlasCanvas;
    private SKFont? atlasFont;
    private SKPaint? atlasPaint;
    private int atlasX = 1, atlasY = 1, atlasRow;

    /// <summary>Bounded, lazily allocated BGRA8888 premultiplied label atlas. This is
    /// font asset storage, not a rasterized frame. The caller must not mutate it.</summary>
    public SKBitmap? Atlas => atlas;
    public long AtlasRevision { get; private set; }
    public SKRectI AtlasDirtyBounds { get; private set; }

    /// <summary>Clear accumulated upload bounds only after the exact current revision
    /// has been submitted. Device recreation must upload the complete atlas.</summary>
    public void AcknowledgeAtlasUpload(long revision)
    {
        if (revision == AtlasRevision) AtlasDirtyBounds = SKRectI.Empty;
    }

    internal void AddLabel(string text, float size, float x, float y, PixelPalette palette, SKTypeface typeface)
    {
        if (!labelsChanged || pendingLabels.Count >= 90) return;
        pendingLabels.Add(new(new(text, size, requestedLabelRasterScale, palette.Label, palette.LabelHalo,
            typeface.Handle, MapRendering.Smooth(frame!.RenderStyle)), x, y, typeface));
    }

    private void PrepareLabelAtlas()
    {
        screen.Clear();
        LabelCount = 0;
        if (pendingLabels.Count == 0) return;
        if (atlas is null)
        {
            atlas = new(AtlasSize, AtlasSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            atlasCanvas = new(atlas);
            atlasFont = new(pendingLabels[0].Typeface, 14) { Edging = SKFontEdging.Antialias };
            atlasPaint = new() { StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
            ResetAtlas();
        }
        // Expired map names never grow memory without bound. Repack only when the
        // atlas is full, preserving reusable label assets across ordinary camera moves.
        bool reset = atlasEntries.Count > 4096;
        if (!reset)
            foreach (var request in pendingLabels)
                if (!atlasEntries.ContainsKey(request.Key) && !TryBakeLabel(request)) { reset = true; break; }
        if (reset)
        {
            ResetAtlas();
            foreach (var request in pendingLabels)
                if (!atlasEntries.ContainsKey(request.Key)) TryBakeLabel(request);
        }
        atlasCanvas!.Flush();
        foreach (var request in pendingLabels)
        {
            if (!atlasEntries.TryGetValue(request.Key, out var asset)) continue;
            LabelCount++;
            float logicalScale = 1 / request.Key.RasterScale;
            foreach (var piece in asset.Segments)
            {
                float x = request.X + piece.X * logicalScale, y = request.Y + piece.Y * logicalScale;
                var area = piece.Texture;
                float width = area.Width * logicalScale, height = area.Height * logicalScale;
                float u = area.Left / (float)AtlasSize, v = area.Top / (float)AtlasSize;
                float right = area.Right / (float)AtlasSize, bottom = area.Bottom / (float)AtlasSize;
                screen.Quad(new(ScreenPosition(x, y), Vector4.One, new(u, v), 1),
                    new(ScreenPosition(x + width, y), Vector4.One, new(right, v), 1),
                    new(ScreenPosition(x + width, y + height), Vector4.One, new(right, bottom), 1),
                    new(ScreenPosition(x, y + height), Vector4.One, new(u, bottom), 1));
            }
        }
    }

    private void ResetAtlas()
    {
        atlasEntries.Clear();
        atlasX = atlasY = 1;
        atlasRow = 0;
        atlasCanvas!.Clear(SKColors.Transparent);
        AtlasRevision++;
        AtlasDirtyBounds = new(0, 0, AtlasSize, AtlasSize);
    }

    private bool TryBakeLabel(LabelRequest request)
    {
        var key = request.Key;
        atlasFont!.Typeface = request.Typeface;
        atlasFont.Size = key.Size;
        atlasPaint!.IsAntialias = key.Antialias;
        atlasFont.MeasureText(key.Text, out var inkBounds);
        float scale = key.RasterScale;
        int padding = (int)MathF.Ceiling(3 * scale);
        int left = (int)MathF.Floor(Math.Min(0, inkBounds.Left) * scale) - padding;
        int top = (int)MathF.Floor(inkBounds.Top * scale) - padding;
        int width = Math.Max(1, (int)MathF.Ceiling(inkBounds.Right * scale) - left + padding);
        int height = Math.Max(1, (int)MathF.Ceiling(inkBounds.Bottom * scale) - top + padding);
        // Labels retain their full source text, including names wider than a single
        // atlas row; split such assets instead of shrinking or truncating their glyphs.
        if (height >= AtlasSize - 2 || width > 32768) return false;
        var segments = new LabelSegment[(width + AtlasSegmentWidth - 1) / AtlasSegmentWidth];
        for (int i = 0, offset = 0; i < segments.Length; i++)
        {
            int partWidth = Math.Min(AtlasSegmentWidth, width - offset);
            // A populated texel on either side preserves linear filtering across a
            // long label's segment boundary, including fractional display densities.
            if (!TryAllocateAtlas(partWidth + 2, height, out var allocation)) return false;
            var region = new SKRectI(allocation.Left + 1, allocation.Top, allocation.Right - 1, allocation.Bottom);
            segments[i] = new(region, left + offset, top);
            offset += partWidth;
        }
        foreach (var segment in segments)
        {
            var area = segment.Texture;
            var rasterArea = new SKRectI(area.Left - 1, area.Top, area.Right + 1, area.Bottom);
            atlasCanvas!.Save();
            atlasCanvas.ClipRect(new(rasterArea.Left, rasterArea.Top, rasterArea.Right, rasterArea.Bottom));
            atlasCanvas.Translate(area.Left - segment.X, area.Top - segment.Y);
            // Scale rasterization without changing the logical font's hinted advances
            // or collision bounds. The same transform scales glyphs, halo and padding.
            atlasCanvas.Scale(scale);
            atlasPaint!.Style = SKPaintStyle.Stroke;
            atlasPaint.StrokeWidth = 2.8f;
            atlasPaint.Color = new(key.Halo);
            atlasCanvas.DrawText(key.Text, 0, 0, SKTextAlign.Left, atlasFont, atlasPaint);
            atlasPaint.Style = SKPaintStyle.Fill;
            atlasPaint.Color = new(key.Ink);
            atlasCanvas.DrawText(key.Text, 0, 0, SKTextAlign.Left, atlasFont, atlasPaint);
            atlasCanvas.Restore();
            var dirty = AtlasDirtyBounds;
            AtlasDirtyBounds = dirty.IsEmpty ? rasterArea : new(Math.Min(dirty.Left, rasterArea.Left), Math.Min(dirty.Top, rasterArea.Top),
                Math.Max(dirty.Right, rasterArea.Right), Math.Max(dirty.Bottom, rasterArea.Bottom));
        }
        atlasEntries.Add(key, new(segments));
        AtlasRevision++;
        return true;
    }

    private bool TryAllocateAtlas(int width, int height, out SKRectI area)
    {
        if (atlasX + width + 1 > AtlasSize)
        {
            atlasX = 1;
            atlasY += atlasRow + 2;
            atlasRow = 0;
        }
        if (atlasY + height + 1 > AtlasSize) { area = default; return false; }
        area = new(atlasX, atlasY, atlasX + width, atlasY + height);
        atlasX += width + 2;
        atlasRow = Math.Max(atlasRow, height);
        return true;
    }
}
