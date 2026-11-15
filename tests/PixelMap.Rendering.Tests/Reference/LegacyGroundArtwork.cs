using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

// Frozen cartographic operations/contours from d014060. This oracle deliberately
// keeps the original iterator and independent path construction for compatibility
// and allocation comparisons; production must not call it.
internal static class LegacyGroundArtwork
{
    internal static SKRect Bounds(MapFeature feature, float unit)
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

    internal static IEnumerable<BatchKey> PaintOperations(MapFeature f)
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

    internal static void Append(SKPath path, MapFeature feature, float unit)
    {
        foreach (var ring in feature.Paths)
        {
            if (ring.Length < 2) continue;
            path.MoveTo(ring[0].X * unit, ring[0].Y * unit);
            for (int i = 1; i < ring.Length; i++) path.LineTo(ring[i].X * unit, ring[i].Y * unit);
            if (feature.Type == GeometryType.Polygon) path.Close();
        }
    }

}
