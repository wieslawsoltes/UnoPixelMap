using System.Diagnostics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace PixelMap.Controls;

/// <summary>A reusable vector map with isometric and perspective pixel rendering and journey simulation.</summary>
public sealed partial class PixelMapControl : UserControl
{
    public static readonly MapCamera OpeningView = new(40.7087, -74.0104, 16.5, -15, 55);
    public static readonly DependencyProperty CameraProperty = DependencyProperty.Register(nameof(Camera),
        typeof(MapCamera), typeof(PixelMapControl), new PropertyMetadata(OpeningView, (o, _) => ((PixelMapControl)o).ViewChanged()));
    public static readonly DependencyProperty PaletteProperty = DependencyProperty.Register(nameof(Palette),
        typeof(PixelStyle), typeof(PixelMapControl), new PropertyMetadata(PixelStyle.Day, (o, _) => ((PixelMapControl)o).InvalidateMap()));

    public static readonly DependencyProperty IsEarthImageryEnabledProperty = DependencyProperty.Register(nameof(IsEarthImageryEnabled),
        typeof(bool), typeof(PixelMapControl), new PropertyMetadata(false, (o, _) => ((PixelMapControl)o).EarthChanged()));

    public static readonly DependencyProperty IsGpuRenderingEnabledProperty = DependencyProperty.Register(nameof(IsGpuRenderingEnabled),
        typeof(bool), typeof(PixelMapControl), new PropertyMetadata(false, (o, _) => ((PixelMapControl)o).GpuRenderingChanged()));

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(18) };
    private readonly MapSurface _surface;
    private readonly Border _pin;
    private readonly TextBlock _pinLabel;
    private readonly TranslateTransform _pinPosition = new();
    private readonly VectorTileSource _source;
    private readonly DispatcherTimer _tilesTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly EarthImagerySource _earthSource;
    private readonly TileViewport<MapTile> _vectors;
    private readonly TileViewport<ImageryTile> _earth;
    private IReadOnlyList<MapTile> _departure = [];
    private IReadOnlyList<MapTile> _world = [];
    private IReadOnlyList<ImageryTile> _earthOverview = [];
    private IReadOnlyList<ImageryTile> _earthDeparture = [];
    private IReadOnlyList<ImageryTile> _earthDisplay = [];
    private CancellationTokenSource? _overviewRequest;
    private (double Latitude, double Longitude)? _marker;
    private bool _active;
    private string _layerStatus = "Opening atlas";

    public PixelMapControl() : this(new VectorTileSource(Http)) { }

    public PixelMapControl(VectorTileSource source, EarthImagerySource? earthSource = null)
        : this(source, earthSource, null) { }

    public PixelMapControl(VectorTileSource source, EarthImagerySource? earthSource, ElevationTileSource? elevationSource)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _earthSource = earthSource ?? new EarthImagerySource(Http);
        _vectors = new(_source.GetTileAsync, LayerChanged);
        _earth = new(_earthSource.GetTileAsync, LayerChanged);
        _elevationSource = elevationSource ?? new ElevationTileSource(Http);
        _elevations = new(_elevationSource.GetTileAsync, LayerChanged, retainPreviousOnFailure: true);
        var dispatcher = DispatcherQueue;
        var layers = new Grid { Background = Brush(170, 187, 161) };
        _surface = new(layers, isActive => dispatcher.TryEnqueue(() => SetGpuRenderingActive(isActive)),
            () => { if (_marker.HasValue && _terrain is not null) dispatcher.TryEnqueue(() => { if (_active) PositionMarker(); }); });
        _pinLabel = new TextBlock { FontSize = 12, Foreground = Brush(255, 247, 220), TextTrimming = TextTrimming.CharacterEllipsis };
        _pin = new Border
        {
            Child = _pinLabel,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brush(27, 50, 52),
            BorderBrush = Brush(246, 192, 105),
            BorderThickness = new(2),
            CornerRadius = new(4),
            Padding = new(8, 3, 8, 3),
            MaxWidth = 230,
            IsHitTestVisible = false,
            RenderTransform = _pinPosition,
            Visibility = Visibility.Collapsed
        };
        _pin.SizeChanged += (_, _) => PositionMarker();
        layers.Children.Add(_surface);
        layers.Children.Add(_pin);
        Content = layers;
        IsTabStop = true;
        AutomationProperties.SetName(this, "Interactive pixel map");
        HookInput();
        HookJourney();
        _tilesTimer.Tick += (_, _) => { _tilesTimer.Stop(); RefreshTiles(_longFlight ? _to : Camera); };
        SizeChanged += (_, _) =>
        {
            Camera = Camera.OnTerrain(_terrain, ActualHeight);
            ViewChanged();
            if (!_active) return;
            if (Journey.State == PixelMap.Simulation.JourneyState.Ready) FitRoute();
            else UpdateRideCamera();
        };
        Loaded += (_, _) =>
        {
            _active = true;
            ViewChanged();
            _ = LoadOverview();
            if (IsEarthImageryEnabled) EarthChanged();
            if (UsesElevation) TerrainChanged();
            if (_sound is null && _audioDevice is not null) _sound = new PixelMap.Audio.JourneySound(_audioDevice);
            OnJourneyChanged();
            if (Journey.State == PixelMap.Simulation.JourneyState.Ready) FitRoute();
        };
        Unloaded += (_, _) =>
        {
            Journey.Pause();
            _gameClock.Stop();
            _sound?.Dispose();
            _sound = null;
            ReleaseDrivingKeys();
            _worldContacts.Update([], ElevationSnapshot.Empty);
            _active = false;
            StopMotion();
            _tilesTimer.Stop();
            _vectors.Clear();
            _earth.Clear();
            ClearElevation();
            _overviewRequest?.Cancel();
            _touches.Clear();
            _departure = _world = [];
            _earthDisplay = _earthDeparture = _earthOverview = [];
            _surface.Release();
            SetGpuRenderingActive(false);
        };
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));

    public MapCamera Camera { get => (MapCamera)GetValue(CameraProperty); set => SetValue(CameraProperty, Normalize(value).OnTerrain(_terrain, ActualHeight)); }
    public PixelStyle Palette { get => (PixelStyle)GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public bool IsEarthImageryEnabled { get => (bool)GetValue(IsEarthImageryEnabledProperty); set => SetValue(IsEarthImageryEnabledProperty, value); }
    public bool IsGpuRenderingEnabled { get => (bool)GetValue(IsGpuRenderingEnabledProperty); set => SetValue(IsGpuRenderingEnabledProperty, value); }
    public bool IsGpuRenderingActive { get; private set; }
    public string StatusText => !IsGpuRenderingEnabled ? _layerStatus
        : $"{_layerStatus} · {(IsGpuRenderingActive ? "GPU active" : "CPU fallback")}";
    public event EventHandler? CameraChanged;
    public event EventHandler? StatusChanged;

    public void SetMarker(double latitude, double longitude, string label)
    {
        _marker = (latitude, longitude);
        _pinLabel.Text = label;
        PositionMarker();
    }

    public void ClearMarker()
    {
        _marker = null;
        _pin.Visibility = Visibility.Collapsed;
    }

    private void PositionMarker()
    {
        if (_marker is not { } pin) return;
        var view = _surface.GetInteractionView(Camera, _terrain, ActualWidth, ActualHeight);
        double surface = view.Terrain?.Elevation.SampleSurfaceMeters(pin.Latitude, pin.Longitude) ?? 0;
        double altitude = view.Terrain?.RenderAltitude(view.Camera, pin.Latitude, pin.Longitude, surface) ?? 0;
        (double X, double Y) projected = default;
        bool meshPosition = view.Terrain is not null && _surface.TryProjectTerrain(pin.Latitude, pin.Longitude,
            (int)Math.Ceiling(view.Width), (int)Math.Ceiling(view.Height), view.Camera, out projected);
        if (!meshPosition && !view.Camera.TryProject(pin.Latitude, pin.Longitude, altitude, view.Width, view.Height, out projected))
        {
            _pin.Visibility = Visibility.Collapsed;
            return;
        }
        var (x, y) = projected;
        _pin.Visibility = x < 0 || y < 0 || x > ActualWidth || y > ActualHeight ? Visibility.Collapsed : Visibility.Visible;
        _pinPosition.X = Math.Clamp(x - _pin.ActualWidth * .5, 0, Math.Max(0, ActualWidth - _pin.ActualWidth));
        _pinPosition.Y = Math.Max(0, y - _pin.ActualHeight - 4);
    }

    private static MapCamera Normalize(MapCamera value)
    {
        if (!double.IsFinite(value.Latitude) || !double.IsFinite(value.Longitude) || !double.IsFinite(value.Zoom)
            || !double.IsFinite(value.Bearing) || !double.IsFinite(value.Pitch) || !double.IsFinite(value.TargetAltitudeMeters) || !double.IsFinite(value.GroundAltitudeMeters))
            throw new ArgumentOutOfRangeException(nameof(value), "Camera coordinates must be finite.");
        return value with
        {
            Latitude = Math.Clamp(value.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude),
            Longitude = Wrap(value.Longitude),
            Bearing = Wrap(value.Bearing),
            Zoom = Math.Clamp(value.Zoom, MapCamera.MinZoom, MapCamera.MaxZoom),
            Pitch = Math.Clamp(value.Pitch, 0, 72),
            TargetAltitudeMeters = Math.Clamp(value.TargetAltitudeMeters, 0, 15000),
            GroundAltitudeMeters = Math.Clamp(value.GroundAltitudeMeters, -32768, 32768)
        };
    }

    private static double Wrap(double angle) => angle - Math.Floor((angle + 180) / 360) * 360;

    private void ViewChanged()
    {
        if (!_active) return;
        InvalidateMap();
        PositionMarker();
        CameraChanged?.Invoke(this, EventArgs.Empty);
        if (!_tilesTimer.IsEnabled && !_longFlight) _tilesTimer.Start();
    }

    private async Task LoadOverview()
    {
        try
        {
            var world = await _source.GetTileAsync(new TileId(0, 0, 0));
            if (!_active) return;
            _world = [world];
            LayerChanged();
        }
        catch (HttpRequestException) { /* Detailed tile requests have their own retry state. */ }
        catch (TaskCanceledException) { }
    }

    private void RefreshTiles(MapCamera view)
    {
        if (!_active || ActualWidth <= 0 || ActualHeight <= 0) return;
        _vectors.Update(RequestTiles(view, VectorTileSource.MaxZoom));
        if (IsEarthImageryEnabled)
            _earth.Update(RequestTiles(view, EarthImagerySource.MaxZoom));
        if (UsesElevation) RefreshElevationTiles(view);
    }

    private void EarthChanged()
    {
        if (IsEarthImageryEnabled && IsElevationLayerEnabled) IsElevationLayerEnabled = false;
        _overviewRequest?.Cancel();
        _earth.Clear();
        _earthDeparture = _earthDisplay = [];
        if (!_active) return;
        if (IsEarthImageryEnabled)
        {
            if (_earthOverview.Count == 0)
            {
                var request = _overviewRequest = new CancellationTokenSource();
                _ = LoadEarthOverview(request);
            }
            RefreshTiles(_longFlight ? _to : Camera);
        }
        LayerChanged();
    }

    private async Task LoadEarthOverview(CancellationTokenSource request)
    {
        try
        {
            var overview = await _earthSource.GetTileAsync(new TileId(0, 0, 0), request.Token);
            if (_active && !request.IsCancellationRequested)
            {
                _earthOverview = [overview];
                LayerChanged();
            }
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_overviewRequest, request)) _overviewRequest = null;
            request.Dispose();
        }
    }

    private void LayerChanged()
    {
        if (!_active) return;
        bool elevationChanged = RefreshElevationSnapshot();
        _worldContacts.Update(_vectors.Display, _elevationSnapshot);
        _earthDisplay = _earth.Display.Count == 0 ? _earthOverview : _earth.Display;
        InvalidateMap();
        string status = _vectors.HasFailures ? "Map service unavailable · retrying"
            : _vectors.Ready.Count < _vectors.Count ? $"Loading map · {_vectors.Ready.Count}/{_vectors.Count}"
            : IsEarthImageryEnabled && _earth.HasFailures ? "Imagery unavailable · retrying"
            : IsEarthImageryEnabled && _earth.Ready.Count < _earth.Count ? $"Loading Earth · {_earth.Ready.Count}/{_earth.Count}"
            : IsEarthImageryEnabled ? "Earth ready" : "Map ready";
        if (UsesElevation)
        {
            status = _elevations.HasFailures ? status + " · Terrain unavailable · retrying"
                : _elevations.Ready.Count < _elevations.Count ? status + $" · Loading terrain · {_elevations.Ready.Count}/{_elevations.Count}"
                : IsElevationLayerEnabled ? "Elevation ready" : status + " · Terrain ready";
        }
        if (_layerStatus == status && !elevationChanged) return;
        _layerStatus = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void GpuRenderingChanged()
    {
        InvalidateMap();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetGpuRenderingActive(bool isActive)
    {
        isActive &= _active;
        if (IsGpuRenderingActive == isActive) return;
        IsGpuRenderingActive = isActive;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<MapTile> DisplayTiles()
    {
        if (_longFlight && _flightPhase < .72)
            return _flightPhase < .22 && _departure.Count != 0 ? _departure : _world;
        return _vectors.Display.Count == 0 ? _world : _vectors.Display;
    }

    private void InvalidateMap()
    {
        if (!_active) return;
        var imagery = !IsEarthImageryEnabled ? null : _longFlight && _flightPhase < .72
            ? _flightPhase < .22 && _earthDeparture.Count > 0 ? _earthDeparture : _earthOverview
            : _earthDisplay;
        _surface.Present(Camera, DisplayTiles(), imagery, Palette,
            _motion.IsEnabled || _touches.Count != 0 || Journey.Active && Journey.Follow,
            Journey.Route, CurrentVehicle(), Journey.NextCheckpoint, IsGpuRenderingEnabled, _terrain);
    }

    private sealed partial class MapSurface(Grid layers, Action<bool> backendChanged, Action framePresented) : SKCanvasElement
    {
        private sealed record Scene(MapCamera Camera, IReadOnlyList<MapTile> Tiles, IReadOnlyList<ImageryTile>? Imagery,
            PixelStyle Palette, bool Moving, RoutePlan? Route, VehiclePose? Vehicle, RoutePosition? Gate, bool UseGpuRendering, TerrainOptions? Terrain);
        private readonly object _drawing = new();
        private PixelMapRenderer? _renderer;
        private volatile Scene? _scene;
        private Scene? _presentedScene;
        private MapCamera? _timedCamera;
        private double _movingCost = 16;
        private int _interactivePixelSize = 2;
        private bool _gpuRenderingActive;


        internal void Present(MapCamera camera, IReadOnlyList<MapTile> tiles, IReadOnlyList<ImageryTile>? imagery,
            PixelStyle palette, bool moving, RoutePlan? route, VehiclePose? vehicle, RoutePosition? gate, bool useGpuRendering, TerrainOptions? terrain)
        {
            lock (_drawing)
            {
                _scene = new Scene(camera, tiles, imagery, palette, moving, route, vehicle, gate, useGpuRendering, terrain);
                PresentGpuTerrain(_scene);
            }
        }

        internal void Release()
        {
            lock (_drawing)
            {
                _scene = _presentedScene = null;
                ReleaseGpuTerrain();
                _renderer?.Dispose();
                _renderer = null;
                ReportBackend(false);
            }
        }

        internal MapFeature? Pick(Point point, int width, int height)
        {
            lock (_drawing)
            {
                if (_presentedScene is not { } scene || PresentedRenderer is not { } renderer) return null;
                if (_terrainGpuPresented && _gpuDisplayed is { } displayed)
                {
                    width = displayed.Frame.Width;
                    height = displayed.Frame.Height;
                }
                return renderer.Pick(point.X, point.Y, width, height, scene.Camera, scene.Tiles);
            }
        }

        internal InteractionView GetInteractionView(MapCamera camera, TerrainOptions? terrain, double width, double height)
        {
            lock (_drawing)
            {
                if (_terrainGpuPresented && _gpuDisplayed is { } displayed)
                    return new(displayed.Frame.Camera, displayed.Request.Scene.Terrain,
                        displayed.Frame.Width, displayed.Frame.Height);
                return new(camera, terrain, width, height);
            }
        }

        internal bool TryProjectTerrain(double latitude, double longitude, int width, int height, MapCamera camera,
            out (double X, double Y) screen)
        {
            lock (_drawing)
            {
                screen = default;
                return PresentedRenderer is { } renderer && renderer.TryProjectTerrain(latitude, longitude, width, height, camera, out screen);
            }
        }

        internal bool TryPickTerrain(double x, double y, int width, int height, MapCamera camera, out TerrainHit hit)
        {
            lock (_drawing)
            {
                hit = default;
                return PresentedRenderer is { } renderer && renderer.TryPickTerrain(x, y, width, height, camera, out hit);
            }
        }

        protected override void RenderOverride(SKCanvas canvas, Size area)
        {
            lock (_drawing)
            {
                if (DrawGpuPresentation(canvas, area)) return;
                if (_scene is { } scene)
                {
                    _terrainGpuPresented = false;
                    DrawScene(canvas, area, scene);
                    if (!ReferenceEquals(_presentedScene, scene))
                    {
                        _presentedScene = scene;
                        framePresented();
                    }
                }
            }
        }

        private void DrawScene(SKCanvas canvas, Size area, Scene scene)
        {
            _renderer ??= new();
            var camera = scene.Camera;
            var moving = scene.Moving;
            int size = moving ? _interactivePixelSize : 2;
            long start = Stopwatch.GetTimestamp();
            bool useSkiaGpu = scene.UseGpuRendering && !UsesGpuTerrain(scene);
            _renderer.Draw(canvas, (int)Math.Ceiling(area.Width), (int)Math.Ceiling(area.Height),
                camera, scene.Tiles, scene.Palette, size, scene.Imagery, useGpuRendering: useSkiaGpu,
                allowDeferredGpuRendering: useSkiaGpu && CompositionCapabilities.GetForCurrentView().AreEffectsFast(),
                terrainOptions: scene.Terrain);
            ReportBackend(_renderer.IsGpuRenderingActive);
            if (scene.Gate is { } gate && scene.Route is { } trip && scene.Vehicle is { } driver)
                _renderer.DrawGate(canvas, camera, area.Width, area.Height, gate, trip.Mode, trip.Mode == TravelMode.Flight ? driver.AltitudeMeters : 0, driver.AnimationSeconds);
            if (scene.Route is { } route)
                _renderer.DrawNavigation(canvas, (int)Math.Ceiling(area.Width), (int)Math.Ceiling(area.Height), camera, route, scene.Vehicle, size);
            TrackFrameCost(scene, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        private void TrackFrameCost(Scene scene, double milliseconds)
        {
            // Adjust only after an actual camera change. Resolution hysteresis avoids thrashing the terrain cache.
            if (scene.Moving && _timedCamera != scene.Camera)
            {
                double ms = Math.Min(100, milliseconds);
                _movingCost = .75 * _movingCost + .25 * ms;
                if (_movingCost > 29 && _interactivePixelSize < 4) { _interactivePixelSize++; _movingCost = 20; }
                else if (_movingCost < 10 && _interactivePixelSize > 2) { _interactivePixelSize--; _movingCost = 16; }
            }
            _timedCamera = scene.Camera;
        }

        private void ReportBackend(bool isActive)
        {
            if (_gpuRenderingActive == isActive) return;
            _gpuRenderingActive = isActive;
            backendChanged(isActive);
        }
    }
}
