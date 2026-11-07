using PixelMap.Data;
using PixelMap.Routing;
using PixelMap.Simulation;
using PixelMap.Simulation.Tests.Reference;

internal static partial class CollisionBroadPhaseChecks
{
    private static readonly VehicleProfile[] Vehicles = [VehicleProfile.Walker, VehicleProfile.Bike,
        VehicleProfile.Car, VehicleProfile.Boat, VehicleProfile.Plane, VehicleProfile.FreeCamera, VehicleProfile.Train];

    private readonly record struct Query(VehicleProfile Vehicle, RoutePoint From, RoutePoint To, double FromAltitude, double ToAltitude);

    internal static void Run()
    {
        int checkedQueries = 0;
        var candidate = new VectorCollisionWorld();
        var reference = new LegacyVectorCollisionWorld();
        foreach (var tileId in new[] { new TileId(14, 8192, 8192), new(14, 8192, 1600), new(14, 16383, 8192) })
        {
            var features = Features();
            var tile = new MapTile(tileId, features);
            var neighbour = new MapTile(new(tileId.Z, (tileId.X + 1) % (1 << tileId.Z), tileId.Y), features.Reverse().ToArray());
            var parent = new MapTile(new(tileId.Z - 1, tileId.X / 2, tileId.Y / 2),
                [Polygon("building", Box(0, 0, 4096, 4096)) with { Height = 400 }]);
            var heights = Enumerable.Range(0, 256).Select(i => (float)(10 + (i % 16) * 3 - (i / 16) * 2)).ToArray();
            var elevation = new ElevationSnapshot([new ElevationTile(tileId, 16, heights)]);
            foreach (var scene in new[]
            {
                (Tiles: new[] { tile, neighbour }, Elevation: ElevationSnapshot.Empty),
                (Tiles: new[] { parent, tile, neighbour }, Elevation: elevation),
                (Tiles: new[] { neighbour, tile with { Features = features.Reverse().ToArray() }, parent }, Elevation: elevation),
                (Tiles: Array.Empty<MapTile>(), Elevation: elevation)
            })
            {
                candidate.Update(scene.Tiles, scene.Elevation);
                reference.Update(scene.Tiles, scene.Elevation);
                foreach (var query in Queries(tileId, 768))
                {
                    Equivalent(candidate, reference, query, $"tile {tileId}; query {checkedQueries}");
                    checkedQueries++;
                    if (checkedQueries % 19 == 0 && candidate.IsNavigableWater(query.To) != reference.IsNavigableWater(query.To))
                        throw new InvalidOperationException($"Water coverage differs at query {checkedQueries}.");
                }
            }
        }
        Console.WriteLine($"Collision broad phase: {checkedQueries:N0} frozen-reference sweeps agree, including wrapped tiles, " +
            "mixed extents, holes, narrow walls, elevation, tunnels and streamed replacement.");
    }

    private static void Equivalent(VectorCollisionWorld candidate, LegacyVectorCollisionWorld reference, Query q, string context)
    {
        bool expected = reference.Blocks(q.Vehicle, q.From, q.To, q.FromAltitude, q.ToAltitude);
        bool actual = candidate.Blocks(q.Vehicle, q.From, q.To, q.FromAltitude, q.ToAltitude);
        if (actual != expected)
            throw new InvalidOperationException($"Collision oracle mismatch ({context}): {q}; expected {expected}, actual {actual}.");
    }

    private static Query[] Queries(TileId tile, int count)
    {
        var random = new Random(0x51A7);
        var queries = new List<Query>();
        for (int i = 0; i < count; i++)
        {
            double x = random.NextDouble() * 4608 - 256, y = random.NextDouble() * 4608 - 256;
            double step = i % 4 == 0 ? 600 : 24;
            double dx = i % 5 == 0 ? 0 : (random.NextDouble() - .5) * step;
            double dy = i % 5 == 0 ? 0 : (random.NextDouble() - .5) * step;
            double altitude = new[] { -20d, 0, 1.5, 29.99, 30, 59.99, 60, 120 }[i % 8];
            double endAltitude = i % 3 == 0 ? altitude + 80 : altitude;
            foreach (var vehicle in Vehicles)
                queries.Add(new(vehicle, At(tile, x, y), At(tile, x + dx, y + dy), altitude, endAltitude));
        }

        // Inclusive bounds must not redefine the polygon's half-open edge ownership.
        foreach (double x in new[] { 2047.999, 2048, 2048.001, 2048.999, 2049, 2049.001 })
        foreach (double y in new[] { 1799.999, 1800, 1800.001, 2300, 2300.001 })
        foreach (var vehicle in Vehicles)
        {
            queries.Add(new(vehicle, At(tile, x - 4, y), At(tile, x + 4, y), 0, 0));
            queries.Add(new(vehicle, At(tile, x, y), At(tile, x, y), -1, 121));
        }
        return queries.ToArray();
    }

    private static MapFeature[] Features()
    {
        var features = new List<MapFeature>();
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            int left = x * 128 + 16, top = y * 128 + 16;
            var footprint = Polygon("building", Box(left, top, left + 66, top + 74)) with
            {
                Height = 60 + x % 3 * 30, MinHeight = y % 5 == 0 ? 30 : 0, Hide3D = (x + y) % 17 == 0
            };
            if ((x + y) % 7 == 0)
                footprint = footprint with { Paths = [footprint.Paths[0], Box(left + 12, top + 12, left + 54, top + 62)] };
            if (x % 9 == 0)
                footprint = footprint with
                {
                    Extent = 8192,
                    Paths = footprint.Paths.Select(r => r.Select(p => new TilePoint(p.X * 2, p.Y * 2)).ToArray()).ToArray()
                };
            features.Add(footprint);
        }
        features.Add(Polygon("water", Box(1900, -256, 2200, 4352)) with
            { Paths = [Box(1900, -256, 2200, 4352), Box(1990, 1950, 2100, 2150)] });
        features.Add(Polygon("building", Box(2048, 1800, 2049, 2300)) with { Height = 120 });
        features.Add(Polygon("building", Box(-96, 2700, 64, 3100)) with { Height = 80 });
        features.Add(Line("transportation", "minor", [new(1400, 2050), new(2800, 2050)]) with { Brunnel = "tunnel" });
        features.Add(Line("transportation", "primary", [new(1400, 2040), new(2800, 2040)]) with { Brunnel = "bridge" });
        features.Add(Line("transportation", "path", [new(1400, 2400), new(2800, 2400)]) with { Brunnel = "bridge", Subclass = "steps" });
        features.Add(Polygon("transportation", Box(1920, 2900, 2110, 3000)) with { Class = "pier" });
        features.Add(Line("waterway", "canal", [new(500, -256), new(3500, 4352)]));
        features.Add(Line("waterway", "canal", [new(3000, -256), new(3000, 4352)]) with { Brunnel = "bridge" });
        return features.ToArray();
    }

    private static MapFeature Polygon(string layer, TilePoint[] ring) =>
        new(layer, "", "", "", 1, 4096, GeometryType.Polygon, [ring], null, null, false, 0, 0, "");

    private static MapFeature Line(string layer, string kind, TilePoint[] line) =>
        new(layer, kind, "", "", 1, 4096, GeometryType.LineString, [line], null, null, false, 0, 0, "");

    private static TilePoint[] Box(int left, int top, int right, int bottom) =>
        [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];

    private static RoutePoint At(TileId tile, double x, double y) => new(
        Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (tile.Y + y / 4096) / (1 << tile.Z)))) * 180 / Math.PI,
        RoutePoint.Wrap((tile.X + x / 4096) / (1 << tile.Z) * 360 - 180));
}
