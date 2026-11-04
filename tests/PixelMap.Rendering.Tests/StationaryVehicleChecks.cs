using System.Reflection;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

internal static class StationaryVehicleChecks
{
    private const int Width = 360, Height = 260;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly RoutePoint Origin = new(-.010986328057677, .010986328125);
    private static readonly ElevationSnapshot Elevation = new([
        new(new(0, 0, 0), 2, new float[] { 1501.3f, 1501.3f, 1501.3f, 1501.3f })]);

    internal static void Run()
    {
        int cases = 0;
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        foreach (var mode in Enum.GetValues<TravelMode>())
        foreach (bool relief in new[] { false, true })
        {
            string name = $"{style} {mode} relief={relief}";
            using var reference = new Scene(style, relief);
            using var reused = new Scene(style, relief);
            // Simulation poses keep physical MSL in a flat view. Navigation terrain
            // removes the DEM datum when projecting these vehicles and their effects.
            var pose = new VehiclePose(Origin, 28, AltitudeMeters: reference.Ground + (mode == TravelMode.Flight ? 5 : 0),
                Moving: false, Continuity: 7);
            Assert(!reused.Renderer.TryAdvanceStationaryVehicle(mode, pose), $"{name} reused an undrawn vehicle.");
            DrawBoth(mode, pose);
            Assert(reused.Bitmap.Pixels.Any(pixel => pixel.Alpha > 0), $"{name} did not paint the test vehicle.");

            for (int step = 1; step <= 6; step++)
            {
                pose = pose with
                {
                    Point = Origin.Move(28, step * .6), AnimationSeconds = step * .2,
                    Moving = true, SpeedMetersPerSecond = 3
                };
                Assert(!reused.Renderer.TryAdvanceStationaryVehicle(mode, pose), $"{name} reused a moving vehicle.");
                DrawBoth(mode, pose);
            }
            Assert((int)Effects(reference).GetType().GetProperty("Count", Members)!.GetValue(Effects(reference))! > 0,
                $"{name} did not seed a visible effects history.");

            pose = pose with { AnimationSeconds = 1.3, Moving = false, SpeedMetersPerSecond = 0 };
            Assert(!reused.Renderer.TryAdvanceStationaryVehicle(mode, pose), $"{name} skipped the stopping transition.");
            DrawBoth(mode, pose);
            byte[] stopped = reused.Bitmap.GetPixelSpan().ToArray();
            double stoppedTime = EffectTime(reference);
            foreach (double seconds in new[] { 1.3, 1.4, 1.6, 2, 6, 32, 100 })
            {
                pose = pose with { AnimationSeconds = seconds };
                Assert(reused.Renderer.TryAdvanceStationaryVehicle(mode, pose), $"{name} rejected an unchanged stationary sample.");
                reference.Draw(mode, pose);
                Compare(name, reference, reused);
                Assert(stopped.AsSpan().SequenceEqual(reference.Bitmap.GetPixelSpan()), $"{name} animated a stationary vehicle.");
                Assert(EffectTime(reused) == stoppedTime, $"{name} advanced the idle effects clock.");
            }

            // Rejected samples must leave the accepted stationary clock untouched.
            var changed = new[]
            {
                pose with { Point = pose.Point.Move(0, .1) },
                pose with { Bearing = pose.Bearing + .1 },
                pose with { AltitudeMeters = pose.AltitudeMeters + .1 },
                pose with { Moving = true },
                pose with { Progress = .2 },
                pose with { SpeedMetersPerSecond = .1 },
                pose with { Continuity = pose.Continuity + 1 },
                pose with { AnimationSeconds = 99 },
                pose with { AnimationSeconds = double.NaN },
                pose with { AnimationSeconds = double.PositiveInfinity }
            };
            foreach (var sample in changed)
            {
                Assert(!reused.Renderer.TryAdvanceStationaryVehicle(mode, sample), $"{name} reused a changed or invalid pose.");
                CompareEffects(name, reference, reused);
            }
            var otherMode = mode == TravelMode.Drive ? TravelMode.Boat : TravelMode.Drive;
            Assert(!reused.Renderer.TryAdvanceStationaryVehicle(otherMode, pose), $"{name} reused a different mode.");
            CompareEffects(name, reference, reused);

            var stoppedPoint = pose.Point;
            foreach (double elapsed in new[] { .05, .2, .4, .6, .8 })
            {
                pose = pose with
                {
                    Point = stoppedPoint.Move(28, elapsed * 3), AnimationSeconds = 100 + elapsed,
                    Moving = true, SpeedMetersPerSecond = 3
                };
                Assert(!reused.Renderer.TryAdvanceStationaryVehicle(mode, pose), $"{name} skipped resumed motion.");
                DrawBoth(mode, pose);
                Assert(Math.Abs(EffectTime(reused) - stoppedTime - elapsed) < 1e-12,
                    $"{name} included the idle interval in resumed effects time.");
            }

            // Each reset path still paints and restores the same effects state.
            pose = pose with { AnimationSeconds = 1, Moving = false, SpeedMetersPerSecond = 0 };
            RejectAndDraw(mode, pose, "time reversal");
            pose = pose with { Point = pose.Point.Move(0, 1000), AnimationSeconds = 2 };
            RejectAndDraw(mode, pose, "teleport");
            pose = pose with { Point = Origin, AnimationSeconds = 3, Continuity = pose.Continuity + 1 };
            RejectAndDraw(mode, pose, "continuity reset");
            RejectAndDraw(otherMode, pose, "mode switch");
            RejectAndDraw(mode, pose, "mode restoration");

            var start = new RouteStop("Start", Origin);
            var finish = new RouteStop("Finish", Origin.Move(28, 15));
            var route = new RoutePlan(mode, [start, finish],
                [new(start, finish, [start.Point, finish.Point], 15, TimeSpan.FromSeconds(5), [])], "Synthetic", false);
            reference.DrawRoute(route, pose);
            reused.DrawRoute(route, pose);
            Assert(!reused.Renderer.TryAdvanceStationaryVehicle(mode, pose), $"{name} reused a route overlay as a standalone vehicle.");
            Compare(name, reference, reused);
            RejectAndDraw(mode, pose, "route exit");
            pose = pose with { AnimationSeconds = 4 };
            Assert(reused.Renderer.TryAdvanceStationaryVehicle(mode, pose), $"{name} did not recover reuse after a standalone redraw.");
            reference.Draw(mode, pose);
            Compare(name, reference, reused);
            cases++;

            void DrawBoth(TravelMode activeMode, VehiclePose sample)
            {
                reference.Draw(activeMode, sample);
                reused.Draw(activeMode, sample);
                Compare(name, reference, reused);
            }

            void RejectAndDraw(TravelMode activeMode, VehiclePose sample, string transition)
            {
                Assert(!reused.Renderer.TryAdvanceStationaryVehicle(activeMode, sample), $"{name} skipped {transition}.");
                DrawBoth(activeMode, sample);
            }
        }
        Console.WriteLine($"Passed {cases} exact standalone idle/reuse/resume pixel and effects-state comparisons, pose guards and route/mode/reset transitions.");
    }

    private static void Compare(string name, Scene reference, Scene reused)
    {
        Assert(reference.Bitmap.GetPixelSpan().SequenceEqual(reused.Bitmap.GetPixelSpan()), $"{name} changed retained vehicle artwork.");
        CompareEffects(name, reference, reused);
    }

    private static void CompareEffects(string name, Scene reference, Scene reused)
    {
        object expected = Effects(reference), actual = Effects(reused);
        foreach (var field in expected.GetType().GetFields(Members))
        {
            object? left = field.GetValue(expected), right = field.GetValue(actual);
            bool same = left is Array a && right is Array b
                ? a.Cast<object>().SequenceEqual(b.Cast<object>()) : Equals(left, right);
            Assert(same, $"{name} changed effects state {field.Name}.");
        }
    }

    private static double EffectTime(Scene scene) =>
        (double)Effects(scene).GetType().GetProperty("Time", Members)!.GetValue(Effects(scene))!;

    private static object Effects(Scene scene)
    {
        object overlay = typeof(PixelMapRenderer).GetField("routeOverlay", Members)!.GetValue(scene.Renderer)!;
        return overlay.GetType().GetField("effects", Members)!.GetValue(overlay)!;
    }

    private sealed class Scene : IDisposable
    {
        internal readonly PixelMapRenderer Renderer = new();
        internal readonly SKBitmap Bitmap = new(Width, Height);
        internal readonly double Ground = Elevation.SampleSurfaceMeters(Origin.Latitude, Origin.Longitude);
        private readonly SKCanvas canvas;
        private readonly MapCamera camera;

        internal Scene(MapRenderStyle style, bool relief)
        {
            canvas = new(Bitmap);
            var eye = Origin.Move(180, 35);
            double datum = relief ? Ground : 0;
            camera = MapCamera.FromEye(eye.Latitude, eye.Longitude, datum + 20, 0, 60, Width, Height,
                groundAltitudeMeters: datum);
            Renderer.Draw(canvas, Width, Height, camera, [], PixelStyle.Day, 2, null, false, false,
                new(Elevation, Relief: relief), style);
        }

        internal void Draw(TravelMode mode, VehiclePose pose)
        {
            canvas.Clear(SKColors.Transparent);
            Renderer.DrawVehicle(canvas, Width, Height, camera, mode, pose, 2);
        }

        internal void DrawRoute(RoutePlan plan, VehiclePose pose)
        {
            canvas.Clear(SKColors.Transparent);
            Renderer.DrawNavigation(canvas, Width, Height, camera, plan, pose, 2);
        }

        public void Dispose()
        {
            Renderer.Dispose();
            canvas.Dispose();
            Bitmap.Dispose();
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
