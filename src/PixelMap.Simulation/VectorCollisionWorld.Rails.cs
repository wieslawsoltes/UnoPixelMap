using PixelMap.Data;
using PixelMap.Routing;

namespace PixelMap.Simulation;

public sealed partial class VectorCollisionWorld
{
    private RailNetwork? _railNetwork;

    public bool TryMoveOnRails(RoutePoint from, double bearing, double distanceMeters, double steering,
        out RoutePoint point, out double heading)
    {
        var tiles = _scene.Tiles;
        var network = _railNetwork;
        if (network is null || !network.Matches(tiles)) _railNetwork = network = new(tiles);
        return network.Move(from, bearing, distanceMeters, steering, out point, out heading);
    }

    /// <summary>A streamed, level-aware physical rail graph. Track joins use source
    /// vertices and clipped tile-border intersections, so geometric overpasses stay separate.</summary>
    private sealed class RailNetwork
    {
        private const long WorldUnits = 1L << 26;
        private const int IndexCells = 4096;
        private readonly MapTile[] sources;
        private readonly Dictionary<(long X, long Y, int Level, string Family), int> vertices = [];
        private readonly List<RoutePoint> points = [];
        private readonly List<List<int>> adjacency = [];
        private readonly List<Edge> edges = [];
        private readonly Dictionary<(int X, int Y), List<int>> cells = [];
        private readonly HashSet<(int A, int B)> uniqueEdges = [];
        private readonly record struct Edge(int A, int B, double Length);
        private readonly record struct Snap(int Edge, double Fraction, double Error);

        internal RailNetwork(IReadOnlyList<MapTile> tiles)
        {
            sources = tiles.ToArray();
            int zoom = tiles.Count == 0 ? 0 : tiles.Max(tile => tile.Id.Z);
            foreach (var tile in tiles)
            {
                if (tile.Id.Z != zoom || tile.Id.Z < 12) continue;
                foreach (var feature in tile.Features)
                {
                    if (feature.Layer != "transportation" || feature.Type != GeometryType.LineString || feature.Extent <= 0 ||
                        feature.Class is not ("rail" or "transit") ||
                        feature.Subclass is not ("rail" or "subway" or "light_rail" or "tram" or "narrow_gauge" or "monorail")) continue;
                    foreach (var path in feature.Paths)
                    for (int i = 1; i < path.Length; i++)
                    {
                        var a = path[i - 1];
                        var b = path[i];
                        double dx = (double)b.X - a.X, dy = (double)b.Y - a.Y, low = 0, high = 1;
                        if (!Clip(-dx, a.X, ref low, ref high) || !Clip(dx, feature.Extent - a.X, ref low, ref high) ||
                            !Clip(-dy, a.Y, ref low, ref high) || !Clip(dy, feature.Extent - a.Y, ref low, ref high) || low >= high) continue;
                        int first = Vertex(tile.Id, feature, a.X + dx * low, a.Y + dy * low);
                        int last = Vertex(tile.Id, feature, a.X + dx * high, a.Y + dy * high);
                        if (first == last || !uniqueEdges.Add((Math.Min(first, last), Math.Max(first, last)))) continue;
                        double length = points[first].DistanceTo(points[last]);
                        if (length < .01) continue;
                        int edge = edges.Count;
                        edges.Add(new(first, last, length));
                        adjacency[first].Add(edge);
                        adjacency[last].Add(edge);
                        Index(edge);
                    }
                }
            }
        }

        internal bool Matches(IReadOnlyList<MapTile> tiles)
        {
            if (sources.Length != tiles.Count) return false;
            bool ordered = true;
            for (int i = 0; i < sources.Length; i++) ordered &= ReferenceEquals(sources[i], tiles[i]);
            if (ordered) return true;
            var previous = new HashSet<MapTile>(sources, ReferenceEqualityComparer.Instance);
            return previous.Count == tiles.Count && tiles.All(previous.Contains);
        }

        internal bool Move(RoutePoint from, double bearing, double distance, double steering,
            out RoutePoint point, out double heading)
        {
            point = from;
            heading = bearing;
            if (!from.IsValid || !double.IsFinite(bearing) || !double.IsFinite(distance) || !double.IsFinite(steering)) return false;
            var snap = Find(from, bearing, distance == 0 ? 220 : 3);
            if (snap is null) return false;
            var current = edges[snap.Value.Edge];
            int edgeId = snap.Value.Edge;
            double fraction = snap.Value.Fraction;
            double forward = points[current.A].BearingTo(points[current.B]);
            bool reversed = distance < 0;
            double travelBearing = RoutePoint.Wrap(bearing + (reversed ? 180 : 0));
            bool towardB = Math.Abs(RoutePoint.Wrap(forward - travelBearing)) <= 90;
            double remaining = Math.Abs(distance);
            point = points[current.A].InterpolateTo(points[current.B], fraction);

            // A fixed-step train moves less than one metre at its normal top speed.
            // The bound also protects custom high-speed profiles and degenerate networks.
            for (int transition = 0; transition < 64; transition++)
            {
                double available = current.Length * (towardB ? 1 - fraction : fraction);
                if (remaining <= available + .000001)
                {
                    fraction += (towardB ? 1 : -1) * remaining / current.Length;
                    point = points[current.A].InterpolateTo(points[current.B], Math.Clamp(fraction, 0, 1));
                    travelBearing = towardB ? forward : RoutePoint.Wrap(forward + 180);
                    heading = RoutePoint.Wrap(travelBearing + (reversed ? 180 : 0));
                    return true;
                }
                remaining -= available;
                int vertex = towardB ? current.B : current.A;
                point = points[vertex];
                travelBearing = towardB ? forward : RoutePoint.Wrap(forward + 180);
                int next = Choose(vertex, edgeId, travelBearing, Math.Clamp(steering, -1, 1));
                if (next < 0)
                {
                    heading = RoutePoint.Wrap(travelBearing + (reversed ? 180 : 0));
                    return from.DistanceTo(point) > .000001 || remaining <= .02;
                }
                edgeId = next;
                current = edges[edgeId];
                towardB = current.A == vertex;
                fraction = towardB ? 0 : 1;
                forward = points[current.A].BearingTo(points[current.B]);
            }
            heading = RoutePoint.Wrap(travelBearing + (reversed ? 180 : 0));
            return true;
        }

        private int Choose(int vertex, int previous, double bearing, double steering)
        {
            int selected = -1;
            double score = double.PositiveInfinity;
            foreach (int candidate in adjacency[vertex])
            {
                if (candidate == previous) continue;
                var edge = edges[candidate];
                int target = edge.A == vertex ? edge.B : edge.A;
                double turn = RoutePoint.Wrap(points[vertex].BearingTo(points[target]) - bearing);
                // Neutral steering chooses the straightest continuation. A held left or
                // right input biases the matching branch without allowing a U-turn.
                if (Math.Abs(turn) > 135) continue;
                double weight = Math.Abs(turn - steering * 70);
                if (weight >= score) continue;
                score = weight;
                selected = candidate;
            }
            return selected;
        }

        private Snap? Find(RoutePoint point, double heading, double radius)
        {
            var cell = Cell(point);
            Snap? selected = null;
            double score = double.PositiveInfinity;
            double cosine = Math.Cos(point.Latitude * Math.PI / 180);
            for (int y = cell.Y - 1; y <= cell.Y + 1; y++)
            for (int x = cell.X - 1; x <= cell.X + 1; x++)
            {
                if (!cells.TryGetValue((WrapCell(x), y), out var candidates)) continue;
                foreach (int index in candidates)
                {
                    var edge = edges[index];
                    var a = points[edge.A];
                    var b = points[edge.B];
                    double dx = RoutePoint.Wrap(b.Longitude - a.Longitude) * cosine;
                    double dy = b.Latitude - a.Latitude;
                    double px = RoutePoint.Wrap(point.Longitude - a.Longitude) * cosine;
                    double py = point.Latitude - a.Latitude;
                    double fraction = Math.Clamp((px * dx + py * dy) / Math.Max(1e-20, dx * dx + dy * dy), 0, 1);
                    var hit = a.InterpolateTo(b, fraction);
                    double error = point.DistanceTo(hit);
                    if (error > radius) continue;
                    double angle = Math.Abs(RoutePoint.Wrap(a.BearingTo(b) - heading));
                    angle = Math.Min(angle, 180 - angle);
                    double weight = error + angle * .01;
                    if (weight >= score) continue;
                    score = weight;
                    selected = new(index, fraction, error);
                }
            }
            return selected;
        }

        private int Vertex(TileId tile, MapFeature feature, double x, double y)
        {
            double count = 1 << tile.Z;
            long gx = (long)Math.Round((tile.X + x / feature.Extent) / count * WorldUnits);
            long gy = (long)Math.Round((tile.Y + y / feature.Extent) / count * WorldUnits);
            if (Math.Abs(x) < 1e-7 || Math.Abs(x - feature.Extent) < 1e-7) gy = (long)Math.Round(gy / 4d) * 4;
            if (Math.Abs(y) < 1e-7 || Math.Abs(y - feature.Extent) < 1e-7) gx = (long)Math.Round(gx / 4d) * 4;
            gx = (gx % WorldUnits + WorldUnits) % WorldUnits;
            var key = (gx, gy, feature.Level, feature.Subclass);
            if (vertices.TryGetValue(key, out int existing)) return existing;
            int id = points.Count;
            points.Add(new(Math.Atan(Math.Sinh(Math.PI * (1 - 2d * gy / WorldUnits))) * 180 / Math.PI,
                gx * 360d / WorldUnits - 180));
            adjacency.Add([]);
            vertices.Add(key, id);
            return id;
        }

        private void Index(int edgeId)
        {
            var edge = edges[edgeId];
            var a = Cell(points[edge.A]);
            var b = Cell(points[edge.B]);
            int bx = b.X;
            if (bx - a.X > IndexCells / 2) bx -= IndexCells;
            if (a.X - bx > IndexCells / 2) bx += IndexCells;
            for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
            for (int x = Math.Min(a.X, bx); x <= Math.Max(a.X, bx); x++)
            {
                var key = (WrapCell(x), y);
                if (!cells.TryGetValue(key, out var bucket)) cells.Add(key, bucket = []);
                bucket.Add(edgeId);
            }
        }

        private static (int X, int Y) Cell(RoutePoint point)
        {
            double latitude = Math.Clamp(point.Latitude, -85.05112878, 85.05112878) * Math.PI / 180;
            return (WrapCell((int)Math.Floor((point.Longitude + 180) / 360 * IndexCells)),
                (int)Math.Floor((1 - Math.Asinh(Math.Tan(latitude)) / Math.PI) * .5 * IndexCells));
        }

        private static int WrapCell(int cell) => (cell % IndexCells + IndexCells) % IndexCells;

        private static bool Clip(double direction, double distance, ref double low, ref double high)
        {
            if (direction == 0) return distance >= 0;
            double fraction = distance / direction;
            if (direction < 0) low = Math.Max(low, fraction); else high = Math.Min(high, fraction);
            return low <= high;
        }
    }
}
