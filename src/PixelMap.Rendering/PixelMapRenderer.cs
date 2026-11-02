using PixelMap.Data;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>Retained vector-tile display lists, orthographic height rasterization and a nearest-neighbor resolve.</summary>
public sealed partial class PixelMapRenderer : IDisposable
{
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private readonly Dictionary<TileId, TileGeometry> geometry = [];
    private readonly List<(TileGeometry Tile, TileTransform Transform, float Scale)> visible = [];
    private readonly Dictionary<(Ink, int), SKShader> shaders = [];
    private readonly Dictionary<Ink, TerrainPattern> patterns = [];
    private readonly List<(Place Label, SKPoint Screen, float Distance)> candidates = [];
    private readonly List<SKRect> occupied = [];
    private readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    private readonly BuildingRasterizer buildings = new();
    private readonly TerrainCache terrain = new();
    private readonly SKPaint paint = new() { IsAntialias = false, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKFont font = new(SKTypeface.Default, 8f) { Edging = SKFontEdging.Antialias };
    private SKBitmap? bitmap;
    private SKCanvas? ground;
    private PixelPalette? palette;
    private long frame;
    private SKPicture? lastPicture;
    private readonly List<MapTile> lastTiles = [];
    private (int Width, int Height, MapCamera Camera, PixelStyle Style, int PixelSize) lastView;

    public unsafe void Draw(SKCanvas canvas, int width, int height, MapCamera camera, IReadOnlyList<MapTile> tiles,
        PixelStyle style = PixelStyle.Day, int pixelSize = 2)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (width <= 0 || height <= 0) return;
        pixelSize = Math.Clamp(pixelSize, 1, 8);
        var view = (width, height, camera, style, pixelSize);
        if (view == lastView && lastPicture is not null && SameTiles(tiles))
        {
            canvas.DrawPicture(lastPicture);
            return;
        }
        lastPicture?.Dispose();
        lastPicture = null;
        int w = (width + pixelSize - 1) / pixelSize, h = (height + pixelSize - 1) / pixelSize;
        var colors = style switch { PixelStyle.Dusk => PixelPalette.Dusk, PixelStyle.Night => PixelPalette.Night, _ => PixelPalette.Day };
        if (palette != colors)
        {
            ReleasePatterns();
            terrain.Clear();
            palette = colors;
        }
        if (bitmap?.Width != w || bitmap.Height != h)
        {
            ground?.Dispose(); bitmap?.Dispose();
            bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
            ground = new SKCanvas(bitmap);
        }
        var surface = ground!;
        surface.Clear(new SKColor(colors.Ground));
        PrepareVisible(width, height, pixelSize, camera, tiles);
        double world = camera.WorldSize / pixelSize;

        foreach (var (tile, transform, scale) in visible)
        {
            surface.Save();
            var matrix = transform.Matrix;
            surface.SetMatrix(in matrix);
            surface.ClipRect(new SKRect(0, 0, TileGeometry.Extent, TileGeometry.Extent));
            terrain.Draw(surface, tile, scale, pixelSize, camera.Pitch >= 1, colors, this);
            surface.Restore();
        }
        paint.Shader = null;
        surface.Flush();
        if (camera.Zoom >= 12)
        {
            float meters = (float)(world / (MapCamera.EarthCircumference *
                Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)));
            buildings.Begin(w, h, (float)Math.Sin(camera.PitchRadians), meters);
            var pixels = new Span<uint>((void*)bitmap.GetPixels(), checked(w * h));
            foreach (var (tile, transform, _) in visible)
            foreach (var building in tile.Buildings)
                buildings.Draw(pixels, building, transform, colors.Materials[building.Seed % (uint)colors.Materials.Length]);
            bitmap.NotifyPixelsChanged();
        }
        surface.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        // Keep every art pixel the same size, including the last partial row or column.
        var viewport = new SKRect(0, 0, width, height);
        using var recorder = new SKPictureRecorder();
        var layer = recorder.BeginRecording(viewport);
        layer.ClipRect(viewport);
        layer.DrawImage(image, new SKRect(0, 0, w * pixelSize, h * pixelSize), Nearest);
        DrawLabels(layer, width, height, camera.Zoom, pixelSize, colors);
        lastPicture = recorder.EndRecording();
        lastView = view;
        lastTiles.Clear();
        foreach (var tile in tiles) lastTiles.Add(tile);
        canvas.DrawPicture(lastPicture);
    }

    private void PrepareVisible(int width, int height, int pixelSize, MapCamera camera, IReadOnlyList<MapTile> tiles)
    {
        frame++;
        visible.Clear();
        // Do the absolute Mercator subtraction in double before constructing the float Skia transform.
        var (s, c) = Math.SinCos(camera.Bearing * Math.PI / 180);
        double tilt = Math.Cos(camera.PitchRadians);
        double world = camera.WorldSize / pixelSize;
        foreach (var tile in tiles)
        {
            if (!geometry.TryGetValue(tile.Id, out var prepared) || !ReferenceEquals(prepared.Source, tile))
            {
                if (prepared is not null) { terrain.Remove(tile.Id); prepared.Dispose(); }
                geometry[tile.Id] = prepared = new(tile);
            }
            prepared.LastFrame = frame;
            int n = 1 << tile.Id.Z;
            var coverage = camera.TileBounds(width, height, n);
            if (tile.Id.Y < coverage.Top || tile.Id.Y > coverage.Bottom) continue;
            int firstCopy = (int)Math.Ceiling((coverage.Left - tile.Id.X) / (double)n);
            int lastCopy = (int)Math.Floor((coverage.Right - tile.Id.X) / (double)n);
            float scale = (float)(world / (n * TileGeometry.Extent));
            for (int copy = firstCopy; copy <= lastCopy; copy++)
            {
                double x = ((double)tile.Id.X / n + copy - camera.CenterX) * world;
                double y = ((double)tile.Id.Y / n - camera.CenterY) * world;
                var transform = new TileTransform((float)(width * .5 / pixelSize + c * x + s * y),
                    (float)(height * .5 / pixelSize + (-s * x + c * y) * tilt),
                    (float)(scale * c), (float)(scale * s), (float)(-scale * s * tilt), (float)(scale * c * tilt));
                visible.Add((prepared, transform, scale));
            }
        }
        if (geometry.Count > Math.Max(32, visible.Count * 2))
        {
            foreach (var id in geometry.Where(p => p.Value.LastFrame != frame).Select(p => p.Key).ToArray())
            {
                terrain.Remove(id);
                geometry[id].Dispose();
                geometry.Remove(id);
            }
        }
    }

    private bool SameTiles(IReadOnlyList<MapTile> tiles)
    {
        if (tiles.Count != lastTiles.Count) return false;
        for (int i = 0; i < tiles.Count; i++)
            if (!ReferenceEquals(tiles[i], lastTiles[i])) return false;
        return true;
    }

    internal void DrawTerrainPatch(SKCanvas canvas, TileGeometry tile, float scale, double zoom, int pixelSize,
        bool shadows, PixelPalette colors, SKRect clip)
    {
        float roadScale = (float)Math.Clamp(Math.Pow(2, (zoom - 14) * .37), .36, 5) / pixelSize;
        clip.Inflate(32 / scale, 32 / scale);
        foreach (var (key, path, bounds) in tile.Batches)
        {
            if (key.Ink == Ink.Shadow && (zoom < 13 || !shadows)) continue;
            if (bounds.Left > clip.Right || bounds.Right < clip.Left || bounds.Top > clip.Bottom || bounds.Bottom < clip.Top) continue;
            paint.Style = key.Width > 0 ? SKPaintStyle.Stroke : SKPaintStyle.Fill;
            paint.StrokeWidth = Math.Max(.6f, key.Width * roadScale) / scale;
            paint.Color = new SKColor(colors.Inks[(int)key.Ink]);
            paint.Shader = null;
            if (zoom >= 12 && key.Ink is Ink.Water or Ink.Grass or Ink.Forest or Ink.Park)
            {
                int exponent = (int)Math.Clamp(Math.Round(Math.Log2(1 / scale)), -5, 8);
                if (!shaders.TryGetValue((key.Ink, exponent), out var shader))
                {
                    if (!patterns.TryGetValue(key.Ink, out var pattern))
                        patterns[key.Ink] = pattern = new(key.Ink, colors);
                    shaders[(key.Ink, exponent)] = shader = pattern.AtScale(MathF.Pow(2, exponent));
                }
                paint.Color = SKColors.White;
                paint.Shader = shader;
            }
            canvas.DrawPath(path, paint);
        }
        paint.Shader = null;
    }

    private void DrawLabels(SKCanvas canvas, int w, int h, double zoom, int pixelSize, PixelPalette colors)
    {
        candidates.Clear(); names.Clear(); occupied.Clear();
        foreach (var (tile, transform, _) in visible)
        foreach (var label in tile.Labels)
        {
            if (label.Street && zoom < 15 || label.Priority > 9 && zoom < 9) continue;
            var screen = transform.At(label.Point.X, label.Point.Y);
            if (label.Street && buildings.Covers(screen.X, screen.Y)) continue;
            screen.X *= pixelSize; screen.Y *= pixelSize;
            if (screen.X < 0 || screen.X >= w || screen.Y < 0 || screen.Y >= h) continue;
            float dx = screen.X - w * .5f, dy = screen.Y - h * .5f;
            candidates.Add((label, screen, dx * dx + dy * dy));
        }
        candidates.Sort(static (a, b) =>
        {
            int order = a.Label.Priority.CompareTo(b.Label.Priority);
            return order == 0 ? a.Distance.CompareTo(b.Distance) : order;
        });
        foreach (var (label, screen, _) in candidates)
        {
            if (occupied.Count >= 90) break;
            if (names.Contains(label.Text)) continue;
            font.Size = label.Street ? 12f : 14f;
            float tw = font.MeasureText(label.Text);
            if (tw <= 0 || tw > w * .8f) continue;
            float x = MathF.Round(screen.X - tw * .5f), y = MathF.Round(screen.Y);
            float pad = label.Street ? 22 : 7;
            var bounds = new SKRect(x - pad, y - font.Size - 9, x + tw + pad, y + 9);
            if (bounds.Left < 1 || bounds.Right > w - 1 || bounds.Top < 1 || bounds.Bottom > h - 1) continue;
            bool collision = false;
            foreach (var prior in occupied)
                if (bounds.IntersectsWith(prior)) { collision = true; break; }
            if (collision) continue;
            names.Add(label.Text);
            occupied.Add(bounds);
            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 2.8f;
            paint.Color = new(colors.LabelHalo);
            canvas.DrawText(label.Text, x, y, SKTextAlign.Left, font, paint);
            paint.Style = SKPaintStyle.Fill; paint.Color = new(colors.Label);
            canvas.DrawText(label.Text, x, y, SKTextAlign.Left, font, paint);
        }
    }

    private void ReleasePatterns()
    {
        paint.Shader = null;
        foreach (var shader in shaders.Values) shader.Dispose();
        shaders.Clear();
        foreach (var pattern in patterns.Values) pattern.Dispose();
        patterns.Clear();
    }

    public void Dispose()
    {
        ReleasePatterns();
        foreach (var tile in geometry.Values) tile.Dispose();
        geometry.Clear();
        terrain.Clear();
        lastPicture?.Dispose();
        ground?.Dispose(); bitmap?.Dispose(); paint.Dispose(); font.Dispose();
    }
}
