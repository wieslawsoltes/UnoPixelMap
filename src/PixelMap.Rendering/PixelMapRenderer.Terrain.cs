using System.Runtime.InteropServices;
using PixelMap.Data;
using SkiaSharp;

namespace PixelMap.Rendering;

public sealed partial class PixelMapRenderer
{
    private TerrainSurface? reliefSurface;
    private TerrainOptions? lastTerrain;
    private MapCamera terrainCamera;
    private readonly List<TileGeometry> reliefTiles = [];
    private readonly Dictionary<Building, double> foundations = [];
    private readonly List<float> perspectiveFoundations = [];
    private readonly List<MapTile> terrainTextureTiles = [];
    private readonly List<ImageryTile> terrainTextureImagery = [];
    private (PixelStyle Style, int PixelSize, bool Shadows) terrainTextureStyle;
    private ElevationSnapshot? foundationSnapshot;
    private long foundationRevision;

    internal TerrainOptions? CurrentTerrain { get; private set; }

    /// <summary>Read the rendered height-field intersection at an art pixel. The retained
    /// view must match the requested camera and viewport. Sky has no terrain intersection.</summary>
    public bool TryPickTerrain(double x, double y, int width, int height, MapCamera camera, out TerrainHit hit)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            hit = default;
            return lastPicture is not null && lastTerrain is not null && lastView.Camera == camera &&
                lastView.Width == width && lastView.Height == height && x >= 0 && y >= 0 && x < width && y < height &&
                reliefSurface is not null && reliefSurface.TryPick(x, y, width, height, lastView.PixelSize, out hit);
        }
    }

    /// <summary>Project a geographic location onto the retained terrain mesh. A matching
    /// camera and viewport are required, allowing callers to use their geographic fallback
    /// while the next frame is pending. The camera's finite frustum is respected.</summary>
    public bool TryProjectTerrain(double latitude, double longitude, int width, int height,
        MapCamera camera, out (double X, double Y) screen)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            screen = default;
            if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || lastPicture is null ||
                lastTerrain is null || lastView.Camera != camera || lastView.Width != width || lastView.Height != height ||
                reliefSurface is null || !reliefSurface.TryHeight((longitude + 180) / 360, MapCamera.MercatorY(latitude), out double surface))
                return false;
            double altitude = camera.GroundAltitudeMeters + lastTerrain.LocalAltitude(camera, surface);
            return camera.TryProject(latitude, longitude, altitude, width, height, out screen);
        }
    }

    private unsafe void DrawTerrainFrame(SKCanvas canvas, int width, int height, MapCamera camera,
        IReadOnlyList<MapTile> tiles, PixelStyle style, int pixelSize, IReadOnlyList<ImageryTile>? imagery,
        bool useGpuRendering, bool allowDeferredGpuRendering, TerrainOptions options)
    {
        pixelSize = Math.Clamp(pixelSize, 1, 8);
        int w = (width + pixelSize - 1) / pixelSize, h = (height + pixelSize - 1) / pixelSize;
        bool gpu = useGpuRendering && (gpuFrame ??= new()).TryPrepare(canvas, w, h, allowDeferredGpuRendering);
        IsGpuRenderingActive = gpu;
        var view = (width, height, camera, style, pixelSize, gpu ? gpuFrame!.Generation : 0);
        if (view == lastView && options == lastTerrain && lastPicture is not null &&
            SameTiles(tiles, lastTiles) && SameTiles(imagery, lastImagery))
        {
            canvas.DrawPicture(lastPicture);
            return;
        }
        reliefSurface ??= new();
        bool shadows = camera.Pitch >= 1;
        var textureStyle = (style, pixelSize, shadows);
        if (terrainTextureStyle != textureStyle || !SameTiles(tiles, terrainTextureTiles) || !SameTiles(imagery, terrainTextureImagery))
        {
            reliefSurface.ClearTextures();
            terrainTextureStyle = textureStyle;
            terrainTextureTiles.Clear(); terrainTextureTiles.AddRange(tiles);
            terrainTextureImagery.Clear(); if (imagery is not null) terrainTextureImagery.AddRange(imagery);
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
            DrawTerrainFrame(canvas, width, height, camera, tiles, style, pixelSize, imagery, false, false, options);
            return;
        }
        surface.Clear(new SKColor(colors.Ground));
        if (camera.Perspective && camera.Pitch > 0)
        {
            float horizon = (float)((height * .5 + (MapCamera.Focal(height) * Math.Cos(camera.PitchRadians) *
                (1 / camera.FarLimit(height) - 1) + camera.TargetPixels / camera.FarLimit(height)) / Math.Sin(camera.PitchRadians)) / pixelSize);
            if (horizon > 0)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = new(style == PixelStyle.Night ? 0xff273849u : style == PixelStyle.Dusk ? 0xffba969au : 0xff9bc1c8u);
                surface.DrawRect(0, 0, w, horizon, paint);
                paint.Color = new(style == PixelStyle.Night ? 0xff405264u : style == PixelStyle.Dusk ? 0xffd2aaa1u : 0xffc6d6cdu);
                surface.DrawRect(0, Math.Max(0, horizon - 7), w, Math.Min(horizon, 7), paint);
            }
        }
        surface.Flush();
        terrainCamera = camera;
        PrepareVisible(width, height, pixelSize, camera, tiles);
        PrepareFoundations(options.Elevation);
        reliefTiles.Clear();
        foreach (var tile in tiles)
        {
            var prepared = geometry[tile.Id];
            if (camera.Zoom >= 12 && shadows) UpdateShadowSources(prepared);
            reliefTiles.Add(prepared);
        }
        reliefTiles.Sort(static (a, b) => a.Source.Id.Z.CompareTo(b.Source.Id.Z));
        imageryOrder.Clear();
        if (imagery is not null) imageryOrder.AddRange(imagery);
        imageryOrder.Sort(static (a, b) => a.Id.Z.CompareTo(b.Id.Z));
        float meters = (float)(camera.WorldSize / pixelSize / (MapCamera.EarthCircumference *
            Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)));
        buildings.Begin(w, h, (float)Math.Sin(camera.PitchRadians), meters, camera.Perspective);
        var projection = new TileProjection(camera, width, height, pixelSize);
        buildings.BeginTerrain(projection.Get(new(0, 0, 0), 0).Transform, reliefSurface);
        bool preservePhoto = imageryOrder.Count > 0 && camera.Zoom >= 12 && camera.Pitch < 30;
        reliefSurface.Draw(bitmap, buildings, camera, width, height, pixelSize, options, preservePhoto,
            (id, texture) => BakeTerrainTexture(id, texture, pixelSize, colors, shadows));
        var pixels = new Span<uint>((void*)bitmap.GetPixels(), checked(w * h));
        if (camera.Zoom >= 12)
        {
            if (camera.Perspective)
            {
                perspectiveModels.Clear();
                perspectiveOrder.Clear();
                perspectiveFoundations.Clear();
                foreach (var (tile, transform, _) in visible)
                foreach (var model in tile.Buildings)
                {
                    float foundation = Foundation(tile, model, options, camera);
                    var bounds = model.Bounds;
                    float closest = transform.Weight(bounds.MidX, bounds.MidY)
                        - (Math.Abs(transform.PX) * bounds.Width + Math.Abs(transform.PY) * bounds.Height) * .5f
                        - (model.Top + foundation) * meters * transform.CosPitch * transform.InverseFocal;
                    perspectiveOrder.Add((perspectiveModels.Count, closest));
                    perspectiveModels.Add((model, transform));
                    perspectiveFoundations.Add(foundation);
                }
                perspectiveOrder.Sort(static (a, b) => a.Near.CompareTo(b.Near));
                var models = CollectionsMarshal.AsSpan(perspectiveModels);
                foreach (var (index, _) in perspectiveOrder)
                {
                    ref readonly var model = ref models[index];
                    float foundation = perspectiveFoundations[index];
                    buildings.Draw(pixels, model.Model, model.Transform,
                        colors.Materials[model.Model.Seed % (uint)colors.Materials.Length], foundation);
                }
            }
            else
            {
                foreach (var (tile, transform, _) in visible)
                foreach (var model in tile.Buildings)
                    buildings.Draw(pixels, model, transform, colors.Materials[model.Seed % (uint)colors.Materials.Length],
                        Foundation(tile, model, options, camera));
            }
        }
        if (preservePhoto) reliefSurface.RestorePhotographs(pixels, (float)(209d / 255 * (1 - camera.Pitch / 30)));
        bitmap.NotifyPixelsChanged();
        if (gpu)
        {
            using var artwork = SKImage.FromBitmap(bitmap);
            surface.DrawImage(artwork, 0, 0, Nearest);
        }
        surface.Flush();
        var viewport = new SKRect(0, 0, width, height);
        using var recorder = new SKPictureRecorder();
        var layer = recorder.BeginRecording(viewport);
        layer.ClipRect(viewport);
        if (gpu)
        {
            if (!gpuFrame!.TryResolve(layer, width, height, pixelSize))
            {
                DrawTerrainFrame(canvas, width, height, camera, tiles, style, pixelSize, imagery, false, false, options);
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
        lastTerrain = options;
        lastTiles.Clear(); lastTiles.AddRange(tiles);
        lastImagery.Clear(); if (imagery is not null) lastImagery.AddRange(imagery);
        canvas.DrawPicture(lastPicture);
    }

    private SKRect[] BakeTerrainTexture(TileId patch, SKBitmap texture, int pixelSize, PixelPalette colors, bool shadows)
    {
        using var canvas = new SKCanvas(texture);
        canvas.Clear(new SKColor(colors.Ground));
        var textureBounds = new SKRect(0, 0, TerrainSurface.TextureSize, TerrainSurface.TextureSize);
        bool fullImagery = false;
        foreach (var tile in imageryOrder)
        {
            if (!TextureArea(patch, tile.Id).Contains(textureBounds) || earth.Get(tile) is null) continue;
            fullImagery = true;
            break;
        }
        if (!fullImagery) DrawVectorArtwork(false);
        var areas = new List<SKRect>();
        using var coverage = new SKPath();
        foreach (var imageTile in imageryOrder)
        {
            var area = TextureArea(patch, imageTile.Id);
            if (!area.IntersectsWith(new(0, 0, TerrainSurface.TextureSize, TerrainSurface.TextureSize)) || earth.Get(imageTile) is not { } image) continue;
            canvas.DrawImage(image, area, Nearest);
            area.Intersect(new(0, 0, TerrainSurface.TextureSize, TerrainSurface.TextureSize));
            coverage.AddRect(area);
            areas.Add(area);
        }
        if (areas.Count > 0)
        {
            canvas.Save(); canvas.ClipPath(coverage);
            DrawVectorArtwork(true);
            canvas.Restore();
        }
        canvas.Flush();
        return areas.ToArray();

        void DrawVectorArtwork(bool overlay)
        {
            int coveringZoom = -1;
            foreach (var tile in reliefTiles)
                if (TextureArea(patch, tile.Source.Id).Contains(textureBounds)) coveringZoom = Math.Max(coveringZoom, tile.Source.Id.Z);
            foreach (var tile in reliefTiles)
            {
                if (tile.Source.Id.Z < coveringZoom) continue;
                var area = TextureArea(patch, tile.Source.Id);
                if (!area.IntersectsWith(new(0, 0, TerrainSurface.TextureSize, TerrainSurface.TextureSize))) continue;
                float scale = area.Width / TileGeometry.Extent;
                var matrix = SKMatrix.CreateScaleTranslation(scale, scale, area.Left, area.Top);
                canvas.Save(); canvas.SetMatrix(in matrix);
                canvas.ClipRect(new(0, 0, TileGeometry.Extent, TileGeometry.Extent));
                // A distant ancestor has no extra source detail beyond its MVT coordinate
                // grid. Retain that native-density artwork once, then magnify the relevant
                // part instead of re-styling global paths for every camera-sized patch.
                terrain.Draw(canvas, tile, Math.Min(1, scale), pixelSize, shadows, overlay, colors, this);
                canvas.Restore();
            }
        }
    }

    private static SKRect TextureArea(TileId patch, TileId source)
    {
        double relative = Math.Pow(2, patch.Z - source.Z);
        float size = (float)(TerrainSurface.TextureSize * relative);
        float left = (float)((source.X * relative - patch.X) * TerrainSurface.TextureSize);
        float top = (float)((source.Y * relative - patch.Y) * TerrainSurface.TextureSize);
        return new(left, top, left + size, top + size);
    }

    private void PrepareFoundations(ElevationSnapshot snapshot)
    {
        if (ReferenceEquals(foundationSnapshot, snapshot) && foundationRevision == geometryRevision) return;
        foundations.Clear();
        foundationSnapshot = snapshot;
        foundationRevision = geometryRevision;
    }

    private float Foundation(TileGeometry tile, Building building, TerrainOptions terrain, MapCamera camera, bool useRenderedMesh = true)
    {
        var id = tile.Source.Id;
        double count = 1 << id.Z;
        double x = (id.X + building.Bounds.MidX / TileGeometry.Extent) / count;
        double y = (id.Y + building.Bounds.MidY / TileGeometry.Extent) / count;
        if (!foundations.TryGetValue(building, out double surface))
        {
            foundations[building] = surface = terrain.Elevation.SampleSurfaceMercator(x, y);
        }
        if (useRenderedMesh && reliefSurface is not null && reliefSurface.TryHeight(x, y, out double rendered)) surface = rendered;
        return (float)terrain.LocalAltitude(camera, surface);
    }

    private float GroundAltitude(TileGeometry tile, SKPoint point, TerrainOptions terrain, MapCamera camera, bool useRenderedMesh = true)
    {
        var id = tile.Source.Id;
        double count = 1 << id.Z;
        double x = (id.X + point.X / TileGeometry.Extent) / count, y = (id.Y + point.Y / TileGeometry.Extent) / count;
        double surface = useRenderedMesh && reliefSurface is not null && reliefSurface.TryHeight(x, y, out double rendered)
            ? rendered : terrain.Elevation.SampleSurfaceMercator(x, y);
        return (float)terrain.LocalAltitude(camera, surface);
    }
}
