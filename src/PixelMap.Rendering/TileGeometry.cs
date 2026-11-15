using PixelMap.Data;
using SkiaSharp;

namespace PixelMap.Rendering;

internal enum Ink
{
    Urban, Industry, Sand, Ice, Grass, Forest, Park, Water, Stream, Boundary,
    Tunnel, RoadEdge, Path, Local, Street, Arterial, Highway, Rail, Runway, Shadow, Ferry, Shore
}

internal readonly record struct BatchKey(int Order, Ink Ink, float Width, int Bucket = 0);
internal readonly record struct GroundPaint(BatchKey First, BatchKey Second = default, int Count = 1)
{
    internal GroundPaint InBucket(int bucket) => Count == 2
        ? new(First with { Bucket = bucket }, Second with { Bucket = bucket }, 2)
        : new(First with { Bucket = bucket });
}
internal readonly record struct GroundBatch(BatchKey Key, SKPath Path, SKRect Bounds);
internal readonly record struct ShadowSource(TileGeometry Tile, float X, float Y);
internal sealed class Building(MapFeature feature, SKRect bounds, float unit, float top, float bottom, uint seed,
    MapFeature? sourceFeature = null)
{
    public readonly MapFeature Feature = feature;
    public readonly MapFeature SourceFeature = sourceFeature ?? feature;
    public readonly SKRect Bounds = bounds;
    public readonly float Unit = unit, Top = top, Bottom = bottom;
    public readonly uint Seed = seed;
    private BuildingVertex[]? vertices;
    private SKRect[]? ringBounds;

    // Bounds and depth rejection precede this access in both render and pick passes.
    // Most models in an overzoomed tile never need a prepared edge table.
    public BuildingVertex[] Vertices => vertices ??= PrepareVertices(Feature, Unit);

    public SKRect BoundsForRing(int index)
    {
        if (ringBounds is null)
        {
            ringBounds = new SKRect[Feature.Paths.Length];
            for (int i = 0; i < ringBounds.Length; i++)
            {
                var ring = Feature.Paths[i];
                if (ring.Length == 0) continue;
                int left = ring[0].X, right = left, top = ring[0].Y, bottom = top;
                foreach (var point in ring)
                {
                    left = Math.Min(left, point.X); right = Math.Max(right, point.X);
                    top = Math.Min(top, point.Y); bottom = Math.Max(bottom, point.Y);
                }
                ringBounds[i] = new(left * Unit, top * Unit, right * Unit, bottom * Unit);
            }
        }
        return ringBounds[index];
    }

    private static BuildingVertex[] PrepareVertices(MapFeature feature, float unit)
    {
        int count = 0;
        foreach (var ring in feature.Paths) count += ring.Length;
        var vertices = new BuildingVertex[count];
        int index = 0;
        foreach (var ring in feature.Paths)
        {
            if (ring.Length == 0) continue;
            var previous = ring[^1];
            foreach (var point in ring)
            {
                vertices[index++] = PrepareVertex(previous, point, unit, feature.Extent);
                previous = point;
            }
        }
        return vertices;
    }

    // GPU construction consumes one edge at a time. Keep the CPU's lazy table
    // available without retaining a second complete edge array for every GPU model.
    internal static BuildingVertex PrepareVertex(TilePoint previous, TilePoint point, float unit, int extent)
    {
        float x = point.X * unit, y = point.Y * unit;
        float dx = point.X - previous.X, dy = point.Y - previous.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        float light = Math.Clamp(.5f + (dx * .75f - dy * .55f) / (length * 2), 0, 1);
        // Keep both formulas in their original coordinate spaces. Nonstandard
        // MVT extents can round their facade lighting differently after scaling.
        float normalizedDx = x - previous.X * unit, normalizedDy = y - previous.Y * unit;
        float normalizedLength = MathF.Sqrt(normalizedDx * normalizedDx + normalizedDy * normalizedDy);
        float normalizedLight = Math.Clamp(.5f + (normalizedDx * .75f - normalizedDy * .55f) / (2 * normalizedLength), 0, 1);
        bool tileCut = previous.X == point.X && (previous.X <= 0 || previous.X >= extent) ||
            previous.Y == point.Y && (previous.Y <= 0 || previous.Y >= extent);
        return new(new(x, y), length * unit, normalizedLength, light, normalizedLight, tileCut);
    }
}

internal readonly record struct BuildingVertex(SKPoint Point, float Length, float PerspectiveLength,
    float Light, float PerspectiveLight, bool TileCut);
internal sealed class Place(string text, SKPoint point, int priority, bool street)
{
    public string Text { get; } = text;
    public SKPoint Point { get; } = point;
    public int Priority { get; } = priority;
    public bool Street { get; } = street;
    public float TextWidth { get; set; } = float.NaN;
    public SKTextBlob? Glyphs { get; set; }
}

/// <summary>Immutable, tile-local display list. Buildings retain their original rings for depth rasterization.</summary>
internal sealed class TileGeometry : IDisposable
{
    internal const float Extent = 4096;
    public readonly MapTile Source;
    public readonly MapRenderStyle RenderStyle;
    public readonly GroundBatch[] Batches;
    public readonly GroundBatch[] Shadows;
    private readonly SKPath[] ownedPaths;
    public readonly SKRect ShadowBounds;
    public readonly List<ShadowSource> ShadowSources = [];
    public readonly List<Building> Buildings = [];
    public readonly List<Place> Labels = [];
    public long LastFrame;
    public long ShadowRevision = -1;

    public TileGeometry(MapTile tile, MapRenderStyle renderStyle = MapRenderStyle.Pixel)
    {
        Source = tile;
        RenderStyle = renderStyle;
        // A road casing and its centerline use exactly the same contours. Retain
        // and append those contours once, then replay their distinct paints. The
        // complete operation pair is the key, so incompatible paint cohorts can
        // never acquire each other's feature geometry.
        var paths = new Dictionary<GroundPaint, SKPath>();
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
                var renderedFeature = BuildingStyle.Prepare(feature, metersToTile, renderStyle);
                var bounds = Bounds(renderedFeature, unit);
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                float top = (float)Math.Clamp(renderedFeature.Height ?? 8, .5, 1500);
                float bottom = (float)Math.Clamp(renderedFeature.MinHeight ?? 0, 0, top);
                // Use a tile-independent key on buffered copies of the same building.
                uint seed = unchecked((uint)(feature.Id ^ (feature.Id >> 32)));
                if (seed == 0)
                    seed = unchecked((uint)((tile.Id.X * 4096L + (int)bounds.MidX) * 73856093
                                           ^ (tile.Id.Y * 4096L + (int)bounds.MidY) * 19349663));
                seed = (seed ^ (seed >> 16)) * 0x7feb352d;
                Buildings.Add(new(renderedFeature, bounds, unit, top, bottom, seed ^ (seed >> 15), feature));
                AddShadow(PathFor(new(new(900, Ink.Shadow, 0, Bucket(bounds)))), renderedFeature, unit, top * metersToTile);
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
            var operations = PaintOperations(feature, renderStyle);
            if (operations.Count == 0) continue;
            Append(PathFor(operations.InBucket(Bucket(Bounds(feature, unit)))), feature, unit);
        }
        ownedPaths = paths.Values.ToArray();
        var paints = new List<GroundBatch>(paths.Count * 2);
        foreach (var (operations, path) in paths)
        {
            var bounds = path.Bounds;
            paints.Add(new(operations.First, path, bounds));
            if (operations.Count == 2) paints.Add(new(operations.Second, path, bounds));
        }
        // Keep stable order for equal order/width cohorts, including antialiased
        // legacy styles. Geometry sharing does not reorder any paint operation.
        var batches = paints.OrderBy(p => p.Key.Order).ThenBy(p => p.Key.Width).ToArray();
        Batches = batches.Where(b => b.Key.Ink != Ink.Shadow).ToArray();
        Shadows = batches.Where(b => b.Key.Ink == Ink.Shadow).ToArray();
        if (Shadows.Length != 0)
        {
            var bounds = Shadows[0].Bounds;
            foreach (var shadow in Shadows) bounds = SKRect.Union(bounds, shadow.Bounds);
            ShadowBounds = bounds;
        }

        SKPath PathFor(GroundPaint key)
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

    // At most two immutable paints; returning a value removes one iterator
    // allocation per non-building feature without dropping any cartographic layer.
    internal static GroundPaint PaintOperations(MapFeature f, MapRenderStyle renderStyle)
    {
        switch (f.Layer)
        {
            case "landcover":
            case "landuse":
            case "park":
                if (f.Type != GeometryType.Polygon) return default;
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
                return new(new(ink == Ink.Urban ? 0 : 2 + (int)ink, ink, 0));
            case "water" when f.Type == GeometryType.Polygon:
                return renderStyle == MapRenderStyle.Pixel
                    ? new(new(19, Ink.Shore, 1.5f), new(20, Ink.Water, 0), 2)
                    : new(new(20, Ink.Water, 0));
            case "waterway" when f.Type == GeometryType.LineString:
                return new(new(21, Ink.Stream, f.Class == "river" ? 4 : 1.5f));
            case "boundary" when f.Type == GeometryType.LineString:
                return new(new(24, Ink.Boundary, .8f));
            case "aeroway":
                return new(new(35, Ink.Runway, f.Type == GeometryType.Polygon ? 0 : 4));
            case "transportation":
                if (f.Type == GeometryType.Polygon) return new(new(31, Ink.Local, 0));
                if (f.Type != GeometryType.LineString || f.Class == "aerialway") return default;
                if (f.Class == "ferry") return new(new(22, Ink.Ferry, 1));
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
                if (f.Brunnel == "tunnel") return new(new(32, Ink.Tunnel, Math.Min(width, 1.5f)));
                if (f.Brunnel == "bridge") level = Math.Max(1, level);
                int deck = 160 + level * 16;
                var foreground = new BatchKey(deck + (int)road - (int)Ink.Path + 2, road, width);
                if (road is not Ink.Path and not Ink.Rail)
                    return new(new(deck, Ink.RoadEdge, width + (renderStyle == MapRenderStyle.Pixel
                        ? road == Ink.Highway ? 2.25f : 1.8f : 1.45f)), foreground, 2);
                return new(foreground);
            default:
                return default;
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

    public void Dispose()
    {
        foreach (var path in ownedPaths) path.Dispose();
        foreach (var label in Labels) label.Glyphs?.Dispose();
        ShadowSources.Clear();
    }
}
