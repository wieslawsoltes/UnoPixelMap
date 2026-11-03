using System.Diagnostics;
using System.Text.Json;

namespace PixelMap.Routing;

/// <summary>FOSSGIS hosts separate OSRM graphs for each profile. The public service is limited to one request per second.</summary>
public sealed class OsrmRouteProvider(HttpClient client) : IRouteChoiceProvider
{
    private static readonly SemaphoreSlim Pace = new(1);
    private static long lastStart;
    private readonly Dictionary<TravelMode, (long Timestamp, bool WithChoices, IReadOnlyList<RoutePlan> Plans)> recent = [];

    public bool Supports(TravelMode mode) => mode is TravelMode.Walk or TravelMode.Bike or TravelMode.Drive;

    public async Task<RoutePlan> PlanAsync(TravelMode mode, IReadOnlyList<RouteStop> stops, CancellationToken token = default) =>
        (await Fetch(mode, stops, false, token).ConfigureAwait(false))[0];

    public Task<IReadOnlyList<RoutePlan>> PlanChoicesAsync(TravelMode mode, IReadOnlyList<RouteStop> stops, CancellationToken token = default) =>
        Fetch(mode, stops, stops.Count == 2, token);

    private async Task<IReadOnlyList<RoutePlan>> Fetch(TravelMode mode, IReadOnlyList<RouteStop> stops, bool alternatives, CancellationToken token)
    {
        string profile = mode switch
        {
            TravelMode.Walk => "foot", TravelMode.Bike => "bike", TravelMode.Drive => "car",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        token.ThrowIfCancellationRequested();
        lock (recent)
        {
            if (recent.TryGetValue(mode, out var entry) && Stopwatch.GetElapsedTime(entry.Timestamp) < TimeSpan.FromMinutes(30)
                && (!alternatives || entry.WithChoices) && entry.Plans[0].Stops.SequenceEqual(stops)) return entry.Plans;
        }
        var coordinates = string.Join(";", stops.Select(s => FormattableString.Invariant($"{s.Point.Longitude:F6},{s.Point.Latitude:F6}")));
        var radiuses = string.Join(";", Enumerable.Repeat("500", stops.Count));
        string url = $"https://routing.openstreetmap.de/routed-{profile}/route/v1/driving/{coordinates}?overview=false&steps=true&geometries=geojson&generate_hints=false&radiuses={radiuses}";
        if (alternatives) url += "&alternatives=2";
        await Pace.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var wait = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(lastStart);
            if (lastStart != 0 && wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            lastStart = Stopwatch.GetTimestamp();
        }
        finally { Pace.Release(); }
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Web Fetch sets its own user agent and referrer. Native consumers identify this app explicitly.
        if (!OperatingSystem.IsBrowser()) request.Headers.UserAgent.ParseAdd("UnoPixelMap/1.0 (+https://github.com/wieslawsoltes/UnoPixelMap)");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if ((int)response.StatusCode != 400) response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, token).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        try
        {
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var root = json.RootElement;
            const string source = "FOSSGIS OSRM · © OpenStreetMap contributors";
            var first = Parse(mode, stops, root, source);
            var plans = new List<RoutePlan> { first };
            if (alternatives)
            {
                var routes = root.GetProperty("routes");
                for (int i = 1; i < routes.GetArrayLength() && plans.Count < 3; i++)
                {
                    var alternative = ReadRoute(mode, stops, routes[i], source);
                    if (!plans.Any(p => p.Geometry.SequenceEqual(alternative.Geometry))) plans.Add(alternative);
                }
            }
            var result = plans.AsReadOnly();
            if (plans.Sum(p => (long)p.Geometry.Count) <= 10000)
            {
                lock (recent) recent[mode] = (Stopwatch.GetTimestamp(), alternatives, result);
            }
            return result;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException or KeyNotFoundException
            or InvalidOperationException or IndexOutOfRangeException or FormatException or OverflowException)
        {
            throw new RouteUnavailableException("The route provider returned incomplete directions. Retry the trip.", e);
        }
    }

    internal static RoutePlan Parse(TravelMode mode, IReadOnlyList<RouteStop> stops, JsonElement json, string source)
    {
        if (Field(json, "code") != "Ok")
            throw new RouteUnavailableException(Field(json, "code") == "NoSegment"
                ? "A stop is more than 500 m from a routable path for this mode."
                : "The route provider could not connect all stops using this travel mode.");
        return ReadRoute(mode, stops, json.GetProperty("routes")[0], source);
    }

    private static RoutePlan ReadRoute(TravelMode mode, IReadOnlyList<RouteStop> stops, JsonElement route, string source)
    {
        var raw = route.GetProperty("legs");
        if (raw.GetArrayLength() != stops.Count - 1) throw new InvalidDataException("Unexpected number of route legs.");
        var legs = new List<RouteLeg>();
        foreach (var leg in raw.EnumerateArray())
        {
            var shape = new List<RoutePoint>();
            var steps = new List<RouteStep>();
            foreach (var step in leg.GetProperty("steps").EnumerateArray())
            {
                var maneuver = step.GetProperty("maneuver");
                foreach (var coordinate in step.GetProperty("geometry").GetProperty("coordinates").EnumerateArray())
                {
                    var point = Point(coordinate);
                    if (shape.Count == 0 || shape[^1] != point) shape.Add(point);
                }
                steps.Add(new(Instruction(step, maneuver), Point(maneuver.GetProperty("location")),
                    step.GetProperty("distance").GetDouble(), TimeSpan.FromSeconds(step.GetProperty("duration").GetDouble()))
                {
                    ManeuverType = Field(maneuver, "type"), Modifier = Field(maneuver, "modifier"),
                    BearingBefore = Number(maneuver, "bearing_before"), BearingAfter = Number(maneuver, "bearing_after"),
                    Exit = maneuver.TryGetProperty("exit", out var exit) ? exit.GetInt32() : null
                });
            }
            if (shape.Count == 0) throw new InvalidDataException("Missing OSRM route geometry.");
            legs.Add(new(stops[legs.Count], stops[legs.Count + 1], shape.ToArray(), leg.GetProperty("distance").GetDouble(),
                TimeSpan.FromSeconds(leg.GetProperty("duration").GetDouble()), steps.ToArray()));
        }
        return new(mode, stops, legs, source, false);
    }

    private static double? Number(JsonElement e, string name) => e.TryGetProperty(name, out var number) && number.TryGetDouble(out var value) ? value : null;
    private static RoutePoint Point(JsonElement c) => new(c[1].GetDouble(), c[0].GetDouble());
    private static string Field(JsonElement e, string key) =>
        e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Instruction(JsonElement step, JsonElement maneuver)
    {
        var modifier = Field(maneuver, "modifier");
        var side = modifier.Length == 0 ? "" : " " + modifier;
        string? street = Field(step, "name");
        if (street.Length == 0) street = Field(step, "ref");
        string road = street.Length == 0 ? "" : " onto " + street;
        return Field(maneuver, "type") switch
        {
            "depart" => street.Length > 0 ? "Start on " + street : "Depart",
            "arrive" => "Arrive at the stop",
            "roundabout" or "rotary" => (maneuver.TryGetProperty("exit", out var exit)
                ? $"Take exit {exit.GetInt32()} at the roundabout" : "Enter the roundabout") + road,
            "exit roundabout" or "exit rotary" => "Exit the roundabout" + road,
            "merge" => "Merge" + side + road,
            "fork" => "Keep" + side + road,
            "on ramp" or "off ramp" => "Take the ramp" + side + road,
            "continue" or "new name" or "notification" => "Continue" + side + road,
            _ when modifier == "uturn" => "Make a U-turn" + road,
            _ => "Turn" + side + road
        };
    }
}
