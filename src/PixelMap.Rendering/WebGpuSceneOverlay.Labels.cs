using System.Numerics;
using SkiaSharp;

namespace PixelMap.Rendering;

public sealed partial class WebGpuSceneOverlay
{
    private const int AtlasSize = 2048;
    private const int AtlasSegmentWidth = 1024;
    private readonly record struct LabelKey(string Text, float Size, uint Ink, uint Halo, nint Typeface, bool Antialias);
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
        pendingLabels.Add(new(new(text, size, palette.Label, palette.LabelHalo, typeface.Handle, MapRendering.Smooth(frame!.RenderStyle)), x, y, typeface));
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
            foreach (var piece in asset.Segments)
            {
                float x = request.X + piece.X, y = request.Y + piece.Y;
                var area = piece.Texture;
                float u = area.Left / (float)AtlasSize, v = area.Top / (float)AtlasSize;
                float right = area.Right / (float)AtlasSize, bottom = area.Bottom / (float)AtlasSize;
                screen.Quad(new(ScreenPosition(x, y), Vector4.One, new(u, v), 1),
                    new(ScreenPosition(x + area.Width, y), Vector4.One, new(right, v), 1),
                    new(ScreenPosition(x + area.Width, y + area.Height), Vector4.One, new(right, bottom), 1),
                    new(ScreenPosition(x, y + area.Height), Vector4.One, new(u, bottom), 1));
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
        int left = (int)MathF.Floor(Math.Min(0, inkBounds.Left)) - 3;
        int top = (int)MathF.Floor(inkBounds.Top) - 3;
        int width = Math.Max(1, (int)MathF.Ceiling(inkBounds.Right) - left + 3);
        int height = Math.Max(1, (int)MathF.Ceiling(inkBounds.Bottom) - top + 3);
        // Labels retain their full source text, including names wider than a single
        // atlas row; split such assets instead of shrinking or truncating their glyphs.
        if (height >= AtlasSize - 2 || width > 32768) return false;
        var segments = new LabelSegment[(width + AtlasSegmentWidth - 1) / AtlasSegmentWidth];
        for (int i = 0, offset = 0; i < segments.Length; i++)
        {
            int partWidth = Math.Min(AtlasSegmentWidth, width - offset);
            if (!TryAllocateAtlas(partWidth, height, out var region)) return false;
            segments[i] = new(region, left + offset, top);
            offset += partWidth;
        }
        foreach (var segment in segments)
        {
            var area = segment.Texture;
            atlasCanvas!.Save();
            atlasCanvas.ClipRect(new(area.Left, area.Top, area.Right, area.Bottom));
            float x = area.Left - segment.X, y = area.Top - segment.Y;
            atlasPaint!.Style = SKPaintStyle.Stroke;
            atlasPaint.StrokeWidth = 2.8f;
            atlasPaint.Color = new(key.Halo);
            atlasCanvas.DrawText(key.Text, x, y, SKTextAlign.Left, atlasFont, atlasPaint);
            atlasPaint.Style = SKPaintStyle.Fill;
            atlasPaint.Color = new(key.Ink);
            atlasCanvas.DrawText(key.Text, x, y, SKTextAlign.Left, atlasFont, atlasPaint);
            atlasCanvas.Restore();
            var dirty = AtlasDirtyBounds;
            AtlasDirtyBounds = dirty.IsEmpty ? area : new(Math.Min(dirty.Left, area.Left), Math.Min(dirty.Top, area.Top),
                Math.Max(dirty.Right, area.Right), Math.Max(dirty.Bottom, area.Bottom));
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
