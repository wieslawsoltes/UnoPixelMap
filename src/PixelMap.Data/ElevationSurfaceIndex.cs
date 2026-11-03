using System.Runtime.CompilerServices;

namespace PixelMap.Data;

/// <summary>Water classification shared by immutable snapshots. A weak key releases prepared
/// polygons with their source tile. Broad polygons are stored once, while small polygons occupy
/// at most 64 cells in the fixed spatial grid.</summary>
internal sealed class ElevationSurfaceIndex
{
    private const int GridSize = 16;
    private static readonly ConditionalWeakTable<MapTile, ElevationSurfaceIndex> Cache = new();
    private readonly Polygon[] _polygons;
    private readonly int[][] _cells;
    private readonly int[] _broad;

    internal static ElevationSurfaceIndex For(MapTile tile) => Cache.GetValue(tile, static source => new(source));

    private ElevationSurfaceIndex(MapTile tile)
    {
        _polygons = tile.Features.Where(feature => feature.Layer == "water" &&
            feature.Type == GeometryType.Polygon && feature.Extent > 0 && feature.Paths.Length > 0)
            .Select(feature => new Polygon(feature)).ToArray();
        var cells = new List<int>?[GridSize * GridSize];
        var broad = new List<int>();
        for (int index = 0; index < _polygons.Length; index++)
        {
            var polygon = _polygons[index];
            if (polygon.Right < 0 || polygon.Left > 1 || polygon.Bottom < 0 || polygon.Top > 1) continue;
            int left = Cell(polygon.Left), right = Cell(polygon.Right);
            int top = Cell(polygon.Top), bottom = Cell(polygon.Bottom);
            if ((right - left + 1) * (bottom - top + 1) > 64) broad.Add(index);
            else
                for (int y = top; y <= bottom; y++)
                for (int x = left; x <= right; x++)
                    (cells[y * GridSize + x] ??= []).Add(index);
        }
        _broad = broad.ToArray();
        _cells = cells.Select(cell => cell?.ToArray() ?? []).ToArray();
    }

    internal bool IsOcean(double x, double y)
    {
        bool ocean = false;
        foreach (int index in _broad)
        {
            var polygon = _polygons[index];
            if (!polygon.Contains(x, y)) continue;
            if (!polygon.Ocean) return false;
            ocean = true;
        }
        foreach (int index in _cells[Cell(y) * GridSize + Cell(x)])
        {
            var polygon = _polygons[index];
            if (!polygon.Contains(x, y)) continue;
            if (!polygon.Ocean) return false;
            ocean = true;
        }
        return ocean;
    }

    internal bool MayContainOcean(double left, double top, double right, double bottom)
    {
        foreach (int index in _broad)
            if (_polygons[index].OceanIntersects(left, top, right, bottom)) return true;
        int firstX = Cell(left), lastX = Cell(right), firstY = Cell(top), lastY = Cell(bottom);
        for (int y = firstY; y <= lastY; y++)
        for (int x = firstX; x <= lastX; x++)
            foreach (int index in _cells[y * GridSize + x])
                if (_polygons[index].OceanIntersects(left, top, right, bottom)) return true;
        return false;
    }

    private static int Cell(double coordinate) => Math.Clamp((int)Math.Floor(coordinate * GridSize), 0, GridSize - 1);

    private sealed class Polygon
    {
        internal double Left { get; } = double.PositiveInfinity;
        internal double Right { get; } = double.NegativeInfinity;
        internal double Top { get; } = double.PositiveInfinity;
        internal double Bottom { get; } = double.NegativeInfinity;
        internal bool Ocean { get; }
        private readonly EdgeNode? _edges;

        internal Polygon(MapFeature feature)
        {
            Ocean = feature.Class is "ocean" or "sea" || feature.Subclass is "ocean" or "sea";
            var edges = new List<Edge>();
            double scale = 1d / feature.Extent;
            foreach (var ring in feature.Paths)
            {
                for (int i = 0, previous = ring.Length - 1; i < ring.Length; previous = i++)
                {
                    double x = ring[i].X * scale, y = ring[i].Y * scale;
                    Left = Math.Min(Left, x); Right = Math.Max(Right, x);
                    Top = Math.Min(Top, y); Bottom = Math.Max(Bottom, y);
                    double priorX = ring[previous].X * scale, priorY = ring[previous].Y * scale;
                    if (y == priorY) continue;
                    edges.Add(y < priorY ? new(y, priorY, x, (priorX - x) / (priorY - y))
                        : new(priorY, y, priorX, (x - priorX) / (y - priorY)));
                }
            }
            _edges = EdgeNode.Build(edges);
        }

        internal bool Contains(double x, double y) =>
            x >= Left && x <= Right && y >= Top && y < Bottom && _edges?.Crossings(x, y) == true;

        internal bool OceanIntersects(double left, double top, double right, double bottom) =>
            Ocean && _edges is not null && left <= Right && right >= Left && top <= Bottom && bottom >= Top;
    }

    private readonly record struct Edge(double Minimum, double Maximum, double X, double Slope)
    {
        internal bool Crosses(double x, double y) => x < X + (y - Minimum) * Slope;
    }

    /// <summary>A Y-interval tree visits only edges crossing the sample's horizontal ray.
    /// Each edge appears in one node and two sorted views, bounding index storage linearly.</summary>
    private sealed class EdgeNode(double center, Edge[] byMinimum, Edge[] byMaximum, EdgeNode? left, EdgeNode? right)
    {
        internal static EdgeNode? Build(List<Edge> edges)
        {
            if (edges.Count == 0) return null;
            edges.Sort(static (a, b) => (a.Minimum + a.Maximum).CompareTo(b.Minimum + b.Maximum));
            var median = edges[edges.Count / 2];
            double center = (median.Minimum + median.Maximum) * .5;
            var left = new List<Edge>();
            var right = new List<Edge>();
            var middle = new List<Edge>();
            foreach (var edge in edges)
            {
                if (edge.Maximum <= center) left.Add(edge);
                else if (edge.Minimum > center) right.Add(edge);
                else middle.Add(edge);
            }
            return new(center, middle.OrderBy(edge => edge.Minimum).ToArray(),
                middle.OrderByDescending(edge => edge.Maximum).ToArray(), Build(left), Build(right));
        }

        internal bool Crossings(double x, double y)
        {
            bool inside = false;
            if (y < center)
            {
                foreach (var edge in byMinimum)
                {
                    if (edge.Minimum > y) break;
                    inside ^= edge.Crosses(x, y);
                }
                return inside ^ (left?.Crossings(x, y) ?? false);
            }
            foreach (var edge in byMaximum)
            {
                if (edge.Maximum <= y) break;
                inside ^= edge.Crosses(x, y);
            }
            return inside ^ (right?.Crossings(x, y) ?? false);
        }
    }
}
