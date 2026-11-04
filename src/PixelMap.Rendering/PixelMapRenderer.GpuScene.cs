using PixelMap.Data;
using SkiaSharp;

namespace PixelMap.Rendering;

public sealed partial class PixelMapRenderer
{
    private const long GeometryGpuGeneration = long.MinValue;
    private readonly Dictionary<TileGeometry, GpuBuildingCache> gpuBuildings = [];
    private GpuMapFrame? lastGpuScene, activeGpuScene;
    private SKPicture? gpuLabelPicture;
    private GpuSceneDepth? activeGpuDepth;

    private sealed class GpuBuildingCache(GpuBuildingMesh mesh)
    {
        internal GpuBuildingMesh Mesh = mesh;
        internal long HeightRevision = -1;
        internal ElevationSnapshot? Elevation;
    }

    /// <summary>Prepare retained terrain and model buffers for hardware rasterization.
    /// This performs adaptive mesh selection, shared-edge stitching and sparse visibility
    /// indexing without producing terrain or model pixels on the CPU. Dispose the returned
    /// frame after the host has finished drawing it.</summary>
    public GpuMapFrame PrepareGpuFrame(int width, int height, MapCamera camera, IReadOnlyList<MapTile> tiles,
        PixelStyle style = PixelStyle.Day, int pixelSize = 2, IReadOnlyList<ImageryTile>? imagery = null,
        TerrainOptions? terrainOptions = null)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(tiles);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
            pixelSize = Math.Clamp(pixelSize, 1, 8);
            CurrentTerrain = terrainOptions is { Elevation.Count: > 0 } && (terrainOptions.Relief || terrainOptions.Hypsometric)
                ? terrainOptions : null;
            var options = CurrentTerrain ?? new TerrainOptions(ElevationSnapshot.Empty, Relief: false);
            var view = (width, height, camera, style, pixelSize, GeometryGpuGeneration);
            if (view == lastView && CurrentTerrain == lastTerrain && gpuLabelPicture is not null && lastGpuScene is { } cached &&
                ReferenceEquals(activeGpuScene, cached) &&
                SameTiles(tiles, lastTiles) && SameTiles(imagery, lastImagery))
            {
                var retained = new GpuMapFrame(width, height, pixelSize, camera, options, cached.Palette, cached.PixelsPerMeter,
                    cached.Terrain, cached.Buildings)
                {
                    Depth = cached.Depth,
                    MinimumAltitude = cached.MinimumAltitude,
                    MaximumAltitude = cached.MaximumAltitude,
                    PreservePhotograph = cached.PreservePhotograph,
                    Overlay = RetainGpuPicture(gpuLabelPicture),
                    Tiles = cached.Tiles,
                    Imagery = cached.Imagery
                };
                lastGpuScene = retained;
                ActivateGpuFrameCore(retained);
                return retained;
            }

            reliefSurface ??= new();
            bool shadows = camera.Pitch >= 1;
            var textureStyle = (style, pixelSize, shadows);
            if (terrainTextureStyle != textureStyle || !SameTerrainSources(tiles, terrainTextureTiles, static tile => tile.Id) ||
                !SameTerrainSources(imagery, terrainTextureImagery, static tile => tile.Id))
            {
                reliefSurface.ClearTextures();
                terrainTextureStyle = textureStyle;
            }
            terrainTextureTiles.Clear(); terrainTextureTiles.AddRange(tiles);
            terrainTextureImagery.Clear(); if (imagery is not null) terrainTextureImagery.AddRange(imagery);
            var colors = style switch { PixelStyle.Dusk => PixelPalette.Dusk, PixelStyle.Night => PixelPalette.Night, _ => PixelPalette.Day };
            if (palette != colors)
            {
                ReleasePatterns();
                terrain.Clear();
                palette = colors;
            }
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
            var terrainDraws = reliefSurface.PrepareGpu(camera, width, height, pixelSize, options,
                (id, texture) => BakeTerrainTexture(id, texture, pixelSize, colors, shadows));
            var modelDraws = new List<GpuBuildingDraw>();
            if (camera.Zoom >= 12)
                foreach (var (tile, transform, _) in visible)
                {
                    if (tile.Buildings.Count == 0) continue;
                    var mesh = PrepareGpuBuildings(tile, options);
                    float dx = transform.XX - transform.CX * transform.PX;
                    float dy = transform.XY - transform.CX * transform.PY;
                    float unitsToMeters = MathF.Sqrt(dx * dx + dy * dy) / meters;
                    modelDraws.Add(new(mesh, transform, unitsToMeters));
                }
            foreach (var tile in gpuBuildings.Keys.ToArray())
                if (!geometry.TryGetValue(tile.Source.Id, out var retained) || !ReferenceEquals(tile, retained)) gpuBuildings.Remove(tile);

            var result = new GpuMapFrame(width, height, pixelSize, camera, options, colors, meters, terrainDraws, modelDraws.ToArray())
            {
                PreservePhotograph = imageryOrder.Count > 0 && camera.Zoom >= 12 && camera.Pitch < 30,
                Tiles = tiles.ToArray(),
                Imagery = imagery?.ToArray() ?? []
            };
            result.Depth = new(result);
            ActivateGpuFrameCore(result);
            using var recorder = new SKPictureRecorder();
            var viewport = new SKRect(0, 0, width, height);
            var overlay = recorder.BeginRecording(viewport);
            overlay.ClipRect(viewport);
            DrawLabels(overlay, width, height, camera.Zoom, pixelSize, colors);
            gpuLabelPicture?.Dispose();
            gpuLabelPicture = recorder.EndRecording();
            result.Overlay = RetainGpuPicture(gpuLabelPicture);
            lastPicture?.Dispose();
            lastPicture = RetainGpuPicture(result.Overlay);
            lastGpuScene = result;
            return result;
        }
    }

    /// <summary>Draw the retained labels over the frame's hardware-rendered terrain and
    /// models, and activate its depth queries for the existing navigation and effects APIs.</summary>
    public void DrawGpuOverlay(SKCanvas canvas, GpuMapFrame frame)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(canvas);
            ArgumentNullException.ThrowIfNull(frame);
            ObjectDisposedException.ThrowIf(frame.IsDisposed, frame);
            ActivateGpuFrameCore(frame);
            if (frame.Overlay is { } overlay) canvas.DrawPicture(overlay);
        }
    }

    /// <summary>Use a retained frame for terrain projection, picking and navigation
    /// visibility. A host can keep interacting with the displayed frame while another
    /// frame is being prepared for asynchronous presentation.</summary>
    public void ActivateGpuFrame(GpuMapFrame frame)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(frame);
            ObjectDisposedException.ThrowIf(frame.IsDisposed, frame);
            ActivateGpuFrameCore(frame);
        }
    }

    private static bool SameTerrainSources<T>(IReadOnlyList<T>? sources, List<T> previous,
        Func<T, TileId> getId) where T : class
    {
        if (SameTiles(sources, previous)) return true;
        if (sources is null || sources.Count != previous.Count) return false;
        // Tile request priority follows the moving camera. That ordering does not
        // change camera-independent artwork clipped to unique source tile bounds.
        // Duplicate IDs can overlap in drawing order and require rebaking.
        var ids = new HashSet<TileId>();
        var remaining = new HashSet<T>(previous.Count, ReferenceEqualityComparer.Instance);
        foreach (var source in previous)
        {
            if (!ids.Add(getId(source))) return false;
            remaining.Add(source);
        }
        foreach (var source in sources)
            if (!remaining.Remove(source)) return false;
        return remaining.Count == 0;
    }

    private GpuBuildingMesh PrepareGpuBuildings(TileGeometry tile, TerrainOptions options)
    {
        if (!gpuBuildings.TryGetValue(tile, out var cached))
        {
            cached = new(new(tile));
            gpuBuildings.Add(tile, cached);
        }
        var mesh = cached.Mesh;
        mesh.Frame = frame;
        if (cached.HeightRevision == reliefSurface!.HeightRevision && ReferenceEquals(cached.Elevation, options.Elevation)) return mesh;
        float[]? models = null;
        double count = 1 << tile.Source.Id.Z;
        for (int i = 0; i < tile.Buildings.Count; i++)
        {
            var model = tile.Buildings[i];
            double x = (tile.Source.Id.X + model.Bounds.MidX / TileGeometry.Extent) / count;
            double y = (tile.Source.Id.Y + model.Bounds.MidY / TileGeometry.Extent) / count;
            if (!foundations.TryGetValue(model, out double surface))
                foundations[model] = surface = options.Elevation.SampleSurfaceMercator(x, y);
            if (reliefSurface.TryHeight(x, y, out double rendered)) surface = rendered;
            int offset = i * GpuBuildingMesh.ModelStride;
            if (mesh.Models[offset] == (float)surface) continue;
            models ??= (float[])mesh.Models.Clone();
            models[offset] = (float)surface;
        }
        if (models is not null) cached.Mesh = mesh = mesh.WithFoundations(models);
        cached.HeightRevision = reliefSurface.HeightRevision;
        cached.Elevation = options.Elevation;
        return mesh;
    }

    private void ActivateGpuFrameCore(GpuMapFrame frame)
    {
        IsGpuRenderingActive = true;
        if (ReferenceEquals(activeGpuScene, frame) && lastView.GpuGeneration == GeometryGpuGeneration) return;
        activeGpuScene = frame;
        if (!ReferenceEquals(activeGpuDepth, frame.Depth))
        {
            frame.Depth.Retain();
            activeGpuDepth?.Release();
            activeGpuDepth = frame.Depth;
        }
        if (palette != frame.Palette)
        {
            ReleasePatterns();
            terrain.Clear();
            palette = frame.Palette;
        }
        CurrentTerrain = frame.TerrainOptions is { Elevation.Count: > 0 } active && (active.Relief || active.Hypsometric) ? active : null;
        terrainCamera = frame.Camera;
        (reliefSurface ??= new()).ActivateGpu(frame);
        var lens = new TileProjection(frame.Camera, frame.Width, frame.Height, frame.PixelSize).Get(new(0, 0, 0), 0).Transform;
        buildings.BeginGpuScene(frame.ArtWidth, frame.ArtHeight, frame.PixelsPerMeter, lens, frame.Depth.CoversAt);
        lastTerrain = CurrentTerrain;
        lastView = (frame.Width, frame.Height, frame.Camera, frame.Palette.Style, frame.PixelSize, GeometryGpuGeneration);
        lastTiles.Clear(); lastTiles.AddRange(frame.Tiles);
        lastImagery.Clear(); lastImagery.AddRange(frame.Imagery);
        if (frame.Overlay is { } overlay)
        {
            lastPicture?.Dispose();
            lastPicture = RetainGpuPicture(overlay);
        }
    }

    private static SKPicture RetainGpuPicture(SKPicture source)
    {
        using var recorder = new SKPictureRecorder();
        recorder.BeginRecording(source.CullRect).DrawPicture(source);
        return recorder.EndRecording();
    }

    private void ReleaseGpuScene()
    {
        lastGpuScene = null;
        activeGpuScene = null;
        gpuBuildings.Clear();
        activeGpuDepth?.Release();
        activeGpuDepth = null;
        gpuLabelPicture?.Dispose();
        gpuLabelPicture = null;
    }
}
