using PixelMap.Routing;

namespace PixelMap.Rendering;

public sealed partial class WebGpuSceneOverlay
{
    private readonly record struct RouteVertex(double X, double Y, double Station, double Elevation = 0);
    private readonly List<RouteVertex> routeSource = [];
    private readonly List<RouteVertex> routeVertices = [];
    private RoutePlan? routePlan;
    private TerrainOptions? routeTerrain;

    private void AppendRoute(RoutePlan route, double progress)
    {
        var terrain = frame!.NavigationTerrain;
        if (!ReferenceEquals(routePlan, route) || routeTerrain != terrain)
        {
            PrepareRoute(route, terrain);
            routePlan = route;
            routeTerrain = terrain;
        }
        var camera = frame.Camera;
        var bounds = camera.TileBounds(frame.Width, frame.Height, 1);
        float body = Math.Max(frame.PixelSize, 3.8f);
        double Lift(RouteVertex vertex)
        {
            if (terrain is not { Relief: true }) return camera.GroundAltitudeMeters;
            double height = frame.TryHeight(vertex.X, vertex.Y, out var value) ? value : vertex.Elevation;
            return camera.GroundAltitudeMeters + terrain.LocalAltitude(camera, height) + 2;
        }
        uint routeColor = RouteOverlay.RouteColor(route.Mode);
        for (int i = 1; i < routeVertices.Count; i++)
        {
            var a = routeVertices[i - 1]; var b = routeVertices[i];
            double altitudeA = Lift(a), altitudeB = Lift(b);
            int first = (int)Math.Ceiling(bounds.Left - Math.Max(a.X, b.X));
            int last = (int)Math.Floor(bounds.Right + 1 - Math.Min(a.X, b.X));
            // World copies can multiply at low zoom, but the camera viewport bounds
            // bound this traversal. Avoid unchecked integer increments at extreme input.
            for (int copy = first; copy <= last && copy - (long)first < 16; copy++)
            {
                var pa = Project(a.X + copy, a.Y, altitudeA);
                var pb = Project(b.X + copy, b.Y, altitudeB);
                int previousCount = world.IndexCount;
                Stroke(pa, pb, body + frame.PixelSize * .5f, 0x80344c54, hidden: true);
                Stroke(pa, pb, Math.Max(frame.PixelSize, body * .48f), 0xd9d8f6dc, hidden: true);
                Stroke(pa, pb, body + Math.Max(frame.PixelSize, 3), 0xff213439, bias: .0000002f);
                Stroke(pa, pb, body, routeColor, bias: .0000003f);
                if (world.IndexCount != previousCount) RouteSegmentCount++;
                if (!double.IsFinite(progress) || progress <= a.Station) continue;
                float fraction = b.Station <= progress || b.Station <= a.Station ? 1 :
                    (float)Math.Clamp((progress - a.Station) / (b.Station - a.Station), 0, 1);
                Stroke(pa, H.Lerp(pa, pb, fraction), Math.Max(frame.PixelSize, 2.2f), 0xff609d92, bias: .0000004f);
            }
        }
        if (route.Geometry.Count == 0) return;
        AppendStop(route.Geometry[0], true, false);
        for (int i = 0; i + 1 < route.Legs.Count; i++)
            if (route.Legs[i].Geometry.Count > 0) AppendStop(route.Legs[i].Geometry[^1], false, false);
        AppendStop(route.Geometry[^1], false, true);
    }

    private void PrepareRoute(RoutePlan route, TerrainOptions? terrain)
    {
        routeSource.Clear();
        routeVertices.Clear();
        double x = route.Geometry.Count == 0 ? 0 : (route.Geometry[0].Longitude + 180) / 360;
        double distance = 0;
        RoutePoint? previous = null;
        int stride = Math.Max(1, (route.Geometry.Count + 32767) / 32768);
        int ordinal = 0;
        foreach (var leg in route.Legs)
        {
            int first = routeSource.Count;
            double local = 0;
            for (int i = 0; i < leg.Geometry.Count; i++, ordinal++)
            {
                var point = leg.Geometry[i];
                if (i > 0) local += leg.Geometry[i - 1].DistanceTo(point);
                x += MapCamera.WrapOffset((point.Longitude + 180) / 360 - x);
                if (previous == point) continue;
                previous = point;
                if (ordinal % stride != 0 && i != 0 && i != leg.Geometry.Count - 1) continue;
                routeSource.Add(new(x, MapCamera.MercatorY(point.Latitude), local));
            }
            for (int i = first; i < routeSource.Count; i++)
            {
                double station = distance + (local > 0 ? routeSource[i].Station / local * leg.DistanceMeters : 0);
                routeSource[i] = routeSource[i] with { Station = route.DistanceMeters > 0 ? station / route.DistanceMeters : 0 };
            }
            distance += leg.DistanceMeters;
        }
        if (routeSource.Count == 0) return;
        if (terrain is not { Relief: true }) { routeVertices.AddRange(routeSource); return; }
        double length = 0;
        for (int i = 1; i < routeSource.Count; i++)
            length += Math.Max(Math.Abs(routeSource[i].X - routeSource[i - 1].X), Math.Abs(routeSource[i].Y - routeSource[i - 1].Y));
        int remaining = Math.Max(0, 65536 - routeSource.Count);
        double cell = 1d / ((1 << Math.Min(terrain.Elevation.MaximumZoom, 20)) * 256d);
        double spacing = Math.Max(cell * .75, length / Math.Max(1, remaining));
        routeVertices.Add(Sample(routeSource[0]));
        for (int i = 1; i < routeSource.Count; i++)
        {
            var a = routeSource[i - 1]; var b = routeSource[i];
            double span = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            int count = Math.Min(remaining + 1, Math.Max(1, (int)Math.Min(32768, Math.Ceiling(span / spacing))));
            remaining -= count - 1;
            for (int sample = 1; sample <= count; sample++)
            {
                double t = sample / (double)count;
                routeVertices.Add(Sample(new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
                    a.Station + (b.Station - a.Station) * t)));
            }
        }
        RouteVertex Sample(RouteVertex value) => value with { Elevation = terrain.Elevation.SampleSurfaceMercator(value.X, value.Y) };
    }

    private void AppendStop(RoutePoint point, bool origin, bool finish)
    {
        var p = GroundPoint(point, frame!.NavigationTerrain is { Relief: true } ? 2 : 0);
        if (!InFrustum(p, 24 * frame.PixelSize)) return;
        float pixel = frame.PixelSize;
        void B(float x, float y, float w, float h, uint color) => Block(p, x * pixel, y * pixel, w * pixel, h * pixel, color);
        if (!origin && !finish)
        {
            B(-3, -3, 6, 6, 0xff263d43); B(-2, -2, 4, 4, 0xfff0bb71); return;
        }
        B(-1, -10, 2, 11, 0xff20343c); B(-3, 0, 6, 2, 0xff26393c); B(1, -10, 8, 6, 0xff254148);
        if (origin) { B(1, -9, 6, 3, 0xff7de0ad); B(1, -9, 4, 1, 0xffd7f7c9); }
        else
        {
            B(1, -9, 6, 4, 0xfff6edd1);
            B(1, -9, 2, 2, 0xff344154); B(5, -9, 2, 2, 0xff344154); B(3, -7, 2, 2, 0xff344154);
        }
    }

    private void AppendGate(RoutePosition gate, TravelMode mode, double altitude, double clock)
    {
        double halfWidth = mode switch { TravelMode.Flight => 46, TravelMode.Boat => 9, TravelMode.Train => 3, _ => 4 };
        Flag(gate.Point.Move(gate.Bearing - 90, halfWidth), -1);
        Flag(gate.Point.Move(gate.Bearing + 90, halfWidth), 1);
        void Flag(RoutePoint point, int direction)
        {
            double absolute = frame!.NavigationTerrain is not null && mode != TravelMode.Flight ? PhysicalSurface(point) : altitude;
            var anchor = Project(point, RenderAltitude(point, absolute) + .6);
            if (!InFrustum(anchor, 30)) return;
            Block(anchor, -2, -18, 4, 18, 0xff193039);
            Block(anchor, direction < 0 ? -10 : 0, -18, 10, 8, 0xff193039);
            Block(anchor, direction < 0 ? -8 : 2, -16, 6, 4, 0xfffdda74);
            Block(anchor, -2, -22, 4, 4, ((int)(clock * 2) & 1) == 0 ? 0xff71fcc0 : 0xff2cb597);
        }
    }
}
