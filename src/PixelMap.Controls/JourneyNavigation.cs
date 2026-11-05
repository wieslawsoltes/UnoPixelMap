using System.Diagnostics;
using Microsoft.UI.Xaml;
using PixelMap.Audio;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using PixelMap.Simulation;
using Windows.System;

namespace PixelMap.Controls;

public enum JourneyCameraRig { Chase, NorthUp, Cockpit, Orbit, FirstPerson }

public sealed class MapPointEventArgs(double latitude, double longitude) : EventArgs
{
    public double Latitude { get; } = latitude;
    public double Longitude { get; } = longitude;
}

public sealed partial class PixelMapControl
{
    private readonly DispatcherTimer _gameClock = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly HashSet<VirtualKey> _held = [];
    private readonly VectorCollisionWorld _worldContacts = new();
    private long _gameTimestamp, _cameraTimestamp;
    private bool _wasTracking;
    private double _rideZoom;
    private Thickness _navigationInsets;
    public Thickness NavigationInsets
    {
        get => _navigationInsets;
        set
        {
            if (_navigationInsets.Equals(value)) return;
            _navigationInsets = value;
            if (_active && Journey.State == JourneyState.Ready && Journey.Route is not null) FitRoute();
            else UpdateRideCamera();
        }
    }
    private (double Width, double Height, double X, double Y) SafeViewport()
    {
        var left = Math.Clamp(NavigationInsets.Left, 0, ActualWidth * .7);
        var right = Math.Clamp(NavigationInsets.Right, 0, Math.Max(0, ActualWidth - left - 64));
        var top = Math.Clamp(NavigationInsets.Top, 0, ActualHeight * .7);
        var bottom = Math.Clamp(NavigationInsets.Bottom, 0, Math.Max(0, ActualHeight - top - 64));
        return (Math.Max(1, ActualWidth - left - right), Math.Max(1, ActualHeight - top - bottom),
            (ActualWidth + left - right) * .5, (ActualHeight + top - bottom) * .5);
    }

    private JourneySound? _sound;
    private IJourneyAudioDevice? _audioDevice;
    private JourneyCameraRig _rig = JourneyCameraRig.Chase;
    public JourneySession Journey { get; } = new();
    public VectorTileSource VectorTiles => _source;
    public bool IsMapPointSelectionEnabled { get; set; }
    public event EventHandler<MapPointEventArgs>? MapPointTapped;
    public JourneyCameraRig CameraRig
    {
        get => _rig;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _rig = value;
            Journey.IsLookRelativeToVehicle = value is JourneyCameraRig.Chase or JourneyCameraRig.Cockpit or JourneyCameraRig.FirstPerson;
            _wasTracking = false;
            UpdateRideCamera();
            InvalidateMap();
        }
    }

    public void AttachAudioDevice(IJourneyAudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _sound?.Dispose();
        _audioDevice = device;
        _sound = new JourneySound(device);
        if (_active) _sound.Update(Journey);
    }

    public void SoundCue() => Journey.Horn();

    public void ShowRoute(RoutePlan? route, bool fit = true)
    {
        ForgetFreeRoamReturn();
        StopMotion();
        Journey.Load(route);
        if (route is not null && fit) FitRoute();
        InvalidateMap();
    }
    public void FitRoute()
    {
        if (Journey.Route is not { } route || ActualWidth <= 0 || ActualHeight <= 0) return;
        Journey.Follow = false;
        var safe = SafeViewport();
        var viewport = new SkiaSharp.SKRect((float)(safe.X - safe.Width * .5), (float)(safe.Y - safe.Height * .5),
            (float)(safe.X + safe.Width * .5), (float)(safe.Y + safe.Height * .5));
        var view = _flight ? _to : Camera;
        double padding = Math.Min(64, Math.Min(safe.Width, safe.Height) * .12);
        FlyTo(_terrain is { Relief: true } terrain
            ? view.FitRoute(route.Geometry, ActualWidth, ActualHeight, viewport, padding, terrain)
            : view.FitRoute(route.Geometry, ActualWidth, ActualHeight, viewport, padding));
    }
    public void OrbitBy(double bearingDelta, double pitchDelta)
    {
        if (TryLookFreeRoam(bearingDelta, pitchDelta)) return;
        Journey.Follow = false;
        var camera = _flight ? _to : Camera;
        FlyTo(TransformCamera(camera, 0, bearingDelta, pitchDelta, ActualWidth / 2, ActualHeight / 2));
    }

    private void HookJourney()
    {
        Journey.World = _worldContacts;
        Journey.Cue += cue => { if (_active) _sound?.Play(Journey, cue); };
        Journey.Changed += (_, _) => OnJourneyChanged();
        _gameClock.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = Stopwatch.GetElapsedTime(_gameTimestamp, now).TotalSeconds;
            _gameTimestamp = now;
            Journey.Tick(elapsed);
        };
        KeyUp += (_, e) =>
        {
            if (_held.Remove(e.Key)) { ApplyDrivingKeys(); e.Handled = true; }
        };
        LostFocus += (_, _) => ReleaseFocusedDrivingKeys();
    }

    private void OnJourneyChanged()
    {
        if (!_active) return;
        if (_freeRoamTerrainActive != Journey.IsFreeRoaming)
        {
            _freeRoamTerrainActive = Journey.IsFreeRoaming;
            TerrainChanged();
        }
        if (Journey.Active && !_gameClock.IsEnabled)
        {
            StopMotion(); _gameTimestamp = Stopwatch.GetTimestamp(); _gameClock.Start();
        }
        else if (!Journey.Active)
        {
            _gameClock.Stop(); ReleaseDrivingKeys(); _wasTracking = false;
        }
        _sound?.Update(Journey);
        var previous = Camera;
        UpdateRideCamera();
        if (previous == Camera) InvalidateMap();
    }

    private void UpdateRideCamera()
    {
        if (Journey.IsFreeRoaming) { UpdateFreeRoamCamera(); return; }
        bool track = _active && Journey.Follow && Journey.Route is not null &&
            (Journey.Active || Journey.State == JourneyState.Paused);
        if (!track) { _wasTracking = false; return; }
        var mode = Journey.Vehicle.Mode;
        double zoom = mode switch { TravelMode.Flight => 12.7, TravelMode.Boat => 15.4, TravelMode.Train => 15.3, _ => 16.4 };
        double heading = Journey.Bearing, pitch = 56;
        double lead = Math.Min(110, Journey.SpeedMetersPerSecond * .9);
        switch (_rig)
        {
            case JourneyCameraRig.NorthUp: heading = 0; pitch = 0; lead = 0; break;
            case JourneyCameraRig.Cockpit:
            case JourneyCameraRig.FirstPerson: zoom += .85; pitch = 70; lead = Math.Max(15, lead * 1.6); break;
            case JourneyCameraRig.Orbit: heading = Journey.Bearing + Journey.AnimationSeconds * 12; pitch = 54; lead = 0; break;
        }
        var center = new RoutePoint(Journey.Latitude, Journey.Longitude).Move(Journey.Bearing, lead);
        var focus = center;
        // In an orthographic map a flying vehicle projects above its ground point.
        // Shift the ground target by that projected height so the sprite stays inside the viewport.
        bool perspective = _rig is JourneyCameraRig.Chase or JourneyCameraRig.Cockpit or JourneyCameraRig.FirstPerson;
        double clearance = Journey.AltitudeAboveGroundMeters;
        if (!perspective && clearance > 0)
            center = center.Move(heading, clearance * Math.Tan(pitch * Math.PI / 180));
        var target = new MapCamera(center.Latitude, center.Longitude, zoom + _rideZoom, heading, pitch, perspective,
            perspective ? clearance : 0).OnTerrain(_terrain, ActualHeight);
        var safe = SafeViewport();
        // Shift at the focus plane. Ground-plane drag would over-offset an airborne target.
        var shifted = (target with { TargetAltitudeMeters = 0 }).PanFromTo(
            ActualWidth * .5, ActualHeight * .5, safe.X, safe.Y, ActualWidth, ActualHeight);
        target = (target with { Latitude = shifted.Latitude, Longitude = shifted.Longitude }).OnTerrain(_terrain, ActualHeight);
        if (_terrain is { Relief: true } terrain)
        {
            double focusAltitude = terrain.Elevation.SampleSurfaceMeters(focus.Latitude, focus.Longitude) + clearance;
            target = target.AnchorAltitude(focus.Latitude, focus.Longitude, focusAltitude,
                safe.X, safe.Y, ActualWidth, ActualHeight, terrain);
        }
        long now = Stopwatch.GetTimestamp();
        if (_wasTracking)
        {
            double dt = Math.Max(0, Stopwatch.GetElapsedTime(_cameraTimestamp, now).TotalSeconds);
            double weight = 1 - Math.Exp(-dt * 8);
            target = Camera.Interpolate(target, weight);
        }
        _cameraTimestamp = now; _wasTracking = true; Camera = target;
    }

    private bool ZoomRide(double delta)
    {
        if (ZoomFreeRoam(delta)) return true;
        if (!Journey.Follow || !Journey.Active) return false;
        _rideZoom = Math.Clamp(_rideZoom + delta, -4, 3);
        UpdateRideCamera(); return true;
    }

    private void ReleaseFocusedDrivingKeys()
    {
        // Touch controls may take focus after installing their input. Remove only
        // keyboard contributions, preserving the independently captured touch state.
        if (_held.Count == 0) return;
        _held.Clear();
        if (Journey.IsFreeRoaming) ApplyFreeRoamKeys();
        else Journey.ClearInput();
    }

    private void ReleaseDrivingKeys()
    {
        _freeTouchInput = default;
        _held.Clear(); Journey.ClearInput();
    }
    private bool HandleDrivingKey(VirtualKey key, bool repeated = false)
    {
        if (Journey.IsFreeRoaming) return HandleFreeRoamKey(key, repeated);
        if (Journey.State != JourneyState.Driving) return false;
        if (key is not (VirtualKey.W or VirtualKey.A or VirtualKey.S or VirtualKey.D or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.Left or VirtualKey.Right or VirtualKey.Space or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.H)) return false;
        if (_held.Add(key))
        {
            if (key == VirtualKey.H) SoundCue();
            else ApplyDrivingKeys();
        }
        return true;
    }
    private void ApplyDrivingKeys()
    {
        if (Journey.IsFreeRoaming) { ApplyFreeRoamKeys(); return; }
        double Down(VirtualKey a, VirtualKey b) => (_held.Contains(a) || _held.Contains(b)) ? 1 : 0;
        Journey.SetInput(Down(VirtualKey.W, VirtualKey.Up), Down(VirtualKey.D, VirtualKey.Right) - Down(VirtualKey.A, VirtualKey.Left),
            Math.Max(Down(VirtualKey.S, VirtualKey.Down), _held.Contains(VirtualKey.Space) ? 1 : 0));
        Journey.SetLift((_held.Contains(VirtualKey.PageUp) ? 1 : 0) - (_held.Contains(VirtualKey.PageDown) ? 1 : 0));
    }

    private VehiclePose? CurrentVehicle()
    {
        if (Journey.IsFreeRoaming)
        {
            if (Journey.IsFreeFlying || Journey.Follow && _rig is JourneyCameraRig.FirstPerson or JourneyCameraRig.Cockpit) return null;
            return new VehiclePose(new(Journey.Latitude, Journey.Longitude), Journey.Bearing, Journey.AnimationSeconds,
                Journey.AltitudeMeters, Journey.Active && Journey.SpeedMetersPerSecond > .05, double.NaN,
                Journey.SpeedMetersPerSecond, Journey.Continuity);
        }
        if (Journey.Route is null || Journey.State == JourneyState.Ready) return null;
        return new VehiclePose(new(Journey.Latitude, Journey.Longitude), Journey.Bearing, Journey.AnimationSeconds,
            Journey.AltitudeMeters, Journey.Active && Journey.SpeedMetersPerSecond > .05, Journey.Progress, Journey.SpeedMetersPerSecond, Journey.Continuity);
    }
}
