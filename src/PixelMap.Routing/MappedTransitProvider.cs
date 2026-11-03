using PixelMap.Data;

namespace PixelMap.Routing;

/// <summary>Physical alignment preview from OpenMapTiles. Timetables, switching restrictions and service availability
/// belong to timetable-aware providers. Water polygons and unclassified rivers are never used as navigable links.</summary>
public sealed class MappedTransitProvider(VectorTileSource tiles) : IRouteProvider
{
    public bool Supports(TravelMode mode) => mode is TravelMode.Train or TravelMode.Boat;

    public async Task<RoutePlan> PlanAsync(TravelMode mode, IReadOnlyList<RouteStop> stops, CancellationToken token = default)
    {
        if (!Supports(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var tileIds = Cover(stops);
        var map = await Task.WhenAll(tileIds.Select(id => tiles.GetTileAsync(id, token))).ConfigureAwait(false);
        var graph = new TransitGraph(tileIds[0].Z);
        foreach (var tile in map)
        {
            token.ThrowIfCancellationRequested();
            foreach (var f in tile.Features)
            {
                if (f.Type != GeometryType.LineString || !Eligible(mode, f)) continue;
                graph.Add(tile.Id, f);
            }
        }
        return graph.Plan(mode, stops, token);
    }

    private static bool Eligible(TravelMode mode, MapFeature f)
    {
        if (f.Layer is not ("transportation" or "transportation_name"))
            return mode == TravelMode.Boat && f.Layer == "waterway" && f.Class == "fairway";
        return mode == TravelMode.Boat ? f.Class == "ferry" :
            (f.Class is "rail" or "transit") && f.Subclass is "rail" or "subway" or "light_rail" or "tram" or "narrow_gauge" or "monorail";
    }

    private static TileId[] Cover(IReadOnlyList<RouteStop> stops)
    {
        for (int z = 14; z >= 12; z--)
        {
            int n = 1 << z;
            var ids = new HashSet<TileId>();
            for (int i = 1; i < stops.Count; i++)
            {
                var (x1, y1) = XY(stops[i - 1].Point, n);
                var (x2, y2) = XY(stops[i].Point, n);
                x2 -= Math.Round((x2 - x1) / n) * n;
                int left = (int)Math.Floor(Math.Min(x1, x2) - .48), right = (int)Math.Floor(Math.Max(x1, x2) + .48);
                int top = Math.Clamp((int)Math.Floor(Math.Min(y1, y2) - .48), 0, n - 1);
                int bottom = Math.Clamp((int)Math.Floor(Math.Max(y1, y2) + .48), 0, n - 1);
                if ((long)(right - left + 1) * (bottom - top + 1) > 48) { ids.Clear(); break; }
                for (int x = left; x <= right; x++)
                    for (int y = top; y <= bottom; y++) ids.Add(new(z, (x % n + n) % n, y));
                if (ids.Count > 48) { ids.Clear(); break; }
            }
            if (ids.Count > 0) return ids.ToArray();
        }
        throw new RouteUnavailableException("The mapped-network preview covers a local corridor. Choose closer stops.");
    }

    private static (double X, double Y) XY(RoutePoint p, int n) =>
        ((p.Longitude + 180) / 360 * n,
         (1 - Math.Asinh(Math.Tan(Math.Clamp(p.Latitude, -85.05112878, 85.05112878) * Geo.Rad)) / Math.PI) * n / 2);
}
