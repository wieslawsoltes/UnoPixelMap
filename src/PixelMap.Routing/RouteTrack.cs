namespace PixelMap.Routing;

public readonly record struct RouteGuidance(int LegIndex, int StepIndex, RouteStep? Current, RouteStep? Next, double MetersToNext);

public readonly record struct RoutePosition(RoutePoint Point, double Bearing, int LegIndex);

/// <summary>Indexed arc-length sampling. Provider distances are mapped proportionally to each leg's measured shape.</summary>
public sealed class RouteTrack
{
    private readonly RoutePlan plan;
    private readonly double[] ends;
    private readonly double[][] distances;
    private readonly double[][] stepStations;

    public double Length => plan.DistanceMeters;

    public RouteTrack(RoutePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.DistanceMeters <= 0 || plan.Geometry.Count < 2)
            throw new ArgumentException("A journey requires a route with two distinct points and positive length.", nameof(plan));
        this.plan = plan;
        ends = new double[plan.Legs.Count];
        distances = new double[ends.Length][];
        stepStations = new double[ends.Length][];
        double end = 0;
        for (int leg = 0; leg < ends.Length; leg++)
        {
            var shape = plan.Legs[leg].Geometry;
            var cumulative = distances[leg] = new double[shape.Count];
            for (int i = 1; i < shape.Count; i++) cumulative[i] = cumulative[i - 1] + Geo.Distance(shape[i - 1], shape[i]);
            if (plan.Legs[leg].DistanceMeters > 0 && cumulative[^1] < .001)
                throw new ArgumentException("A positive-length leg requires distinct geographic vertices.", nameof(plan));
            var steps = plan.Legs[leg].Steps;
            var starts = stepStations[leg] = new double[steps.Count];
            double total = steps.Sum(s => s.DistanceMeters);
            double scale = total > 0 ? plan.Legs[leg].DistanceMeters / total : 0;
            double at = end;
            for (int i = 0; i < steps.Count; i++)
            {
                starts[i] = at;
                at += steps[i].DistanceMeters * scale;
            }
            ends[leg] = end += plan.Legs[leg].DistanceMeters;
            if (steps.Count > 0 && steps[^1].ManeuverType == "arrive") starts[^1] = end;
        }
    }

    public RoutePosition Sample(double meters)
    {
        if (!double.IsFinite(meters)) throw new ArgumentOutOfRangeException(nameof(meters));
        double distance = Math.Clamp(meters, 0, plan.DistanceMeters);
        int leg = UpperBound(ends, distance);
        var points = plan.Legs[leg].Geometry;
        var cumulative = distances[leg];
        if (points.Count == 1 || cumulative[^1] == 0) return new(points[0], 0, leg);
        double fraction = (distance - (leg == 0 ? 0 : ends[leg - 1])) / Math.Max(plan.Legs[leg].DistanceMeters, 1e-9);
        double local = Math.Clamp(fraction, 0, 1) * cumulative[^1];
        int i = Math.Max(1, UpperBound(cumulative, local));
        double t = (local - cumulative[i - 1]) / Math.Max(cumulative[i] - cumulative[i - 1], 1e-9);
        var point = Geo.Interpolate(points[i - 1], points[i], Math.Clamp(t, 0, 1));
        return new(point, Geo.Bearing(points[i - 1], points[i]), leg);
    }

    /// <summary>Current instruction and the next maneuver at a station measured in the provider's metres.</summary>
    public RouteGuidance GuidanceAt(double station)
    {
        if (!double.IsFinite(station)) throw new ArgumentOutOfRangeException(nameof(station));
        station = Math.Clamp(station, 0, Length);
        int leg = UpperBound(ends, station);
        var steps = plan.Legs[leg].Steps;
        if (steps.Count == 0) return new(leg, -1, null, null, 0);
        var starts = stepStations[leg];
        int low = 0, high = starts.Length;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (starts[mid] <= station) low = mid + 1; else high = mid;
        }
        int current = Math.Max(0, low - 1);
        if (current + 1 < steps.Count)
            return new(leg, current, steps[current], steps[current + 1], Math.Max(0, starts[current + 1] - station));
        if (leg + 1 < plan.Legs.Count && plan.Legs[leg + 1].Steps.Count > 0)
            return new(leg, current, steps[current], plan.Legs[leg + 1].Steps[0], Math.Max(0, ends[leg] - station));
        return new(leg, current, steps[current], null, 0);
    }

    /// <summary>Find the closest position inside the vehicle's reachable station interval.
    /// At crossings this prevents a jump to a distant lap. The result uses provider metres.</summary>
    public (double Station, double Error) Nearest(RoutePoint p, double around, double radius)
    {
        if (!p.IsValid || !double.IsFinite(around) || !double.IsFinite(radius) || radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "Supply a valid location, station and nonnegative search radius.");
        double lo = Math.Clamp(around - radius, 0, Length), hi = Math.Clamp(around + radius, 0, Length);
        if (lo == hi) return (lo, p.DistanceTo(Sample(lo).Point));
        int first = UpperBound(ends, lo), last = UpperBound(ends, hi);
        double bestStation = lo, bestError = double.PositiveInfinity;
        for (int leg = first; leg <= last; leg++)
        {
            var shape = plan.Legs[leg].Geometry;
            var cumulative = distances[leg];
            double origin = leg == 0 ? 0 : ends[leg - 1];
            double scale = plan.Legs[leg].DistanceMeters / Math.Max(cumulative[^1], 1e-9);
            if (scale <= 0 || shape.Count < 2) continue;
            int begin = Math.Max(1, UpperBound(cumulative, Math.Max(0, (lo - origin) / scale)));
            int finish = Math.Max(1, UpperBound(cumulative, Math.Max(0, (hi - origin) / scale)));
            for (int i = begin; i <= finish; i++)
            {
                double start = origin + cumulative[i - 1] * scale;
                double length = (cumulative[i] - cumulative[i - 1]) * scale;
                if (length < 1e-9) continue;
                var a = shape[i - 1]; var b = shape[i];
                double bearing = a.BearingTo(b);
                double angular = a.DistanceTo(p) / Geo.Radius;
                double relative = (a.BearingTo(p) - bearing) * Geo.Rad;
                double along = Math.Atan2(Math.Sin(angular) * Math.Cos(relative), Math.Cos(angular)) * Geo.Radius;
                double t = Math.Clamp(along * scale / length, Math.Clamp((lo - start) / length, 0, 1),
                    Math.Clamp((hi - start) / length, 0, 1));
                double error = p.DistanceTo(a.Move(bearing, t * length / scale));
                if (error < bestError)
                {
                    bestError = error;
                    bestStation = Math.Clamp(start + t * length, lo, hi);
                }
            }
        }
        return (bestStation, bestError);
    }

    private static int UpperBound(double[] array, double value)
    {
        int lo = 0, hi = array.Length - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (array[mid] <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

internal static class Geo
{
    internal const double Rad = Math.PI / 180, Radius = 6371008.8;
    public static double Distance(RoutePoint a, RoutePoint b)
    {
        double p = Math.Sin((b.Latitude - a.Latitude) * Rad / 2), l = Math.Sin((b.Longitude - a.Longitude) * Rad / 2);
        double h = p * p + Math.Cos(a.Latitude * Rad) * Math.Cos(b.Latitude * Rad) * l * l;
        return 2 * Radius * Math.Asin(Math.Sqrt(Math.Clamp(h, 0, 1)));
    }
    public static double Bearing(RoutePoint a, RoutePoint b)
    {
        double p = a.Latitude * Rad, q = b.Latitude * Rad, l = (b.Longitude - a.Longitude) * Rad;
        double bearing = Math.Atan2(Math.Sin(l) * Math.Cos(q), Math.Cos(p) * Math.Sin(q) - Math.Sin(p) * Math.Cos(q) * Math.Cos(l));
        return (bearing / Rad + 360) % 360;
    }
    public static RoutePoint Interpolate(RoutePoint a, RoutePoint b, double t)
    {
        if (t <= 0) return a;
        if (t >= 1) return b;
        double length = Distance(a, b) / Radius;
        if (length < 1e-10) return a;
        return Move(a, Bearing(a, b), t * length * Radius);
    }

    internal static RoutePoint Move(RoutePoint a, double bearing, double meters)
    {
        if (meters == 0) return a;
        double angle = bearing * Rad, delta = meters / Radius, lat = a.Latitude * Rad;
        var (sd, cd) = Math.SinCos(delta);
        var (sl, cl) = Math.SinCos(lat);
        double p = Math.Asin(Math.Clamp(sl * cd + cl * sd * Math.Cos(angle), -1, 1));
        double lon = a.Longitude + Math.Atan2(Math.Sin(angle) * sd * cl, cd - sl * Math.Sin(p)) / Rad;
        return new(p / Rad, RoutePoint.Wrap(lon));
    }
    public static RouteLeg Estimate(RouteStop a, RouteStop b, IReadOnlyList<RoutePoint> points, double speed, string action)
    {
        double meters = 0;
        for (int i = 1; i < points.Count; i++) meters += Distance(points[i - 1], points[i]);
        var duration = TimeSpan.FromSeconds(meters / speed);
        return new(a, b, points, meters, duration, [
            new(action, points[0], meters, duration) { ManeuverType = "depart", BearingAfter = points[0].BearingTo(points[1]) },
            new("Arrive at " + b.Name, points[^1], 0, TimeSpan.Zero) { ManeuverType = "arrive", BearingBefore = points[^2].BearingTo(points[^1]) }]);
    }
}
