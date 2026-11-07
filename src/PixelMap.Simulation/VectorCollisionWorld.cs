using System.Runtime.CompilerServices;
using PixelMap.Data;
using PixelMap.Routing;

namespace PixelMap.Simulation;

/// <summary>Swept building contacts and shoreline barriers against streamed cartography. Each resident tile is indexed once on first use.</summary>
public sealed partial class VectorCollisionWorld : IJourneyWorld, IFreeRoamRailWorld, IFreeRoamWaterWorld
{
    private const double EarthCircumference = 40075016.69;
    private readonly ConditionalWeakTable<MapTile, TileObstacles> _indices = new();
    private sealed record Scene(IReadOnlyList<MapTile> Tiles, ElevationSnapshot Elevation);
    private volatile Scene _scene = new([], ElevationSnapshot.Empty);

    public void Update(IReadOnlyList<MapTile> visible) => Update(visible, _scene.Elevation);

    public void Update(IReadOnlyList<MapTile> visible, ElevationSnapshot elevation)
    {
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(elevation);
        _scene = new(visible, elevation);
        if (_railNetwork is { } rail && !rail.Matches(visible)) _railNetwork = null;
    }

    public double SurfaceElevationMeters(VehicleProfile vehicle, RoutePoint point)
        => _scene.Elevation.SampleSurfaceMeters(point.Latitude, point.Longitude);

    public bool IsNavigableWater(RoutePoint point)
    {
        if (!point.IsValid) return false;
        var scene = _scene;
        var index = ObstaclesAt(scene.Tiles, point, out double x, out double y, out double tileMeters);
        return index is not null && !index.Obstructs(VehicleProfile.Boat, x, y,
            scene.Elevation.SampleSurfaceMeters(point.Latitude, point.Longitude), tileMeters, scene.Elevation);
    }

    public bool Blocks(VehicleProfile vehicle, RoutePoint from, RoutePoint to, double altitudeMeters)
        => Blocks(vehicle, from, to, altitudeMeters, altitudeMeters);

    public bool Blocks(VehicleProfile vehicle, RoutePoint from, RoutePoint to, double fromAltitudeMeters, double toAltitudeMeters)
    {
        if (vehicle.Motion == MotionModel.Rail) return false;
        var scene = _scene;
        var tiles = scene.Tiles;
        var elevation = scene.Elevation;
        if (vehicle.Motion is MotionModel.Aircraft or MotionModel.FreeFlight && CrossesTerrain(elevation, from, to, fromAltitudeMeters, toAltitudeMeters))
            return true;

        // An unchanged pose cannot enter geometry and keeps the same streamed-tile escape behavior.
        if (from == to && fromAltitudeMeters == toAltitudeMeters) return false;
        var sweep = new ContactSweep(this, scene, vehicle, from, to, fromAltitudeMeters, toAltitudeMeters);

        // A newly streamed tile may put the current vehicle inside a polygon. Allow escape.
        if (sweep.Obstructed(from, fromAltitudeMeters)) return false;
        if (sweep.Obstructed(to, toAltitudeMeters)) return true;

        var (startX, startY) = Project(from);
        var (endX, endY) = Project(to);
        endX = startX + RoutePoint.Wrap((endX - startX) * 360) / 360;
        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (tile.Id.Z < 12) continue;
            double count = Math.Pow(2, tile.Id.Z);
            double originX = tile.Id.X + Math.Round(startX - tile.Id.X / count) * count;
            double ax = startX * count - originX, ay = startY * count - tile.Id.Y;
            double bx = endX * count - originX, by = endY * count - tile.Id.Y;
            if (Math.Max(ax, bx) < 0 || Math.Min(ax, bx) >= 1 || Math.Max(ay, by) < 0 || Math.Min(ay, by) >= 1) continue;
            if (_indices.GetValue(tile, static resident => new(resident))
                .CrossesBuilding(ax, ay, bx, by, sweep)) return true;
        }
        return false;
    }

    private readonly record struct ContactSweep(VectorCollisionWorld World, Scene Scene, VehicleProfile Vehicle,
        RoutePoint From, RoutePoint To, double FromAltitude, double ToAltitude)
    {
        public bool Obstructed(RoutePoint point, double altitude)
        {
            var index = World.ObstaclesAt(Scene.Tiles, point, out double x, out double y, out double tileMeters);
            return index is not null && index.Obstructs(Vehicle, x, y, altitude, tileMeters, Scene.Elevation);
        }

        public bool ObstructedBetween(double fraction) => Obstructed(From.InterpolateTo(To, fraction),
            FromAltitude + (ToAltitude - FromAltitude) * fraction);
    }

    private TileObstacles? ObstaclesAt(IReadOnlyList<MapTile> tiles, RoutePoint point,
        out double localX, out double localY, out double tileMeters)
    {
        double x = (RoutePoint.Wrap(point.Longitude) + 180) / 360;
        double latitude = Math.Clamp(point.Latitude, -85.05112878, 85.05112878) * Math.PI / 180;
        double y = (1 - Math.Asinh(Math.Tan(latitude)) / Math.PI) * .5;
        MapTile? selected = null;
        localX = localY = tileMeters = 0;
        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (tile.Id.Z < 12 || tile.Id.Z <= (selected?.Id.Z ?? -1)) continue;
            double count = Math.Pow(2, tile.Id.Z);
            double tx = x * count - tile.Id.X, ty = y * count - tile.Id.Y;
            if (tx < 0 || ty < 0 || tx >= 1 || ty >= 1) continue;
            selected = tile;
            localX = tx;
            localY = ty;
        }
        if (selected is null) return null;
        tileMeters = EarthCircumference * Math.Cos(latitude) / Math.Pow(2, selected.Id.Z);
        return _indices.GetValue(selected, static tile => new(tile));
    }

    private static bool CrossesTerrain(ElevationSnapshot elevation, RoutePoint from, RoutePoint to,
        double fromAltitude, double toAltitude)
    {
        if (elevation.Count == 0 || Math.Min(fromAltitude, toAltitude) >= Math.Max(0, elevation.MaximumMeters)) return false;
        double startHeight = elevation.SampleSurfaceMeters(from.Latitude, from.Longitude);
        // Preserve the escape path when newly streamed terrain surrounds an existing pose.
        if (fromAltitude < startHeight - .02) return false;
        double length = from.DistanceTo(to);
        int samples = (int)Math.Clamp(Math.Ceiling(length * 2), 2, 256);
        for (int i = 1; i <= samples; i++)
        {
            double fraction = (double)i / samples;
            var point = from.InterpolateTo(to, fraction);
            double height = elevation.SampleSurfaceMeters(point.Latitude, point.Longitude);
            double altitude = fromAltitude + (toAltitude - fromAltitude) * fraction;
            if (height > altitude + .02) return true;
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
        private sealed record Obstacle(MapFeature Feature, double WorldX, double WorldY, int BuildingIndex,
            int MinX, int MinY, int MaxX, int MaxY)
        {
            // The bucket is a coarse candidate set. Keep inclusive bounds so polygon edge
            // ownership remains exclusively with Contains/CrossesInterior below.
            public bool MayContain(double x, double y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

            public bool MayIntersect(double ax, double ay, double bx, double by) =>
                Math.Max(ax, bx) >= MinX && Math.Min(ax, bx) <= MaxX &&
                Math.Max(ay, by) >= MinY && Math.Min(ay, by) <= MaxY;
        }
        private readonly List<Obstacle>?[] _buckets = new List<Obstacle>?[Cells * Cells];
        private readonly List<Obstacle> _buildings = [];
        private readonly ConditionalWeakTable<ElevationSnapshot, double[]> _foundations = new();
        private readonly ConditionalWeakTable<ElevationSnapshot, double[]>.CreateValueCallback _sampleFoundations;

        public TileObstacles(MapTile tile)
        {
            _sampleFoundations = SampleFoundations;
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
                var obstacle = new Obstacle(feature,
                    (tile.Id.X + ((double)minX + maxX) / (2d * feature.Extent)) / count,
                    (tile.Id.Y + ((double)minY + maxY) / (2d * feature.Extent)) / count,
                    feature.Layer == "building" ? _buildings.Count : -1, minX, minY, maxX, maxY);
                if (obstacle.BuildingIndex >= 0) _buildings.Add(obstacle);
                double margin = feature.Type == GeometryType.LineString
                    ? HalfWidthMeters(feature) * feature.Extent / minimumTileMeters : 0;
                int Cell(double value) => Math.Clamp((int)Math.Floor(value * Cells / feature.Extent), 0, Cells - 1);
                for (int cy = Cell(minY - margin); cy <= Cell(maxY + margin); cy++)
                for (int cx = Cell(minX - margin); cx <= Cell(maxX + margin); cx++)
                    (_buckets[cy * Cells + cx] ??= []).Add(obstacle);
            }
        }

        private double[] SampleFoundations(ElevationSnapshot elevation)
        {
            var heights = new double[_buildings.Count];
            foreach (var obstacle in _buildings)
                heights[obstacle.BuildingIndex] = elevation.SampleSurfaceMercator(obstacle.WorldX, obstacle.WorldY);
            return heights;
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

        public bool Obstructs(VehicleProfile vehicle, double tx, double ty, double altitude, double tileMeters,
            ElevationSnapshot elevation)
        {
            var entries = _buckets[(int)(ty * Cells) * Cells + (int)(tx * Cells)];
            double[]? foundations = null;
            bool ground = vehicle.Motion is MotionModel.Ground or MotionModel.Pedestrian;
            bool vessel = vehicle.Motion == MotionModel.Vessel;
            bool water = false, building = false, crossing = false;
            if (entries is not null)
            foreach (var obstacle in entries)
            {
                var feature = obstacle.Feature;
                double x = tx * feature.Extent, y = ty * feature.Extent;
                if (feature.Layer == "building")
                {
                    if (building || !obstacle.MayContain(x, y)) continue;
                    if (elevation.Count > 0) foundations ??= _foundations.GetValue(elevation, _sampleFoundations);
                    double foundation = foundations is null ? 0 : foundations[obstacle.BuildingIndex];
                    building = altitude >= foundation + (feature.MinHeight ?? 0) && altitude < foundation + (feature.Height ?? 8)
                        && Contains(feature.Paths, x, y);
                    // Only ground vehicles can use a tunnel to override a building contact.
                    if (building && !ground) return true;
                }
                else if (feature.Layer is "water" or "waterway")
                {
                    // An aqueduct carries water above a ground vehicle's surface.
                    if (!water && (ground || vessel) && (!ground || feature.Brunnel != "bridge") &&
                        (feature.Type != GeometryType.Polygon || obstacle.MayContain(x, y)))
                        water = Covers(feature, x, y, tileMeters);
                }
                else if (ground && (!crossing || feature.Brunnel == "tunnel") && SupportsCrossing(vehicle.Mode, feature) &&
                    (feature.Type != GeometryType.Polygon || obstacle.MayContain(x, y)) && Covers(feature, x, y, tileMeters))
                {
                    crossing = true;
                    // A usable tunnel clears both surface buildings and shoreline barriers.
                    if (feature.Brunnel == "tunnel") return false;
                }
            }

            // Ground vehicles use mapped tunnel passages beneath surface footprints.
            // Bridge decks only provide a crossing over water, preserving building contacts.
            if (building) return true;
            if (vessel) return !water;
            return ground && water && !crossing;
        }

        public bool CrossesBuilding(double ax, double ay, double bx, double by, in ContactSweep sweep)
        {
            var elevation = sweep.Scene.Elevation;
            double fromAltitude = sweep.FromAltitude, toAltitude = sweep.ToAltitude;
            double[]? foundations = null;
            int Cell(double value) => Math.Clamp((int)Math.Floor(value * Cells), 0, Cells - 1);
            for (int cy = Cell(Math.Min(ay, by)); cy <= Cell(Math.Max(ay, by)); cy++)
            for (int cx = Cell(Math.Min(ax, bx)); cx <= Cell(Math.Max(ax, bx)); cx++)
            {
                var entries = _buckets[cy * Cells + cx];
                if (entries is null) continue;
                foreach (var obstacle in entries)
                {
                    var feature = obstacle.Feature;
                    if (feature.Layer != "building") continue;
                    double localAx = ax * feature.Extent, localAy = ay * feature.Extent;
                    double localBx = bx * feature.Extent, localBy = by * feature.Extent;
                    if (!obstacle.MayIntersect(localAx, localAy, localBx, localBy)) continue;
                    if (elevation.Count > 0) foundations ??= _foundations.GetValue(elevation, _sampleFoundations);
                    double foundation = foundations is null ? 0 : foundations[obstacle.BuildingIndex];
                    double bottom = foundation + (feature.MinHeight ?? 0), top = foundation + (feature.Height ?? 8);
                    if (Math.Max(fromAltitude, toAltitude) < bottom || Math.Min(fromAltitude, toAltitude) >= top) continue;
                    double first = 0, last = 1;
                    if (fromAltitude != toAltitude)
                    {
                        double enter = (bottom - fromAltitude) / (toAltitude - fromAltitude);
                        double leave = (top - fromAltitude) / (toAltitude - fromAltitude);
                        first = Math.Clamp(Math.Min(enter, leave), 0, 1);
                        last = Math.Clamp(Math.Max(enter, leave), 0, 1);
                        if (last <= first) continue;
                    }
                    if (CrossesInterior(feature.Paths, localAx, localAy, localBx, localBy, first, last, sweep)) return true;
                }
            }
            return false;
        }

        private static bool CrossesInterior(TilePoint[][] rings, double ax, double ay, double bx, double by,
            double first, double last, in ContactSweep sweep)
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
                        if (along >= first && along <= last && edge is >= 0 and <= 1) (crossings ??= []).Add(along);
                    }
                    a = b;
                }
            }
            if (crossings is null)
            {
                double middle = (first + last) * .5;
                return Contains(rings, ax + dx * middle, ay + dy * middle) && sweep.ObstructedBetween(middle);
            }
            crossings.Add(first);
            crossings.Add(last);
            crossings.Sort();
            for (int i = 1; i < crossings.Count; i++)
            {
                if (crossings[i] - crossings[i - 1] < 1e-10) continue;
                double fraction = (crossings[i] + crossings[i - 1]) * .5;
                if (Contains(rings, ax + dx * fraction, ay + dy * fraction) && sweep.ObstructedBetween(fraction)) return true;
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
