using PixelMap.Data;

namespace PixelMap.Routing;

/// <summary>Joins tile geometry only at shared vertices on the same level and track family.
/// Clips buffered ways to their owner tile so long segments meet at tile boundaries.</summary>
internal sealed class TransitGraph(int zoom)
{
    private const int Extent = 4096;
    private readonly long world = (1L << zoom) * Extent;
    private readonly Dictionary<(long X, long Y, int Level, string Family), int> index = [];
    private readonly List<RoutePoint> points = [];
    private readonly List<List<(int To, double Length, string Name)>> adjacency = [];
    private readonly List<int> parents = [];
    private readonly List<Segment> segments = [];
    private readonly HashSet<(int A, int B)> edges = [];
    private readonly record struct Segment(int A, int B, string Name);
    private readonly record struct Snap(int Segment, double At, double Offset, RoutePoint Point);

    public void Add(TileId tile, MapFeature feature)
    {
        foreach (var path in feature.Paths)
        {
            for (int i = 1; i < path.Length; i++)
            {
                var a = path[i - 1]; var b = path[i];
                double low = 0, high = 1, dx = (double)b.X - a.X, dy = (double)b.Y - a.Y;
                if (!Clip(-dx, a.X, ref low, ref high) || !Clip(dx, feature.Extent - a.X, ref low, ref high) ||
                    !Clip(-dy, a.Y, ref low, ref high) || !Clip(dy, feature.Extent - a.Y, ref low, ref high) || low >= high) continue;
                int u = Vertex(tile, feature, a.X + dx * low, a.Y + dy * low);
                int v = Vertex(tile, feature, a.X + dx * high, a.Y + dy * high);
                if (u == v || !edges.Add((Math.Min(u, v), Math.Max(u, v)))) continue;
                string name = feature.Name.Length > 0 ? feature.Name : feature.Subclass.Length > 0 ? feature.Subclass.Replace('_', ' ') : feature.Class;
                Join(u, v, name);
                segments.Add(new(u, v, name));
                int ra = Root(u), rb = Root(v);
                if (ra != rb) parents[ra] = rb;
            }
        }
    }

    private static bool Clip(double direction, double distance, ref double lo, ref double hi)
    {
        if (direction == 0) return distance >= 0;
        double t = distance / direction;
        if (direction < 0) lo = Math.Max(lo, t); else hi = Math.Min(hi, t);
        return lo <= hi;
    }

    private int Vertex(TileId tile, MapFeature f, double x, double y)
    {
        long gx = tile.X * (long)Extent + (long)Math.Round(x * Extent / f.Extent);
        long gy = tile.Y * (long)Extent + (long)Math.Round(y * Extent / f.Extent);
        // Border intersections on adjacent quantized vector tiles can differ by one MVT unit.
        if (gx % Extent == 0) gy = (long)Math.Round(gy / 4d) * 4;
        if (gy % Extent == 0) gx = (long)Math.Round(gx / 4d) * 4;
        gx = (gx + world) % world;
        var key = (gx, gy, f.Level, f.Subclass.Length > 0 ? f.Subclass : f.Class);
        if (index.TryGetValue(key, out int existing)) return existing;
        int id = New(new(Math.Atan(Math.Sinh(Math.PI * (1 - 2.0 * gy / world))) / Geo.Rad, gx * 360.0 / world - 180));
        index.Add(key, id);
        return id;
    }

    private int New(RoutePoint p)
    {
        int id = points.Count;
        points.Add(p); adjacency.Add([]); parents.Add(id);
        return id;
    }

    private int Root(int id)
    {
        while (id != parents[id]) { parents[id] = parents[parents[id]]; id = parents[id]; }
        return id;
    }

    private void Join(int a, int b, string name)
    {
        double length = Geo.Distance(points[a], points[b]);
        adjacency[a].Add((b, length, name)); adjacency[b].Add((a, length, name));
    }

    private Dictionary<int, Snap> Candidates(RoutePoint target, double max)
    {
        var result = new Dictionary<int, Snap>();
        double cos = Math.Cos(target.Latitude * Geo.Rad);
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i]; var a = points[s.A]; var b = points[s.B];
            double dx = Math.IEEERemainder(b.Longitude - a.Longitude, 360) * cos, dy = b.Latitude - a.Latitude;
            double px = Math.IEEERemainder(target.Longitude - a.Longitude, 360) * cos, py = target.Latitude - a.Latitude;
            double t = Math.Clamp((px * dx + py * dy) / Math.Max(dx * dx + dy * dy, 1e-20), 0, 1);
            double offset = Math.Sqrt(Math.Pow(px - t * dx, 2) + Math.Pow(py - t * dy, 2)) * Geo.Rad * Geo.Radius;
            if (offset > max) continue;
            int component = Root(s.A);
            if (!result.TryGetValue(component, out var prior) || offset < prior.Offset)
                result[component] = new(i, t, offset, Geo.Interpolate(a, b, t));
        }
        return result;
    }

    public RoutePlan Plan(TravelMode mode, IReadOnlyList<RouteStop> stops, CancellationToken token)
    {
        double radius = mode == TravelMode.Boat ? 350 : 220;
        var snaps = stops.Select(s => Candidates(s.Point, radius)).ToArray();
        string network = mode == TravelMode.Boat ? "ferry or designated fairway" : "rail";
        if (snaps.Any(s => s.Count == 0))
            throw new RouteUnavailableException($"Place each stop within {radius:0} m of a mapped {network} alignment.");
        int component = snaps[0].Keys.Where(key => snaps.All(s => s.ContainsKey(key)))
            .OrderBy(key => snaps.Sum(s => s[key].Offset)).FirstOrDefault(-1);
        if (component < 0) throw new RouteUnavailableException($"No connected {network} alignment joins these stops in the loaded corridor.");
        int[] stopsOnGraph = new int[stops.Count];
        var cuts = snaps.Select((s, stop) => (Snap: s[component], Stop: stop)).GroupBy(s => s.Snap.Segment);
        foreach (var group in cuts)
        {
            var edge = segments[group.Key];
            int previous = edge.A;
            foreach (var item in group.OrderBy(c => c.Snap.At))
            {
                int node = item.Snap.At <= 1e-9 ? edge.A : item.Snap.At >= 1 - 1e-9 ? edge.B : New(item.Snap.Point);
                stopsOnGraph[item.Stop] = node;
                if (previous != node) Join(previous, node, edge.Name);
                previous = node;
            }
            if (previous != edge.B) Join(previous, edge.B, edge.Name);
        }
        var legs = new List<RouteLeg>();
        for (int i = 1; i < stops.Count; i++)
        {
            var nodes = Path(stopsOnGraph[i - 1], stopsOnGraph[i], token);
            if (nodes.Count < 2) throw new RouteUnavailableException("The stops snap to the same point on the network.");
            var geometry = nodes.Select(n => points[n]).ToArray();
            var steps = new List<RouteStep>();
            double distance = 0, groupLength = 0, speed = mode == TravelMode.Boat ? 5 : 11;
            string current = ""; int groupStart = 0;
            for (int j = 1; j < nodes.Count; j++)
            {
                var edge = adjacency[nodes[j - 1]].First(e => e.To == nodes[j]);
                if (current.Length > 0 && current != edge.Name)
                {
                    steps.Add(Follow(current, groupStart, groupLength));
                    groupLength = 0; groupStart = j - 1;
                }
                current = edge.Name;
                distance += edge.Length; groupLength += edge.Length;
            }
            steps.Add(Follow(current, groupStart, groupLength));
            steps.Add(new("Arrive at " + stops[i].Name, geometry[^1], 0, TimeSpan.Zero)
                { ManeuverType = "arrive", BearingBefore = geometry[^2].BearingTo(geometry[^1]) });
            legs.Add(new(stops[i - 1], stops[i], geometry, distance, TimeSpan.FromSeconds(distance / speed), steps.ToArray()));

            RouteStep Follow(string name, int at, double meters) =>
                new("Follow " + name, geometry[at], meters, TimeSpan.FromSeconds(meters / speed))
                {
                    ManeuverType = at == 0 ? "depart" : "continue",
                    BearingBefore = at == 0 ? null : geometry[at - 1].BearingTo(geometry[at]),
                    BearingAfter = geometry[at].BearingTo(geometry[at + 1])
                };
        }
        return new(mode, stops, legs, "OpenFreeMap / OpenStreetMap physical alignment · estimated time, no timetable", true);
    }

    private List<int> Path(int start, int end, CancellationToken token)
    {
        var distance = new double[points.Count]; Array.Fill(distance, double.PositiveInfinity);
        int[] parent = new int[points.Count];
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(start, 0); distance[start] = 0;
        var closed = new bool[points.Count];
        while (queue.TryDequeue(out int node, out _))
        {
            if (closed[node]) continue;
            if (node == end)
            {
                var path = new List<int> { end };
                while (path[^1] != start) path.Add(parent[path[^1]]);
                path.Reverse(); return path;
            }
            closed[node] = true;
            token.ThrowIfCancellationRequested();
            foreach (var edge in adjacency[node])
            {
                double next = distance[node] + edge.Length;
                if (next >= distance[edge.To]) continue;
                distance[edge.To] = next; parent[edge.To] = node;
                queue.Enqueue(edge.To, next + Geo.Distance(points[edge.To], points[end]));
            }
        }
        throw new RouteUnavailableException("The mapped alignment is disconnected.");
    }
}
