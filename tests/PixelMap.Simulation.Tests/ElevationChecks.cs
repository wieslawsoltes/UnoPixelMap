using PixelMap.Data;
using PixelMap.Routing;
using PixelMap.Simulation;

internal static class ElevationChecks
{
    private static readonly TileId Tile = new(14, 8192, 8192);

    public static void Run()
    {
        GroundedJourneys();
        StreamingUpdates();
        AircraftAltitude();
        WaterSurfaces();
        BuildingFoundations();
        TerrainContacts();
        Console.WriteLine("Elevation grounding, streaming, aircraft clearance, water surfaces and 3D contact checks passed.");
    }

    private static void GroundedJourneys()
    {
        var elevation = Constant(1500);
        var world = new VectorCollisionWorld();
        world.Update([], elevation);
        foreach (var mode in Enum.GetValues<TravelMode>())
        {
            var route = Route(mode);
            var session = new JourneySession { World = world };
            session.Load(route);
            Check(session.AltitudeMeters == 1500 && session.GroundElevationMeters == 1500 && session.AltitudeAboveGroundMeters == 0,
                $"{mode} starts at the absolute terrain elevation.");
            session.Seek(.5);
            double envelope = mode == TravelMode.Flight ? Math.Min(2500, route.DistanceMeters * .09) : 0;
            Near(session.AltitudeMeters, 1500 + envelope, "Seeking places the vehicle above the local surface.");
            Near(session.AltitudeAboveGroundMeters, envelope, "Preview clearance is independent of mean sea level.");
            session.Resume();
            session.Tick(.1);
            Near(session.GroundElevationMeters, 1500, "Preview uses the current terrain.");
            session.Reset();
            Near(session.AltitudeMeters, 1500, "Reset restores the terrain-supported pose.");
            session.Drive();
            session.SetInput(1, 0, 0);
            Advance(session, 120);
            session.Respawn();
            Near(session.AltitudeMeters, 1500, "Recovery restores terrain elevation.");
            session.Load(null);
            Check(session.AltitudeMeters == 0 && session.GroundElevationMeters == 0, "Unloading clears the complete elevation pose.");
        }

        var ramp = Grid(4, (x, _) => 1000 + x * 150);
        world.Update([], ramp);
        foreach (var mode in new[] { TravelMode.Drive, TravelMode.Bike, TravelMode.Walk, TravelMode.Train, TravelMode.Boat })
        foreach (bool cruise in new[] { false, true })
        {
            var session = new JourneySession { World = world };
            session.Load(Route(mode));
            double start = session.AltitudeMeters;
            session.Drive();
            session.SetInput(1, 0, 0);
            session.SetCruise(cruise);
            Advance(session, 600);
            double ground = ramp.SampleSurfaceMeters(session.Latitude, session.Longitude);
            Check(session.DistanceMeters > 0 && session.AltitudeMeters > start, $"{mode} follows rising terrain while moving.");
            Near(session.AltitudeMeters, ground, $"{mode} stays on its supporting surface.");
            Check(session.AltitudeAboveGroundMeters == 0, $"{mode} retains zero surface clearance.");
        }

        var first = new JourneySession { World = world };
        var second = new JourneySession { World = world };
        foreach (var session in new[] { first, second })
        {
            session.Load(Route(TravelMode.Drive));
            session.Drive();
            session.SetInput(1, .4, 0);
        }
        Advance(first, 180);
        for (int i = 0; i < 45; i++) second.Tick(1d / 15);
        Near(first.AltitudeMeters, second.AltitudeMeters, "Terrain following retains fixed-step determinism.");
        Near(first.Longitude, second.Longitude, "Terrain sampling does not change frame-rate-independent movement.");
    }

    private static void StreamingUpdates()
    {
        var world = new SurfaceWorld(_ => 1000);
        var session = new JourneySession { World = world };
        session.Load(Route(TravelMode.Drive));
        long continuity = session.Continuity;
        world.Sample = _ => 1200;
        session.RefreshSurface();
        Check(session.State == JourneyState.Ready && session.AltitudeMeters == 1200 && session.Continuity == continuity + 1,
            "Streamed terrain updates a ready pose and invalidates interpolated effects.");
        continuity = session.Continuity;
        session.RefreshSurface();
        Check(session.Continuity == continuity, "An unchanged elevation sample retains continuity.");
        session.Drive();
        session.SetCruise(true);
        Advance(session, 600);
        session.Pause();
        double station = session.DistanceMeters, elapsed = session.ElapsedSeconds, speed = session.SpeedMetersPerSecond;
        int score = session.Score, passed = session.CheckpointsPassed;
        world.Sample = _ => -100;
        session.RefreshSurface();
        Check(session.State == JourneyState.Paused && session.DistanceMeters == station && session.ElapsedSeconds == elapsed &&
            session.SpeedMetersPerSecond == speed && session.Score == score && session.CheckpointsPassed == passed,
            "Paused terrain refresh preserves transport, physics and scoring state.");
        Check(session.AltitudeMeters == -100 && session.GroundElevationMeters == -100, "Below-sea-level terrain remains signed.");
        session.World = null;
        Check(session.AltitudeMeters == 0, "Detaching a world restores the default flat surface.");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            session.World = new SurfaceWorld(_ => invalid);
            session.RefreshSurface();
            Check(session.AltitudeMeters == 0 && session.GroundElevationMeters == 0, "Invalid extension samples cannot poison the pose.");
        }

        var flight = Fly(world = new SurfaceWorld(_ => 1000));
        flight.Pause();
        double altitude = flight.AltitudeMeters;
        world.Sample = _ => 600;
        flight.RefreshSurface();
        Near(flight.AltitudeMeters, altitude, "Streamed lower terrain preserves an airborne manual altitude.");
        Near(flight.AltitudeAboveGroundMeters, altitude - 600, "Airborne clearance includes the changed terrain datum.");
        world.Sample = _ => altitude + 100;
        flight.RefreshSurface();
        Near(flight.AltitudeMeters, altitude + 100, "New terrain above an existing pose safely restores surface clearance.");

        flight.Seek(.5);
        double previewClearance = flight.AltitudeAboveGroundMeters;
        world.Sample = _ => 2500;
        flight.RefreshSurface();
        Near(flight.AltitudeAboveGroundMeters, previewClearance, "A paused preview retains its terrain-relative flight envelope.");
    }

    private static void AircraftAltitude()
    {
        var flat = Fly(new SurfaceWorld(_ => 0));
        var highWorld = new SurfaceWorld(_ => 4500);
        var high = Fly(highWorld);
        Check(high.AltitudeMeters > 4500 && high.AltitudeAboveGroundMeters > 20, "An aircraft can take off from an airport above 4200 metres.");
        Near(high.AltitudeMeters - flat.AltitudeMeters, 4500, "Flight integration is invariant under a constant elevation offset.");
        high.SetLift(0);
        double altitude = high.AltitudeMeters;
        highWorld.Sample = _ => 4000;
        high.Tick(1d / 60);
        Near(high.AltitudeMeters, altitude, "Terrain falling away during movement cannot drag an airborne aircraft down.");
        Near(high.GroundElevationMeters, 4000, "Horizontal flight updates the underlying ground datum.");

        var below = Fly(new SurfaceWorld(_ => -430));
        Check(below.AltitudeMeters < 0 && below.AltitudeAboveGroundMeters > 0,
            "Aircraft over below-sea-level terrain use signed absolute altitude.");
        Near(below.AltitudeAboveGroundMeters, flat.AltitudeAboveGroundMeters, "Flight clearance is consistent below mean sea level.");
        int contacts = 0;
        below.Cue += cue => { if (cue == JourneyCue.Contact) contacts++; };
        below.SetInput(0, 0, 1);
        below.SetLift(-1);
        for (int i = 0; i < 1800 && below.AltitudeAboveGroundMeters > .000001; i++) below.Tick(1d / 60);
        Near(below.AltitudeMeters, -430, "Descent lands on the signed terrain floor.");
        Check(contacts == 1, "Landing produces one contact cue.");
        Advance(below, 180);
        Check(contacts == 1, "Standing on the ground does not repeat landing contacts.");

        var ramp = Grid(4, (x, _) => 1000 + x * 100);
        var world = new VectorCollisionWorld();
        world.Update([], ramp);
        var taxi = new JourneySession { World = world };
        taxi.Load(Route(TravelMode.Flight));
        double start = taxi.AltitudeMeters;
        taxi.Drive();
        taxi.SetInput(1, 0, 0);
        Advance(taxi, 120);
        Check(taxi.AltitudeMeters > start && taxi.AltitudeAboveGroundMeters == 0, "A grounded aircraft taxis along a gentle runway grade.");
    }

    private static void WaterSurfaces()
    {
        var water = Polygon("water", Box(0, 0, 4096, 4096)) with { Class = "ocean" };
        var mapped = new MapTile(Tile, [water]);
        var samples = new ElevationTile(Tile, 2, new float[] { -430, -430, -430, -430 });
        var ocean = new ElevationSnapshot([samples], [mapped]);
        var world = new VectorCollisionWorld();
        world.Update([mapped], ocean);
        var boat = new JourneySession { World = world };
        boat.Load(Route(TravelMode.Boat));
        Check(boat.AltitudeMeters == 0 && boat.GroundElevationMeters == 0, "Ocean vessels sit at sea level above bathymetry.");
        boat.Drive();
        boat.SetInput(1, 0, 0);
        Advance(boat, 120);
        Check(boat.DistanceMeters > 0 && boat.AltitudeMeters == 0, "Ocean travel preserves the water surface.");

        mapped = new MapTile(Tile, [water with { Class = "lake" }]);
        world.Update([mapped], new ElevationSnapshot([samples], [mapped]));
        boat.RefreshSurface();
        Check(boat.AltitudeMeters == -430 && boat.AltitudeAboveGroundMeters == 0, "Inland lakes below sea level retain their actual elevation.");
        Advance(boat, 120);
        Check(boat.AltitudeMeters == -430, "A vessel follows a signed inland-water surface while moving.");

        mapped = new MapTile(Tile, []);
        world.Update([mapped], new ElevationSnapshot([samples], [mapped]));
        var walker = new JourneySession { World = world };
        walker.Load(Route(TravelMode.Walk));
        Check(walker.AltitudeMeters == -430, "Dry below-sea-level valleys remain below sea level.");
    }

    private static void BuildingFoundations()
    {
        var tower = Polygon("building", Box(2000, 1950, 2200, 2150)) with { Height = 60, MinHeight = 30 };
        var mapped = new MapTile(Tile, [tower]);
        var world = new VectorCollisionWorld();
        world.Update([mapped], Constant(1500));
        RoutePoint outside = At(1950, 2050), inside = At(2050, 2050);
        Check(!world.Blocks(VehicleProfile.Plane, outside, inside, 1529), "A raised building retains clearance above an elevated foundation.");
        Check(world.Blocks(VehicleProfile.Plane, outside, inside, 1530), "Raised contact begins at foundation plus minimum height.");
        Check(!world.Blocks(VehicleProfile.Plane, outside, inside, 1560), "The mapped roof is foundation plus building height.");
        Check(world.Blocks(VehicleProfile.Plane, inside, inside, 1520, 1570), "A vertical ascent cannot skip a raised building volume.");
        Check(world.Blocks(VehicleProfile.Plane, inside, inside, 1570, 1520), "A vertical descent cannot skip a raised building volume.");
        Check(world.Blocks(VehicleProfile.Plane, outside, At(2250, 2050), 1510, 1580), "A diagonal altitude sweep meets the middle of an elevated volume.");

        world.Update([mapped], Constant(1800));
        Check(!world.Blocks(VehicleProfile.Car, outside, inside, 1530), "A new terrain snapshot invalidates cached building foundations.");
        Check(world.Blocks(VehicleProfile.Plane, outside, inside, 1830), "The same resident vector tile uses the new foundation height.");
        world.Update([mapped]);
        Check(world.Blocks(VehicleProfile.Plane, outside, inside, 1830), "Legacy vector-only updates preserve the current elevation snapshot.");

        var ramp = Grid(4, (x, _) => 1000 + x * 150);
        var thin = tower with { Paths = [Box(2048, 1950, 2049, 2150)], MinHeight = 0 };
        mapped = new MapTile(Tile, [thin]);
        world.Update([mapped], ramp);
        var from = At(2047.8, 2050);
        var to = At(2049.2, 2050);
        double first = ramp.SampleSurfaceMeters(from.Latitude, from.Longitude);
        double last = ramp.SampleSurfaceMeters(to.Latitude, to.Longitude);
        Check(world.Blocks(VehicleProfile.Car, from, to, first, last), "Terrain-following endpoints cannot skip a thin building on a slope.");

        var water = Polygon("water", Box(2000, 1800, 2200, 2300)) with { Class = "lake" };
        var bridge = new MapFeature("transportation", "minor", "", "", 1, 4096, GeometryType.LineString,
            [[new(1900, 2050), new(2300, 2050)]], null, null, false, 0, 0, "bridge");
        world.Update([new(Tile, [water, bridge])], Constant(1500));
        Check(!world.Blocks(VehicleProfile.Car, outside, inside, 1500), "An elevated road bridge retains its water crossing.");
        world.Update([new(Tile, [water, tower with { MinHeight = 0 }, bridge with { Brunnel = "tunnel", Level = -1 }])], Constant(1500));
        Check(!world.Blocks(VehicleProfile.Car, outside, inside, 1500), "Tunnel corridors remain passable under elevated buildings and water.");
        Check(world.Blocks(VehicleProfile.Plane, outside, inside, 1530), "Aircraft cannot use a ground tunnel exemption at elevated terrain.");
    }

    private static void TerrainContacts()
    {
        var ridge = Grid(8, (x, _) => x is 3 or 4 ? 1600 : 1000);
        var world = new VectorCollisionWorld();
        world.Update([], ridge);
        RoutePoint from = At(1280, 2048), to = At(2816, 2048);
        Check(world.Blocks(VehicleProfile.Plane, from, to, 1200), "A swept flight cannot pass through a ridge between clear endpoints.");
        Check(!world.Blocks(VehicleProfile.Plane, from, to, 1700), "Flight above the terrain envelope remains clear.");
        Check(world.Blocks(VehicleProfile.Plane, At(2048, 2048), At(2048, 2048), 1700, 1500), "Vertical descent tests terrain clearance.");
        Check(!world.Blocks(VehicleProfile.Plane, At(2048, 2048), to, 1500), "A pose embedded by new terrain can escape.");
        Check(!world.Blocks(VehicleProfile.Car, from, to, 1000), "Ground travel is supported by terrain instead of blocked by its volume.");

        var legacy = new SurfaceWorld(_ => 0);
        IJourneyWorld compatible = legacy;
        Check(!compatible.Blocks(VehicleProfile.Car, from, to, 10, 20) && legacy.LastAltitude == 20,
            "Existing world extensions receive destination altitude through the compatible swept overload.");
    }

    private static JourneySession Fly(SurfaceWorld world)
    {
        var session = new JourneySession { World = world };
        session.Load(Route(TravelMode.Flight));
        session.Drive();
        session.SetInput(1, 0, 0);
        session.SetLift(1);
        Advance(session, 480);
        return session;
    }

    private static void Advance(JourneySession session, int frames)
    {
        for (int i = 0; i < frames; i++) session.Tick(1d / 60);
    }

    private static ElevationSnapshot Constant(float meters) => new([new ElevationTile(Tile, 2, new[] { meters, meters, meters, meters })]);

    private static ElevationSnapshot Grid(int size, Func<int, int, float> height)
    {
        var heights = new float[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) heights[y * size + x] = height(x, y);
        return new([new ElevationTile(Tile, size, heights)]);
    }

    private static RoutePlan Route(TravelMode mode)
    {
        var start = new RouteStop("Start", At(1000, 2048));
        var end = new RouteStop("Finish", At(3500, 2048));
        double length = start.Point.DistanceTo(end.Point);
        var leg = new RouteLeg(start, end, [start.Point, end.Point], length, TimeSpan.FromMinutes(10), []);
        return new(mode, [start, end], [leg], "Elevation fixture", false);
    }

    private static MapFeature Polygon(string layer, TilePoint[] ring) =>
        new(layer, "", "", "", 1, 4096, GeometryType.Polygon, [ring], null, null, false, 0, 0, "");

    private static TilePoint[] Box(int left, int top, int right, int bottom) =>
        [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];

    private static RoutePoint At(double x, double y) => new(
        Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (Tile.Y + y / 4096) / Math.Pow(2, Tile.Z)))) * 180 / Math.PI,
        (Tile.X + x / 4096) / Math.Pow(2, Tile.Z) * 360 - 180);

    private static void Near(double actual, double expected, string message) =>
        Check(Math.Abs(actual - expected) < .000001, $"{message} Expected {expected}, received {actual}.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SurfaceWorld(Func<RoutePoint, double> sample) : IJourneyWorld
    {
        public Func<RoutePoint, double> Sample { get; set; } = sample;
        public double LastAltitude { get; private set; }
        public bool Blocks(VehicleProfile vehicle, RoutePoint from, RoutePoint to, double altitudeMeters)
        {
            LastAltitude = altitudeMeters;
            return false;
        }
        public double SurfaceElevationMeters(VehicleProfile vehicle, RoutePoint point) => Sample(point);
    }
}
