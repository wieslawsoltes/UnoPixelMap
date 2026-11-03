using System.Runtime.CompilerServices;
using PixelMap.Data;
using PixelMap.Routing;

namespace PixelMap.Simulation;

/// <summary>Swept building contacts and shoreline barriers against streamed cartography. Each resident tile is indexed once on first use.</summary>
public sealed class VectorCollisionWorld : IJourneyWorld
{
    private const double EarthCircumference = 40075016.69;
    private readonly ConditionalWeakTable<MapTile, TileObstacles> _indices = new();
    private IReadOnlyList<MapTile> _tiles = [];

    public void Update(IReadOnlyList<MapTile> visible) => _tiles = visible;

    public bool Blocks(VehicleProfile vehicle, RoutePoint from, RoutePoint to, double altitudeMeters)
    {
        if (vehicle.Motion == MotionModel.Rail) return false;
        var tiles = _tiles;

        bool Obstructed(RoutePoint point)
        {
            double x = (RoutePoint.Wrap(point.Longitude) + 180) / 360;
            double latitude = Math.Clamp(point.Latitude, -85.05112878, 85.05112878) * Math.PI / 180;
            double y = (1 - Math.Asinh(Math.Tan(latitude)) / Math.PI) * .5;
            MapTile? selected = null;
            double localX = 0, localY = 0;
            foreach (var tile in tiles)
            {
                if (tile.Id.Z < 12 || tile.Id.Z <= (selected?.Id.Z ?? -1)) continue;
                double count = Math.Pow(2, tile.Id.Z);
                double tx = x * count - tile.Id.X, ty = y * count - tile.Id.Y;
                if (tx < 0 || ty < 0 || tx >= 1 || ty >= 1) continue;
                selected = tile;
                localX = tx;
                localY = ty;
            }
            if (selected is null) return false;
            double tileMeters = EarthCircumference * Math.Cos(latitude) / Math.Pow(2, selected.Id.Z);
            return _indices.GetValue(selected, static tile => new(tile))
                .Obstructs(vehicle, localX, localY, altitudeMeters, tileMeters);
        }

        // A newly streamed tile may put the current vehicle inside a polygon. Allow escape.
        if (Obstructed(from)) return false;
        if (Obstructed(to)) return true;

        var (startX, startY) = Project(from);
        var (endX, endY) = Project(to);
        endX = startX + RoutePoint.Wrap((endX - startX) * 360) / 360;
        bool ObstructedBetween(double fraction) => Obstructed(from.InterpolateTo(to, fraction));
        foreach (var tile in tiles)
        {
            if (tile.Id.Z < 12) continue;
            double count = Math.Pow(2, tile.Id.Z);
            double originX = tile.Id.X + Math.Round(startX - tile.Id.X / count) * count;
            double ax = startX * count - originX, ay = startY * count - tile.Id.Y;
            double bx = endX * count - originX, by = endY * count - tile.Id.Y;
            if (Math.Max(ax, bx) < 0 || Math.Min(ax, bx) >= 1 || Math.Max(ay, by) < 0 || Math.Min(ay, by) >= 1) continue;
            if (_indices.GetValue(tile, static resident => new(resident))
                .CrossesBuilding(ax, ay, bx, by, altitudeMeters, ObstructedBetween)) return true;
        }
        return false;
    }

    private static (double X, double Y) Project(RoutePoint point)
    {
        double latitude = Math.Clamp(point.Latitude, -85.05112878, 85.05112878) * Math.PI / 180;
        return ((RoutePoint.Wrap(point.Longitude) + 180) / 360, (1 - Math.Asinh(Math.Tan(latitude)) / Math.PI) * .5);
    }

    private sealed class TileObstacles
    {
        private const int Cells = 16;
        private readonly List<MapFeature>?[] _buckets = new List<MapFeature>?[Cells * Cells];

        public TileObstacles(MapTile tile)
        {
            double count = Math.Pow(2, tile.Id.Z);
            double north = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * tile.Id.Y / count)));
            double south = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (tile.Id.Y + 1) / count)));
            double minimumTileMeters = EarthCircumference * Math.Cos(Math.Max(Math.Abs(north), Math.Abs(south))) / count;
            foreach (var feature in tile.Features)
            {
                if (!Relevant(feature) || feature.Extent <= 0 || feature.Paths.Length == 0) continue;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                foreach (var path in feature.Paths)
                foreach (var point in path)
                {
                    minX = Math.Min(minX, point.X);
                    minY = Math.Min(minY, point.Y);
                    maxX = Math.Max(maxX, point.X);
                    maxY = Math.Max(maxY, point.Y);
                }
                if (minX > maxX) continue;
                double margin = feature.Type == GeometryType.LineString
                    ? HalfWidthMeters(feature) * feature.Extent / minimumTileMeters : 0;
                int Cell(double value) => Math.Clamp((int)Math.Floor(value * Cells / feature.Extent), 0, Cells - 1);
                for (int cy = Cell(minY - margin); cy <= Cell(maxY + margin); cy++)
                for (int cx = Cell(minX - margin); cx <= Cell(maxX + margin); cx++)
                    (_buckets[cy * Cells + cx] ??= []).Add(feature);
            }
        }

        private static bool Relevant(MapFeature feature) => feature.Layer switch
        {
            "building" => !feature.Hide3D && feature.Type == GeometryType.Polygon,
            "water" => feature.Type == GeometryType.Polygon && feature.Brunnel != "tunnel",
            "waterway" => feature.Type == GeometryType.LineString && feature.Brunnel != "tunnel"
                && feature.Class is "river" or "canal" or "fairway",
            "transportation" => feature.Brunnel is "bridge" or "tunnel" || feature.Class == "pier",
            _ => false
        };

        public bool Obstructs(VehicleProfile vehicle, double tx, double ty, double altitude, double tileMeters)
        {
            var entries = _buckets[(int)(ty * Cells) * Cells + (int)(tx * Cells)];
            bool ground = vehicle.Motion is MotionModel.Ground or MotionModel.Pedestrian;
            bool water = false, building = false, crossing = false, tunnel = false;
            if (entries is not null)
            foreach (var feature in entries)
            {
                double x = tx * feature.Extent, y = ty * feature.Extent;
                if (feature.Layer == "building")
                {
                    building |= altitude >= (feature.MinHeight ?? 0) && altitude < (feature.Height ?? 8)
                        && Contains(feature.Paths, x, y);
                }
                else if (feature.Layer is "water" or "waterway")
                {
                    // An aqueduct carries water above a ground vehicle's surface.
                    if (!ground || feature.Brunnel != "bridge") water |= Covers(feature, x, y, tileMeters);
                }
                else if (ground && SupportsCrossing(vehicle.Mode, feature) && Covers(feature, x, y, tileMeters))
                {
                    crossing = true;
                    tunnel |= feature.Brunnel == "tunnel";
                }
            }

            // Ground vehicles use mapped tunnel passages beneath surface footprints.
            // Bridge decks only provide a crossing over water, preserving building contacts.
            if (building && !tunnel) return true;
            if (vehicle.Motion == MotionModel.Vessel) return !water;
            return ground && water && !crossing;
        }

        public bool CrossesBuilding(double ax, double ay, double bx, double by, double altitude, Func<double, bool> obstructed)
        {
            int Cell(double value) => Math.Clamp((int)Math.Floor(value * Cells), 0, Cells - 1);
            for (int cy = Cell(Math.Min(ay, by)); cy <= Cell(Math.Max(ay, by)); cy++)
            for (int cx = Cell(Math.Min(ax, bx)); cx <= Cell(Math.Max(ax, bx)); cx++)
            {
                var entries = _buckets[cy * Cells + cx];
                if (entries is null) continue;
                foreach (var feature in entries)
                {
                    if (feature.Layer != "building" || altitude < (feature.MinHeight ?? 0) || altitude >= (feature.Height ?? 8)) continue;
                    if (CrossesInterior(feature.Paths, ax * feature.Extent, ay * feature.Extent,
                        bx * feature.Extent, by * feature.Extent, obstructed)) return true;
                }
            }
            return false;
        }

        private static bool CrossesInterior(TilePoint[][] rings, double ax, double ay, double bx, double by, Func<double, bool> obstructed)
        {
            double dx = bx - ax, dy = by - ay;
            List<double>? crossings = null;
            foreach (var ring in rings)
            {
                if (ring.Length < 3) continue;
                var a = ring[^1];
                foreach (var b in ring)
                {
                    double edgeX = b.X - a.X, edgeY = b.Y - a.Y;
                    double determinant = dx * edgeY - dy * edgeX;
                    if (Math.Abs(determinant) > 1e-12)
                    {
                        double offsetX = a.X - ax, offsetY = a.Y - ay;
                        double along = (offsetX * edgeY - offsetY * edgeX) / determinant;
                        double edge = (offsetX * dy - offsetY * dx) / determinant;
                        if (along is >= 0 and <= 1 && edge is >= 0 and <= 1) (crossings ??= []).Add(along);
                    }
                    a = b;
                }
            }
            if (crossings is null) return Contains(rings, ax + dx * .5, ay + dy * .5) && obstructed(.5);
            crossings.Add(0);
            crossings.Add(1);
            crossings.Sort();
            for (int i = 1; i < crossings.Count; i++)
            {
                if (crossings[i] - crossings[i - 1] < 1e-10) continue;
                double fraction = (crossings[i] + crossings[i - 1]) * .5;
                if (Contains(rings, ax + dx * fraction, ay + dy * fraction) && obstructed(fraction)) return true;
            }
            return false;
        }

        private static bool SupportsCrossing(TravelMode mode, MapFeature feature) => feature.Class switch
        {
            "motorway" or "trunk" or "raceway" or "busway" => mode == TravelMode.Drive,
            "primary" or "secondary" or "tertiary" or "minor" or "service" or "track" =>
                mode is TravelMode.Drive or TravelMode.Bike or TravelMode.Walk,
            "path" or "pier" => mode == TravelMode.Walk || mode == TravelMode.Bike && feature.Subclass != "steps",
            _ => false
        };

        // Vector tiles provide centre lines, so these are conservative physical corridors.
        // Access permissions and deck clearances require richer network data.
        private static double HalfWidthMeters(MapFeature feature) => feature.Layer == "waterway" ? 12 : feature.Class switch
        {
            "motorway" => 8,
            "trunk" or "primary" or "secondary" => 6,
            "tertiary" or "minor" or "raceway" or "busway" => 4.5,
            "service" or "track" => 3.5,
            "pier" => 4,
            "path" => 1.75,
            _ => 0
        };

        private static bool Covers(MapFeature feature, double x, double y, double tileMeters)
        {
            if (feature.Type == GeometryType.Polygon) return Contains(feature.Paths, x, y);
            if (feature.Type != GeometryType.LineString) return false;
            double radius = HalfWidthMeters(feature) * feature.Extent / tileMeters;
            foreach (var line in feature.Paths)
            for (int i = 1; i < line.Length; i++)
            {
                var a = line[i - 1];
                var b = line[i];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double lengthSquared = dx * dx + dy * dy;
                double along = lengthSquared == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared, 0, 1);
                double offsetX = x - a.X - along * dx, offsetY = y - a.Y - along * dy;
                if (offsetX * offsetX + offsetY * offsetY <= radius * radius) return true;
            }
            return false;
        }

        private static bool Contains(TilePoint[][] rings, double x, double y)
        {
            bool inside = false;
            foreach (var ring in rings)
            {
                if (ring.Length < 3) continue;
                var a = ring[^1];
                foreach (var b in ring)
                {
                    if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                        inside = !inside;
                    a = b;
                }
            }
            return inside;
        }
    }
}
