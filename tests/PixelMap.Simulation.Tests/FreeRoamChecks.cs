using PixelMap.Audio;
using PixelMap.Data;
using PixelMap.Routing;
using PixelMap.Simulation;

internal static class FreeRoamChecks
{
    public static void Run()
    {
        UnboundedWorldMotion();
        DeterministicInputs();
        CharacterContacts();
        VehicleAndFlightMotion();
        WaterNavigation();
        RailContinuations();
        SessionLifecycle();
        Console.WriteLine("Free-roam movement, reverse, jump, gravity, sliding, flight, water, streamed rail and lifecycle checks passed.");
    }

    private static void UnboundedWorldMotion()
    {
        var oldRoute = RouteDemos.ForMode(TravelMode.Drive);
        var session = new JourneySession { World = new SurfaceWorld(_ => 600) };
        session.Load(oldRoute);
        session.StartFreeRoam(VehicleProfile.Car, new(0, 179.9998), 90);
        session.SetFreeRoamInput(new(Forward: 1));
        Advance(session, 60 * 120);
        Check(session.Route is null && session.IsFreeRoaming && session.State == JourneyState.Driving,
            "Free movement has no route or automatic destination finish.");
        Check(session.RunDistanceMeters > oldRoute.DistanceMeters * 3 && session.Longitude < -179.9,
            "A free vehicle travels beyond the former route corridor and crosses the antimeridian.");
        Check(session.AltitudeMeters == 600 && session.OffRouteMeters == 0 && session.Progress == 0 &&
            session.NextCheckpoint is null && session.CheckpointsTotal == 0 && !session.IsScoredRun && session.Score == 0,
            "Free travel retains terrain support while remaining independent of route scoring.");

        var walker = new JourneySession();
        walker.StartFreeRoam(VehicleProfile.Walker, new(0, 0));
        walker.SetLook(90, 120);
        walker.SetFreeRoamInput(new(Forward: 1, Boost: true));
        Advance(walker, 60 * 20);
        Check(walker.Longitude > 0 && Math.Abs(walker.Latitude) < .000001 && walker.RunDistanceMeters > 60,
            "Walk direction follows free look while pitch does not change grounded speed.");
    }

    private static void DeterministicInputs()
    {
        foreach (var profile in new[] { VehicleProfile.Walker, VehicleProfile.Car, VehicleProfile.Bike,
            VehicleProfile.Boat, VehicleProfile.Plane, VehicleProfile.FreeCamera })
        {
            JourneySession Create()
            {
                var session = new JourneySession { World = new SurfaceWorld(_ => 1000) };
                session.StartFreeRoam(profile, new(45.8, 10.8), 30, 1100);
                session.SetLook(30, 100);
                session.SetFreeRoamInput(new(.8, .4, .2, .3, Boost: true));
                return session;
            }
            var first = Create();
            var second = Create();
            Advance(first, 600);
            for (int i = 0; i < 150; i++) second.Tick(1d / 15);
            Near(first.Latitude, second.Latitude, 1e-10, profile.Id + " latitude is fixed-step deterministic.");
            Near(first.Longitude, second.Longitude, 1e-10, profile.Id + " longitude is fixed-step deterministic.");
            Near(first.AltitudeMeters, second.AltitudeMeters, 1e-8, profile.Id + " altitude is fixed-step deterministic.");
            Near(first.SpeedMetersPerSecond, second.SpeedMetersPerSecond, 1e-8, profile.Id + " speed is fixed-step deterministic.");
            Check(first.Route is null && first.CheckpointsTotal == 0, profile.Id + " has no synthetic route dependency.");
        }
    }

    private static void CharacterContacts()
    {
        var session = new JourneySession { World = new SurfaceWorld(_ => 250) };
        session.StartFreeRoam(VehicleProfile.Walker, new(0, 0));
        session.SetFreeRoamInput(new(Jump: true));
        double maximum = session.AltitudeMeters;
        for (int i = 0; i < 180; i++)
        {
            session.SetFreeRoamInput(new(Jump: true));
            session.Tick(1d / 60);
            maximum = Math.Max(maximum, session.AltitudeMeters);
        }
        Check(maximum > 251 && session.IsGrounded && session.AltitudeMeters == 250,
            "Jump rises and lands without repeated jumps while the button stays held.");
        Advance(session, 120);
        Check(session.AltitudeMeters == 250, "Holding jump after landing cannot auto-bunnyhop.");
        session.SetFreeRoamInput(default);
        session.SetFreeRoamInput(new(Jump: true));
        session.Tick(1d / 60);
        Check(session.AltitudeMeters > 250, "Releasing and pressing jump permits another jump.");

        double wallLongitude = new RoutePoint(0, 0).Move(90, 2).Longitude;
        var wall = new SurfaceWorld(_ => 0) { Obstructs = (_, to, _, _) => to.Longitude > wallLongitude };
        var sliding = new JourneySession { World = wall };
        sliding.StartFreeRoam(VehicleProfile.Walker, new(0, 0));
        sliding.SetFreeRoamInput(new(Forward: 1, Strafe: 1));
        Advance(sliding, 600);
        Check(sliding.Longitude <= wallLongitude && sliding.Latitude > new RoutePoint(0, 0).Move(0, 8).Latitude,
            "A diagonal walking contact slides along the wall without passing through it.");

        double edge = new RoutePoint(0, 0).Move(90, 3).Longitude;
        var drop = new SurfaceWorld(point => point.Longitude < edge ? 100 : 0);
        var falling = new JourneySession { World = drop };
        falling.StartFreeRoam(VehicleProfile.Walker, new(0, 0), 90);
        falling.SetFreeRoamInput(new(Forward: 1));
        bool wasFalling = false;
        for (int i = 0; i < 600; i++)
        {
            falling.Tick(1d / 60);
            wasFalling |= falling.AltitudeAboveGroundMeters > 50 && falling.VerticalSpeedMetersPerSecond < 0;
        }
        Check(wasFalling && falling.AltitudeMeters == 0 && falling.IsGrounded,
            "A walk off a cliff falls under gravity and lands on the lower surface.");

        var cliff = new SurfaceWorld(point => point.Longitude < edge ? 0 : 100);
        var climbing = new JourneySession { World = cliff };
        climbing.StartFreeRoam(VehicleProfile.Walker, new(0, 0), 90);
        climbing.SetFreeRoamInput(new(Forward: 1, Jump: true));
        Advance(climbing, 600);
        Check(climbing.Longitude < edge && climbing.AltitudeMeters < 2,
            "A jump cannot teleport the character onto a high terrain wall.");
    }

    private static void VehicleAndFlightMotion()
    {
        var car = new JourneySession();
        car.StartFreeRoam(VehicleProfile.Car, new(0, 0));
        car.SetFreeRoamInput(new(Forward: 1));
        Advance(car, 600);
        double furthest = car.Latitude;
        car.SetFreeRoamInput(new(Forward: -1));
        Advance(car, 1200);
        Check(car.SignedSpeedMetersPerSecond < -1 && car.Latitude < furthest && car.Bearing == 0,
            "Holding reverse brakes, reverses the drivetrain and preserves the chassis heading.");
        car.SetFreeRoamInput(new(Forward: -1, Turn: 1));
        Advance(car, 120);
        Check(car.Bearing < 0, "Reverse steering changes the chassis heading with the correct sign.");

        var flight = new JourneySession { World = new SurfaceWorld(_ => 1000) };
        flight.StartFreeRoam(VehicleProfile.Plane, new(0, 0), 0, 1100);
        flight.SetFreeRoamInput(new(Forward: 1, Vertical: 1));
        Advance(flight, 60 * 300);
        Check(flight.AltitudeMeters > 6000 && flight.AltitudeAboveGroundMeters > 5000 && flight.Active,
            "Free aircraft can climb beyond the route-mode altitude ceiling without finishing a route.");

        var free = new JourneySession { World = new SurfaceWorld(_ => -50) };
        free.StartFreeFlight(new(0, 0), 100, 0);
        free.SetLook(0, 135);
        free.SetFreeRoamInput(new(Forward: 1, Strafe: 1, Boost: true));
        Advance(free, 180);
        Check(free.Latitude > 0 && free.Longitude > 0 && free.AltitudeMeters > 200 && free.IsFreeFlying,
            "Six-axis flight combines the upward look ray and lateral movement.");
        Check(free.SpeedMetersPerSecond <= free.FreeFlightSpeed * 4 + .000001,
            "Diagonal flight normalizes its three-dimensional movement vector.");
        free.SetFreeRoamInput(new(Vertical: -1));
        Advance(free, 1200);
        Near(free.AltitudeMeters, -49.85, 1e-8, "Free-camera descent retains clearance above signed terrain.");

        double wall = new RoutePoint(0, 0).Move(0, 20).Latitude;
        foreach (var profile in new[] { VehicleProfile.Plane, VehicleProfile.FreeCamera })
        {
            var ridge = new JourneySession { World = new SurfaceWorld(point => point.Latitude < wall ? 0 : 300) };
            ridge.StartFreeRoam(profile, new(0, 0), 0, 50);
            ridge.SetLook(0, 90);
            ridge.SetFreeRoamInput(new(Forward: 1));
            Advance(ridge, 600);
            Check(ridge.Latitude < wall && ridge.AltitudeMeters < 100,
                profile.Id + " meets rising terrain without teleporting to its top.");
        }
    }

    private static void WaterNavigation()
    {
        var id = new TileId(14, 8192, 8192);
        var water = new MapFeature("water", "lake", "", "Water", 1, 4096, GeometryType.Polygon,
            [[new(1500, 500), new(2400, 500), new(2400, 3500), new(1500, 3500)]], null, null, false, 0, 0, "");
        var tile = new MapTile(id, [water]);
        var world = new VectorCollisionWorld();
        world.Update([tile]);
        var land = At(id, 1000, 2000);
        var wet = At(id, 2000, 2000);
        Check(!world.IsNavigableWater(land) && world.IsNavigableWater(wet),
            "Navigable-water queries use the resident water polygon index.");
        Check(!world.Blocks(VehicleProfile.Boat, land, wet, 0),
            "Planned-journey contacts retain the existing escape from an obstructed starting pose.");

        var boat = new JourneySession { World = world };
        boat.StartFreeRoam(VehicleProfile.Boat, land, 90);
        Check(boat.IsWaitingForWater, "A dry free-boat spawn reports its waiting state immediately.");
        boat.SetFreeRoamInput(new(Forward: 1, Turn: 1));
        Advance(boat, 300);
        Check(new RoutePoint(boat.Latitude, boat.Longitude) == land && boat.RunDistanceMeters == 0 &&
            boat.SpeedMetersPerSecond == 0 && boat.Bearing == 90 && boat.IsWaitingForWater,
            "A free boat on dry land holds position and heading while controls remain held.");
        boat.StartFreeRoam(VehicleProfile.Boat, wet, 90);
        boat.SetFreeRoamInput(new(Forward: 1));
        Advance(boat, 600);
        Check(boat.RunDistanceMeters > 30 && boat.SpeedMetersPerSecond > 0 && !boat.IsWaitingForWater,
            "A free vessel accelerates across mapped water without a route.");
        Advance(boat, 1800);
        Check(boat.Longitude < At(id, 2400, 2000).Longitude && boat.IsWaitingForWater && boat.SpeedMetersPerSecond == 0,
            "The water edge stops free sailing before the vessel enters dry land.");
        double stoppedLongitude = boat.Longitude;
        Advance(boat, 120);
        Check(boat.Longitude == stoppedLongitude, "Held throttle cannot escape through a shoreline contact.");
        boat.SetFreeRoamInput(new(Forward: -1));
        Advance(boat, 120);
        Check(boat.Longitude < stoppedLongitude && !boat.IsWaitingForWater && boat.SignedSpeedMetersPerSecond < 0,
            "A shoreline contact can reverse back into navigable water.");

        world.Update([]);
        boat.StartFreeRoam(VehicleProfile.Boat, wet, 0);
        boat.SetFreeRoamInput(new(Forward: 1));
        Advance(boat, 120);
        Check(boat.IsWaitingForWater && boat.RunDistanceMeters == 0,
            "A free boat waits for detailed water coverage before moving.");
        world.Update([tile]);
        boat.RefreshSurface();
        Check(!boat.IsWaitingForWater, "Loaded water coverage clears waiting even when its elevation is unchanged.");
        Advance(boat, 120);
        Check(boat.RunDistanceMeters > 1 && boat.SpeedMetersPerSecond > 0,
            "The held vessel input resumes when mapped water arrives.");
        var position = new RoutePoint(boat.Latitude, boat.Longitude);
        world.Update([]);
        boat.Tick(1d / 60);
        Check(boat.IsWaitingForWater && boat.SpeedMetersPerSecond == 0 &&
            new RoutePoint(boat.Latitude, boat.Longitude) == position,
            "Loss of water coverage holds the vessel at its last known navigable position.");
        boat.Pause();
        world.Update([tile]);
        boat.RefreshSurface();
        Check(!boat.IsWaitingForWater && boat.State == JourneyState.Paused,
            "Coverage refresh updates a paused boat without resuming its clock.");
        boat.StartFreeFlight(position, 20);
        Check(!boat.IsWaitingForWater && new RoutePoint(boat.Latitude, boat.Longitude) == position && boat.State == JourneyState.Paused,
            "Switching from a waiting vessel to free flight preserves position and pause state.");
        boat.Load(null);
        Check(!boat.IsWaitingForWater, "Unloading clears the vessel waiting state.");

        world.Update([new MapTile(new TileId(0, 0, 0), [water])]);
        Check(!world.IsNavigableWater(wet), "Coarse overview coverage cannot authorize free water navigation.");
        var island = water with { Paths = [water.Paths[0], [new(1900, 1900), new(2100, 1900), new(2100, 2100), new(1900, 2100)]] };
        world.Update([new(id, [island])]);
        Check(!world.IsNavigableWater(wet) && world.IsNavigableWater(At(id, 2200, 2000)),
            "Water navigation preserves islands represented by polygon holes.");
    }

    private static void RailContinuations()
    {
        var id = new TileId(14, 8192, 8192);
        var world = new VectorCollisionWorld();
        var approach = Rail([new(1000, 2000), new(1800, 2000)]);
        var straight = Rail([new(1800, 2000), new(3000, 2000)]);
        var left = Rail([new(1800, 2000), new(2600, 1200)]);
        var right = Rail([new(1800, 2000), new(2600, 2800)]);
        var tile = new MapTile(id, [approach, straight, left, right]);
        world.Update([tile]);
        var start = At(id, 1700, 2000);
        Check(world.TryMoveOnRails(start, 90, 120, 0, out var through, out var forward) &&
            Math.Abs(through.Latitude - start.Latitude) < .000001 && Math.Abs(forward - 90) < .01,
            "Neutral steering follows the straight continuation through a rail junction.");
        Check(world.TryMoveOnRails(start, 90, 120, -1, out var turnLeft, out _) && turnLeft.Latitude > start.Latitude,
            "Left steering chooses a connected left rail branch.");
        Check(world.TryMoveOnRails(start, 90, 120, 1, out var turnRight, out _) && turnRight.Latitude < start.Latitude,
            "Right steering chooses a connected right rail branch.");

        var train = new JourneySession { World = world };
        train.StartFreeRoam(VehicleProfile.Train, At(id, 1000, 2100), 90);
        Near(train.Latitude, At(id, 1000, 2000).Latitude, 1e-7, "Train spawn snaps to a nearby physical track.");
        train.SetFreeRoamInput(new(Forward: 1));
        Advance(train, 1200);
        Check(train.RunDistanceMeters > 200 && !train.IsWaitingForTrack && train.Route is null && train.Active,
            "A free train follows streamed tracks without a planned route.");
        var oldPoint = new RoutePoint(train.Latitude, train.Longitude);
        world.Update([]);
        train.Tick(1d / 60);
        Check(train.IsWaitingForTrack && train.SpeedMetersPerSecond == 0 && oldPoint == new RoutePoint(train.Latitude, train.Longitude),
            "Missing track coverage holds the train safely at its current world position.");
        world.Update([tile]);
        Advance(train, 60);
        Check(!train.IsWaitingForTrack && train.SpeedMetersPerSecond > 0,
            "Streaming the track back resumes the held rail input.");

        var nextId = new TileId(14, 8193, 8192);
        world.Update([new(id, [Rail([new(4000, 2000), new(4196, 2000)])]),
            new(nextId, [Rail([new(-96, 2000), new(300, 2000)])])]);
        Check(world.TryMoveOnRails(At(id, 4050, 2000), 90, 80, 0, out var across, out _) && across.Longitude > At(nextId, 0, 2000).Longitude,
            "Buffered rails join at clipped neighboring tile boundaries.");
        world.Update([new(id, [Rail([new(4000, 2000), new(4196, 2000)])]),
            new(nextId, [Rail([new(-96, 2000), new(300, 2000)]) with { Level = 1 }])]);
        world.TryMoveOnRails(At(id, 4050, 2000), 90, 80, 0, out var separateLevel, out _);
        Check(separateLevel.Longitude <= At(nextId, 0, 2000).Longitude + .0000001,
            "Track endpoints at different levels cannot become an accidental junction.");
        RailSnapshotReuse();
    }

    private static void RailSnapshotReuse()
    {
        var id = new TileId(14, 8192, 8192);
        var otherId = new TileId(14, 8193, 8192);
        var track = Rail([new(1000, 2000), new(3000, 2000)]);
        var tile = new MapTile(id, [track]);
        var other = new MapTile(otherId, [track]);
        var world = new VectorCollisionWorld();
        RoutePoint start = At(id, 1700, 2000), otherStart = At(otherId, 1700, 2000);
        world.Update([tile, other]);
        Check(world.TryMoveOnRails(start, 90, .5, 0, out var expected, out var expectedHeading),
            "Both resident source tiles contribute to the rail graph.");
        var networkField = typeof(VectorCollisionWorld).GetField("_railNetwork",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var original = networkField.GetValue(world);
        world.Update([other, tile]);
        Check(ReferenceEquals(original, networkField.GetValue(world)), "Reordering resident sources retains the rail graph.");
        for (int i = 0; i < 32; i++) world.TryMoveOnRails(start, 90, .5, 0, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        bool same = true;
        for (int i = 0; i < 256; i++)
            same &= world.TryMoveOnRails(start, 90, .5, 0, out var point, out var heading) &&
                point == expected && heading == expectedHeading;
        Check(same && GC.GetAllocatedBytesForCurrentThread() == allocated,
            "Reordered source snapshots preserve train movement without allocating per-step membership sets.");

        var replacement = tile with { };
        Check(replacement == tile && !ReferenceEquals(replacement, tile), "The replacement has equal data and a distinct immutable identity.");
        world.Update([other, replacement]);
        Check(world.TryMoveOnRails(start, 90, .5, 0, out _, out _) && !ReferenceEquals(original, networkField.GetValue(world)),
            "An equal-valued replacement tile rebuilds the rail graph using instance identity.");

        world.Update([tile, other]);
        Check(world.TryMoveOnRails(otherStart, 90, .5, 0, out _, out _), "The second source initially has navigable rails.");
        world.Update([tile, tile]);
        Check(!world.TryMoveOnRails(otherStart, 90, .5, 0, out _, out _) &&
            world.TryMoveOnRails(start, 90, .5, 0, out _, out _),
            "A duplicated remaining source cannot preserve rails from a removed tile.");
        original = networkField.GetValue(world);
        world.TryMoveOnRails(start, 90, .5, 0, out _, out _);
        Check(ReferenceEquals(original, networkField.GetValue(world)), "An unchanged duplicate source sequence retains its graph.");

        MapTile[] mutableSources = [tile, other];
        world.Update(mutableSources);
        Check(world.TryMoveOnRails(otherStart, 90, .5, 0, out _, out _), "The mutable source list initially contains both rail tiles.");
        mutableSources[1] = new(otherId, []);
        Check(!world.TryMoveOnRails(otherStart, 90, .5, 0, out _, out _),
            "A changed caller-owned source sequence cannot reuse a stale matched order.");
    }

    private static void SessionLifecycle()
    {
        var surface = new SurfaceWorld(_ => 400);
        var session = new JourneySession { World = surface };
        session.StartFreeRoam(VehicleProfile.Walker, new(45, 10));
        session.SetFreeRoamInput(new(Forward: 1, Jump: true));
        Advance(session, 20);
        session.Pause();
        var pose = new RoutePoint(session.Latitude, session.Longitude);
        double altitude = session.AltitudeMeters, clock = session.AnimationSeconds;
        session.Tick(.25);
        Check(new RoutePoint(session.Latitude, session.Longitude) == pose && session.AltitudeMeters == altitude && session.AnimationSeconds == clock,
            "Pausing freezes the free-roam pose and animation clock.");
        session.SelectVehicle(VehicleProfile.Car with { Id = "custom-free-car", MaxSpeed = 30 });
        Check(session.State == JourneyState.Paused && new RoutePoint(session.Latitude, session.Longitude) == pose && session.Vehicle.Id == "custom-free-car",
            "Changing a free-roam profile preserves pause state and world position.");
        session.Resume();
        Advance(session, 30);
        Check(session.SpeedMetersPerSecond == 0, "Mode changes and pause release continuous input.");
        session.SetFreeRoamInput(new(Forward: 1));
        Advance(session, 120);
        session.Pause();
        session.Reset();
        Check(session.State == JourneyState.Paused && new RoutePoint(session.Latitude, session.Longitude) == pose && session.RunDistanceMeters == 0,
            "A paused respawn restores its free spawn without resuming the clock.");
        surface.Sample = _ => 700;
        session.RefreshSurface();
        Check(session.AltitudeMeters == 700 && session.GroundElevationMeters == 700,
            "New elevation data updates a paused surface-supported game pose.");

        long continuity = session.Continuity;
        Throws<ArgumentException>(() => session.StartFreeRoam(VehicleProfile.Car with { Acceleration = double.NaN }, new(0, 0)));
        Throws<ArgumentOutOfRangeException>(() => session.StartFreeRoam(VehicleProfile.Car, new(double.NaN, 0)));
        Throws<ArgumentOutOfRangeException>(() => session.StartFreeRoam(VehicleProfile.Car, new(0, 0), double.NaN));
        Check(session.Continuity == continuity && session.State == JourneyState.Paused,
            "Invalid free-roam start requests leave the prior session intact.");
        session.Resume();
        session.SetFreeRoamInput(new(double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.NaN, double.PositiveInfinity));
        Advance(session, 30);
        Check(session.SpeedMetersPerSecond == 0 && double.IsFinite(session.AltitudeMeters), "Nonfinite analog input is neutralized.");

        var audio = new AudioProbe();
        using var sound = new JourneySound(audio);
        session.Muted = false;
        sound.Update(session);
        Check(audio.Enabled && audio.Running && audio.Instrument == "Drive", "Free-roam vehicles use the established audio device.");
        sound.Play(session, JourneyCue.Horn);
        Check(audio.Cues == 1, "Free-roam horns are available without a route.");
        session.Pause();
        sound.Update(session);
        Check(!audio.Running, "Paused free-roam audio releases the continuous engine bed.");
        session.Load(null);
        Check(!session.IsFreeRoaming && !session.Active && session.State == JourneyState.Ready && session.AltitudeMeters == 0,
            "Unloading clears the full free-roam state.");
        session.Load(RouteDemos.ForMode(TravelMode.Bike));
        session.Drive();
        Check(session.Route is not null && !session.IsFreeRoaming && session.IsScoredRun,
            "A planned journey starts normally after free roam.");
    }

    private static MapFeature Rail(TilePoint[] points) =>
        new("transportation", "rail", "rail", "Track", 1, 4096, GeometryType.LineString, [points], null, null, false, 0, 0, "");

    private static RoutePoint At(TileId id, double x, double y)
    {
        double count = 1 << id.Z;
        return new(Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (id.Y + y / 4096) / count))) * 180 / Math.PI,
            (id.X + x / 4096) / count * 360 - 180);
    }

    private static void Advance(JourneySession session, int ticks)
    {
        for (int i = 0; i < ticks; i++) session.Tick(1d / 60);
    }

    private static void Near(double actual, double expected, double tolerance, string message)
        => Check(Math.Abs(actual - expected) <= tolerance, $"{message} Expected {expected:R}, actual {actual:R}.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class SurfaceWorld(Func<RoutePoint, double> sample) : IJourneyWorld, IFreeRoamWaterWorld
    {
        public Func<RoutePoint, double> Sample = sample;
        public Func<RoutePoint, RoutePoint, double, double, bool>? Obstructs;
        public double SurfaceElevationMeters(VehicleProfile vehicle, RoutePoint point) => Sample(point);
        public bool IsNavigableWater(RoutePoint point) => true;
        public bool Blocks(VehicleProfile vehicle, RoutePoint from, RoutePoint to, double altitudeMeters)
            => Obstructs?.Invoke(from, to, altitudeMeters, altitudeMeters) == true;
        public bool Blocks(VehicleProfile vehicle, RoutePoint from, RoutePoint to, double fromAltitudeMeters, double toAltitudeMeters)
            => Obstructs?.Invoke(from, to, fromAltitudeMeters, toAltitudeMeters) == true;
    }

    private sealed class AudioProbe : IJourneyAudioDevice
    {
        public bool Enabled, Running;
        public int Cues;
        public string Instrument = "";
        public void Enable(bool enabled) => Enabled = enabled;
        public void Update(string instrument, double speed, double load, bool running) { Instrument = instrument; Running = running; }
        public void Play(string instrument, JourneyCue cue) => Cues++;
        public void Close() => Enabled = false;
    }
}
