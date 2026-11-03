using PixelMap.Audio;
using PixelMap.Routing;
using PixelMap.Simulation;

internal static class LifecycleChecks
{
    public static void Run()
    {
        var route = RouteDemos.ForMode(TravelMode.Drive);
        var session = new JourneySession();
        session.Load(route);
        session.Preview();
        session.Tick(.2);
        session.Seek(.9);
        Check(session.State == JourneyState.Preview && !session.IsScoredRun && session.NextCheckpoint is null,
            "A preview seek stays unscored.");
        session.Drive();
        Check(session.State == JourneyState.Driving && session.Progress == 0 && session.CheckpointsPassed == 0 &&
            session.Score == 0 && session.ElapsedSeconds == 0 && session.RunDistanceMeters == 0,
            "Starting a challenge after seeking resets the entire scored run.");

        session.SetInput(1, .3, 0);
        session.Tick(.2);
        double speed = session.SpeedMetersPerSecond;
        double station = session.DistanceMeters;
        var point = new RoutePoint(session.Latitude, session.Longitude);
        session.Pause();
        session.Tick(1);
        Check(session.State == JourneyState.Paused && session.SpeedMetersPerSecond == speed &&
            session.DistanceMeters == station && point == new RoutePoint(session.Latitude, session.Longitude),
            "Pausing freezes the complete physical pose and clock.");
        session.Drive();
        Check(session.State == JourneyState.Driving && session.DistanceMeters == station, "Drive resumes a paused challenge in place.");
        session.Tick(1d / 60);
        Check(session.SpeedMetersPerSecond < speed && session.EngineLoad == 0, "Pause releases the throttle before resuming.");

        session.SetCruise(true);
        for (int i = 0; i < 2000 && session.CheckpointsPassed == 0; i++) session.Tick(.1);
        Check(session.CheckpointsPassed == 1 && session.Score == 100, "A physical challenge earns its first gate.");
        int passed = session.CheckpointsPassed;
        session.Pause();
        station = session.DistanceMeters;
        session.Resume();
        Check(session.CheckpointsPassed == passed && session.Score == 100 && session.DistanceMeters == station,
            "Pause and resume retain earned gates.");
        session.Seek(.99);
        Check(session.State == JourneyState.Paused && !session.IsScoredRun && session.NextCheckpoint is null,
            "Seeking during a challenge enters a paused preview.");
        session.Resume();
        Check(session.State == JourneyState.Preview, "A seek resumes as preview playback.");
        session.Drive();
        Check(session.Score == 0 && session.CheckpointsPassed == 0 && session.DistanceMeters == 0,
            "A sought challenge cannot reuse earlier checkpoint credit.");

        session.SetInput(1, 0, 0);
        session.Tick(.1);
        var modeBefore = session.State;
        speed = session.SpeedMetersPerSecond;
        station = session.DistanceMeters;
        long continuity = session.Continuity;
        var invalidMode = new RoutePlan((TravelMode)999, route.Stops, route.Legs, "Invalid mode fixture", false);
        Throws<ArgumentOutOfRangeException>(() => session.Load(invalidMode));
        Check(ReferenceEquals(session.Route, route) && session.State == modeBefore && session.DistanceMeters == station &&
            session.SpeedMetersPerSecond == speed && session.Continuity == continuity,
            "A rejected route mode leaves the current journey intact.");
        var invalidGeometry = new RoutePlan(route.Mode, route.Stops,
            [route.Legs[0] with { Geometry = [route.Geometry[0], route.Geometry[0]] }], "Invalid geometry fixture", false);
        Throws<ArgumentException>(() => session.Load(invalidGeometry));
        Check(ReferenceEquals(session.Route, route) && session.Continuity == continuity,
            "A rejected degenerate track leaves the current journey intact.");
        Throws<ArgumentException>(() => session.SelectVehicle(VehicleProfile.Car with { Motion = (MotionModel)999 }));
        Check(session.Vehicle == VehicleProfile.Car && session.State == modeBefore, "Undefined physical models are rejected atomically.");

        session.Reset();
        session.Drive();
        session.SetInput(double.NaN, double.PositiveInfinity, double.NegativeInfinity);
        session.SetLift(double.NaN);
        foreach (double elapsed in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 0d }) session.Tick(elapsed);
        Check(session.ElapsedSeconds == 0 && session.Progress == 0, "Invalid frame durations cannot alter the clock.");
        session.Tick(.1);
        Check(session.SpeedMetersPerSecond == 0 && new RoutePoint(session.Latitude, session.Longitude).IsValid,
            "Nonfinite controls resolve to neutral input.");
        station = session.DistanceMeters;
        continuity = session.Continuity;
        session.Seek(double.NaN);
        session.PlaybackRate = double.PositiveInfinity;
        Check(session.DistanceMeters == station && session.Continuity == continuity && session.PlaybackRate == 1,
            "Nonfinite seeks and playback rates leave valid state intact.");
        session.PlaybackRate = 0;
        Check(session.PlaybackRate == .25, "Finite playback rates respect the lower bound.");
        session.PlaybackRate = 100;
        Check(session.PlaybackRate == 32, "Finite playback rates respect the upper bound.");

        var device = new LifetimeAudioProbe();
        Throws<ArgumentNullException>(() => new JourneySound(null!));
        var mixer = new JourneySound(device);
        mixer.Update(session);
        Check(device.Enables == 0 && device.Frames == 0, "Muted sessions allocate no audio graph.");
        session.Muted = false;
        mixer.Update(session);
        mixer.Update(session);
        Check(device.Enables == 1 && device.Frames == 1 && device.Running, "Identical active frames are coalesced.");
        session.Pause();
        mixer.Update(session);
        Check(!device.Running, "A pause stops the continuous sound bed.");
        session.Muted = true;
        mixer.Play(session, JourneyCue.Horn);
        mixer.Update(session);
        Check(device.Plays == 0 && !device.Enabled, "Muted cues stay silent and the graph is disabled.");
        session.Muted = false;
        mixer.Play(session, JourneyCue.Horn);
        Check(device.Plays == 1 && device.Enabled && !device.Running, "An idle horn enables only its cue.");
        mixer.Dispose();
        mixer.Dispose();
        Check(device.Closes == 1, "Disposal closes the graph exactly once.");
        Throws<ObjectDisposedException>(() => mixer.Update(session));
        Throws<ObjectDisposedException>(() => mixer.Play(session, JourneyCue.Horn));
        Check(device.Closes == 1 && device.Plays == 1, "A disposed mixer cannot revive the audio graph.");
        using var replacement = new JourneySound(device);
        replacement.Update(session);
        Check(device.Enabled && !device.Running, "A new mixer can recreate its device graph after a view reload.");
        Console.WriteLine("Seek, score, pause, invalid-input, transactional-load and audio-lifetime checks passed.");
    }

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

    private sealed class LifetimeAudioProbe : IJourneyAudioDevice
    {
        public int Enables, Frames, Plays, Closes;
        public bool Enabled, Running;
        public void Enable(bool enabled) { Enables++; Enabled = enabled; }
        public void Update(string instrument, double speed, double load, bool running) { Frames++; Running = running; }
        public void Play(string instrument, JourneyCue cue) => Plays++;
        public void Close() { Closes++; Enabled = false; }
    }
}
