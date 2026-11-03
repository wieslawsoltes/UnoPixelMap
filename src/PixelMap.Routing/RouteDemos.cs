using System.Text.Json;

namespace PixelMap.Routing;

/// <summary>Real offline paths for a first trip without relying on public routing services.</summary>
public static class RouteDemos
{
    public static RoutePlan ForMode(TravelMode mode)
    {
        if (mode == TravelMode.Flight)
            return new FlightProvider().PlanAsync(mode, [new("LaGuardia Airport", new(40.7769, -73.8740)),
                new("Newark Liberty", new(40.6895, -74.1745))]).GetAwaiter().GetResult();
        string file = mode switch
        {
            TravelMode.Walk => "foot", TravelMode.Bike => "bike", TravelMode.Drive => "car",
            TravelMode.Train => "train", TravelMode.Boat => "boat", _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        using var stream = typeof(RouteDemos).Assembly.GetManifestResourceStream($"PixelMap.Routing.Seed.{file}.json")
            ?? throw new InvalidDataException("Missing offline route.");
        using var json = JsonDocument.Parse(stream);
        if (mode is TravelMode.Boat or TravelMode.Train)
        {
            var shape = json.RootElement.EnumerateArray().Select(e => new RoutePoint(e[0].GetDouble(), e[1].GetDouble())).ToArray();
            bool boat = mode == TravelMode.Boat;
            RouteStop a = new(boat ? "Pier 11 / Wall Street" : "Trinity Place", shape[0]);
            RouteStop b = new(boat ? "DUMBO / Fulton Ferry" : "City Hall district", shape[^1]);
            return new(mode, [a, b], [Geo.Estimate(a, b, shape, boat ? 5 : 11,
                boat ? "Follow the East River ferry crossing" : "Follow the mapped subway alignment")],
                "Offline OpenFreeMap / OpenStreetMap alignment · estimated travel time", true);
        }
        return OsrmRouteProvider.Parse(mode, [new("Wall Street", new(40.70693, -74.00873)),
            new("City Hall Park", new(40.71236, -74.01045))], json.RootElement,
            "Offline FOSSGIS OSRM route · © OpenStreetMap contributors");
    }

    internal static bool TryMatch(TravelMode mode, IReadOnlyList<RouteStop> stops, out RoutePlan plan)
    {
        plan = ForMode(mode);
        return stops.Count == 2 && Geo.Distance(stops[0].Point, plan.Stops[0].Point) < 3 &&
            Geo.Distance(stops[1].Point, plan.Stops[1].Point) < 3;
    }
}
