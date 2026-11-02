using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace PixelMap.Controls;

/// <summary>A reusable, navigable vector map with an orthographic pixel-art building renderer.</summary>
public sealed partial class PixelMapControl : UserControl
{
    public static readonly MapCamera OpeningView = new(40.7087, -74.0104, 16.5, -15, 55);
    public static readonly DependencyProperty CameraProperty = DependencyProperty.Register(nameof(Camera),
        typeof(MapCamera), typeof(PixelMapControl), new PropertyMetadata(OpeningView, (o, _) => ((PixelMapControl)o).ViewChanged()));
    public static readonly DependencyProperty PaletteProperty = DependencyProperty.Register(nameof(Palette),
        typeof(PixelStyle), typeof(PixelMapControl), new PropertyMetadata(PixelStyle.Day, (o, _) => ((PixelMapControl)o)._surface.Invalidate()));

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(18) };
    private readonly MapSurface _surface;
    private readonly Border _pin;
    private readonly TextBlock _pinLabel;
    private readonly TranslateTransform _pinPosition = new();
    private readonly VectorTileSource _source;
    private readonly DispatcherTimer _tilesTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly Dictionary<TileId, CancellationTokenSource> _requests = [];
    private readonly Dictionary<TileId, MapTile> _loaded = [];
    private readonly Dictionary<TileId, int> _failures = [];
    private TileId[] _wanted = [];
    private IReadOnlyList<MapTile> _display = [];
    private PixelMapRenderer? _renderer;
    private (double Latitude, double Longitude)? _marker;
    private bool _active;

    public PixelMapControl() : this(new VectorTileSource(Http)) { }

    public PixelMapControl(VectorTileSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _surface = new(this);
        _pinLabel = new TextBlock { FontSize = 12, Foreground = Brush(255, 247, 220), TextTrimming = TextTrimming.CharacterEllipsis };
        _pin = new Border
        {
            Child = _pinLabel, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = Brush(27, 50, 52), BorderBrush = Brush(246, 192, 105), BorderThickness = new(2),
            CornerRadius = new(4), Padding = new(8, 3, 8, 3), MaxWidth = 230, IsHitTestVisible = false,
            RenderTransform = _pinPosition, Visibility = Visibility.Collapsed
        };
        _pin.SizeChanged += (_, _) => PositionMarker();
        Content = new Grid { Background = Brush(170, 187, 161), Children = { _surface, _pin } };
        IsTabStop = true;
        AutomationProperties.SetName(this, "Interactive pixel map");
        HookInput();
        _tilesTimer.Tick += (_, _) => { _tilesTimer.Stop(); RefreshTiles(); };
        SizeChanged += (_, _) => ViewChanged();
        Loaded += (_, _) => { _active = true; ViewChanged(); };
        Unloaded += (_, _) =>
        {
            _active = false;
            StopMotion();
            _tilesTimer.Stop();
            foreach (var request in _requests.Values.ToArray()) request.Cancel();
            _requests.Clear(); _touches.Clear(); _wanted = []; _display = [];
            _loaded.Clear(); _failures.Clear();
            _renderer?.Dispose(); _renderer = null;
        };
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));

    public MapCamera Camera { get => (MapCamera)GetValue(CameraProperty); set => SetValue(CameraProperty, Normalize(value)); }
    public PixelStyle Palette { get => (PixelStyle)GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public string StatusText { get; private set; } = "Opening atlas";
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
        var (x, y) = Camera.Project(pin.Latitude, pin.Longitude, ActualWidth, ActualHeight);
        _pin.Visibility = x < 0 || y < 0 || x > ActualWidth || y > ActualHeight ? Visibility.Collapsed : Visibility.Visible;
        _pinPosition.X = Math.Clamp(x - _pin.ActualWidth * .5, 0, Math.Max(0, ActualWidth - _pin.ActualWidth));
        _pinPosition.Y = Math.Max(0, y - _pin.ActualHeight - 4);
    }

    private static MapCamera Normalize(MapCamera value)
    {
        if (!double.IsFinite(value.Latitude) || !double.IsFinite(value.Longitude) || !double.IsFinite(value.Zoom)
            || !double.IsFinite(value.Bearing) || !double.IsFinite(value.Pitch))
            throw new ArgumentOutOfRangeException(nameof(value), "Camera coordinates must be finite.");
        return value with
        {
            Latitude = Math.Clamp(value.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude),
            Longitude = Wrap(value.Longitude), Bearing = Wrap(value.Bearing),
            Zoom = Math.Clamp(value.Zoom, MapCamera.MinZoom, MapCamera.MaxZoom), Pitch = Math.Clamp(value.Pitch, 0, 72)
        };
    }

    private static double Wrap(double angle) => angle - Math.Floor((angle + 180) / 360) * 360;

    private void ViewChanged()
    {
        if (!_active) return;
        _surface.Invalidate();
        PositionMarker();
        CameraChanged?.Invoke(this, EventArgs.Empty);
        if (!_tilesTimer.IsEnabled && !_longFlight) _tilesTimer.Start();
    }

    private void RefreshTiles()
    {
        if (!_active || ActualWidth <= 0 || ActualHeight <= 0) return;
        var ids = Camera.VisibleTiles(ActualWidth, ActualHeight);
        if (_wanted.SequenceEqual(ids) && _failures.Count == 0) return;
        _wanted = ids;
        foreach (var id in _requests.Keys.Except(ids).ToArray())
        {
            var obsolete = _requests[id];
            _requests.Remove(id);
            obsolete.Cancel();
        }
        foreach (var id in _failures.Keys.Except(ids).ToArray()) _failures.Remove(id);
        foreach (var id in _loaded.Keys.Except(ids).ToArray()) _loaded.Remove(id);
        UpdateDisplay();
        foreach (var id in ids)
        {
            if (_loaded.ContainsKey(id) || _requests.ContainsKey(id) || _failures.ContainsKey(id)) continue;
            var request = new CancellationTokenSource();
            _requests.Add(id, request);
            _ = LoadTile(id, request);
        }
    }

    private async Task LoadTile(TileId id, CancellationTokenSource request)
    {
        try
        {
            var tile = await _source.GetTileAsync(id, request.Token);
            if (_active && !request.IsCancellationRequested) _loaded[id] = tile;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            if (_active && !request.IsCancellationRequested)
            {
                int generation = ++_retryGeneration;
                _failures[id] = generation;
                _ = RetryLater(id, generation);
            }
        }
        finally
        {
            if (_requests.TryGetValue(id, out var current) && current == request)
            {
                _requests.Remove(id);
                UpdateDisplay();
            }
            request.Dispose();
        }
    }

    private int _retryGeneration;
    private async Task RetryLater(TileId id, int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (!_active || !_failures.TryGetValue(id, out int current) || current != generation) return;
        _failures.Remove(id);
        _wanted = []; // Re-evaluate the current viewport; it may have moved since the error.
        RefreshTiles();
    }

    private void UpdateDisplay()
    {
        if (!_active) return;
        _display = _wanted.Where(_loaded.ContainsKey).Select(id => _loaded[id]).ToArray();
        _surface.Invalidate();
        string status = _failures.Count > 0 ? "Map service unavailable · retrying"
            : _display.Count < _wanted.Length ? $"Loading map · {_display.Count}/{_wanted.Length}"
            : "Map ready";
        if (StatusText == status) return;
        StatusText = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class MapSurface(PixelMapControl owner) : SKCanvasElement
    {
        protected override void RenderOverride(SKCanvas canvas, Size area)
        {
            if (!owner._active) return;
            owner._renderer ??= new();
            owner._renderer.Draw(canvas, (int)Math.Ceiling(area.Width), (int)Math.Ceiling(area.Height),
                owner.Camera, owner._display, owner.Palette, owner._artScale);
        }
    }
}
