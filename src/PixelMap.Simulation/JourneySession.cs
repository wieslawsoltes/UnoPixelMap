using PixelMap.Routing;

namespace PixelMap.Simulation;

/// <summary>Fixed 60 Hz simulation clock shared by replay, driving, camera, audio and effects.</summary>
public sealed partial class JourneySession
{
    private const double Step = 1d / 60;
    private RouteTrack? _track;
    private JourneyState _resumeState;
    private double _accumulator, _throttle, _steer, _brake, _lift, _yawRate, _contactCooldown, _rate = 1;
    private bool _follow = true, _muted = true;
    private RoutePoint _position;
    private IJourneyWorld? _world;
    public RoutePlan? Route { get; private set; }
    public JourneyState State { get; private set; }
    public VehicleProfile Vehicle { get; private set; } = VehicleProfile.Car;
    public IJourneyWorld? World
    {
        get => _world;
        set
        {
            if (ReferenceEquals(_world, value)) return;
            _world = value;
            RefreshSurface();
        }
    }
    public double Latitude => _position.Latitude;
    public double Longitude => _position.Longitude;
    public double Bearing { get; private set; }
    /// <summary>Vehicle altitude in metres above mean sea level.</summary>
    public double AltitudeMeters { get; private set; }
    /// <summary>The supporting terrain or water surface in metres above mean sea level.</summary>
    public double GroundElevationMeters { get; private set; }
    public double AltitudeAboveGroundMeters => Math.Max(0, AltitudeMeters - GroundElevationMeters);
    public double SpeedMetersPerSecond { get; private set; }
    public double DistanceMeters { get; private set; }
    public double RunDistanceMeters { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public double AnimationSeconds { get; private set; }
    public double EngineLoad => IsFreeRoaming ? Math.Abs(_freeInput.Forward) : State == JourneyState.Preview ? .6 : Cruise ? .65 : _throttle;
    public double OffRouteMeters { get; private set; }
    public double Progress => _track is { } t ? DistanceMeters / t.Length : 0;
    public int CheckpointsTotal { get; private set; }
    public int CheckpointsPassed { get; private set; }
    public int Score { get; private set; }
    public bool Cruise { get; private set; }
    public long Continuity { get; private set; }
    public bool IsScoredRun => !IsFreeRoaming && (State == JourneyState.Driving || State == JourneyState.Paused && _resumeState == JourneyState.Driving);
    private double NextStation => _track is { } t && CheckpointsTotal > 0 ? t.Length * (CheckpointsPassed + 1d) / CheckpointsTotal : 0;
    public double NextCheckpointMeters => NextCheckpoint is { } gate ? _position.DistanceTo(gate.Point) : 0;
    public RoutePosition? NextCheckpoint { get; private set; }
    public bool Active => State is JourneyState.Driving or JourneyState.Preview;
    public bool Follow { get => _follow; set { if (_follow != value) { _follow = value; Notify(); } } }
    public bool Muted { get => _muted; set { if (_muted != value) { _muted = value; Notify(); } } }
    public double PlaybackRate
    {
        get => _rate;
        set { if (double.IsFinite(value)) { _rate = Math.Clamp(value, .25, 32); Notify(); } }
    }
    public event EventHandler? Changed;
    public event Action<JourneyCue>? Cue;

    public void Load(RoutePlan? route)
    {
        var vehicle = route is null ? Vehicle : VehicleProfile.ForMode(route.Mode);
        var track = route is null ? null : new RouteTrack(route);
        _freeSpawn = null;
        _verticalVelocity = _eastVelocity = _northVelocity = _forwardVelocity = 0;
        IsWaitingForTrack = IsWaitingForWater = false;
        Route = route; _track = track; Vehicle = vehicle;
        Reset();
    }

    /// <summary>Install a custom vehicle compatible with the current route, such as a kayak or a tram.</summary>
    public void SelectVehicle(VehicleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.IsValid || !IsFreeRoaming && (Route?.Mode != profile.Mode || profile.Motion == MotionModel.FreeFlight))
            throw new ArgumentException("Profile is invalid or incompatible with the route.", nameof(profile));
        if (IsFreeRoaming)
        {
            StartFreeRoam(profile, _position, Bearing, AltitudeMeters);
            return;
        }
        Vehicle = profile;
        Reset();
    }

    public void Reset()
    {
        if (IsFreeRoaming) { ResetFreeRoam(); return; }
        Continuity++;
        State = JourneyState.Ready; _resumeState = JourneyState.Ready;
        DistanceMeters = RunDistanceMeters = ElapsedSeconds = AnimationSeconds = OffRouteMeters = 0;
        _accumulator = _yawRate = _contactCooldown = SpeedMetersPerSecond = AltitudeMeters = GroundElevationMeters = 0;
        CheckpointsPassed = Score = 0;
        CheckpointsTotal = _track is { } t ? Math.Clamp((int)Math.Ceiling(t.Length / 350), 3, 24) : 0;
        NextCheckpoint = null;
        ClearInput(); Cruise = false; PlaceOnTrack(); Notify();
    }

    public void Preview() => Start(JourneyState.Preview);
    public void Drive() => Start(JourneyState.Driving);
    private void Start(JourneyState mode)
    {
        if (IsFreeRoaming)
        {
            if (mode == JourneyState.Driving) Resume();
            return;
        }
        if (_track is null) return;
        if (State == JourneyState.Finished) Reset();
        if (State == JourneyState.Paused && _resumeState == mode) { Resume(); return; }
        if (State == mode) return;
        bool fromGame = State == JourneyState.Driving || State == JourneyState.Paused && _resumeState == JourneyState.Driving;
        if (mode == JourneyState.Driving && !fromGame)
        {
            // Starting a game begins a new scored run. A preview seek cannot earn checkpoints.
            Reset();
        }
        if (mode == JourneyState.Preview)
        {
            PlaceOnTrack(); AltitudeMeters = GroundElevationMeters + FlightEnvelope(); OffRouteMeters = 0;
        }
        NextCheckpoint = mode == JourneyState.Driving ? _track.Sample(NextStation) : null;
        State = _resumeState = mode; _accumulator = 0; _follow = true; ClearInput();
        Cue?.Invoke(JourneyCue.Depart); Notify();
    }

    public void Pause()
    {
        if (!Active) return;
        _resumeState = State; State = JourneyState.Paused; _accumulator = 0; ClearInput(); Notify();
    }
    public void Resume()
    {
        if (State != JourneyState.Paused) return;
        State = _resumeState; _accumulator = 0; Notify();
    }
    public void Seek(double fraction)
    {
        if (_track is null || !double.IsFinite(fraction)) return;
        Continuity++; NextCheckpoint = null;
        // Scrubbing enters unscored preview. Manual mode always starts a fresh challenge.
        bool playing = State == JourneyState.Preview;
        State = playing ? JourneyState.Preview : JourneyState.Paused; _resumeState = JourneyState.Preview;
        DistanceMeters = _track.Length * Math.Clamp(fraction, 0, 1); OffRouteMeters = 0;
        SpeedMetersPerSecond = _yawRate = _accumulator = 0; ClearInput(); PlaceOnTrack();
        AltitudeMeters = GroundElevationMeters + FlightEnvelope(); AnimationSeconds = Progress * PreviewSeconds; Notify();
    }

    public void Respawn()
    {
        if (IsFreeRoaming) { ResetFreeRoam(); return; }
        if (!IsScoredRun || _track is null) return;
        Continuity++; ClearInput();
        DistanceMeters = _track.Length * CheckpointsPassed / CheckpointsTotal;
        OffRouteMeters = SpeedMetersPerSecond = _yawRate = 0;
        PlaceOnTrack(); AltitudeMeters = GroundElevationMeters + FlightEnvelope(); Score = Math.Max(0, Score - 25);
        Notify();
    }

    /// <summary>Refresh a ready, paused or active pose after the world's terrain changes.
    /// Airborne manual vehicles retain their absolute altitude when the surface falls away.</summary>
    public void RefreshSurface()
    {
        if (IsFreeRoaming) { RefreshFreeRoamSurface(); return; }
        if (_track is null) return;
        double previousGround = GroundElevationMeters, previousAltitude = AltitudeMeters;
        GroundElevationMeters = SurfaceElevationAt(_position);
        if (Vehicle.Motion != MotionModel.Aircraft || State == JourneyState.Ready || previousAltitude <= previousGround + .000001)
            AltitudeMeters = GroundElevationMeters;
        else if (_resumeState == JourneyState.Preview)
            AltitudeMeters = GroundElevationMeters + FlightEnvelope();
        else
            AltitudeMeters = Math.Max(AltitudeMeters, GroundElevationMeters);
        if (previousAltitude == AltitudeMeters && previousGround == GroundElevationMeters) return;
        Continuity++;
        Notify();
    }

    public void SetCruise(bool enabled)
    {
        if (IsFreeRoaming) return;
        if (Cruise != enabled) { Cruise = enabled; Notify(); }
    }
    public void SetInput(double throttle, double steering, double brake)
    {
        _throttle = Clip(throttle, 0, 1); _steer = Clip(steering, -1, 1); _brake = Clip(brake, 0, 1);
        if (IsFreeRoaming) SetFreeRoamInput(_freeInput with { Forward = _throttle, Turn = _steer, Brake = _brake });
    }
    public void SetLift(double vertical)
    {
        _lift = Clip(vertical, -1, 1);
        if (IsFreeRoaming) _freeInput = _freeInput with { Vertical = _lift };
    }
    public void ClearInput()
    {
        _throttle = _steer = _brake = _lift = 0;
        _freeInput = default;
        _jumpRequested = _jumpHeld = false;
    }
    private static double Clip(double x, double min, double max) => double.IsFinite(x) ? Math.Clamp(x, min, max) : 0;
    public void Horn() { if (Route is not null || IsFreeRoaming) Cue?.Invoke(JourneyCue.Horn); }
    public void Tick(double wallSeconds)
    {
        if (!Active || _track is null && !IsFreeRoaming || !double.IsFinite(wallSeconds) || wallSeconds <= 0) return;
        _accumulator += Math.Min(.25, wallSeconds) * _rate;
        while (_accumulator + 1e-12 >= Step && Active)
        {
            _accumulator -= Step;
            ElapsedSeconds += Step; AnimationSeconds += Step; _contactCooldown = Math.Max(0, _contactCooldown - Step);
            if (IsFreeRoaming) FreeRoamStep();
            else if (State == JourneyState.Preview) PreviewStep();
            else DriveStep();
        }
        Notify();
    }

    private double PreviewSeconds => Math.Clamp((Route?.Duration.TotalSeconds ?? 60) / 5, 12, 80);
    private void PreviewStep()
    {
        SpeedMetersPerSecond = _track!.Length / PreviewSeconds;
        DistanceMeters = Math.Min(_track.Length, DistanceMeters + SpeedMetersPerSecond * Step);
        PlaceOnTrack(); AltitudeMeters = GroundElevationMeters + FlightEnvelope();
        if (DistanceMeters >= _track.Length) Finish(false);
    }

    private void DriveStep()
    {
        var track = _track!; var v = Vehicle;
        double speed = SpeedMetersPerSecond;
        double targetSpeed = v.CruiseSpeed;
        if (Cruise)
        {
            var ahead = track.Sample(Math.Min(track.Length, DistanceMeters + Math.Max(8, speed * 2)));
            double turn = Math.Abs(RoutePoint.Wrap(ahead.Bearing - Bearing));
            if (v.Motion is MotionModel.Ground or MotionModel.Rail)
                targetSpeed /= 1 + turn / 110;
            targetSpeed = Math.Min(targetSpeed, Math.Max(1, Math.Sqrt(2 * v.Braking * (track.Length - DistanceMeters))));
        }
        double engine = Cruise ? Math.Clamp((targetSpeed - speed) * .7, 0, 1) : _throttle;
        double brake = Cruise ? Math.Max(_brake, Math.Clamp((speed - targetSpeed) * .45, 0, 1)) : _brake;
        double loss = v.CoastDrag + (v.Acceleration - v.CoastDrag) * Math.Pow(speed / v.MaxSpeed, 2);
        SpeedMetersPerSecond = Math.Clamp(speed + (engine * v.Acceleration - loss - brake * v.Braking) * Step, 0, v.MaxSpeed);
        if (v.Motion == MotionModel.Aircraft)
        {
            MoveVertically();
        }
        double ds = (speed + SpeedMetersPerSecond) * .5 * Step;
        if (Cruise || v.Motion == MotionModel.Rail)
        {
            if (Cruise && OffRouteMeters > 2)
            {
                var target = track.Sample(Math.Min(track.Length, DistanceMeters + Math.Max(10, 2 * speed))).Point;
                MoveFree(_position.BearingTo(target), ds);
            }
            else
            {
                MoveOnTrack(ds);
            }
        }
        else
        {
            double authority = v.Motion == MotionModel.Pedestrian ? 1 : Math.Min(1, speed / 3);
            double desired = _steer * v.TurnRate * authority / (1 + speed / v.CruiseSpeed);
            _yawRate += (desired - _yawRate) * (1 - Math.Exp(-v.Response * Step));
            MoveFree(RoutePoint.Wrap(Bearing + _yawRate * Step), ds);
        }
        double gateRadius = v.Motion == MotionModel.Aircraft ? 140 : 24;
        if (NextCheckpoint is { } gate && DistanceMeters + .001 >= NextStation &&
            _position.DistanceTo(gate.Point) <= Math.Min(gateRadius, Math.Max(2, track.Length / CheckpointsTotal * .3)))
        {
            CheckpointsPassed++; Score += 100;
            NextCheckpoint = CheckpointsPassed < CheckpointsTotal ? track.Sample(NextStation) : null;
            Cue?.Invoke(JourneyCue.Checkpoint);
        }
        if (CheckpointsPassed == CheckpointsTotal && Progress >= 1 - 1e-8 && OffRouteMeters <= gateRadius) Finish(true);
    }

    private void MoveFree(double bearing, double ds)
    {
        Bearing = bearing;
        if (ds <= 0) return;
        var next = _position.Move(bearing, ds);
        double altitude = AltitudeAtMove(next, ds, out double surface);
        if (World?.Blocks(Vehicle, _position, next, AltitudeMeters, altitude) == true)
        {
            Contact();
            return;
        }
        _position = next; GroundElevationMeters = surface; AltitudeMeters = altitude; RunDistanceMeters += ds;
        var hit = _track!.Nearest(next, DistanceMeters, Math.Max(35, SpeedMetersPerSecond * 2));
        DistanceMeters = hit.Station; OffRouteMeters = hit.Error;
    }

    private void MoveOnTrack(double distance)
    {
        double station = Math.Min(_track!.Length, DistanceMeters + distance);
        var sample = _track.Sample(station);
        double altitude = AltitudeAtMove(sample.Point, _position.DistanceTo(sample.Point), out double surface);
        if (Vehicle.Motion == MotionModel.Aircraft && World?.Blocks(Vehicle, _position, sample.Point, AltitudeMeters, altitude) == true)
        {
            Contact();
            return;
        }
        DistanceMeters = station; _position = sample.Point; Bearing = sample.Bearing;
        GroundElevationMeters = surface; AltitudeMeters = altitude;
        OffRouteMeters = 0; RunDistanceMeters += distance;
    }

    private void MoveVertically()
    {
        double climb = Cruise ? Math.Clamp((GroundElevationMeters + FlightEnvelope() - AltitudeMeters) * .18, -12, 18) : _lift * 20;
        double airspeed = Math.Clamp((SpeedMetersPerSecond - 12) / 20, 0, 1);
        double proposed = AltitudeMeters + (climb * airspeed - (1 - airspeed) * 2.5) * Step;
        double ceiling = Math.Max(4200, GroundElevationMeters + 4200);
        // A valley opening beneath an airborne vehicle cannot lower its absolute pose.
        double next = Math.Clamp(proposed, GroundElevationMeters, Math.Max(ceiling, AltitudeMeters));
        if (World?.Blocks(Vehicle, _position, _position, AltitudeMeters, next) == true)
        {
            Contact();
            return;
        }
        bool landed = AltitudeMeters > GroundElevationMeters + .000001 && proposed <= GroundElevationMeters;
        AltitudeMeters = next;
        if (landed) Contact();
    }

    private double AltitudeAtMove(RoutePoint next, double distance, out double surface)
    {
        surface = SurfaceElevationAt(next);
        if (Vehicle.Motion != MotionModel.Aircraft) return surface;
        // Grounded aircraft can taxi along a gentle surface. A drop leaves the aircraft airborne.
        bool taxi = AltitudeMeters <= GroundElevationMeters + .000001 &&
            Math.Abs(surface - GroundElevationMeters) <= distance * .35 + .02;
        return taxi ? surface : AltitudeMeters;
    }

    private double SurfaceElevationAt(RoutePoint point)
    {
        double elevation = World?.SurfaceElevationMeters(Vehicle, point) ?? 0;
        return double.IsFinite(elevation) ? elevation : 0;
    }

    private void Contact()
    {
        SpeedMetersPerSecond *= .2;
        if (_contactCooldown > 0) return;
        Score = Math.Max(0, Score - 25); _contactCooldown = 1; Cue?.Invoke(JourneyCue.Contact);
    }

    private double FlightEnvelope() => Vehicle.Motion == MotionModel.Aircraft && _track is { } track
        ? Math.Min(2500, Math.Min(track.Length * .12, Math.Min(DistanceMeters, track.Length - DistanceMeters) * .18)) : 0;
    private void PlaceOnTrack()
    {
        if (_track is null)
        {
            _position = default; Bearing = AltitudeMeters = GroundElevationMeters = 0;
            return;
        }
        var sample = _track.Sample(DistanceMeters); _position = sample.Point; Bearing = sample.Bearing;
        GroundElevationMeters = SurfaceElevationAt(_position);
        if (Vehicle.Motion != MotionModel.Aircraft || State == JourneyState.Ready) AltitudeMeters = GroundElevationMeters;
    }
    private void Finish(bool scored)
    {
        State = JourneyState.Finished; SpeedMetersPerSecond = 0; _accumulator = 0;
        if (scored) Score += 250;
        ClearInput(); Cue?.Invoke(JourneyCue.Finish);
    }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
