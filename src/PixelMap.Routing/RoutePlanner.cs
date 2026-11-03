using PixelMap.Data;

namespace PixelMap.Routing;

/// <summary>Plans stops in user order. Supply custom providers before the defaults to override a mode.</summary>
public sealed class RoutePlanner
{
    private readonly IRouteProvider[] providers;

    public RoutePlanner(HttpClient client, VectorTileSource? tiles = null, IEnumerable<IRouteProvider>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        providers = [.. overrides ?? [], new OsrmRouteProvider(client),
            new MappedTransitProvider(tiles ?? new VectorTileSource(client)), new FlightProvider()];
    }

    public async Task<RoutePlan> PlanAsync(TravelMode mode, IReadOnlyList<RouteStop> stops, CancellationToken token = default) =>
        (await Plan(mode, stops, false, token).ConfigureAwait(false))[0];

    public Task<IReadOnlyList<RoutePlan>> PlanChoicesAsync(TravelMode mode, IReadOnlyList<RouteStop> stops, CancellationToken token = default) =>
        Plan(mode, stops, true, token);

    private async Task<IReadOnlyList<RoutePlan>> Plan(TravelMode mode, IReadOnlyList<RouteStop> stops, bool alternatives, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stops);
        token.ThrowIfCancellationRequested();
        var ordered = stops.ToArray();
        if (ordered.Length is < 2 or > 24 || ordered.Any(s => s is null || !s.Point.IsValid))
            throw new ArgumentException("Choose 2 to 24 valid geographic stops.", nameof(stops));
        if (ordered.Zip(ordered.Skip(1)).Any(pair => Geo.Distance(pair.First.Point, pair.Second.Point) < 1))
            throw new RouteUnavailableException("Adjacent stops must be at least one metre apart.");
        var provider = providers.FirstOrDefault(p => p.Supports(mode))
            ?? throw new RouteUnavailableException("No provider is configured for this travel mode.");
        try
        {
            if (alternatives && ordered.Length == 2 && provider is IRouteChoiceProvider choices)
                return await choices.PlanChoicesAsync(mode, ordered, token).ConfigureAwait(false);
            return [await provider.PlanAsync(mode, ordered, token).ConfigureAwait(false)];
        }
        catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            if (RouteDemos.TryMatch(mode, ordered, out var example)) return [example];
            throw new RouteUnavailableException("The route source is unavailable. Retry or choose the offline example for this mode.", error);
        }
    }
}

internal sealed class FlightProvider : IRouteProvider
{
    public bool Supports(TravelMode mode) => mode == TravelMode.Flight;

    public Task<RoutePlan> PlanAsync(TravelMode mode, IReadOnlyList<RouteStop> stops, CancellationToken token = default)
    {
        var legs = new List<RouteLeg>();
        for (int i = 1; i < stops.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var a = stops[i - 1]; var b = stops[i];
            int segments = Math.Max(2, (int)Math.Ceiling(Geo.Distance(a.Point, b.Point) / 15000));
            var line = new RoutePoint[segments + 1];
            for (int j = 0; j <= segments; j++) line[j] = Geo.Interpolate(a.Point, b.Point, (double)j / segments);
            legs.Add(Geo.Estimate(a, b, line, 220, "Fly the great-circle course to " + b.Name));
        }
        return Task.FromResult(new RoutePlan(mode, stops, legs, "Direct great-circle flight · estimated cruise time", true));
    }
}
