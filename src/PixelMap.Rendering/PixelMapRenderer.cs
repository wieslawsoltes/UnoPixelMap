using System.Runtime.InteropServices;
using PixelMap.Data;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>Retained vector-tile display lists, orthographic height rasterization and a nearest-neighbor resolve.</summary>
public sealed partial class PixelMapRenderer : IDisposable
{
    private readonly Lock sync = new();
    private bool disposed;
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private readonly Dictionary<TileId, TileGeometry> geometry = [];
    private readonly List<(TileGeometry Tile, TileTransform Transform, float Scale)> visible = [];
    private readonly List<(Building Model, TileTransform Transform)> perspectiveModels = [];
    private readonly List<(int Index, float Near)> perspectiveOrder = [];
    private readonly Dictionary<(Ink, int), SKShader> shaders = [];
    private readonly Dictionary<Ink, TerrainPattern> patterns = [];
    private readonly Dictionary<(Ink, float), SKPathEffect> dashes = [];
    private readonly List<(Place Label, SKPoint Screen, float Distance)> candidates = [];
    private readonly List<SKRect> occupied = [];
    private readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    private readonly BuildingRasterizer buildings = new();
    private readonly TerrainCache terrain = new();
    private readonly EarthRaster earth = new();
    private readonly SKPath earthCoverage = new();
    private readonly SKPath tileClip = new();
    private readonly List<ImageryTile> imageryOrder = [];
    private readonly List<ImageryTile> lastImagery = [];
    private readonly SKPath shadowMask = new();
    private readonly List<ShadowSource> shadowSources = [];
    private readonly SKPaint paint = new() { IsAntialias = false, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKFont font = new(SKTypeface.Default, 8f) { Edging = SKFontEdging.Antialias };
    private SKBitmap? bitmap;
    private SKCanvas? ground;
    private GpuFrameSurface? gpuFrame;
    private string? reportedGpuFailure;
    private PixelPalette? palette;
    private long frame;
    private long geometryRevision;
    private SKPicture? lastPicture;
    private readonly List<MapTile> lastTiles = [];
    private (int Width, int Height, MapCamera Camera, PixelStyle Style, int PixelSize, long GpuGeneration) lastView;

    /// <summary>Whether the most recent draw selected the GPU rendering pipeline.</summary>
    public bool IsGpuRenderingActive { get; private set; }

    public void Draw(SKCanvas canvas, int width, int height, MapCamera camera, IReadOnlyList<MapTile> tiles,
        PixelStyle style = PixelStyle.Day, int pixelSize = 2, IReadOnlyList<ImageryTile>? imagery = null)
        => Draw(canvas, width, height, camera, tiles, style, pixelSize, imagery, false);

    /// <summary>Optionally projects terrain and composes artwork on the destination GPU.
    /// Unsupported contexts use the CPU path. Model depth and navigation share both paths.</summary>
    public void Draw(SKCanvas canvas, int width, int height, MapCamera camera, IReadOnlyList<MapTile> tiles,
        PixelStyle style, int pixelSize, IReadOnlyList<ImageryTile>? imagery, bool useGpuRendering)
        => Draw(canvas, width, height, camera, tiles, style, pixelSize, imagery, useGpuRendering, false);

    /// <summary>A GPU-backed host may allow lazy picture images when its callback records
    /// drawing commands. Skia generates their textures on the final playback context.</summary>
    public void Draw(SKCanvas canvas, int width, int height, MapCamera camera, IReadOnlyList<MapTile> tiles,
        PixelStyle style, int pixelSize, IReadOnlyList<ImageryTile>? imagery, bool useGpuRendering, bool allowDeferredGpuRendering)
        => Draw(canvas, width, height, camera, tiles, style, pixelSize, imagery, useGpuRendering, allowDeferredGpuRendering, null);

    /// <summary>Render immutable elevation data with independently selectable relief and
    /// hypsometric tint. An absent or empty elevation field retains the flat pipeline.</summary>
    public void Draw(SKCanvas canvas, int width, int height, MapCamera camera, IReadOnlyList<MapTile> tiles,
        PixelStyle style, int pixelSize, IReadOnlyList<ImageryTile>? imagery, bool useGpuRendering,
        bool allowDeferredGpuRendering, TerrainOptions? terrainOptions)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CurrentTerrain = terrainOptions is { Elevation.Count: > 0 } && (terrainOptions.Relief || terrainOptions.Hypsometric)
                ? terrainOptions : null;
            if (!useGpuRendering) gpuFrame?.Reset();
            DrawFrame(canvas, width, height, camera, tiles, style, pixelSize, imagery, useGpuRendering, allowDeferredGpuRendering);
            if (useGpuRendering && !IsGpuRenderingActive && gpuFrame?.FailureReason is { } failure && failure != reportedGpuFailure)
            {
                reportedGpuFailure = failure;
                Console.WriteLine($"Pixel Atlas GPU fallback. {failure}");
            }
        }
    }

    private unsafe void DrawFrame(SKCanvas canvas, int width, int height, MapCamera camera, IReadOnlyList<MapTile> tiles,
        PixelStyle style, int pixelSize, IReadOnlyList<ImageryTile>? imagery, bool useGpuRendering, bool allowDeferredGpuRendering)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (width <= 0 || height <= 0) return;
        if (CurrentTerrain is { } relief)
        {
            DrawTerrainFrame(canvas, width, height, camera, tiles, style, pixelSize, imagery,
                useGpuRendering, allowDeferredGpuRendering, relief);
            return;
        }
        pixelSize = Math.Clamp(pixelSize, 1, 8);
        int w = (width + pixelSize - 1) / pixelSize, h = (height + pixelSize - 1) / pixelSize;
        bool gpu = useGpuRendering && (gpuFrame ??= new()).TryPrepare(canvas, w, h, allowDeferredGpuRendering);
        IsGpuRenderingActive = gpu;
        var view = (width, height, camera, style, pixelSize, gpu ? gpuFrame!.Generation : 0);
        if (lastTerrain is null && view == lastView && lastPicture is not null && SameTiles(tiles, lastTiles) && SameTiles(imagery, lastImagery))
        {
            canvas.DrawPicture(lastPicture);
            return;
        }
        lastPicture?.Dispose();
        lastPicture = null;
        var colors = style switch { PixelStyle.Dusk => PixelPalette.Dusk, PixelStyle.Night => PixelPalette.Night, _ => PixelPalette.Day };
        if (palette != colors)
        {
            ReleasePatterns();
            terrain.Clear();
            palette = colors;
        }
        var alpha = gpu ? SKAlphaType.Premul : SKAlphaType.Opaque;
        if (bitmap?.Width != w || bitmap.Height != h || bitmap.AlphaType != alpha)
        {
            ground?.Dispose(); bitmap?.Dispose();
            bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, alpha);
            ground = new SKCanvas(bitmap);
        }
        if (gpu) ground!.Clear(SKColors.Transparent);
        var surface = gpu ? gpuFrame!.BeginFrame() : ground;
        if (surface is null)
        {
            DrawFrame(canvas, width, height, camera, tiles, style, pixelSize, imagery, false, false);
            return;
        }
        surface.Clear(new SKColor(colors.Ground));
        if (camera.Perspective && camera.Pitch > 0)
        {
            float horizon = (float)((height * .5 + (MapCamera.Focal(height) * Math.Cos(camera.PitchRadians) * (1 / camera.FarLimit(height) - 1) + camera.TargetPixels / camera.FarLimit(height)) / Math.Sin(camera.PitchRadians)) / pixelSize);
            if (horizon > 0)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = new(style == PixelStyle.Night ? 0xff273849u : style == PixelStyle.Dusk ? 0xffba969au : 0xff9bc1c8u);
                surface.DrawRect(0, 0, w, horizon, paint);
                paint.Color = new(style == PixelStyle.Night ? 0xff405264u : style == PixelStyle.Dusk ? 0xffd2aaa1u : 0xffc6d6cd);
                surface.DrawRect(0, Math.Max(0, horizon - 7), w, Math.Min(horizon, 7), paint);
            }
        }
        PrepareVisible(width, height, pixelSize, camera, tiles);
        double world = camera.WorldSize / pixelSize;

        bool hasEarth = imagery is { Count: > 0 } && DrawImagery(surface, width, height, pixelSize, camera, imagery);
        if (hasEarth)
        {
            surface.Save();
            surface.ClipPath(earthCoverage, SKClipOperation.Difference);
            if (!surface.LocalClipBounds.IsEmpty)
                DrawGround(surface, camera, pixelSize, colors, false);
            surface.Restore();
            surface.Save();
            surface.ClipPath(earthCoverage);
            DrawGround(surface, camera, pixelSize, colors, true);
            surface.Restore();
        }
        else
        {
            DrawGround(surface, camera, pixelSize, colors, false);
        }
        paint.Shader = null;
        surface.Flush();
        // In plan view keep the source's photographed roofs visible through the map footprints.
        // The opacity transitions continuously into full three-dimensional façades on tilt.
        bool preservePhoto = hasEarth && camera.Zoom >= 12 && camera.Pitch < 30;
        SKImage? underlay = null;
        if (preservePhoto)
        {
            if (gpu)
            {
                underlay = gpuFrame!.TrySnapshot(out var continuation);
                if (underlay is null || continuation is null)
                {
                    underlay?.Dispose();
                    DrawFrame(canvas, width, height, camera, tiles, style, pixelSize, imagery, false, false);
                    return;
                }
                surface = continuation;
            }
            else underlay = SKImage.FromBitmap(bitmap);
        }
        using var photoUnderlay = underlay;
        if (camera.Zoom >= 12)
        {
            float meters = (float)(world / (MapCamera.EarthCircumference *
                Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)));
            buildings.Begin(w, h, (float)Math.Sin(camera.PitchRadians), meters, camera.Perspective);
            var pixels = new Span<uint>((void*)bitmap.GetPixels(), checked(w * h));
            if (camera.Perspective)
            {
                perspectiveModels.Clear();
                perspectiveOrder.Clear();
                foreach (var (tile, transform, _) in visible)
                foreach (var model in tile.Buildings)
                {
                    var bounds = model.Bounds;
                    float closest = transform.Weight(bounds.MidX, bounds.MidY)
                        - (Math.Abs(transform.PX) * bounds.Width + Math.Abs(transform.PY) * bounds.Height) * .5f
                        - model.Top * meters * transform.CosPitch * transform.InverseFocal;
                    perspectiveOrder.Add((perspectiveModels.Count, closest));
                    perspectiveModels.Add((model, transform));
                }
                perspectiveOrder.Sort(static (a, b) => a.Near.CompareTo(b.Near));
                var models = CollectionsMarshal.AsSpan(perspectiveModels);
                foreach (var (index, _) in perspectiveOrder)
                {
                    ref readonly var model = ref models[index];
                    buildings.Draw(pixels, model.Model, model.Transform, colors.Materials[model.Model.Seed % (uint)colors.Materials.Length]);
                }
            }
            else
            {
                foreach (var (tile, transform, _) in visible)
                foreach (var building in tile.Buildings)
                    buildings.Draw(pixels, building, transform, colors.Materials[building.Seed % (uint)colors.Materials.Length]);
            }
            bitmap.NotifyPixelsChanged();
            if (gpu)
            {
                using var models = SKImage.FromBitmap(bitmap);
                surface.DrawImage(models, 0, 0, Nearest);
            }
        }
        if (photoUnderlay is not null)
        {
            surface.Save();
            surface.ClipPath(earthCoverage);
            paint.Style = SKPaintStyle.Fill;
            paint.Color = SKColors.White.WithAlpha((byte)Math.Round(209 * (1 - camera.Pitch / 30)));
            surface.DrawImage(photoUnderlay, 0, 0, Nearest, paint);
            surface.Restore();
        }
        surface.Flush();
        // Keep every art pixel the same size, including the last partial row or column.
        var viewport = new SKRect(0, 0, width, height);
        using var recorder = new SKPictureRecorder();
        var layer = recorder.BeginRecording(viewport);
        layer.ClipRect(viewport);
        if (gpu)
        {
            if (!gpuFrame!.TryResolve(layer, width, height, pixelSize))
            {
                DrawFrame(canvas, width, height, camera, tiles, style, pixelSize, imagery, false, false);
                return;
            }
        }
        else
        {
            using var image = SKImage.FromBitmap(bitmap);
            layer.DrawImage(image, new SKRect(0, 0, w * pixelSize, h * pixelSize), Nearest);
        }
        DrawLabels(layer, width, height, camera.Zoom, pixelSize, colors);
        lastPicture = recorder.EndRecording();
        lastView = view;
        lastTerrain = null;
        lastTiles.Clear();
        foreach (var tile in tiles) lastTiles.Add(tile);
        lastImagery.Clear();
        if (imagery is not null) foreach (var tile in imagery) lastImagery.Add(tile);
        canvas.DrawPicture(lastPicture);
    }

    private void DrawGround(SKCanvas surface, MapCamera camera, int pixelSize, PixelPalette colors, bool overlay)
    {
        Span<SKPoint> polygon = stackalloc SKPoint[16];
        foreach (var (tile, transform, scale) in visible)
        {
            int count = transform.VisiblePolygon(bitmap!.Width, bitmap.Height, polygon);
            if (count < 3) continue;
            surface.Save();
            var matrix = transform.Matrix;
            surface.SetMatrix(in matrix);
            if (transform.Perspective)
            {
                SetClip(polygon[..count]);
                surface.ClipPath(tileClip);
            }
            else surface.ClipRect(new SKRect(0, 0, TileGeometry.Extent, TileGeometry.Extent));
            if (camera.Zoom >= 12 && camera.Pitch >= 1) UpdateShadowSources(tile);
            if (transform.Perspective)
                terrain.DrawPerspective(surface, tile, transform, bitmap.Width, bitmap.Height,
                    pixelSize, camera.Pitch >= 1, overlay, colors, this);
            else terrain.Draw(surface, tile, scale, pixelSize, camera.Pitch >= 1, overlay, colors, this);
            surface.Restore();
        }
    }

    private void SetClip(ReadOnlySpan<SKPoint> polygon)
    {
        tileClip.Rewind(); tileClip.MoveTo(polygon[0]);
        for (int i = 1; i < polygon.Length; i++) tileClip.LineTo(polygon[i]);
        tileClip.Close();
    }

    private bool DrawImagery(SKCanvas surface, int width, int height, int pixelSize, MapCamera camera, IReadOnlyList<ImageryTile> imagery)
    {
        var projection = new TileProjection(camera, width, height, pixelSize);
        earthCoverage.Rewind();
        imageryOrder.Clear();
        foreach (var tile in imagery) imageryOrder.Add(tile);
        // Lower-resolution ancestors fill first, detailed children replace only their own area.
        imageryOrder.Sort(static (a, b) => a.Id.Z.CompareTo(b.Id.Z));
        var rect = new SKRect(0, 0, TileGeometry.Extent, TileGeometry.Extent);
        Span<SKPoint> polygon = stackalloc SKPoint[16];
        foreach (var tile in imageryOrder)
        {
            var copies = projection.Copies(tile.Id);
            if (copies.Last < copies.First || earth.Get(tile) is not { } image) continue;
            for (int i = copies.First; i <= copies.Last; i++)
            {
                var (transform, _) = projection.Get(tile.Id, i);
                int count = transform.VisiblePolygon(bitmap!.Width, bitmap.Height, polygon);
                if (count < 3) continue;
                surface.Save();
                var matrix = transform.Matrix;
                surface.SetMatrix(in matrix);
                if (transform.Perspective) { SetClip(polygon[..count]); surface.ClipPath(tileClip); }
                surface.DrawImage(image, rect, Nearest);
                surface.Restore();
                earthCoverage.MoveTo(transform.At(polygon[0].X, polygon[0].Y));
                for (int j = 1; j < count; j++) earthCoverage.LineTo(transform.At(polygon[j].X, polygon[j].Y));
                earthCoverage.Close();
            }
        }
        return !earthCoverage.IsEmpty;
    }

    private void PrepareVisible(int width, int height, int pixelSize, MapCamera camera, IReadOnlyList<MapTile> tiles)
    {
        frame++;
        visible.Clear();
        var projection = new TileProjection(camera, width, height, pixelSize);
        foreach (var tile in tiles)
        {
            if (!geometry.TryGetValue(tile.Id, out var prepared) || !ReferenceEquals(prepared.Source, tile))
            {
                if (prepared is not null) { terrain.Remove(tile.Id); prepared.Dispose(); }
                geometry[tile.Id] = prepared = new(tile);
                geometryRevision++;
            }
            prepared.LastFrame = frame;
            var copies = projection.Copies(tile.Id);
            if (CurrentTerrain is not null && copies.Last < copies.First)
            {
                int copy = (int)Math.Round(camera.CenterX - (tile.Id.X + .5) / (1 << tile.Id.Z));
                copies = (copy, copy);
            }
            for (int copy = copies.First; copy <= copies.Last; copy++)
            {
                var (transform, scale) = projection.Get(tile.Id, copy);
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
                geometryRevision++;
            }
        }
    }

    private void UpdateShadowSources(TileGeometry target)
    {
        if (target.ShadowRevision == geometryRevision) return;
        shadowSources.Clear();
        var targetId = target.Source.Id;
        int n = 1 << targetId.Z;
        var tileBounds = new SKRect(0, 0, TileGeometry.Extent, TileGeometry.Extent);
        foreach (var source in geometry.Values)
        {
            var id = source.Source.Id;
            if (id.Z != targetId.Z || source.Shadows.Length == 0) continue;
            int dx = id.X - targetId.X;
            if (dx > n / 2) dx -= n;
            if (dx < -n / 2) dx += n;
            float x = dx * TileGeometry.Extent, y = (id.Y - targetId.Y) * TileGeometry.Extent;
            var bounds = source.ShadowBounds;
            bounds.Offset(x, y);
            if (bounds.IntersectsWith(tileBounds)) shadowSources.Add(new(source, x, y));
        }
        target.ShadowRevision = geometryRevision;
        if (target.ShadowSources.SequenceEqual(shadowSources)) return;
        // A newly arrived neighbor can cast onto terrain that was already cached.
        target.ShadowSources.Clear();
        target.ShadowSources.AddRange(shadowSources);
        terrain.Remove(targetId);
    }

    private static bool SameTiles<T>(IReadOnlyList<T>? tiles, List<T> previous) where T : class
    {
        if (tiles is null) return previous.Count == 0;
        if (tiles.Count != previous.Count) return false;
        for (int i = 0; i < tiles.Count; i++)
            if (!ReferenceEquals(tiles[i], previous[i])) return false;
        return true;
    }

    internal void DrawTerrainPatch(SKCanvas canvas, TileGeometry tile, float scale, double zoom, int pixelSize,
        bool shadows, bool overlay, PixelPalette colors, SKRect clip)
    {
        float roadScale = (float)Math.Clamp(Math.Pow(2, (zoom - 14) * .37), .36, 5) / pixelSize;
        clip.Inflate(32 / scale, 32 / scale);
        foreach (var (key, path, bounds) in tile.Batches)
        {
            if (bounds.Left > clip.Right || bounds.Right < clip.Left || bounds.Top > clip.Bottom || bounds.Bottom < clip.Top) continue;
            if (overlay && (key.Width <= 0 || key.Ink is Ink.Stream or Ink.Path or Ink.Tunnel)) continue;
            paint.Style = key.Width > 0 ? SKPaintStyle.Stroke : SKPaintStyle.Fill;
            paint.StrokeWidth = Math.Max(.6f, key.Width * roadScale * (overlay ? .52f : 1)) / scale;
            paint.Color = new SKColor(colors.Inks[(int)key.Ink]);
            if (overlay) paint.Color = paint.Color.WithAlpha(key.Ink == Ink.RoadEdge ? (byte)90 : (byte)150);
            paint.Shader = null;
            paint.PathEffect = null;
            if (key.Ink is Ink.Tunnel or Ink.Boundary or Ink.Ferry or Ink.Rail)
            {
                if (!dashes.TryGetValue((key.Ink, scale), out var dash))
                {
                    float length = key.Ink == Ink.Ferry ? 5 : 3;
                    dashes[(key.Ink, scale)] = dash = SKPathEffect.CreateDash([length / scale, (length + 1) / scale], 0);
                }
                paint.PathEffect = dash;
            }
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
        paint.PathEffect = null;
        if (zoom >= 13 && shadows)
        {
            shadowMask.Rewind();
            foreach (var source in tile.ShadowSources)
            {
                var local = clip;
                local.Offset(-source.X, -source.Y);
                foreach (var batch in source.Tile.Shadows)
                    if (batch.Bounds.IntersectsWith(local)) shadowMask.AddPath(batch.Path, source.X, source.Y);
            }
            // Fill the union once, including buffered copies and casters across XYZ boundaries.
            // No overlapping window or tower part can darken the same ground texel twice.
            paint.Style = SKPaintStyle.Fill;
            paint.Color = new(colors.Inks[(int)Ink.Shadow]);
            canvas.DrawPath(shadowMask, paint);
        }
    }

    private void DrawLabels(SKCanvas canvas, int w, int h, double zoom, int pixelSize, PixelPalette colors)
    {
        candidates.Clear(); names.Clear(); occupied.Clear();
        foreach (var (tile, transform, _) in visible)
        foreach (var label in tile.Labels)
        {
            if (label.Street && zoom < 15 || label.Priority > 9 && zoom < 9) continue;
            SKPoint screen;
            if (CurrentTerrain is { } elevation)
            {
                float meters = (float)(terrainCamera.WorldSize / pixelSize / (MapCamera.EarthCircumference *
                    Math.Cos(Math.Clamp(terrainCamera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)));
                float altitude = GroundAltitude(tile, label.Point, elevation, terrainCamera);
                var point = transform.Vertex(label.Point.X, label.Point.Y, altitude * meters);
                if (point.W < MapCamera.Near || point.W > transform.Far) continue;
                screen = point.Point;
                if (buildings.CoversAt(screen.X, screen.Y, altitude + Math.Max(4, .75f / meters))) continue;
            }
            else
            {
                if (transform.Weight(label.Point.X, label.Point.Y) < MapCamera.Near || transform.Weight(label.Point.X, label.Point.Y) > transform.Far) continue;
                screen = transform.At(label.Point.X, label.Point.Y);
                if (label.Street && buildings.Covers(screen.X, screen.Y)) continue;
            }
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
            float size = label.Street ? 12f : 14f;
            float tw = label.TextWidth;
            if (float.IsNaN(tw))
            {
                font.Size = size;
                label.TextWidth = tw = font.MeasureText(label.Text);
            }
            if (tw <= 0 || tw > w * .8f) continue;
            float x = MathF.Round(screen.X - tw * .5f), y = MathF.Round(screen.Y);
            float pad = label.Street ? 22 : 7;
            var bounds = new SKRect(x - pad, y - size - 9, x + tw + pad, y + 9);
            if (bounds.Left < 1 || bounds.Right > w - 1 || bounds.Top < 1 || bounds.Bottom > h - 1) continue;
            bool collision = false;
            foreach (var prior in occupied)
                if (bounds.IntersectsWith(prior)) { collision = true; break; }
            if (collision) continue;
            names.Add(label.Text);
            occupied.Add(bounds);
            if (label.Glyphs is null)
            {
                font.Size = size;
                label.Glyphs = SKTextBlob.Create(label.Text, font);
            }
            if (label.Glyphs is not { } glyphs) continue;
            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 2.8f;
            paint.Color = new(colors.LabelHalo);
            canvas.DrawText(glyphs, x, y, paint);
            paint.Style = SKPaintStyle.Fill; paint.Color = new(colors.Label);
            canvas.DrawText(glyphs, x, y, paint);
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
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            ReleaseGpuScene();
            ReleasePatterns();
            foreach (var tile in geometry.Values) tile.Dispose();
            geometry.Clear();
            routeOverlay?.Dispose();
            terrain.Clear();
            reliefSurface?.Dispose();
            foreach (var dash in dashes.Values) dash.Dispose();
            dashes.Clear();
            lastPicture?.Dispose();
            gpuFrame?.Dispose();
            earth.Dispose(); earthCoverage.Dispose(); tileClip.Dispose(); shadowMask.Dispose(); ground?.Dispose(); bitmap?.Dispose(); paint.Dispose(); font.Dispose();
        }
    }
}
