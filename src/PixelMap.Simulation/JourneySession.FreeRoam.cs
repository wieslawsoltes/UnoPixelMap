using PixelMap.Routing;

namespace PixelMap.Simulation;

public sealed partial class JourneySession
{
    private const double Gravity = 9.80665;
    private sealed record FreeSpawn(RoutePoint Point, double Bearing, double Altitude, double Speed);
    private FreeSpawn? _freeSpawn;
    private FreeRoamInput _freeInput;
    private double _forwardVelocity, _eastVelocity, _northVelocity, _verticalVelocity;
    private double _freeFlightSpeed = VehicleProfile.FreeCamera.CruiseSpeed;
    private bool _jumpHeld, _jumpRequested;

    /// <summary>A free session integrates a world-space pose without a route station,
    /// checkpoint corridor or destination. Planned journeys use their existing track.</summary>
    public bool IsFreeRoaming => _freeSpawn is not null;
    public bool IsFreeFlying => IsFreeRoaming && Vehicle.Motion == MotionModel.FreeFlight;
    public double LookBearing { get; private set; }
    /// <summary>Vehicle-mounted views retain their relative look offset while the
    /// chassis turns. Independent orbit views keep a world-aligned look direction.</summary>
    public bool IsLookRelativeToVehicle { get; set; } = true;
    /// <summary>Look angle from nadir. 90° is horizontal and angles above 90° look upward.</summary>
    public double LookPitch { get; private set; } = 90;
    public double VerticalSpeedMetersPerSecond => IsFreeRoaming ? _verticalVelocity : 0;
    public double SignedSpeedMetersPerSecond => IsFreeRoaming ? _forwardVelocity : SpeedMetersPerSecond;
    public bool IsGrounded => AltitudeMeters <= GroundElevationMeters + .02 && _verticalVelocity <= 0;
    public bool IsWaitingForTrack { get; private set; }
    /// <summary>A free vessel holds its position until mapped, navigable water is available.</summary>
    public bool IsWaitingForWater { get; private set; }

    /// <summary>Unboosted free-camera speed in metres per second. Scrolling can change
    /// this independently of scene scale or the physical vehicle's performance.</summary>
    public double FreeFlightSpeed
    {
        get => _freeFlightSpeed;
        set
        {
            if (!double.IsFinite(value)) return;
            double speed = Math.Clamp(value, .1, VehicleProfile.FreeCamera.MaxSpeed);
            if (_freeFlightSpeed == speed) return;
            _freeFlightSpeed = speed;
            Notify();
        }
    }

    /// <summary>Begin an unscored, route-independent game at a geographic pose.
    /// Ground vehicles attach to the sampled surface. Aircraft placed in the air
    /// begin with enough forward airspeed to sustain level flight.</summary>
    public void StartFreeRoam(VehicleProfile profile, RoutePoint point, double bearing = 0,
        double? altitudeMeters = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsValid) throw new ArgumentException("The vehicle profile is invalid.", nameof(profile));
        if (!point.IsValid) throw new ArgumentOutOfRangeException(nameof(point));
        if (!double.IsFinite(bearing)) throw new ArgumentOutOfRangeException(nameof(bearing));
        if (altitudeMeters.HasValue && !double.IsFinite(altitudeMeters.Value))
            throw new ArgumentOutOfRangeException(nameof(altitudeMeters));

        if (profile.Motion == MotionModel.Rail && World is IFreeRoamRailWorld rail &&
            rail.TryMoveOnRails(point, bearing, 0, 0, out var trackPoint, out double trackHeading))
        {
            point = trackPoint;
            bearing = trackHeading;
        }

        double ground = World?.SurfaceElevationMeters(profile, point) ?? 0;
        if (!double.IsFinite(ground)) ground = 0;
        bool airborne = profile.Motion is MotionModel.Aircraft or MotionModel.FreeFlight;
        double altitude = airborne ? Math.Max(ground, altitudeMeters ?? ground) : ground;
        double speed = profile.Motion == MotionModel.Aircraft && altitude > ground + 2 ? profile.CruiseSpeed * .65 : 0;
        var spawn = new FreeSpawn(point, RoutePoint.Wrap(bearing), altitude, speed);

        bool paused = IsFreeRoaming && State == JourneyState.Paused;
        Route = null;
        _track = null;
        Vehicle = profile;
        _freeSpawn = spawn;
        _rate = 1;
        State = paused ? JourneyState.Paused : JourneyState.Driving;
        ResetFreeRoam();
        Cue?.Invoke(JourneyCue.Depart);
    }

    /// <summary>Start a six-axis flying camera. Movement follows the look ray while
    /// vertical input remains aligned with world up. Terrain and structures retain contacts.</summary>
    public void StartFreeFlight(RoutePoint point, double altitudeMeters, double bearing = 0)
        => StartFreeRoam(VehicleProfile.FreeCamera, point, bearing, altitudeMeters);

    public void SetFreeRoamInput(FreeRoamInput input)
    {
        if (!IsFreeRoaming) return;
        _freeInput = new(Clip(input.Forward, -1, 1), Clip(input.Strafe, -1, 1),
            Clip(input.Turn, -1, 1), Clip(input.Vertical, -1, 1), Clip(input.Brake, 0, 1), input.Jump, input.Boost);
        if (input.Jump && !_jumpHeld) _jumpRequested = true;
        _jumpHeld = input.Jump;
    }

    /// <summary>Set an absolute free-look orientation, retaining the world-space pose.</summary>
    public void SetLook(double bearing, double pitch)
    {
        if (!IsFreeRoaming || !double.IsFinite(bearing) || !double.IsFinite(pitch)) return;
        bearing = RoutePoint.Wrap(bearing);
        pitch = Math.Clamp(pitch, 0, 179.5);
        if (LookBearing == bearing && LookPitch == pitch) return;
        LookBearing = bearing;
        LookPitch = pitch;
        if (Vehicle.Motion is MotionModel.Pedestrian or MotionModel.FreeFlight) Bearing = bearing;
        Notify();
    }

    private void ResetFreeRoam()
    {
        var spawn = _freeSpawn!;
        bool paused = State == JourneyState.Paused;
        Continuity++;
        State = paused ? JourneyState.Paused : JourneyState.Driving;
        _resumeState = JourneyState.Driving;
        _position = spawn.Point;
        Bearing = LookBearing = spawn.Bearing;
        LookPitch = Vehicle.Motion == MotionModel.FreeFlight ? 72 : 90;
        GroundElevationMeters = SurfaceElevationAt(_position);
        AltitudeMeters = Vehicle.Motion is MotionModel.Aircraft or MotionModel.FreeFlight
            ? Math.Max(GroundElevationMeters, spawn.Altitude) : GroundElevationMeters;
        _forwardVelocity = SpeedMetersPerSecond = spawn.Speed;
        _eastVelocity = _northVelocity = _verticalVelocity = 0;
        _accumulator = _yawRate = _contactCooldown = 0;
        DistanceMeters = RunDistanceMeters = ElapsedSeconds = AnimationSeconds = OffRouteMeters = 0;
        CheckpointsPassed = CheckpointsTotal = Score = 0;
        NextCheckpoint = null;
        IsWaitingForTrack = false;
        IsWaitingForWater = Vehicle.Motion == MotionModel.Vessel && !HasNavigableWaterAt(_position);
        _follow = true;
        Cruise = false;
        ClearInput();
        Notify();
    }

    private void RefreshFreeRoamSurface()
    {
        double previousGround = GroundElevationMeters;
        double previousAltitude = AltitudeMeters;
        bool previouslyWaitingForWater = IsWaitingForWater;
        IsWaitingForWater = Vehicle.Motion == MotionModel.Vessel && !HasNavigableWaterAt(_position);
        GroundElevationMeters = SurfaceElevationAt(_position);
        bool supported = previousAltitude <= previousGround + .02 && _verticalVelocity <= 0;
        AltitudeMeters = supported ? GroundElevationMeters : Math.Max(GroundElevationMeters, AltitudeMeters);
        if (AltitudeMeters <= GroundElevationMeters) _verticalVelocity = Math.Max(0, _verticalVelocity);
        if (previousGround == GroundElevationMeters && previousAltitude == AltitudeMeters &&
            previouslyWaitingForWater == IsWaitingForWater) return;
        Continuity++;
        Notify();
    }

    private void FreeRoamStep()
    {
        switch (Vehicle.Motion)
        {
            case MotionModel.Pedestrian: WalkFree(); break;
            case MotionModel.FreeFlight: FlyFree(); break;
            default: DriveFree(); break;
        }
    }

    private void WalkFree()
    {
        LookBearing = RoutePoint.Wrap(LookBearing + _freeInput.Turn * Vehicle.TurnRate * Step);
        Bearing = LookBearing;
        var (sin, cos) = Math.SinCos(LookBearing * Math.PI / 180);
        double forward = _freeInput.Forward, right = _freeInput.Strafe;
        double magnitude = Math.Max(1, Math.Sqrt(forward * forward + right * right));
        double speed = _freeInput.Boost ? Vehicle.MaxSpeed : Vehicle.CruiseSpeed;
        double targetEast = (forward * sin + right * cos) * speed / magnitude;
        double targetNorth = (forward * cos - right * sin) * speed / magnitude;
        ApproachPlanar(targetEast, targetNorth, forward == 0 && right == 0 ? Vehicle.Braking : Vehicle.Acceleration);

        bool grounded = IsGrounded;
        if (_jumpRequested && grounded)
        {
            _verticalVelocity = 5.4;
            grounded = false;
        }
        _jumpRequested = false;
        _verticalVelocity = grounded ? 0 : Math.Max(-55, _verticalVelocity - Gravity * Step);
        double proposedAltitude = AltitudeMeters + _verticalVelocity * Step;
        double east = _eastVelocity * Step, north = _northVelocity * Step;
        var next = Offset(_position, east, north);
        if (!TryWalkMove(next, proposedAltitude, grounded, out double altitude, out double surface))
        {
            // Axis-separated sliding lets a character move along a contacted wall.
            // Test the greater component first to keep the result deterministic.
            bool preferEast = Math.Abs(east) >= Math.Abs(north);
            next = Offset(_position, preferEast ? east : 0, preferEast ? 0 : north);
            if (TryWalkMove(next, proposedAltitude, grounded, out altitude, out surface))
            {
                if (preferEast) _northVelocity = 0; else _eastVelocity = 0;
            }
            else
            {
                next = Offset(_position, preferEast ? 0 : east, preferEast ? north : 0);
                if (TryWalkMove(next, proposedAltitude, grounded, out altitude, out surface))
                {
                    if (preferEast) _eastVelocity = 0; else _northVelocity = 0;
                }
                else
                {
                    _eastVelocity = _northVelocity = 0;
                    next = _position;
                    surface = GroundElevationMeters;
                    altitude = Math.Max(surface, proposedAltitude);
                    if (Blocked(next, altitude)) { altitude = AltitudeMeters; _verticalVelocity = 0; }
                }
            }
        }
        LandAt(altitude, surface);
        AdvanceFreePose(next, altitude, surface);
        _forwardVelocity = _eastVelocity * sin + _northVelocity * cos;
        SpeedMetersPerSecond = Math.Sqrt(_eastVelocity * _eastVelocity + _northVelocity * _northVelocity);
    }

    private bool TryWalkMove(RoutePoint next, double proposedAltitude, bool grounded,
        out double altitude, out double surface)
    {
        surface = SurfaceElevationAt(next);
        double distance = _position.DistanceTo(next);
        double rise = surface - GroundElevationMeters;
        if (grounded && rise > .25 + distance * 1.2)
        {
            altitude = AltitudeMeters;
            return false;
        }
        if (!grounded && surface > Math.Max(AltitudeMeters, proposedAltitude) + .25)
        {
            altitude = AltitudeMeters;
            return false;
        }
        bool followsGround = grounded && rise >= -.25 - distance * 1.2;
        altitude = followsGround ? surface : Math.Max(surface, proposedAltitude);
        return !Blocked(next, altitude);
    }

    private void FlyFree()
    {
        LookBearing = RoutePoint.Wrap(LookBearing + _freeInput.Turn * Vehicle.TurnRate * Step);
        Bearing = LookBearing;
        var (sin, cos) = Math.SinCos(LookBearing * Math.PI / 180);
        var (pitchSin, pitchCos) = Math.SinCos(LookPitch * Math.PI / 180);
        double east = _freeInput.Forward * pitchSin * sin + _freeInput.Strafe * cos;
        double north = _freeInput.Forward * pitchSin * cos - _freeInput.Strafe * sin;
        double vertical = -_freeInput.Forward * pitchCos + _freeInput.Vertical;
        double magnitude = Math.Max(1, Math.Sqrt(east * east + north * north + vertical * vertical));
        double speed = Math.Min(Vehicle.MaxSpeed, FreeFlightSpeed * (_freeInput.Boost ? 4 : 1));
        double response = 1 - Math.Exp(-Vehicle.Response * Step);
        _eastVelocity += (east * speed / magnitude - _eastVelocity) * response;
        _northVelocity += (north * speed / magnitude - _northVelocity) * response;
        _verticalVelocity += (vertical * speed / magnitude - _verticalVelocity) * response;
        if (_freeInput.Brake > 0)
        {
            double brake = Math.Exp(-Vehicle.Response * 2 * _freeInput.Brake * Step);
            _eastVelocity *= brake;
            _northVelocity *= brake;
            _verticalVelocity *= brake;
        }
        var next = Offset(_position, _eastVelocity * Step, _northVelocity * Step);
        double surface = SurfaceElevationAt(next);
        double proposedAltitude = AltitudeMeters + _verticalVelocity * Step;
        double altitude = Math.Max(surface + .15, proposedAltitude);
        bool risingTerrain = surface > Math.Max(AltitudeMeters, proposedAltitude) + .15;
        if (risingTerrain || Blocked(next, altitude))
        {
            next = _position;
            surface = GroundElevationMeters;
            altitude = Math.Max(surface + .15, AltitudeMeters + _verticalVelocity * Step);
            _eastVelocity = _northVelocity = 0;
            if (Blocked(next, altitude)) { altitude = AltitudeMeters; _verticalVelocity = 0; }
            FreeContact();
        }
        if (altitude <= surface + .15 && _verticalVelocity < 0) _verticalVelocity = 0;
        AdvanceFreePose(next, altitude, surface);
        _forwardVelocity = _eastVelocity * sin + _northVelocity * cos;
        SpeedMetersPerSecond = Math.Sqrt(_eastVelocity * _eastVelocity + _northVelocity * _northVelocity + _verticalVelocity * _verticalVelocity);
        _jumpRequested = false;
    }

    private void DriveFree()
    {
        var profile = Vehicle;
        IsWaitingForWater = profile.Motion == MotionModel.Vessel && !HasNavigableWaterAt(_position);
        if (IsWaitingForWater)
        {
            _forwardVelocity = SpeedMetersPerSecond = _yawRate = 0;
            return;
        }
        double previousSpeed = _forwardVelocity;
        double forward = _freeInput.Forward;
        double reverse = profile.Motion == MotionModel.Aircraft ? 0 : profile.MaxSpeed * .25;
        double target = forward * (forward < 0 ? reverse : profile.MaxSpeed);
        double drag = profile.CoastDrag + (profile.Acceleration - profile.CoastDrag) *
            Math.Pow(Math.Abs(previousSpeed) / profile.MaxSpeed, 2);
        double braking = _freeInput.Brake * profile.Braking;
        if (forward == 0 || _freeInput.Brake > 0)
            _forwardVelocity = Approach(previousSpeed, 0, (drag + braking) * Step);
        else
        {
            double acceleration = profile.Acceleration * Math.Abs(forward) * (_freeInput.Boost ? 1.25 : 1);
            if (previousSpeed * forward < 0) acceleration += profile.Braking * .6;
            _forwardVelocity = Approach(previousSpeed, target, acceleration * Step);
            _forwardVelocity = Approach(_forwardVelocity, 0, drag * Step);
        }
        double speed = Math.Abs(_forwardVelocity);
        double authority = Math.Min(1, speed / 3);
        double desiredYaw = _freeInput.Turn * profile.TurnRate * authority /
            (1 + speed / profile.CruiseSpeed) * Math.Sign(_forwardVelocity);
        _yawRate += (desiredYaw - _yawRate) * (1 - Math.Exp(-profile.Response * Step));
        double headingDelta = _yawRate * Step;
        Bearing = RoutePoint.Wrap(Bearing + headingDelta);
        if (IsLookRelativeToVehicle) LookBearing = RoutePoint.Wrap(LookBearing + headingDelta);
        double distance = (previousSpeed + _forwardVelocity) * .5 * Step;
        var next = _position.Move(Bearing, distance);
        if (profile.Motion == MotionModel.Vessel && !HasNavigableWaterAt(next))
        {
            IsWaitingForWater = true;
            FreeContact();
            _forwardVelocity = SpeedMetersPerSecond = _yawRate = 0;
            return;
        }
        if (profile.Motion == MotionModel.Rail)
        {
            if (World is not IFreeRoamRailWorld rail ||
                !rail.TryMoveOnRails(_position, Bearing, distance, _freeInput.Turn, out next, out double heading))
            {
                IsWaitingForTrack = true;
                _forwardVelocity = SpeedMetersPerSecond = 0;
                return;
            }
            IsWaitingForTrack = false;
            if (IsLookRelativeToVehicle) LookBearing = RoutePoint.Wrap(LookBearing + RoutePoint.Wrap(heading - Bearing));
            Bearing = heading;
        }
        double surface = SurfaceElevationAt(next);
        double altitude;
        if (profile.Motion == MotionModel.Aircraft)
        {
            double airspeed = Math.Clamp((speed - 12) / 20, 0, 1);
            double targetClimb = _freeInput.Vertical * profile.CruiseSpeed * .35 * airspeed - (1 - airspeed) * 3;
            _verticalVelocity += (targetClimb - _verticalVelocity) * (1 - Math.Exp(-2.5 * Step));
            bool taxi = IsGrounded && Math.Abs(surface - GroundElevationMeters) <= Math.Abs(distance) * .35 + .02;
            double proposedAltitude = AltitudeMeters + _verticalVelocity * Step;
            if (!taxi && surface > Math.Max(AltitudeMeters, proposedAltitude) + .02)
            {
                StopFreeVehicleAtContact();
                return;
            }
            altitude = taxi && _verticalVelocity <= 0 ? surface : Math.Max(surface, proposedAltitude);
        }
        else if (profile.Motion is MotionModel.Vessel or MotionModel.Rail)
        {
            altitude = surface;
            _verticalVelocity = 0;
        }
        else
        {
            double rise = surface - GroundElevationMeters;
            bool grounded = IsGrounded;
            if (grounded && rise > Math.Abs(distance) * .85 + .25)
            {
                StopFreeVehicleAtContact();
                return;
            }
            bool follows = grounded && rise >= -Math.Abs(distance) * .85 - .25;
            _verticalVelocity = follows ? 0 : Math.Max(-80, _verticalVelocity - Gravity * Step);
            double proposedAltitude = AltitudeMeters + _verticalVelocity * Step;
            if (!grounded && surface > Math.Max(AltitudeMeters, proposedAltitude) + .25)
            {
                StopFreeVehicleAtContact();
                return;
            }
            altitude = follows ? surface : Math.Max(surface, proposedAltitude);
        }
        if (profile.Motion != MotionModel.Rail && Blocked(next, altitude))
        {
            StopFreeVehicleAtContact();
            return;
        }
        LandAt(altitude, surface);
        AdvanceFreePose(next, altitude, surface);
        SpeedMetersPerSecond = Math.Abs(_forwardVelocity);
        _jumpRequested = false;
    }

    private void AdvanceFreePose(RoutePoint next, double altitude, double surface)
    {
        double distance = _position.DistanceTo(next);
        _position = next;
        AltitudeMeters = altitude;
        GroundElevationMeters = surface;
        DistanceMeters += distance;
        RunDistanceMeters += distance;
    }

    private bool HasNavigableWaterAt(RoutePoint point)
        => World is IFreeRoamWaterWorld water && water.IsNavigableWater(point);

    private void LandAt(double altitude, double surface)
    {
        if (altitude > surface + .000001 || _verticalVelocity >= 0) return;
        if (_verticalVelocity < -6 && AltitudeMeters > GroundElevationMeters + .02) FreeContact();
        _verticalVelocity = 0;
    }

    private bool Blocked(RoutePoint next, double altitude)
    {
        if (World is not { } world) return false;
        double body = Vehicle.Motion switch
        {
            MotionModel.Pedestrian => 1.7,
            MotionModel.Ground => 1.5,
            MotionModel.Vessel => 2,
            MotionModel.Aircraft => 1.5,
            _ => .15
        };
        return world.Blocks(Vehicle, _position, next, AltitudeMeters, altitude) ||
            world.Blocks(Vehicle, _position, next, AltitudeMeters + body, altitude + body) ||
            world.Blocks(Vehicle, next, next, altitude, altitude + body);
    }

    private void FreeContact()
    {
        _forwardVelocity *= .2;
        SpeedMetersPerSecond *= .2;
        if (_contactCooldown > 0) return;
        _contactCooldown = 1;
        Cue?.Invoke(JourneyCue.Contact);
    }

    private void StopFreeVehicleAtContact()
    {
        double altitude = Math.Max(GroundElevationMeters, AltitudeMeters + _verticalVelocity * Step);
        if (Blocked(_position, altitude)) _verticalVelocity = 0;
        else
        {
            AltitudeMeters = altitude;
            if (altitude <= GroundElevationMeters) _verticalVelocity = 0;
        }
        FreeContact();
        SpeedMetersPerSecond = Math.Abs(_forwardVelocity);
    }

    private void ApproachPlanar(double east, double north, double acceleration)
    {
        double dx = east - _eastVelocity, dy = north - _northVelocity;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double weight = length > 0 ? Math.Min(1, acceleration * Step / length) : 0;
        _eastVelocity += dx * weight;
        _northVelocity += dy * weight;
    }

    private static RoutePoint Offset(RoutePoint point, double east, double north)
    {
        double distance = Math.Sqrt(east * east + north * north);
        return distance == 0 ? point : point.Move(Math.Atan2(east, north) * 180 / Math.PI, distance);
    }

    private static double Approach(double value, double target, double delta)
        => value < target ? Math.Min(value + delta, target) : Math.Max(value - delta, target);
}

/// <summary>Optional physical rail support for a free session. The implementation
/// follows streamed connected track and uses steering to choose an upcoming junction.</summary>
public interface IFreeRoamRailWorld
{
    bool TryMoveOnRails(RoutePoint from, double bearing, double distanceMeters, double steering,
        out RoutePoint point, out double heading);
}

/// <summary>Optional mapped-water support for route-independent vessels. Unloaded
/// coverage and dry or obstructed surfaces return false, allowing the session to wait.</summary>
public interface IFreeRoamWaterWorld
{
    bool IsNavigableWater(RoutePoint point);
}
