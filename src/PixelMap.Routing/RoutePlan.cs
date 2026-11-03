namespace PixelMap.Routing;

public enum TravelMode { Walk, Bike, Drive, Train, Boat, Flight }

public readonly record struct RoutePoint(double Latitude, double Longitude)
{
    public static double Wrap(double angle) => angle - 360 * Math.Floor((angle + 180) / 360);
    public RoutePoint Move(double bearing, double meters) => Geo.Move(this, bearing, meters);
    public double DistanceTo(RoutePoint other) => Geo.Distance(this, other);
    public double BearingTo(RoutePoint other) => Geo.Bearing(this, other);
    public RoutePoint InterpolateTo(RoutePoint other, double fraction) => Geo.Interpolate(this, other, Math.Clamp(fraction, 0, 1));

    public bool IsValid => double.IsFinite(Latitude) && double.IsFinite(Longitude) &&
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

public sealed record RouteStop(string Name, RoutePoint Point);
public sealed record RouteStep(string Instruction, RoutePoint Point, double DistanceMeters, TimeSpan Duration)
{
    public string ManeuverType { get; init; } = "continue";
    public string Modifier { get; init; } = "";
    public double? BearingBefore { get; init; }
    public double? BearingAfter { get; init; }
    public int? Exit { get; init; }
}
public sealed record RouteLeg(RouteStop Start, RouteStop End, IReadOnlyList<RoutePoint> Geometry,
    double DistanceMeters, TimeSpan Duration, IReadOnlyList<RouteStep> Steps);

public sealed class RoutePlan
{
    public TravelMode Mode { get; }
    public IReadOnlyList<RouteStop> Stops { get; }
    public IReadOnlyList<RouteLeg> Legs { get; }
    public IReadOnlyList<RoutePoint> Geometry { get; }
    public double DistanceMeters { get; }
    public TimeSpan Duration { get; }
    public string Source { get; }
    public bool IsEstimate { get; }

    public RoutePlan(TravelMode mode, IReadOnlyList<RouteStop> stops, IReadOnlyList<RouteLeg> legs, string source, bool isEstimate)
    {
        if (stops.Count < 2 || legs.Count != stops.Count - 1) throw new ArgumentException("A leg is required between each pair of stops.");
        var shape = new List<RoutePoint>();
        foreach (var leg in legs)
        {
            if (leg.Geometry.Count == 0 || !double.IsFinite(leg.DistanceMeters) || leg.DistanceMeters < 0 || leg.Duration < TimeSpan.Zero)
                throw new ArgumentException("Each leg requires a valid path, distance and duration.");
            foreach (var point in leg.Geometry)
            {
                if (!point.IsValid) throw new ArgumentException("Invalid route vertex.");
                if (shape.Count == 0 || shape[^1] != point) shape.Add(point);
            }
            DistanceMeters += leg.DistanceMeters;
            Duration += leg.Duration;
        }
        Mode = mode; Stops = stops.ToArray(); Legs = legs.ToArray(); Geometry = shape.ToArray();
        Source = source; IsEstimate = isEstimate;
    }
}

public sealed class RouteUnavailableException(string message, Exception? cause = null) : Exception(message, cause);

public interface IRouteProvider
{
    bool Supports(TravelMode mode);
    Task<RoutePlan> PlanAsync(TravelMode mode, IReadOnlyList<RouteStop> stops, CancellationToken token = default);
}


/// <summary>Returns the recommended route first, followed by distinct alternatives when available.</summary>
public interface IRouteChoiceProvider : IRouteProvider
{
    Task<IReadOnlyList<RoutePlan>> PlanChoicesAsync(TravelMode mode, IReadOnlyList<RouteStop> stops, CancellationToken token = default);
}
