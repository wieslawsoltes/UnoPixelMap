using PixelMap.Data;
using SkiaSharp;

namespace PixelMap.Rendering;

internal enum Ink
{
    Urban, Industry, Sand, Ice, Grass, Forest, Park, Water, Stream, Boundary,
    Tunnel, RoadEdge, Path, Local, Street, Arterial, Highway, Rail, Runway, Shadow, Ferry
}

internal readonly record struct BatchKey(int Order, Ink Ink, float Width, int Bucket = 0);
internal readonly record struct GroundBatch(BatchKey Key, SKPath Path, SKRect Bounds);
internal readonly record struct Building(MapFeature Feature, SKRect Bounds, float Unit, float Top, float Bottom, uint Seed);
internal readonly record struct Place(string Text, SKPoint Point, int Priority, bool Street);

/// <summary>Immutable, tile-local display list. Buildings retain their original rings for depth rasterization.</summary>
internal sealed class TileGeometry : IDisposable
{
    internal const float Extent = 4096;
    public readonly MapTile Source;
    public readonly GroundBatch[] Batches;
    public readonly List<Building> Buildings = [];
    public readonly List<Place> Labels = [];
    public long LastFrame;

    public TileGeometry(MapTile tile)
    {
        Source = tile;
        var paths = new Dictionary<BatchKey, SKPath>();
        int n = 1 << tile.Id.Z;
        var centerLatitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (tile.Id.Y + .5) / n)));
        float metersToTile = (float)(Extent * n / (MapCamera.EarthCircumference * Math.Cos(centerLatitude)));

        foreach (var feature in tile.Features)
        {
            if (feature.Paths.Length == 0 || feature.Extent <= 0) continue;
            float unit = Extent / feature.Extent;
            if (feature.Layer == "building" && feature.Type == GeometryType.Polygon)
            {
                if (feature.Hide3D) continue;
                var bounds = Bounds(feature, unit);
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                float top = (float)Math.Clamp(feature.Height ?? 8, .5, 1500);
                float bottom = (float)Math.Clamp(feature.MinHeight ?? 0, 0, top);
                // Use a tile-independent key on buffered copies of the same building.
                uint seed = unchecked((uint)(feature.Id ^ (feature.Id >> 32)));
                if (seed == 0)
                    seed = unchecked((uint)((tile.Id.X * 4096L + (int)bounds.MidX) * 73856093
                                           ^ (tile.Id.Y * 4096L + (int)bounds.MidY) * 19349663));
                seed = (seed ^ (seed >> 16)) * 0x7feb352d;
                Buildings.Add(new(feature, bounds, unit, top, bottom, seed ^ (seed >> 15)));
                AddShadow(PathFor(new(900, Ink.Shadow, 0, Bucket(bounds))), feature, unit, top * metersToTile);
                continue;
            }

            if (feature.Layer is "place" or "transportation_name" or "water_name" or "mountain_peak")
            {
                var path = feature.Paths[0];
                if (path.Length == 0 || string.IsNullOrWhiteSpace(feature.Name)) continue;
                var point = path[path.Length / 2];
                int priority = feature.Layer switch
                {
                    "place" => feature.Class switch
                    {
                        "country" => 0, "state" => 2, "city" => 4, "town" => 6,
                        "village" => 9, "suburb" => 12, _ => 16
                    },
                    "water_name" => 20, "mountain_peak" => 22, _ => 30
                };
                Labels.Add(new(feature.Name, new(point.X * unit, point.Y * unit), priority, feature.Layer == "transportation_name"));
                continue;
            }
            var operations = PaintOperations(feature);
            int bucket = -1;
            foreach (var key in operations)
            {
                if (bucket < 0) bucket = Bucket(Bounds(feature, unit));
                Append(PathFor(key with { Bucket = bucket }), feature, unit);
            }
        }
        Batches = paths.OrderBy(p => p.Key.Order).ThenBy(p => p.Key.Width).Select(p => new GroundBatch(p.Key, p.Value, p.Value.Bounds)).ToArray();

        SKPath PathFor(BatchKey key)
        {
            if (!paths.TryGetValue(key, out var path)) paths.Add(key, path = new SKPath());
            return path;
        }
    }

    // Paths in the same material and spatial cell share a native draw call. Overzoomed
    // street views can reject entire cells before Skia traverses their contours.
    private static int Bucket(SKRect bounds) =>
        Math.Clamp((int)(bounds.MidX / 512), 0, 7) + Math.Clamp((int)(bounds.MidY / 512), 0, 7) * 8;

    private static SKRect Bounds(MapFeature feature, float unit)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var ring in feature.Paths)
        foreach (var p in ring)
        {
            left = Math.Min(left, p.X); right = Math.Max(right, p.X);
            top = Math.Min(top, p.Y); bottom = Math.Max(bottom, p.Y);
        }
        return new(left * unit, top * unit, right * unit, bottom * unit);
    }

    private static IEnumerable<BatchKey> PaintOperations(MapFeature f)
    {
        switch (f.Layer)
        {
            case "landcover":
            case "landuse":
            case "park":
                if (f.Type != GeometryType.Polygon) yield break;
                var ink = f.Class switch
                {
                    "wood" or "forest" or "orchard" => Ink.Forest,
                    "sand" or "beach" or "bare_rock" => Ink.Sand,
                    "ice" or "glacier" => Ink.Ice,
                    "grass" or "farmland" or "meadow" or "scrub" or "wetland" or "heath" => Ink.Grass,
                    "park" or "garden" or "recreation_ground" or "golf_course" or "cemetery" => Ink.Park,
                    "industrial" or "railway" or "quarry" => Ink.Industry,
                    _ => f.Layer == "park" ? Ink.Park : Ink.Urban
                };
                yield return new(ink == Ink.Urban ? 0 : 2 + (int)ink, ink, 0);
                break;
            case "water" when f.Type == GeometryType.Polygon:
                yield return new(20, Ink.Water, 0); break;
            case "waterway" when f.Type == GeometryType.LineString:
                yield return new(21, Ink.Stream, f.Class == "river" ? 4 : 1.5f); break;
            case "boundary" when f.Type == GeometryType.LineString:
                yield return new(24, Ink.Boundary, .8f); break;
            case "aeroway":
                yield return new(35, Ink.Runway, f.Type == GeometryType.Polygon ? 0 : 4); break;
            case "transportation":
                if (f.Type == GeometryType.Polygon) { yield return new(31, Ink.Local, 0); break; }
                if (f.Type != GeometryType.LineString || f.Class == "aerialway") yield break;
                if (f.Class == "ferry") { yield return new(22, Ink.Ferry, 1); break; }
                var (road, width) = f.Class switch
                {
                    "motorway" or "trunk" => (Ink.Highway, 6f),
                    "primary" or "secondary" => (Ink.Arterial, 4.5f),
                    "tertiary" or "minor" => (Ink.Street, 3.2f),
                    "service" => (Ink.Local, 2f),
                    "track" or "path" => (Ink.Path, 1f),
                    "rail" or "transit" => (Ink.Rail, 1.25f),
                    _ => (Ink.Local, 2.4f)
                };
                int level = Math.Clamp(f.Level, -6, 6);
                if (f.Brunnel == "tunnel") { yield return new(32, Ink.Tunnel, Math.Min(width, 1.5f)); break; }
                if (f.Brunnel == "bridge") level = Math.Max(1, level);
                int deck = 160 + level * 16;
                if (road is not Ink.Path and not Ink.Rail)
                    yield return new(deck, Ink.RoadEdge, width + 1.45f);
                yield return new(deck + (int)road - (int)Ink.Path + 2, road, width);
                break;
        }
    }

    private static void Append(SKPath path, MapFeature feature, float unit)
    {
        foreach (var ring in feature.Paths)
        {
            if (ring.Length < 2) continue;
            path.MoveTo(ring[0].X * unit, ring[0].Y * unit);
            for (int i = 1; i < ring.Length; i++) path.LineTo(ring[i].X * unit, ring[i].Y * unit);
            if (feature.Type == GeometryType.Polygon) path.Close();
        }
    }

    private static void AddShadow(SKPath shadow, MapFeature f, float unit, float height)
    {
        // Project the footprint and its outward-facing edges along a fixed geographic sun vector.
        float dx = height * .48f, dy = height * .28f;
        foreach (var ring in f.Paths)
        {
            if (ring.Length < 3) continue;
            shadow.MoveTo(ring[0].X * unit + dx, ring[0].Y * unit + dy);
            for (int i = 1; i < ring.Length; i++) shadow.LineTo(ring[i].X * unit + dx, ring[i].Y * unit + dy);
            shadow.Close();
            var a = ring[^1];
            foreach (var b in ring)
            {
                float ax = a.X * unit, ay = a.Y * unit, bx = b.X * unit, by = b.Y * unit;
                if ((bx - ax) * dy - (by - ay) * dx > 0)
                {
                    shadow.MoveTo(ax, ay); shadow.LineTo(bx, by);
                    shadow.LineTo(bx + dx, by + dy); shadow.LineTo(ax + dx, ay + dy); shadow.Close();
                }
                a = b;
            }
        }
    }

    public void Dispose() { foreach (var batch in Batches) batch.Path.Dispose(); }
}
