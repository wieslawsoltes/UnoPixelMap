using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using PixelMap.Rendering;
using Windows.Foundation;
using Windows.System;

namespace PixelMap.Controls;

public sealed partial class PixelMapControl
{
    private readonly DispatcherTimer _motion = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Dictionary<uint, Point> _touches = [];
    private MapCamera _from, _to;
    private Point _down, _lastTap;
    private long _start, _lastMove, _lastTapTime;
    private double _duration, _arc, _vx, _vy;
    private bool _dragged, _rotating, _flight, _longFlight;
    private double _flightPhase;

    private void HookInput()
    {
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += Released;
        PointerCanceled += Interrupted;
        PointerCaptureLost += Interrupted;
        PointerWheelChanged += Wheel;
        KeyDown += OnMapKey;
        _motion.Tick += (_, _) => Advance();
    }

    public void PanBy(double dx, double dy) { StopMotion(); Camera = Camera.PanBy(dx, dy); }
    public void ZoomBy(double delta) => FlyTo((_flight ? _to : Camera).ZoomAt(delta, ActualWidth / 2, ActualHeight / 2, ActualWidth, ActualHeight));
    public void RotateBy(double degrees)
    {
        var target = _flight ? _to : Camera;
        FlyTo(target with { Bearing = target.Bearing + degrees });
    }
    public void ToggleProjection() => FlyTo(Camera with { Pitch = (_flight ? _to : Camera).Pitch > 10 ? 0 : 55 });

    public void FlyTo(double latitude, double longitude, double zoom) =>
        FlyTo(Camera with { Latitude = latitude, Longitude = longitude, Zoom = zoom });

    public void FlyTo(MapCamera destination)
    {
        StopMotion();
        _to = Normalize(destination);
        _from = Camera;
        if (!_active || _from == _to) { Camera = _to; return; }
        var end = _from.Project(_to.Latitude, _to.Longitude, 0, 0);
        var spans = Math.Sqrt(end.X * end.X + end.Y * end.Y) / Math.Max(256, ActualWidth);
        _longFlight = spans > 4;
        _arc = _longFlight ? Math.Min(Math.Min(_from.Zoom, _to.Zoom) - 1, Math.Log2(spans)) : 0;
        _duration = _longFlight ? 1050 : 240;
        _flight = true;
        _flightPhase = 0;
        if (_longFlight)
        {
            _departure = _vectors.Display;
            _earthDeparture = _earthDisplay;
            _tilesTimer.Stop();
            RefreshTiles(_to);
        }
        _start = Stopwatch.GetTimestamp();
        _motion.Start();
    }

    private void Advance()
    {
        if (_flight)
        {
            double t = Math.Min(1, Stopwatch.GetElapsedTime(_start).TotalMilliseconds / _duration);
            _flightPhase = t;
            double u = Smooth(_longFlight ? Math.Clamp((t - .20) / .60, 0, 1) : t);
            double zoom = _from.Zoom + (_to.Zoom - _from.Zoom) * u;
            if (_longFlight)
            {
                double overview = Math.Max(0, Math.Min(_from.Zoom, _to.Zoom) - Math.Max(0, _arc));
                zoom = t < .5 ? _from.Zoom + (overview - _from.Zoom) * Smooth(Math.Min(1, t / .38))
                    : overview + (_to.Zoom - overview) * Smooth(Math.Clamp((t - .62) / .38, 0, 1));
            }
            // Interpolate on the wrapped Mercator plane so that antimeridian flights take the short path.
            var pos = _from.Project(_to.Latitude, _to.Longitude, 0, 0);
            var geo = _from.Unproject(pos.X * u, pos.Y * u, 0, 0);
            Camera = t == 1 ? _to : new MapCamera(geo.Latitude, geo.Longitude,
                zoom,
                _from.Bearing + Wrap(_to.Bearing - _from.Bearing) * u, _from.Pitch + (_to.Pitch - _from.Pitch) * u);
            if (t == 1) { StopMotion(); ViewChanged(); }
            return;
        }

        var dt = Math.Clamp(Stopwatch.GetElapsedTime(_lastMove).TotalMilliseconds, 1, 80);
        _lastMove = Stopwatch.GetTimestamp();
        double decay = Math.Exp(-dt / 160);
        Camera = Camera.PanBy(_vx * 160 * (1 - decay), _vy * 160 * (1 - decay));
        _vx *= decay; _vy *= decay;
        if (Math.Abs(_vx) + Math.Abs(_vy) < .018) StopMotion();
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private void StopMotion()
    {
        _motion.Stop();
        _flight = _longFlight = false;
        _vx = _vy = 0;
        _departure = [];
        _earthDeparture = [];
        if (_active) InvalidateMap();
    }

    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        StopMotion();
        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(this);
        if (_touches.Count == 0)
        {
            _down = point.Position;
            _rotating = point.Properties.IsRightButtonPressed;
            _dragged = false;
        }
        else _dragged = true;
        _touches[e.Pointer.PointerId] = point.Position;
        _lastMove = Stopwatch.GetTimestamp();
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        var id = e.Pointer.PointerId;
        if (!_touches.TryGetValue(id, out var old)) return;
        var next = e.GetCurrentPoint(this).Position;
        _touches[id] = next;
        double dx = next.X - old.X, dy = next.Y - old.Y;
        if (dx == 0 && dy == 0) return;
        if (_touches.Count == 2)
        {
            var other = _touches.First(p => p.Key != id).Value;
            var previousCenter = new Point((old.X + other.X) * .5, (old.Y + other.Y) * .5);
            var center = new Point((next.X + other.X) * .5, (next.Y + other.Y) * .5);
            var before = Distance(old, other); var after = Distance(next, other);
            var view = Camera.PanBy(center.X - previousCenter.X, center.Y - previousCenter.Y);
            if (before > 4 && after > 4)
                view = view.ZoomAt(Math.Log2(after / before), center.X, center.Y, ActualWidth, ActualHeight);
            Camera = view;
        }
        else if (_touches.Count == 1)
        {
            if (!_dragged && Distance(next, _down) < 3) return;
            if (!_dragged) { dx = next.X - _down.X; dy = next.Y - _down.Y; }
            _dragged = true;
            if (_rotating) Camera = Camera with { Bearing = Camera.Bearing + dx * .35, Pitch = Camera.Pitch - dy * .22 };
            else Camera = Camera.PanBy(dx, dy);
            var dt = Math.Clamp(Stopwatch.GetElapsedTime(_lastMove).TotalMilliseconds, 4, 100);
            _vx = Math.Clamp(.5 * _vx + .5 * dx / dt, -3, 3);
            _vy = Math.Clamp(.5 * _vy + .5 * dy / dt, -3, 3);
        }
        _lastMove = Stopwatch.GetTimestamp();
        e.Handled = true;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_touches.Remove(e.Pointer.PointerId)) return;
        var pos = e.GetCurrentPoint(this).Position;
        ReleasePointerCapture(e.Pointer);
        if (_touches.Count == 0)
        {
            if (!_dragged && !_rotating)
            {
                if (_lastTapTime != 0 && Stopwatch.GetElapsedTime(_lastTapTime).TotalMilliseconds < 360 && Distance(pos, _lastTap) < 18)
                {
                    ClearMarker();
                    FlyTo(Camera.ZoomAt(1, pos.X, pos.Y, ActualWidth, ActualHeight));
                    _lastTapTime = 0;
                }
                else
                {
                    _lastTapTime = Stopwatch.GetTimestamp();
                    _lastTap = pos;
                    Inspect(pos);
                }
            }
            else if (!_rotating && Stopwatch.GetElapsedTime(_lastMove).TotalMilliseconds < 85 && Math.Abs(_vx) + Math.Abs(_vy) > .1)
            {
                _lastMove = Stopwatch.GetTimestamp();
                _motion.Start();
            }
        }
        else { _vx = _vy = 0; _lastMove = Stopwatch.GetTimestamp(); }
        InvalidateMap();
        e.Handled = true;
    }

    private void Inspect(Point pos)
    {
        var geo = Camera.Unproject(pos.X, pos.Y, ActualWidth, ActualHeight);
        var feature = _surface.Pick(pos, (int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight));
        var label = feature is null ? FormattableString.Invariant($"{geo.Latitude:0.0000}°, {geo.Longitude:0.0000}°")
            : !string.IsNullOrWhiteSpace(feature.Name) ? feature.Name
            : !string.IsNullOrWhiteSpace(feature.Class) ? feature.Class.Replace('_', ' ') : feature.Layer;
        if (feature?.Height is double height) label += $" · {height:0} m";
        SetMarker(geo.Latitude, geo.Longitude, label);
    }

    private void Interrupted(object sender, PointerRoutedEventArgs e)
    {
        if (_touches.Remove(e.Pointer.PointerId)) { _dragged = true; _vx = _vy = 0; }
    }

    private void Wheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var delta = point.Properties.MouseWheelDelta;
        if (point.Properties.IsHorizontalMouseWheel) PanBy(-delta * .35, 0);
        else FlyTo((_flight ? _to : Camera).ZoomAt(Math.Clamp(delta / 300d, -2, 2), point.Position.X, point.Position.Y, ActualWidth, ActualHeight));
        e.Handled = true;
    }

    private void OnMapKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left: PanBy(96, 0); break;
            case VirtualKey.Right: PanBy(-96, 0); break;
            case VirtualKey.Up: PanBy(0, 96); break;
            case VirtualKey.Down: PanBy(0, -96); break;
            case VirtualKey.Add: case (VirtualKey)187: ZoomBy(1); break;
            case VirtualKey.Subtract: case (VirtualKey)189: ZoomBy(-1); break;
            case VirtualKey.Q: RotateBy(-45); break;
            case VirtualKey.E: RotateBy(45); break;
            case VirtualKey.P: ToggleProjection(); break;
            case VirtualKey.Home: ClearMarker(); FlyTo(OpeningView); break;
            case VirtualKey.Escape: ClearMarker(); break;
            default: return;
        }
        e.Handled = true;
    }
}
