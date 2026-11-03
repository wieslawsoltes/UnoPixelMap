using PixelMap.Data;
using PixelMap.Routing;
using PixelMap.Simulation;

internal static class WorldChecks
{
    private static readonly TileId Tile = new(14, 8192, 8192);

    public static void Run()
    {
        var world = new VectorCollisionWorld();
        var water = Polygon("water", Box(2000, 1800, 2200, 2300));
        RoutePoint land = At(1950, 2050), wet = At(2050, 2050);
        world.Update([new(Tile, [water])]);
        foreach (var vehicle in new[] { VehicleProfile.Car, VehicleProfile.Bike, VehicleProfile.Walker })
        {
            Check(world.Blocks(vehicle, land, wet, 0), $"{vehicle.Id} must meet a shoreline barrier.");
            Check(!world.Blocks(vehicle, wet, land, 0), "A vehicle caught in newly loaded water can leave it.");
        }
        Check(world.Blocks(VehicleProfile.Boat, wet, land, 0), "A vessel cannot climb onto land.");
        Check(!world.Blocks(VehicleProfile.Plane, land, wet, 50), "Aircraft can fly across water.");

        var bridge = Line("transportation", "minor", [new(1900, 2050), new(2300, 2050)]) with { Brunnel = "bridge" };
        world.Update([new(Tile, [water, bridge])]);
        foreach (var vehicle in new[] { VehicleProfile.Car, VehicleProfile.Bike, VehicleProfile.Walker })
            Check(!world.Blocks(vehicle, land, wet, 0), $"{vehicle.Id} can use a mapped road bridge.");
        Check(world.Blocks(VehicleProfile.Car, At(1950, 2090), At(2050, 2090), 0), "Bridge access is confined to its deck corridor.");
        Check(!world.Blocks(VehicleProfile.Boat, At(2050, 2000), At(2050, 2100), 0), "Water remains passable beneath a road bridge.");

        var footbridge = bridge with { Class = "path", Subclass = "footway" };
        world.Update([new(Tile, [water, footbridge])]);
        Check(!world.Blocks(VehicleProfile.Walker, land, wet, 0), "Walkers can use a footbridge.");
        Check(world.Blocks(VehicleProfile.Car, land, wet, 0), "A footbridge does not become a car crossing.");
        world.Update([new(Tile, [water, bridge with { Class = "rail", Subclass = "rail" }])]);
        Check(world.Blocks(VehicleProfile.Car, land, wet, 0), "A railway bridge does not become a road crossing.");
        world.Update([new(Tile, [water, bridge with { Brunnel = "" }])]);
        Check(world.Blocks(VehicleProfile.Car, land, wet, 0), "An ordinary centre line over water requires a mapped crossing.");

        var pier = Polygon("transportation", Box(1950, 2030, 2100, 2070)) with { Class = "pier" };
        world.Update([new(Tile, [water, pier])]);
        Check(!world.Blocks(VehicleProfile.Walker, land, wet, 0), "A mapped pier provides a walkable surface.");

        var tower = Polygon("building", Box(2000, 1950, 2200, 2150)) with { Height = 60 };
        world.Update([new(Tile, [water, bridge, tower])]);
        Check(world.Blocks(VehicleProfile.Car, land, wet, 0), "A bridge does not erase a building collision.");
        var tunnel = bridge with { Brunnel = "tunnel", Level = -1 };
        world.Update([new(Tile, [water, tunnel, tower])]);
        Check(!world.Blocks(VehicleProfile.Car, land, wet, 0), "Mapped road tunnels pass beneath water and surface footprints.");
        Check(world.Blocks(VehicleProfile.Car, At(1950, 2090), At(2050, 2090), 0), "The tunnel exemption is confined to its passage.");
        Check(world.Blocks(VehicleProfile.Plane, land, wet, 30), "An aircraft cannot borrow an underground passage.");

        world.Update([new(Tile, [tower with { MinHeight = 30 }])]);
        Check(!world.Blocks(VehicleProfile.Car, land, wet, 0), "Elevated building parts leave the space below open.");
        Check(!world.Blocks(VehicleProfile.Plane, land, wet, 29), "A raised base retains its actual altitude.");
        Check(world.Blocks(VehicleProfile.Plane, land, wet, 30), "Contact starts at a raised building base.");
        Check(world.Blocks(VehicleProfile.Plane, land, wet, 59), "An aircraft meets the upper building volume.");
        Check(!world.Blocks(VehicleProfile.Plane, land, wet, 60), "The volume ends at the mapped roof height.");

        var parent = tower with { Hide3D = true };
        var part = Polygon("building", Box(2120, 1950, 2200, 2150)) with { Height = 40 };
        world.Update([new(Tile, [parent, part])]);
        Check(!world.Blocks(VehicleProfile.Car, land, wet, 0), "Hidden parent outlines do not fill gaps between building parts.");
        Check(world.Blocks(VehicleProfile.Car, wet, At(2150, 2050), 0), "The visible building parts remain solid.");

        var courtyard = tower with { Paths = [Box(2000, 1950, 2200, 2150), Box(2040, 2000, 2100, 2100)] };
        world.Update([new(Tile, [courtyard])]);
        Check(!world.Blocks(VehicleProfile.Car, At(2050, 2050), At(2080, 2050), 0), "Courtyard holes stay open.");
        Check(world.Blocks(VehicleProfile.Car, At(2080, 2050), At(2120, 2050), 0), "Courtyard walls retain collision.");
        world.Update([new(Tile, [water with { Paths = [water.Paths[0], Box(2040, 2000, 2100, 2100)] }])]);
        Check(!world.Blocks(VehicleProfile.Car, land, wet, 0), "Water polygon islands retain their dry surface.");
        Check(world.Blocks(VehicleProfile.Boat, At(2020, 2050), wet, 0), "An island forms a shoreline for vessels.");

        // The line and contact fall on opposite sides of the spatial index's cell boundary.
        var wideBridge = Line("transportation", "primary", [new(1900, 2040), new(2300, 2040)]) with { Brunnel = "bridge" };
        world.Update([new(Tile, [water, wideBridge])]);
        Check(!world.Blocks(VehicleProfile.Car, land, wet, 0), "Index bounds include the full physical crossing width.");

        var canal = Line("waterway", "canal", [new(2000, 1800), new(2000, 2300)]);
        world.Update([new(Tile, [canal])]);
        Check(world.Blocks(VehicleProfile.Car, At(1950, 2050), At(2000, 2050), 0), "Surface canal centre lines provide a shoreline corridor.");
        world.Update([new(Tile, [canal with { Brunnel = "tunnel" }])]);
        Check(!world.Blocks(VehicleProfile.Car, At(1950, 2050), At(2000, 2050), 0), "Culverted waterways do not flood the road surface.");
        world.Update([new(Tile, [canal with { Brunnel = "bridge" }])]);
        Check(!world.Blocks(VehicleProfile.Car, At(1950, 2050), At(2000, 2050), 0), "An elevated aqueduct does not flood the ground below.");

        var thinWall = Polygon("building", Box(2048, 1950, 2049, 2150)) with { Height = 60 };
        world.Update([new(Tile, [thinWall])]);
        Check(world.Blocks(VehicleProfile.Car, At(2047.8, 2050), At(2049.2, 2050), 0),
            "A sub-metre car step cannot skip an entire narrow wall.");
        Check(world.Blocks(VehicleProfile.Plane, At(2046.5, 2050), At(2050, 2050), 30),
            "A fast aircraft step cannot skip a narrow building part.");
        Check(!world.Blocks(VehicleProfile.Plane, At(2046.5, 2050), At(2050, 2050), 60),
            "Swept contacts preserve roof clearance.");
        Check(!world.Blocks(VehicleProfile.Car, At(2048.5, 2050), At(2050, 2050), 0),
            "A vehicle inside newly streamed geometry can escape through the boundary.");
        world.Update([new(Tile, [thinWall with { Hide3D = true }])]);
        Check(!world.Blocks(VehicleProfile.Car, At(2047.8, 2050), At(2049.2, 2050), 0),
            "Swept contacts ignore hidden parent footprints.");
        world.Update([new(Tile, [thinWall, tunnel])]);
        Check(!world.Blocks(VehicleProfile.Car, At(2047.8, 2050), At(2049.2, 2050), 0),
            "Swept contacts retain mapped tunnel passages.");
        world.Update([new(Tile, [courtyard])]);
        Check(world.Blocks(VehicleProfile.Car, At(1950, 2050), At(2050, 2050), 0),
            "A move into a courtyard must cross its surrounding wall.");
        Check(!world.Blocks(VehicleProfile.Car, At(2050, 2050), At(2080, 2050), 0),
            "A swept move entirely inside a courtyard stays clear.");

        var coarse = new MapTile(new(13, 4096, 4096), [Polygon("water", Box(1000, 900, 1100, 1150))]);
        world.Update([coarse, new(Tile, [])]);
        Check(!world.Blocks(VehicleProfile.Car, land, wet, 0), "The finest resident tile replaces coarse shoreline geometry.");
        world.Update([coarse with { Features = [] }, new(Tile, [water])]);
        Check(world.Blocks(VehicleProfile.Car, land, wet, 0), "Fine shoreline geometry wins even when a parent appears first.");
        world.Update([]);
        Check(!world.Blocks(VehicleProfile.Car, land, wet, 0), "Unloaded geometry does not leave stale collisions.");
        Console.WriteLine("Water, bridge, tunnel, raised-building, courtyard and tile-resolution checks passed.");
    }

    private static MapFeature Polygon(string layer, TilePoint[] ring) =>
        new(layer, "", "", "", 1, 4096, GeometryType.Polygon, [ring], null, null, false, 0, 0, "");

    private static MapFeature Line(string layer, string kind, TilePoint[] line) =>
        new(layer, kind, "", "", 1, 4096, GeometryType.LineString, [line], null, null, false, 0, 0, "");

    private static TilePoint[] Box(int left, int top, int right, int bottom) =>
        [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];

    private static RoutePoint At(double x, double y) => new(
        Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (Tile.Y + y / 4096) / Math.Pow(2, Tile.Z)))) * 180 / Math.PI,
        (Tile.X + x / 4096) / Math.Pow(2, Tile.Z) * 360 - 180);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
