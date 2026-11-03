using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

internal static class NavigationChecks
{
    internal static void Run()
    {
        const int w = 620, h = 380;
        var camera = new MapCamera(45, 179.996, 14.5, 168, 37);
        var anchor = camera.Unproject(442, 106, w, h);
        var changed = camera.TransformAt(.7, 81, 25, 442, 106, w, h);
        var after = changed.Project(anchor.Latitude, anchor.Longitude, w, h);
        Assert(Math.Abs(after.X - 442) + Math.Abs(after.Y - 106) < 1e-6, "Orbit and zoom moved the pointer anchor");
        RoutePoint[] points = [new(45.002, 179.982), new(45.007, -179.993), new(44.994, -179.974)];
        var fit = camera.FitRoute(points, w, h, 49);
        foreach (var p in points)
        {
            var s = fit.Project(p.Latitude, p.Longitude, w, h);
            Assert(s.X >= 48.99 && s.X <= w - 48.99 && s.Y >= 48.99 && s.Y <= h - 48.99, "Fit lost a wrapped route point");
        }
        Assert(Math.Abs(fit.Longitude) > 179 && fit.Zoom > 10, "Route framed the wrong copy of Earth");
        var mid = new MapCamera(0, 179, 8, 170).Interpolate(new(0, -179, 10, -170), .5);
        Assert(Math.Abs(mid.Longitude) > 179.99 && Math.Abs(mid.Bearing) > 179.99 && mid.Zoom == 9, "Flight took the long arc");

        using var canvasImage = new SKBitmap(w, h);
        using var canvas = new SKCanvas(canvasImage);
        using var renderer = new PixelMapRenderer();
        var shapes = new HashSet<ulong>();
        foreach (var mode in Enum.GetValues<TravelMode>())
        {
            var plan = Plan(mode, points);
            Draw(plan, null);
            var center = fit.Project(points[1].Latitude, points[1].Longitude, w, h);
            int cx = (int)center.X, cy = (int)center.Y;
            ulong routeOnly = Hash(cx, cy);
            var p = new VehiclePose(points[1], 85, 0, 0, true, .45, 10);
            Draw(plan, p);
            ulong headingA = Hash(cx, cy);
            Assert(headingA != routeOnly, $"{mode} did not produce a vehicle sprite");
            Draw(plan, p with { Bearing = 170 });
            Assert(Hash(cx, cy) != headingA, $"{mode} sprite did not rotate");
            shapes.Add(headingA);
            for (int i = 1; i <= 5; i++)
                Draw(plan, p with { Point = new(points[1].Latitude, points[1].Longitude + .00002 * i), AnimationSeconds = i * .3 });
            var stopped = p with { Point = new(points[1].Latitude, points[1].Longitude + .0001), AnimationSeconds = 1.5, Moving = false };
            Draw(plan, stopped);
            var paused = canvasImage.GetPixelSpan().ToArray();
            Draw(plan, stopped with { AnimationSeconds = 16 });
            Assert(paused.AsSpan().SequenceEqual(canvasImage.GetPixelSpan()), $"{mode} effects advanced while paused");
            var seek = stopped with { Continuity = 1, AnimationSeconds = 1.5 };
            Draw(plan, seek);
            var reset = canvasImage.GetPixelSpan().ToArray();
            using var fresh = new PixelMapRenderer();
            canvas.Clear(SKColors.Transparent); fresh.DrawNavigation(canvas, w, h, fit, plan, seek);
            Assert(reset.AsSpan().SequenceEqual(canvasImage.GetPixelSpan()), $"{mode} retained stale effects after seeking");
        }
        Assert(shapes.Count == 6, "Travel modes collapsed to identical sprites");
        canvas.Clear(SKColors.Transparent);
        renderer.DrawNavigation(canvas, w, h, new MapCamera(45, 0, 4, 0, 0), Plan(TravelMode.Boat, points));
        Assert(canvasImage.Pixels.All(p => p.Alpha == 0), "Wrapped route crossed the Greenwich viewport");
        Console.WriteLine("Passed camera orbit, fit, short-arc flight, six vehicle symbols, pause/seek effects and route wrap checks.");

        void Draw(RoutePlan plan, VehiclePose? pose)
        {
            canvas.Clear(SKColors.Transparent);
            renderer.DrawNavigation(canvas, w, h, fit, plan, pose);
        }
        ulong Hash(int x, int y)
        {
            ulong hash = 14695981039346656037;
            for (int sy = y - 36; sy <= y + 36; sy++)
            for (int sx = x - 36; sx <= x + 36; sx++)
                hash = unchecked((hash ^ (uint)canvasImage.GetPixel(sx, sy)) * 1099511628211);
            return hash;
        }
    }

    private static RoutePlan Plan(TravelMode mode, RoutePoint[] points)
    {
        var a = new RouteStop("Start", points[0]); var b = new RouteStop("Finish", points[^1]);
        return new(mode, [a, b], [new(a, b, points, 4000, TimeSpan.FromMinutes(8), [])], "Synthetic", false);
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
