namespace PixelMap.Data;

public readonly record struct TileId(int Z, int X, int Y);

public readonly record struct TilePoint(int X, int Y);

public enum GeometryType { Point = 1, LineString = 2, Polygon = 3 }

/// <summary>Paths are in tile-local coordinates. Polygon rings omit the repeated closing vertex.
/// Exterior rings wind clockwise when viewed with positive Y down; holes follow their exterior.</summary>
public sealed record MapFeature(
    string Layer, string Class, string Subclass, string Name, ulong Id,
    int Extent, GeometryType Type, TilePoint[][] Paths,
    double? Height, double? MinHeight, bool Hide3D, int SortRank, int Level, string Brunnel);

public sealed record MapTile(TileId Id, IReadOnlyList<MapFeature> Features);
