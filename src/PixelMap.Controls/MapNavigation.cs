using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using PixelMap.Rendering;
using Windows.Devices.Input;
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
    private MapCamera _gestureCamera;
    private Point _firstFinger, _secondFinger;
    private uint _gestureFirst, _gestureSecond;
    private bool _tilting, _planarGesture;
    private double _gestureScale = 1, _gestureTwist;
    private Point _gestureCenter;
    private bool _looking;
    private BrowserMapInput? _browserInput;


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
        Loaded += (_, _) => _browserInput ??= BrowserMapInput.TryCreate(SuspendBrowserInput, MoveCapturedMouse);
        Unloaded += (_, _) =>
        {
            ResetMapPointers();
            _browserInput?.Dispose();
            _browserInput = null;
        };
        Journey.Changed += (_, _) =>
        {
            if (!Journey.IsFreeRoaming || !Journey.Active) _browserInput?.ReleaseMouse();
        };
    }

    /// <summary>Capture relative mouse motion for an active first-person or free camera.
    /// Browsers require this call to originate from a user gesture.</summary>
    public bool CaptureMouseForFreeLook() => Journey.IsFreeRoaming && Journey.Active &&
        _browserInput?.CaptureMouse() == true;

    public bool IsMouseCaptured => _browserInput?.IsMouseCaptured == true;
    public void ReleaseMouseCapture() => _browserInput?.ReleaseMouse();

    private void MoveCapturedMouse(double x, double y)
    {
        if (_active && Journey.IsFreeRoaming && Journey.Active) TryLookFreeRoam(x * .18, -y * .18);
    }

    private void SuspendBrowserInput()
    {
        ResetMapPointers();
        StopMotion();
        ReleaseDrivingKeys();
        if (Journey.Active) Journey.Pause();
    }

    private void ResetMapPointers()
    {
        _touches.Clear();
        _dragged = true;
        _vx = _vy = 0;
        _browserInput?.ReleasePointers();
        ReleasePointerCaptures();
    }

    public void PanBy(double dx, double dy) { StopTrackingForGesture(); StopMotion(); Camera = PanCamera(Camera, ActualWidth * .5, ActualHeight * .5, ActualWidth * .5 + dx, ActualHeight * .5 + dy); }
    public void ZoomBy(double delta) { if (!ZoomRide(delta)) FlyTo(TransformCamera(_flight ? _to : Camera, delta, 0, 0, ActualWidth / 2, ActualHeight / 2)); }
    public void RotateBy(double degrees)
    {
        var target = _flight ? _to : Camera;
        FlyTo(target with { Bearing = target.Bearing + degrees });
    }
    public void ToggleProjection() => FlyTo(Camera with { Pitch = (_flight ? _to : Camera).Pitch > 10 ? 0 : 55 });

    public void FlyTo(double latitude, double longitude, double zoom)
    {
        var view = Camera;
        if (view.IsFreeLook || view.Perspective && view.Pitch >= 90)
            view = view with { IsFreeLook = false, ViewDistanceMeters = 0, ViewportHeight = 0, Pitch = Math.Min(view.Pitch, 55) };
        FlyTo(view with { Latitude = latitude, Longitude = longitude, Zoom = zoom, TargetAltitudeMeters = 0 });
    }

    public void FlyTo(MapCamera destination)
    {
        StopTrackingForGesture();
        StopMotion();
        _to = Normalize(destination).ForViewport(ActualWidth, ActualHeight).OnTerrain(_terrain, ActualHeight);
        _from = Camera;
        if (!_active || _from == _to) { Camera = _to; return; }
        var mapPlane = _from with { Perspective = false, TargetAltitudeMeters = 0 };
        var end = mapPlane.Project(_to.Latitude, _to.Longitude, 0, 0);
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
            Camera = t == 1 ? _to : _from.Interpolate(_to, u) with { Zoom = zoom };
            if (t == 1) { StopMotion(); ViewChanged(); }
            return;
        }

        var dt = Math.Clamp(Stopwatch.GetElapsedTime(_lastMove).TotalMilliseconds, 1, 80);
        _lastMove = Stopwatch.GetTimestamp();
        double decay = Math.Exp(-dt / 160);
        var centerX = ActualWidth * .5;
        var centerY = ActualHeight * .5;
        Camera = PanCamera(Camera, centerX, centerY,
            centerX + _vx * 160 * (1 - decay), centerY + _vy * 160 * (1 - decay));
        _vx *= decay; _vy *= decay;
        if (Math.Abs(_vx) + Math.Abs(_vy) < .018) StopMotion();
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private void StopMotion(bool invalidate = true)
    {
        _motion.Stop();
        _flight = _longFlight = false;
        _vx = _vy = 0;
        _departure = [];
        _earthDeparture = [];
        if (_active && invalidate) InvalidateMap();
    }

    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        StopMotion(invalidate: false);
        Focus(FocusState.Pointer);
        var point = e.GetCurrentPoint(this);
        if (_touches.Count == 0)
        {
            _down = point.Position;
            _rotating = point.Properties.IsRightButtonPressed ||
                (e.KeyModifiers & (VirtualKeyModifiers.Shift | VirtualKeyModifiers.Menu)) != 0;
            _looking = Journey.IsFreeRoaming;
            _dragged = _looking;
        }
        else _dragged = true;
        _touches[e.Pointer.PointerId] = point.Position;
        if (_touches.Count == 2) StartTwoFingerGesture();
        _lastMove = Stopwatch.GetTimestamp();
        CapturePointer(e.Pointer);
        _browserInput?.CapturePointer(e.Pointer.PointerId);
        if (_looking && Journey.Active && point.PointerDevice.PointerDeviceType == PointerDeviceType.Mouse &&
            point.Properties.IsLeftButtonPressed && (Journey.IsFreeFlying ||
                CameraRig is JourneyCameraRig.FirstPerson or JourneyCameraRig.Cockpit))
            CaptureMouseForFreeLook();
        e.Handled = true;
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_browserInput?.IsMouseCaptured == true)
        {
            e.Handled = true;
            return;
        }
        var id = e.Pointer.PointerId;
        if (!_touches.TryGetValue(id, out var old)) return;
        var next = e.GetCurrentPoint(this).Position;
        _touches[id] = next;
        double dx = next.X - old.X, dy = next.Y - old.Y;
        if (dx == 0 && dy == 0) return;
        if (_touches.Count == 2)
        {
            StopTrackingForGesture();
            UpdateTwoFingerGesture();
        }
        else if (_touches.Count == 1)
        {
            if (!_dragged && Distance(next, _down) < 3) return;
            if (!_dragged) { dx = next.X - _down.X; dy = next.Y - _down.Y; }
            _dragged = true;
            if (TryLookFreeRoam(dx * .22, -dy * .22))
            {
                _looking = true;
                _vx = _vy = 0;
                _lastMove = Stopwatch.GetTimestamp();
                e.Handled = true;
                return;
            }
            StopTrackingForGesture();
            if (_rotating) Camera = TransformCamera(Camera, 0, dx * .35, -dy * .22, _down.X, _down.Y);
            else Camera = PanCamera(Camera, next.X - dx, next.Y - dy, next.X, next.Y);
            var dt = Math.Clamp(Stopwatch.GetElapsedTime(_lastMove).TotalMilliseconds, 4, 100);
            _vx = Math.Clamp(.5 * _vx + .5 * dx / dt, -3, 3);
            _vy = Math.Clamp(.5 * _vy + .5 * dy / dt, -3, 3);
        }
        _lastMove = Stopwatch.GetTimestamp();
        e.Handled = true;
    }

    private void StopTrackingForGesture()
    {
        if (Journey.Route is not null && Journey.Follow) Journey.Follow = false;
    }

    private void StartTwoFingerGesture()
    {
        var iter = _touches.GetEnumerator();
        iter.MoveNext(); var first = iter.Current;
        iter.MoveNext(); var second = iter.Current;
        _gestureFirst = first.Key; _gestureSecond = second.Key;
        _firstFinger = first.Value; _secondFinger = second.Value; _gestureCamera = Camera;
        _tilting = _planarGesture = false;
        _gestureScale = 1;
        _gestureTwist = 0;
        _gestureCenter = new((_firstFinger.X + _secondFinger.X) * .5, (_firstFinger.Y + _secondFinger.Y) * .5);
        _vx = _vy = 0;
    }

    private void UpdateTwoFingerGesture()
    {
        var a = _touches[_gestureFirst]; var b = _touches[_gestureSecond];
        double oldX = _secondFinger.X - _firstFinger.X, oldY = _secondFinger.Y - _firstFinger.Y;
        double x = b.X - a.X, y = b.Y - a.Y;
        var before = new Point((_firstFinger.X + _secondFinger.X) * .5, (_firstFinger.Y + _secondFinger.Y) * .5);
        var after = new Point((a.X + b.X) * .5, (a.Y + b.Y) * .5);
        double dx = after.X - before.X, dy = after.Y - before.Y;
        double scale = Distance(a, b) / Math.Max(1, Distance(_firstFinger, _secondFinger));
        double twist = Math.Atan2(oldX * y - oldY * x, oldX * x + oldY * y) * 180 / Math.PI;
        if (Journey.IsFreeRoaming)
        {
            ZoomRide(Math.Log2(Math.Max(.001, scale) / _gestureScale));
            TryLookFreeRoam((after.X - _gestureCenter.X) * .22 - Wrap(twist - _gestureTwist),
                -(after.Y - _gestureCenter.Y) * .22);
            _gestureScale = Math.Max(.001, scale);
            _gestureTwist = twist;
            _gestureCenter = after;
            return;
        }
        if (!_tilting && !_planarGesture)
        {
            double ay = a.Y - _firstFinger.Y, by = b.Y - _secondFinger.Y;
            _tilting = Math.Abs(dy) > 10 && ay * by > 0 && Math.Abs(dx) < Math.Abs(dy) * .45 &&
                Math.Abs(Math.Log2(Math.Max(.001, scale))) < .11 && Math.Abs(twist) < 6;
            _planarGesture = !_tilting && (Math.Abs(scale - 1) > .1 || Math.Abs(twist) > 6 || Math.Abs(dx) > 10);
            if (_tilting)
            {
                // Preserve the view already reached while recognizing the gesture.
                // Subsequent tilt, pinch and twist start from this same camera pose.
                _gestureCamera = Camera;
                _firstFinger = a;
                _secondFinger = b;
                return;
            }
        }
        if (_tilting)
        {
            Camera = TransformCamera(_gestureCamera, Math.Log2(Math.Max(.02, scale)), -twist,
                -dy * .28, before.X, before.Y);
            return;
        }
        var view = TransformCamera(_gestureCamera, Math.Log2(Math.Max(.02, scale)), -twist, 0, before.X, before.Y);
        Camera = PanCamera(view, before.X, before.Y, after.X, after.Y);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_touches.Remove(e.Pointer.PointerId)) return;
        var pos = e.GetCurrentPoint(this).Position;
        ReleasePointerCapture(e.Pointer);
        _browserInput?.ReleasePointer(e.Pointer.PointerId);
        if (_touches.Count == 0)
        {
            if (!_dragged && !_rotating && IsMapPointSelectionEnabled)
            {
                _lastTapTime = 0;
                if (TryGetLocation(pos.X, pos.Y, out var geo))
                    MapPointTapped?.Invoke(this, new MapPointEventArgs(geo.Latitude, geo.Longitude));
            }
            else if (!_dragged && !_rotating)
            {
                if (_lastTapTime != 0 && Stopwatch.GetElapsedTime(_lastTapTime).TotalMilliseconds < 360 && Distance(pos, _lastTap) < 18)
                {
                    ClearMarker();
                    FlyTo(TransformCamera(Camera, 1, 0, 0, pos.X, pos.Y));
                    _lastTapTime = 0;
                }
                else
                {
                    _lastTapTime = Stopwatch.GetTimestamp();
                    _lastTap = pos;
                    Inspect(pos);
                }
            }
            else if (!_rotating && !_looking && Stopwatch.GetElapsedTime(_lastMove).TotalMilliseconds < 85 && Math.Abs(_vx) + Math.Abs(_vy) > .1)
            {
                _lastMove = Stopwatch.GetTimestamp();
                _motion.Start();
            }
        }
        else { if (_touches.Count == 2) StartTwoFingerGesture(); _vx = _vy = 0; _lastMove = Stopwatch.GetTimestamp(); }
        InvalidateMap();
        e.Handled = true;
    }

    private void Inspect(Point pos)
    {
        if (!TryGetLocation(pos.X, pos.Y, out var geo)) return;
        var feature = _surface.Pick(pos, (int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight));
        var label = feature is null ? FormattableString.Invariant($"{geo.Latitude:0.0000}°, {geo.Longitude:0.0000}°")
            : !string.IsNullOrWhiteSpace(feature.Name) ? feature.Name
            : !string.IsNullOrWhiteSpace(feature.Class) ? feature.Class.Replace('_', ' ') : feature.Layer;
        if (feature?.Height is double height) label += $" · {height:0} m building";
        if (UsesElevation && HasElevation) label += $" · {geo.ElevationMeters:0} m elevation";
        SetMarker(geo.Latitude, geo.Longitude, label);
    }

    private void Interrupted(object sender, PointerRoutedEventArgs e)
    {
        if (!_touches.Remove(e.Pointer.PointerId)) return;
        _browserInput?.ReleasePointer(e.Pointer.PointerId);
        _dragged = true;
        _vx = _vy = 0;
        if (_touches.Count == 2) StartTwoFingerGesture();
        if (_touches.Count == 0) InvalidateMap();
    }

    private void Wheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (_browserInput?.TryTakeWheel(out var wheel) == true)
        {
            ApplyBrowserWheel(wheel, point.Position);
            e.Handled = true;
            return;
        }
        var delta = point.Properties.MouseWheelDelta;
        bool control = (e.KeyModifiers & VirtualKeyModifiers.Control) != 0;
        if (ZoomRide(control ? delta / 100d : delta / 300d)) { e.Handled = true; return; }
        StopTrackingForGesture();
        if ((e.KeyModifiers & VirtualKeyModifiers.Shift) != 0)
        {
            FlyTo(TransformCamera(_flight ? _to : Camera, 0, delta / 10d, 0, point.Position.X, point.Position.Y));
            e.Handled = true; return;
        }
        if (point.Properties.IsHorizontalMouseWheel) PanBy(-delta, 0);
        else if (!control && Math.Abs(delta) < 120) PanBy(0, delta);
        else if (control)
        {
            StopMotion(invalidate: false);
            Camera = TransformCamera(Camera, Math.Clamp(delta / 100d, -2, 2), 0, 0, point.Position.X, point.Position.Y);
        }
        else FlyTo(TransformCamera(_flight ? _to : Camera, Math.Clamp(delta / 300d, -2, 2), 0, 0, point.Position.X, point.Position.Y));
        e.Handled = true;
    }

    private void ApplyBrowserWheel(BrowserWheelSample wheel, Point point)
    {
        if (wheel.Kind == BrowserWheelKind.Consumed) return;
        if (wheel.Kind == BrowserWheelKind.Gesture)
        {
            if (ZoomRide(wheel.X)) { TryLookFreeRoam(wheel.Y, 0); return; }
            StopTrackingForGesture();
            StopMotion(invalidate: false);
            Camera = TransformCamera(Camera, wheel.X, wheel.Y, 0, point.X, point.Y);
            return;
        }
        bool pinch = wheel.Kind == BrowserWheelKind.PinchZoom;
        if (!pinch && (wheel.Shift || wheel.Alt))
        {
            double bearing = wheel.Shift ? -(wheel.X + wheel.Y) * .16 : 0;
            double pitch = wheel.Alt ? -wheel.Y * .12 : 0;
            if (TryLookFreeRoam(bearing, pitch)) return;
            StopTrackingForGesture();
            StopMotion(invalidate: false);
            Camera = TransformCamera(Camera, 0, bearing, pitch, point.X, point.Y);
            return;
        }
        if (wheel.Kind == BrowserWheelKind.Pan)
        {
            if (TryLookFreeRoam(-wheel.X * .18, wheel.Y * .18)) return;
            StopTrackingForGesture();
            StopMotion(invalidate: false);
            Camera = PanCamera(Camera, point.X, point.Y, point.X - wheel.X, point.Y - wheel.Y);
            return;
        }
        double zoom = Math.Clamp(-wheel.Y / (pinch ? 100 : 300), -2, 2);
        if (ZoomRide(zoom)) return;
        StopTrackingForGesture();
        if (pinch)
        {
            StopMotion(invalidate: false);
            Camera = TransformCamera(Camera, zoom, 0, 0, point.X, point.Y);
        }
        else FlyTo(TransformCamera(_flight ? _to : Camera, zoom, 0, 0, point.X, point.Y));
    }

    private void OnMapKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) _browserInput?.ReleaseMouse();
        bool repeated = e.KeyStatus.WasKeyDown;
        repeated = _browserInput?.IsRepeatedKey(repeated) ?? repeated;
        if (HandleDrivingKey(e.Key, repeated)) { e.Handled = true; return; }
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
            case VirtualKey.PageUp: OrbitBy(0, 8); break;
            case VirtualKey.PageDown: OrbitBy(0, -8); break;
            case VirtualKey.P: ToggleProjection(); break;
            case VirtualKey.Home: ClearMarker(); FlyTo(OpeningView); break;
            case VirtualKey.Escape: ClearMarker(); break;
            default: return;
        }
        e.Handled = true;
    }
}
