using System.Diagnostics;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using Uno.WinUI.Graphics3DWebGpu;
using Windows.Foundation;

namespace PixelMap.Controls;

public sealed partial class PixelMapControl
{
    /// <summary>Owns the map's WebGPU resources. Uno owns the device, queue and
    /// target; all presentation stays on that shared device, including overlays.</summary>
    private sealed unsafe class MapSurface(Action<bool> backendChanged, Action framePresented) : WebGpuCanvasElement
    {
        private sealed record Scene(MapCamera Camera, IReadOnlyList<MapTile> Tiles,
            IReadOnlyList<ImageryTile>? Imagery, PixelStyle Palette, bool Moving,
            RoutePlan? Route, VehiclePose? Vehicle, TravelMode VehicleMode, RoutePosition? Gate,
            WebGpuQuality Quality, TerrainOptions? Terrain, MapRenderStyle RenderStyle);

        private sealed class PreparedScene(Scene scene, int width, int height, int pixelSize)
        {
            internal readonly Scene Scene = scene;
            internal readonly int Width = width, Height = height, PixelSize = pixelSize;
            internal readonly MapTile[] Tiles = scene.Tiles.ToArray();
            internal readonly ImageryTile[]? Imagery = scene.Imagery?.ToArray();

            internal bool Matches(Scene other, int w, int h, int pixel) =>
                Width == w && Height == h && PixelSize == pixel && Scene.Camera == other.Camera &&
                Scene.Palette == other.Palette && Scene.Terrain == other.Terrain &&
                Scene.RenderStyle == other.RenderStyle && SameSources(Tiles, other.Tiles) &&
                SameSources(Imagery, other.Imagery);

            private static bool SameSources<T>(T[]? snapshot, IReadOnlyList<T>? current) where T : class
            {
                if (snapshot is null || current is null) return snapshot is null && current is null;
                if (snapshot.Length != current.Count) return false;
                for (int i = 0; i < snapshot.Length; i++)
                    if (!ReferenceEquals(snapshot[i], current[i])) return false;
                return true;
            }
        }

        private readonly object _drawing = new();
        private PixelMapRenderer? _sceneBuilder;
        private WebGpuMapRenderer? _gpu;
        private WebGpuSceneOverlay? _overlay;
        private GpuMapFrame? _frame;
        private PreparedScene? _prepared;
        private Scene? _scene, _presentedScene;
        private MapCamera? _timedCamera;
        private double _movingCost = 16;
        private int _interactivePixelSize = 2;
        private bool _active;
        private long _frames, _preparations;

        internal string? Failure { get; private set; }

        internal void Present(MapCamera camera, IReadOnlyList<MapTile> tiles, IReadOnlyList<ImageryTile>? imagery,
            PixelStyle palette, bool moving, RoutePlan? route, VehiclePose? vehicle, TravelMode vehicleMode,
            RoutePosition? gate, WebGpuQuality quality, TerrainOptions? terrain, MapRenderStyle renderStyle)
        {
            lock (_drawing)
            {
                var scene = new Scene(camera, tiles, imagery, palette, moving, route, vehicle,
                    vehicleMode, gate, quality, terrain, renderStyle);
                if (scene == _scene) return;
                bool retainedIdle = Failure is null && scene.Route is null && scene.Gate is null &&
                    _presentedScene is { } displayed && scene.Vehicle is { Moving: false } idle &&
                    (scene with { Vehicle = displayed.Vehicle }) == displayed &&
                    _prepared?.Matches(scene, Math.Max(1, (int)Math.Ceiling(ActualWidth)),
                        Math.Max(1, (int)Math.Ceiling(ActualHeight)), ArtworkPixelSize(scene)) == true &&
                    _overlay?.TryAdvanceStationaryVehicle(scene.VehicleMode, idle) == true;
                _scene = scene;
                if (retainedIdle)
                {
                    _presentedScene = scene;
                    return;
                }
                if (Failure is null) Invalidate();
            }
        }

        protected override void Init(WebGpuCanvasContext context)
        {
            lock (_drawing)
            {
                Failure = null;
                try
                {
                    // The API is Uno's ABI-compatible shim. The context's borrowed
                    // device and queue must never be released by this control.
                    _gpu = new(context.Api, context.Device, context.Queue);
                    _overlay = new();
                    _sceneBuilder ??= new();
                }
                catch (Exception error)
                {
                    // Uno does not call OnDestroy when Init fails.
                    _gpu?.Dispose();
                    _gpu = null;
                    _overlay?.Dispose();
                    _overlay = null;
                    Fail(error);
                    throw;
                }
            }
        }

        protected override void RenderOverride(WebGpuCanvasContext context)
        {
            lock (_drawing)
            {
                if (Failure is not null || _scene is not { } scene || _gpu is not { } gpu ||
                    _sceneBuilder is not { } builder || _overlay is not { } overlay ||
                    context.PixelWidth <= 0 || context.PixelHeight <= 0) return;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    int width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
                    int height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
                    int pixelSize = ArtworkPixelSize(scene);
                    if (_frame is null || _prepared?.Matches(scene, width, height, pixelSize) != true)
                    {
                        var request = new PreparedScene(scene, width, height, pixelSize);
                        var next = builder.PrepareGpuFrame(width, height, scene.Camera, request.Tiles,
                            scene.Palette, pixelSize, request.Imagery, scene.Terrain, scene.RenderStyle,
                            prepareLegacyOverlay: false);
                        var previous = _frame;
                        _frame = next;
                        _prepared = request;
                        previous?.Dispose();
                        _preparations++;
                    }
                    builder.ActivateGpuFrame(_frame);
                    builder.PrepareWebGpuOverlay(_frame, overlay, scene.Route, scene.Vehicle, scene.VehicleMode, scene.Gate);
                    gpu.Quality = scene.Quality;
                    gpu.Draw(_frame, context.TargetView, context.TargetFormat,
                        (uint)context.PixelWidth, (uint)context.PixelHeight, sceneOverlay: overlay);
                    // The texture and the scene used for picking advance together.
                    // No browser image, CPU readback or second rendering context exists.
                    _presentedScene = scene;
                    ReportBackend(true);
                    _frames++;
                    double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    TrackFrameCost(scene, elapsed);
                    BrowserWebGpuDiagnostics.Report(_frames, _preparations, width, height,
                        (int)context.PixelWidth, (int)context.PixelHeight, pixelSize,
                        _frame.TerrainPatchCount, _frame.BuildingTriangleCount, gpu.LastDrawCalls,
                        gpu.LastUploadBytes, gpu.ResidentBytes, elapsed, gpu.LastSubmissionMilliseconds,
                        scene.RenderStyle.ToString(), scene.Quality.ToString(), gpu.RenderWidth, gpu.RenderHeight,
                        gpu.AttachmentBytes, scene.Imagery?.Count ?? 0,
                        scene.Terrain?.Relief == true, scene.Terrain?.Hypsometric == true, scene.Camera.Zoom);
                    framePresented();
                }
                catch (Exception error)
                {
                    Fail(error);
                }
            }
        }

        protected override void OnWebGpuUnavailable()
        {
            lock (_drawing)
                Fail(new NotSupportedException("WebGPU could not initialize. Use a browser with WebGPU enabled and an available graphics adapter, then reload Pixel Atlas."));
        }

        protected override void OnDestroy(WebGpuCanvasContext context)
        {
            lock (_drawing)
            {
                _gpu?.Dispose();
                _gpu = null;
                ReleaseScene();
                ReportBackend(false);
            }
        }

        internal void Release()
        {
            lock (_drawing)
            {
                _scene = null;
                ReleaseScene();
                // GPU resources are released by OnDestroy in Uno's lifecycle.
                ReportBackend(false);
            }
        }

        private void ReleaseScene()
        {
            _frame?.Dispose();
            _frame = null;
            _prepared = null;
            _presentedScene = null;
            _overlay?.Dispose();
            _overlay = null;
            _sceneBuilder?.Dispose();
            _sceneBuilder = null;
        }

        private void Fail(Exception error)
        {
            if (Failure is not null) return;
            Failure = error.Message;
            // Preparation can advance CPU geometry before a GPU submission fails.
            // Never expose that unpresented scene through picking or terrain queries.
            _presentedScene = null;
            Console.Error.WriteLine($"Pixel Atlas WebGPU rendering failed: {error}");
            ReportBackend(false);
            BrowserWebGpuDiagnostics.Fail(error.Message);
        }

        internal MapFeature? Pick(Point point, int width, int height)
        {
            lock (_drawing)
            {
                if (Failure is not null || _frame is not { } frame || _presentedScene is not { } scene || _sceneBuilder is not { } builder)
                    return null;
                builder.ActivateGpuFrame(frame);
                return builder.Pick(point.X, point.Y, frame.Width, frame.Height, frame.Camera, scene.Tiles);
            }
        }

        internal InteractionView GetInteractionView(MapCamera camera, TerrainOptions? terrain, double width, double height)
        {
            lock (_drawing)
                return Failure is null && _frame is { } frame && _presentedScene is { } scene
                    ? new(frame.Camera, scene.Terrain, frame.Width, frame.Height)
                    : new(camera, terrain, width, height);
        }

        internal bool TryProjectTerrain(double latitude, double longitude, int width, int height, MapCamera camera,
            out (double X, double Y) screen)
        {
            lock (_drawing)
            {
                screen = default;
                return Failure is null && _presentedScene is not null && _frame is { } && _sceneBuilder is { } renderer &&
                    renderer.TryProjectTerrain(latitude, longitude, width, height, camera, out screen);
            }
        }

        internal bool TryPickTerrain(double x, double y, int width, int height, MapCamera camera, out TerrainHit hit)
        {
            lock (_drawing)
            {
                hit = default;
                return Failure is null && _presentedScene is not null && _frame is { } && _sceneBuilder is { } renderer &&
                    renderer.TryPickTerrain(x, y, width, height, camera, out hit);
            }
        }

        internal bool TryGetTerrainElevation(double latitude, double longitude, out double elevationMeters)
        {
            lock (_drawing)
            {
                elevationMeters = 0;
                return Failure is null && _presentedScene is not null && _frame is { } && _sceneBuilder is { } renderer &&
                    renderer.TryGetTerrainElevation(latitude, longitude, out elevationMeters);
            }
        }

        private int ArtworkPixelSize(Scene scene) => scene.RenderStyle is MapRenderStyle.Normal or MapRenderStyle.Roblox
            ? 1 : scene.Moving && scene.Quality == WebGpuQuality.Balanced ? _interactivePixelSize : 2;

        private void TrackFrameCost(Scene scene, double milliseconds)
        {
            if (scene.Moving && _timedCamera != scene.Camera)
            {
                _movingCost = .75 * _movingCost + .25 * Math.Min(100, milliseconds);
                if (_movingCost > 29 && _interactivePixelSize < 4) { _interactivePixelSize++; _movingCost = 20; }
                else if (_movingCost < 10 && _interactivePixelSize > 2) { _interactivePixelSize--; _movingCost = 16; }
            }
            _timedCamera = scene.Camera;
        }

        private void ReportBackend(bool active)
        {
            if (_active == active) return;
            _active = active;
            backendChanged(active);
        }
    }
}
