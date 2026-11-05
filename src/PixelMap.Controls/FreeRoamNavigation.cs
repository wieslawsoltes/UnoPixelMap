using System.Diagnostics;
using Microsoft.UI.Xaml;
using PixelMap.Rendering;
using PixelMap.Routing;
using PixelMap.Simulation;
using Windows.System;

namespace PixelMap.Controls;

public sealed partial class PixelMapControl
{
    private RoutePlan? _routeBeforeFreeRoam;
    private MapCamera _cameraBeforeFreeRoam;
    private bool _hasFreeRoamReturn;
    private FreeRoamInput _freeTouchInput;
    private double _freeCameraDistance = 18;

    /// <summary>Start at the current map center, or change the current free-roam
    /// vehicle in place. A planned route is restored when leaving free roam.</summary>
    public void StartFreeRoam(TravelMode mode)
    {
        var profile = VehicleProfile.ForMode(mode);
        StartFreeRoam(profile);
    }

    /// <summary>Use a custom physical vehicle in a route-independent session.</summary>
    public void StartFreeRoam(VehicleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsValid) throw new ArgumentException("The vehicle profile is invalid.", nameof(profile));
        var point = FreeRoamStartPoint();
        double bearing = Journey.IsFreeRoaming ? Journey.Bearing : Camera.Bearing;
        double ground = _worldContacts.SurfaceElevationMeters(profile, point);
        double altitude = Journey.IsFreeRoaming ? Journey.AltitudeMeters : ground;
        if (profile.Motion == MotionModel.Aircraft) altitude = Math.Max(ground + 80, altitude);
        if (profile.Motion == MotionModel.FreeFlight) altitude = Math.Max(ground + 3, altitude);
        PrepareFreeRoam();
        _freeCameraDistance = profile.Mode switch
        {
            TravelMode.Walk => 6,
            TravelMode.Bike => 10,
            TravelMode.Boat => 28,
            TravelMode.Train => 32,
            TravelMode.Flight => 140,
            _ => 18
        };
        Journey.StartFreeRoam(profile, point, bearing, altitude);
        Journey.IsLookRelativeToVehicle = _rig is JourneyCameraRig.Chase or JourneyCameraRig.Cockpit or JourneyCameraRig.FirstPerson;
        Focus(FocusState.Programmatic);
    }

    /// <summary>Fly an independent camera through the world with six-axis controls.</summary>
    public void StartFreeFlight()
    {
        var point = FreeRoamStartPoint();
        double bearing = Journey.IsFreeRoaming ? Journey.LookBearing : Camera.Bearing;
        double ground = _worldContacts.SurfaceElevationMeters(VehicleProfile.FreeCamera, point);
        double altitude = Journey.IsFreeRoaming ? Journey.AltitudeMeters : ground + 30;
        PrepareFreeRoam();
        Journey.StartFreeFlight(point, Math.Max(ground + 3, altitude), bearing);
        _wasTracking = false;
        UpdateRideCamera();
        Focus(FocusState.Programmatic);
    }

    public void StopFreeRoam()
    {
        if (!Journey.IsFreeRoaming) return;
        var point = new RoutePoint(Journey.Latitude, Journey.Longitude);
        double bearing = Journey.LookBearing;
        var route = _routeBeforeFreeRoam;
        var old = _hasFreeRoamReturn ? _cameraBeforeFreeRoam : OpeningView;
        ForgetFreeRoamReturn();
        Journey.Load(route);
        Camera = new MapCamera(point.Latitude, point.Longitude, old.Zoom, bearing,
            old.Pitch, old.Perspective).OnTerrain(_terrain, ActualHeight);
        InvalidateMap();
    }

    private RoutePoint FreeRoamStartPoint() => Journey.IsFreeRoaming
        ? new(Journey.Latitude, Journey.Longitude) : new(Camera.Latitude, Camera.Longitude);

    private void PrepareFreeRoam()
    {
        if (!Journey.IsFreeRoaming)
        {
            _routeBeforeFreeRoam = Journey.Route;
            _cameraBeforeFreeRoam = Camera;
            _hasFreeRoamReturn = true;
        }
        ReleaseDrivingKeys();
        StopMotion();
        ClearMarker();
        IsMapPointSelectionEnabled = false;
        _wasTracking = false;
    }

    private void ForgetFreeRoamReturn()
    {
        _routeBeforeFreeRoam = null;
        _hasFreeRoamReturn = false;
        ReleaseDrivingKeys();
        _wasTracking = false;
    }

    /// <summary>Set touch/gamepad controls independently of currently held keys.</summary>
    public void SetFreeRoamInput(FreeRoamInput input)
    {
        _freeTouchInput = input;
        if (Journey.IsFreeRoaming && Journey.Active) ApplyFreeRoamKeys();
    }

    /// <summary>Rotate the active first-person view or the vehicle's orbit camera.</summary>
    public void LookFreeRoam(double bearingDelta, double pitchDelta)
        => TryLookFreeRoam(bearingDelta, pitchDelta);

    private bool TryLookFreeRoam(double bearingDelta, double pitchDelta)
    {
        if (!Journey.IsFreeRoaming) return false;
        if (!double.IsFinite(bearingDelta) || !double.IsFinite(pitchDelta)) return true;
        Journey.Follow = true;
        if (_rig == JourneyCameraRig.NorthUp) CameraRig = JourneyCameraRig.Orbit;
        Journey.SetLook(Journey.LookBearing + bearingDelta, Journey.LookPitch + pitchDelta);
        return true;
    }

    private bool ZoomFreeRoam(double delta)
    {
        if (!Journey.IsFreeRoaming) return false;
        if (!double.IsFinite(delta)) return true;
        if (Journey.IsFreeFlying)
        {
            Journey.FreeFlightSpeed *= Math.Pow(2, delta * .5);
            return true;
        }
        if (_rig is JourneyCameraRig.FirstPerson or JourneyCameraRig.Cockpit)
        {
            if (delta >= 0) return true;
            CameraRig = JourneyCameraRig.Chase;
        }
        double distance = _freeCameraDistance * Math.Pow(2, -delta * .5);
        if (double.IsFinite(distance)) _freeCameraDistance = Math.Max(.25, distance);
        Journey.Follow = true;
        _wasTracking = false;
        UpdateRideCamera();
        return true;
    }

    private void UpdateFreeRoamCamera()
    {
        if (!_active || !Journey.Follow || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _wasTracking = false;
            return;
        }
        var body = new RoutePoint(Journey.Latitude, Journey.Longitude);
        double heading = Journey.LookBearing;
        double pitch = Journey.LookPitch;
        bool firstPerson = Journey.IsFreeFlying || _rig is JourneyCameraRig.FirstPerson or JourneyCameraRig.Cockpit;
        double eyeHeight = Journey.Vehicle.Mode switch
        {
            TravelMode.Walk => 1.7,
            TravelMode.Bike => 1.6,
            TravelMode.Boat => 2.1,
            TravelMode.Train => 3.1,
            TravelMode.Flight => 1.8,
            _ => 1.2
        };
        var eye = body;
        double datum = _terrain is { Relief: true } ? _elevationSnapshot.SampleSurfaceMeters(body.Latitude, body.Longitude) : 0;
        double altitude;
        double focusAltitude;
        if (firstPerson)
        {
            altitude = RenderFreeRoamAltitude(body, Journey.AltitudeMeters, datum) + (Journey.IsFreeFlying ? 0 : eyeHeight);
            focusAltitude = altitude;
        }
        else
        {
            pitch = _rig == JourneyCameraRig.NorthUp ? 0 : Math.Clamp(pitch - 24, 0, 179.5);
            if (_rig == JourneyCameraRig.NorthUp) heading = 0;
            double radians = pitch * Math.PI / 180;
            double horizontal = _freeCameraDistance * Math.Sin(radians);
            eye = body.Move(heading + 180, horizontal);
            datum = _terrain is { Relief: true } ? _elevationSnapshot.SampleSurfaceMeters(eye.Latitude, eye.Longitude) : 0;
            focusAltitude = RenderFreeRoamAltitude(body, Journey.AltitudeMeters, datum) + eyeHeight * .5;
            altitude = focusAltitude + _freeCameraDistance * Math.Cos(radians);
            double floor = RenderFreeRoamAltitude(eye, _elevationSnapshot.SampleSurfaceMeters(eye.Latitude, eye.Longitude), datum) + .35;
            if (altitude < floor)
            {
                altitude = floor;
                pitch = Math.Atan2(horizontal, altitude - focusAltitude) * 180 / Math.PI;
            }
        }
        double viewDistance = Math.Max(6000, _freeCameraDistance * 3);
        var target = MapCamera.FromEye(eye.Latitude, eye.Longitude, altitude, heading, pitch,
            ActualWidth, ActualHeight, viewDistance, datum);
        if (!firstPerson)
        {
            // Solve the unobstructed screen ray while retaining the physical eye.
            // A no-roll camera keeps its original aim when that ray is unreachable.
            var safe = SafeViewport();
            if (target.TryAimAt(body.Latitude, body.Longitude, focusAltitude,
                safe.X, safe.Y, ActualWidth, ActualHeight, out var aimed)) target = aimed;
        }
        // First-person input tracks the eye directly. Vehicle-mounted third-person
        // cameras retain a short, frame-rate-independent spring after startup.
        long now = Stopwatch.GetTimestamp();
        if (_wasTracking && !firstPerson)
        {
            double elapsed = Math.Max(0, Stopwatch.GetElapsedTime(_cameraTimestamp, now).TotalSeconds);
            target = Camera.Interpolate(target, 1 - Math.Exp(-elapsed * 12));
        }
        _cameraTimestamp = now;
        _wasTracking = true;
        Camera = target;
    }

    private double RenderFreeRoamAltitude(RoutePoint point, double physicalAltitude, double datum)
    {
        double surface = _elevationSnapshot.SampleSurfaceMeters(point.Latitude, point.Longitude);
        if (_terrain is not { Relief: true } terrain) return physicalAltitude - surface;
        double meshHeight = TryGetRenderedTerrainElevation(point.Latitude, point.Longitude, out double sampled) ? sampled : surface;
        double scale = double.IsFinite(terrain.Exaggeration) ? Math.Clamp(terrain.Exaggeration, .25, 8) : 1;
        return datum + (meshHeight - datum) * scale + physicalAltitude - surface;
    }

    private bool HandleFreeRoamKey(VirtualKey key, bool repeated)
    {
        if (key is VirtualKey.Escape or VirtualKey.P)
        {
            if (repeated) return true;
            if (_held.Add(key))
            {
                if (Journey.Active) Journey.Pause();
                else if (key == VirtualKey.P) Journey.Resume();
            }
            return true;
        }
        if (key is not (VirtualKey.W or VirtualKey.A or VirtualKey.S or VirtualKey.D or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.Left or VirtualKey.Right or VirtualKey.Q or VirtualKey.E or VirtualKey.Space or VirtualKey.PageUp
            or VirtualKey.PageDown or VirtualKey.H or VirtualKey.R or VirtualKey.C or VirtualKey.Shift or VirtualKey.LeftShift
            or VirtualKey.RightShift or VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl)) return false;
        // A paused game retains ownership of its movement keys. Letting them fall
        // through would pan the map or start an unrelated camera flight.
        if (!Journey.Active) return true;
        // A focus or pause transition clears the input state. An operating-system
        // autorepeat must wait for a fresh press before restoring that held control.
        if (repeated && !_held.Contains(key)) return true;
        if (_held.Add(key))
        {
            if (key == VirtualKey.H) SoundCue();
            else if (key == VirtualKey.R && !repeated) Journey.Respawn();
            else if (key == VirtualKey.C && !repeated)
                CameraRig = _rig is JourneyCameraRig.FirstPerson or JourneyCameraRig.Cockpit ? JourneyCameraRig.Chase : JourneyCameraRig.FirstPerson;
            else ApplyFreeRoamKeys();
        }
        return true;
    }

    private void ApplyFreeRoamKeys()
    {
        double Down(VirtualKey first, VirtualKey second) => _held.Contains(first) || _held.Contains(second) ? 1 : 0;
        bool directional = Journey.Vehicle.Motion is MotionModel.Pedestrian or MotionModel.FreeFlight;
        double lateral = Down(VirtualKey.D, VirtualKey.Right) - Down(VirtualKey.A, VirtualKey.Left);
        double vertical = (_held.Contains(VirtualKey.PageUp) ? 1 : 0) - (_held.Contains(VirtualKey.PageDown) ? 1 : 0);
        bool space = _held.Contains(VirtualKey.Space);
        if (Journey.IsFreeFlying)
            vertical += (space ? 1 : 0) - (_held.Contains(VirtualKey.Control) || _held.Contains(VirtualKey.LeftControl) || _held.Contains(VirtualKey.RightControl) ? 1 : 0);
        Journey.SetFreeRoamInput(new(
            _freeTouchInput.Forward + Down(VirtualKey.W, VirtualKey.Up) - Down(VirtualKey.S, VirtualKey.Down),
            _freeTouchInput.Strafe + (directional ? lateral : 0),
            _freeTouchInput.Turn + (directional ? (_held.Contains(VirtualKey.E) ? 1 : 0) - (_held.Contains(VirtualKey.Q) ? 1 : 0) : lateral),
            _freeTouchInput.Vertical + vertical,
            Math.Max(_freeTouchInput.Brake, !directional && space ? 1 : 0),
            _freeTouchInput.Jump || Journey.Vehicle.Motion == MotionModel.Pedestrian && space,
            _freeTouchInput.Boost || _held.Contains(VirtualKey.Shift) || _held.Contains(VirtualKey.LeftShift) || _held.Contains(VirtualKey.RightShift)));
    }
}
