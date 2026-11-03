using System.Net;
using System.Text.Json.Nodes;
using PixelMap.Routing;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var offline = new TestHandler((_, _) => throw new HttpRequestException("Offline"));
using var http = new HttpClient(offline);
var planner = new RoutePlanner(http);
foreach (var mode in Enum.GetValues<TravelMode>())
{
    var demo = RouteDemos.ForMode(mode);
    Check(demo.Geometry.Count > 2 && demo.DistanceMeters > 100 && demo.Duration > TimeSpan.Zero, $"Invalid {mode} demo.");
    var track = new RouteTrack(demo);
    Check(track.Sample(0).Point == demo.Geometry[0] && track.Sample(demo.DistanceMeters).Point.DistanceTo(demo.Geometry[^1]) < .01,
        $"{mode} linear referencing must reach exact endpoints.");
    var sample = track.Sample(track.Length * .42);
    var nearest = track.Nearest(sample.Point, track.Length * .42, 30);
    Check(Math.Abs(nearest.Station - track.Length * .42) < .5 && nearest.Error < .5, $"Round-trip station error for {mode}.");
    var limited = track.Nearest(track.Sample(track.Length).Point, 0, 5);
    Check(limited.Station is >= 0 and <= 5, "Projection must stay inside reachable station window.");
    for (int i = 0; i < 20; i++)
    {
        double station = track.Length * i / 20;
        var direction = track.GuidanceAt(station);
        if (direction.Next is { } next)
            Check(track.Sample(station + direction.MetersToNext).Point.DistanceTo(next.Point) < 5,
                $"{mode} maneuver station must match its geographic location.");
    }
    Check(demo.Legs[0].Steps.Count >= 2, "Each mode has maneuver or arrival information.");
}

foreach (var mode in new[] { TravelMode.Train, TravelMode.Boat })
{
    var example = RouteDemos.ForMode(mode);
    // Pick interior points from a real alignment so this exercises graph search independently of the saved demo.
    RouteStop[] stops = [new("Start", example.Geometry[1]), new("Via", example.Geometry[example.Geometry.Count / 2]),
        new("Finish", example.Geometry[^2])];
    var route = await planner.PlanAsync(mode, stops);
    Check(route.Legs.Count == 2 && route.IsEstimate && route.Geometry.Count >= 3, $"Expected a routed {mode} itinerary.");
    Check(route.Legs[0].Geometry[^1] == route.Legs[1].Geometry[0], "Consecutive legs share the same snapped via.");
    Console.WriteLine($"{mode} local mapped route: {route.DistanceMeters:N0} m, {route.Geometry.Count} nodes");
}
Check(offline.Requests == 0, "The local Manhattan transit graph must route entirely from the embedded tiles.");

var land = new RouteStop("Inland", new(40.709,-74.009));
try
{
    await planner.PlanAsync(TravelMode.Boat, [land, new("Wall Street", new(40.7075,-74.0095))]);
    throw new Exception("Boat planner crossed dry land.");
}
catch (RouteUnavailableException) { }

var dateLine = await planner.PlanAsync(TravelMode.Flight,
    [new("West", new(10,179.7)), new("East", new(10,-179.7)), new("North", new(11,-178))]);
Check(dateLine.Legs[0].DistanceMeters is > 60000 and < 75000, "Flight takes the short path across the antimeridian.");
var flightTrack = new RouteTrack(dateLine);
Check(Math.Abs(flightTrack.Sample(dateLine.Legs[0].DistanceMeters / 2).Point.Longitude) > 179.9,
    "Flight interpolation remains at the date line.");
Check(flightTrack.Sample(dateLine.Legs[0].DistanceMeters + 1).LegIndex == 1, "Stationing crosses the via stop.");
var beforeVia = flightTrack.GuidanceAt(dateLine.Legs[0].DistanceMeters - 5);
Check(beforeVia.LegIndex == 0 && beforeVia.Next?.ManeuverType == "arrive" && Math.Abs(beforeVia.MetersToNext - 5) < .001,
    "The approaching via instruction uses the first leg's provider station.");
var atVia = flightTrack.GuidanceAt(dateLine.Legs[0].DistanceMeters);
Check(atVia.LegIndex == 1 && atVia.Current?.ManeuverType == "depart" && atVia.Next?.Point == dateLine.Legs[1].Geometry[^1],
    "At an exact via boundary, guidance advances to the next leg.");

var demoRoad = RouteDemos.ForMode(TravelMode.Drive);
var savedRoad = await planner.PlanAsync(TravelMode.Drive, demoRoad.Stops);
Check(savedRoad.Source.Contains("Offline"), "An offline demo retains source identity.");
try
{
    await planner.PlanAsync(TravelMode.Walk, [land, new("Unknown", new(41,-72))]);
    throw new Exception("A disconnected route was fabricated.");
}
catch (RouteUnavailableException) { }

var noRoute = new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
{ Content = new StringContent("""{"code":"NoSegment","message":"No matching segment"}""") }));
using var unavailable = new HttpClient(noRoute);
try
{
    await new RoutePlanner(unavailable).PlanAsync(TravelMode.Bike, [land, new("In water", new(40.689,-74.021))]);
    throw new Exception("A rejected provider route was replaced.");
}
catch (RouteUnavailableException error) { Check(error.Message.Contains("500 m"), "Snap error should explain the search radius."); }
Check(noRoute.LastUri!.AbsolutePath.StartsWith("/routed-bike/route/v1/"), "Bike must use a dedicated bike instance.");
Check(noRoute.LastUri.Query.Contains("steps=true"), "Directions must be requested from OSRM.");

var broken = new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{ Content = new StringContent("""{"code":"Ok","routes":[]}""") }));
using var brokenHttp = new HttpClient(broken);
try
{
    await new RoutePlanner(brokenHttp).PlanAsync(TravelMode.Walk, [land, new("Destination", new(40.706,-74.005))]);
    throw new Exception("Malformed provider success escaped validation.");
}
catch (RouteUnavailableException e) { Check(e.InnerException != null, "Malformed provider replies preserve the cause."); }

// Recommended ordering comes from the OSRM profile. A geometrically identical recommendation
// with a different reported time is not a distinct alternative.
using var seedText = typeof(RouteDemos).Assembly.GetManifestResourceStream("PixelMap.Routing.Seed.car.json")!;
var envelope = JsonNode.Parse(seedText)!;
var rows = envelope["routes"]!.AsArray();
var duplicate = rows[0]!.DeepClone();
var detour = rows[0]!.DeepClone();
var detourLeg = detour["legs"]![0]!;
detourLeg["duration"] = 888;
detourLeg["steps"]![0]!["geometry"]!["coordinates"]![1]![1] = 40.70001;
rows.Add(duplicate); rows.Add(detour);
var alternateHandler = new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{ Content = new StringContent(envelope.ToJsonString()) }));
using var choicesHttp = new HttpClient(alternateHandler);
var choicePlanner = new RoutePlanner(choicesHttp);
var alternatives = await choicePlanner.PlanChoicesAsync(TravelMode.Drive, demoRoad.Stops);
Check(alternatives.Count == 2 && alternatives[1].Duration == TimeSpan.FromSeconds(888),
    "Preserve distinct detours, route metrics and recommended order.");
Check(alternateHandler.LastUri!.Query.Contains("alternatives=2"), "Two-stop directions request provider alternatives.");
Check(!alternatives[0].Geometry.SequenceEqual(alternatives[1].Geometry), "Route alternatives require different polylines.");
var primary = await choicePlanner.PlanAsync(TravelMode.Drive, demoRoad.Stops);
Check(ReferenceEquals(primary, alternatives[0]) && alternateHandler.Requests == 1, "A full choice set can satisfy a single-route call.");

var wrongLegs = envelope.DeepClone();
wrongLegs["routes"]![0]!["legs"]!.AsArray().Clear();
var wrongLegsHandler = new TestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{ Content = new StringContent(wrongLegs.ToJsonString()) }));
using var wrongLegsHttp = new HttpClient(wrongLegsHandler);
try
{
    await new RoutePlanner(wrongLegsHttp).PlanChoicesAsync(TravelMode.Drive, demoRoad.Stops);
    throw new Exception("An invalid provider itinerary was replaced by an offline example.");
}
catch (RouteUnavailableException error)
{
    Check(error.InnerException is InvalidDataException, "An invalid leg count is a malformed reply, preserving its cause.");
}

using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
int previousRequests = alternateHandler.Requests;
try
{
    await choicePlanner.PlanChoicesAsync(TravelMode.Drive, demoRoad.Stops, cancelled.Token);
    throw new Exception("Cancellation was ignored by the route cache.");
}
catch (OperationCanceledException) { }
Check(alternateHandler.Requests == previousRequests, "A cancelled choice request cannot make a network call.");

var guidance = new RouteTrack(primary);
var departure = guidance.GuidanceAt(0);
Check(departure.Current?.ManeuverType == "depart" && departure.Next != null && departure.MetersToNext > 0 &&
    departure.Current.BearingAfter.HasValue, "Directions expose current heading and upcoming maneuver.");
var approaching = guidance.GuidanceAt(departure.MetersToNext * .75);
Check(approaching.Next == departure.Next && approaching.MetersToNext < departure.MetersToNext,
    "Remaining maneuver distance decreases within the current step.");
Check(guidance.GuidanceAt(guidance.Length) is { Next: null, Current.ManeuverType: "arrive" },
    "The final guidance state is arrival.");
var allFlights = await choicePlanner.PlanChoicesAsync(TravelMode.Flight, dateLine.Stops);
Check(allFlights.Count == 1 && allFlights[0].Legs.Count == 2 && allFlights[0].Stops.SequenceEqual(dateLine.Stops),
    "Multiple stops preserve exact order and single-provider modes return one choice.");

if (args.Contains("--live"))
{
    using var live = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var service = new RoutePlanner(live);
    foreach (var mode in new[] { TravelMode.Drive, TravelMode.Bike, TravelMode.Walk })
    {
        var route = (await service.PlanChoicesAsync(mode, [new("Paris", new(48.86,2.35)), new("Via",new(48.858,2.355)),new("Arrival",new(48.856,2.36))]))[0];
        Check(route.Legs.Count == 2 && route.DistanceMeters > 500 && route.Geometry.Count > 3 && !route.Source.Contains("Offline"), "Live route missing.");
        Console.WriteLine($"{mode} Paris live: {route.DistanceMeters:N0} m, {route.Geometry.Count} points, {route.Legs.Sum(l=>l.Steps.Count)} maneuvers");
    }
    var nyc = await service.PlanChoicesAsync(TravelMode.Drive,
        [new("Wall Street",new(40.70693,-74.00873)), new("Metropolitan Museum",new(40.7794,-73.9732))]);
    Console.WriteLine("NYC public alternatives: " + string.Join(", ", nyc.Select(p => $"{p.DistanceMeters:N0} m / {p.Duration.TotalMinutes:F1} min")));
    Check(nyc.Count >= 1 && nyc.All(p => p.Geometry.Count > 3), "The real directions choice endpoint must return usable geometry.");
}
Console.WriteLine("All routing checks passed.");

sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    public int Requests { get; private set; }
    public Uri? LastUri { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++; LastUri = request.RequestUri;
        return callback(request, token);
    }
}
