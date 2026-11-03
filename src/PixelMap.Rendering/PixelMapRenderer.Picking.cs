using PixelMap.Data;
using SkiaSharp;

namespace PixelMap.Rendering;

public sealed partial class PixelMapRenderer
{
    private readonly BuildingRasterizer pickRaster = new();
    private static readonly Material PickInk = new(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue,
        uint.MaxValue, uint.MaxValue, uint.MaxValue);

    /// <summary>Inspect the visible surface at a logical screen position. Model picking replays the
    /// orthographic depth pass into a single art pixel, so idle drawing carries no object-ID buffer.</summary>
    public MapFeature? Pick(double x, double y, int width, int height, MapCamera camera, IReadOnlyList<MapTile> tiles)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return PickSurface(x, y, width, height, camera, tiles);
        }
    }

    private MapFeature? PickSurface(double x, double y, int width, int height, MapCamera camera, IReadOnlyList<MapTile> tiles)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return null;
        int pixelSize = lastView.PixelSize > 0 ? lastView.PixelSize : 2;
        bool aligned = lastPicture is not null && lastView.Camera == camera && lastView.Width == width &&
            lastView.Height == height && SameTiles(tiles, lastTiles);
        PrepareVisible(width, height, pixelSize, camera, tiles);
        TerrainHit? groundHit = null;
        if (CurrentTerrain is { } terrain)
        {
            PrepareFoundations(terrain.Elevation);
            if (TryPickTerrain(x, y, width, height, camera, out var terrainHit) ||
                camera.TryUnprojectTerrain(x, y, width, height, terrain, out terrainHit)) groundHit = terrainHit;
        }
        if (camera.Zoom >= 12)
        {
            float offsetX = (float)Math.Floor(x / pixelSize), offsetY = (float)Math.Floor(y / pixelSize);
            float meters = (float)(camera.WorldSize / pixelSize / (MapCamera.EarthCircumference *
                Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)));
            pickRaster.Begin(1, 1, (float)Math.Sin(camera.PitchRadians), meters, camera.Perspective);
            if (CurrentTerrain is not null)
            {
                var projection = new TileProjection(camera, width, height, pixelSize);
                var lens = projection.Get(new(0, 0, 0), 0).Transform.Translate(-offsetX, -offsetY);
                pickRaster.BeginTerrain(lens);
                if (aligned &&
                    reliefSurface is not null && reliefSurface.TrySample((int)offsetX, (int)offsetY, out float z, out float inverse))
                    pickRaster.SeedTerrain(z, inverse);
                else if (groundHit is { } terrainHit)
                {
                    z = (float)CurrentTerrain.LocalAltitude(camera, terrainHit.ElevationMeters) * meters;
                    inverse = camera.Perspective ? (lens.CosPitch + (.5f - lens.CY) * lens.SinPitch * lens.InverseFocal) /
                        (lens.CosPitch - (z - lens.TargetZ) * lens.InverseFocal) : 1;
                    pickRaster.SeedTerrain(z, inverse);
                }
                pickRaster.TerrainComplete();
            }
            Span<uint> hit = stackalloc uint[1];
            MapFeature? surface = null;
            foreach (var (tile, transform, _) in visible)
            {
                var sample = transform.Translate(-offsetX, -offsetY);
                foreach (var building in tile.Buildings)
                {
                    hit[0] = 0;
                    pickRaster.Draw(hit, building, sample, PickInk, CurrentTerrain is { } active ? Foundation(tile, building, active, camera, aligned) : 0);
                    if (hit[0] != 0) surface = building.Feature;
                }
            }
            if (surface is not null) return surface;
        }
        if (CurrentTerrain is not null && groundHit is null) return null;
        if (groundHit is null && !camera.TryUnproject(x,y,width,height,out _)) return null;
        return PickGround((float)(x / pixelSize), (float)(y / pixelSize), pixelSize, camera, groundHit, width, height, aligned);
    }

    private MapFeature? PickGround(float x, float y, int pixelSize, MapCamera camera, TerrainHit? groundHit,
        int width, int height, bool aligned)
    {
        MapFeature? best = null;
        int bestRank = int.MaxValue;
        float bestDistance = float.PositiveInfinity;
        float meters = (float)(camera.WorldSize / pixelSize / (MapCamera.EarthCircumference *
            Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)));
        foreach (var (tile, transform, _) in visible)
        {
            var t = groundHit is { } hit && CurrentTerrain is { } active
                ? transform.AtAltitude((float)active.LocalAltitude(camera, hit.ElevationMeters) * meters) : transform;
            if (!t.Invert(x, y, out var local, out var determinant)) continue;
            float margin = 15f / (pixelSize * Math.Max(.00001f, MathF.Sqrt(t.YX * t.YX + t.YY * t.YY)));
            if (local.X < -margin || local.Y < -margin || local.X > TileGeometry.Extent + margin || local.Y > TileGeometry.Extent + margin) continue;
            foreach (var f in tile.Source.Features)
            {
                if (f.Layer == "building" || f.Extent <= 0) continue;
                bool named = !string.IsNullOrEmpty(f.Name);
                int rank;
                float distance;
                float unit = TileGeometry.Extent / f.Extent;
                switch (f.Type)
                {
                    case GeometryType.Point:
                        if (!named) continue;
                        rank = f.Layer == "poi" ? 0 : 2;
                        distance = float.PositiveInfinity;
                        foreach (var path in f.Paths)
                        foreach (var p in path)
                        {
                            var point = new SKPoint(p.X * unit, p.Y * unit);
                            SKPoint s;
                            if (CurrentTerrain is { } terrain)
                            {
                                float altitude = GroundAltitude(tile, point, terrain, camera, aligned);
                                var projected = transform.Vertex(point.X, point.Y, altitude * meters);
                                if (projected.W < MapCamera.Near || projected.W > transform.Far) continue;
                                s = projected.Point;
                                if (!VisibleGround(s, altitude)) continue;
                            }
                            else s = t.At(point.X, point.Y);
                            float px = s.X - x, py = s.Y - y;
                            distance = Math.Min(distance, px * px + py * py);
                        }
                        if (distance * pixelSize * pixelSize > 14 * 14) continue;
                        break;
                    case GeometryType.LineString:
                        rank = named ? 1 : 4;
                        if (rank > bestRank) continue;
                        distance = float.PositiveInfinity;
                        foreach (var path in f.Paths)
                        {
                            if (path.Length < 2) continue;
                            var a = t.At(path[0].X * unit, path[0].Y * unit);
                            for (int i = 1; i < path.Length; i++)
                            {
                                var b = t.At(path[i].X * unit, path[i].Y * unit);
                                if (CurrentTerrain is { } terrain)
                                {
                                    float ax = path[i - 1].X * unit, ay = path[i - 1].Y * unit;
                                    float dx = path[i].X * unit - ax, dy = path[i].Y * unit - ay;
                                    float length = dx * dx + dy * dy;
                                    float along = length == 0 ? 0 : Math.Clamp(((local.X - ax) * dx + (local.Y - ay) * dy) / length, 0, 1);
                                    var point = new SKPoint(ax + dx * along, ay + dy * along);
                                    float altitude = GroundAltitude(tile, point, terrain, camera, aligned);
                                    var projected = transform.Vertex(point.X, point.Y, altitude * meters);
                                    if (projected.W >= MapCamera.Near && projected.W <= transform.Far && VisibleGround(projected.Point, altitude))
                                    {
                                        float px = projected.Point.X - x, py = projected.Point.Y - y;
                                        distance = Math.Min(distance, px * px + py * py);
                                    }
                                }
                                else distance = Math.Min(distance, SegmentDistanceSquared(x, y, a, b));
                                a = b;
                            }
                        }
                        if (distance * pixelSize * pixelSize > 7 * 7) continue;
                        break;
                    case GeometryType.Polygon:
                        rank = named ? 3 : 5;
                        if (rank > bestRank || !Contains(f.Paths, local.X / unit, local.Y / unit, out distance)) continue;
                        distance *= unit * unit * determinant;
                        break;
                    default:
                        continue;
                }
                if (rank < bestRank || rank == bestRank && distance < bestDistance)
                {
                    best = f;
                    bestRank = rank;
                    bestDistance = distance;
                }
            }
        }
        return best;

        bool VisibleGround(SKPoint point, float altitude)
        {
            float tolerance = Math.Max(4, .75f / meters);
            if (aligned) return !buildings.CoversAt(point.X, point.Y, altitude + tolerance);
            return CurrentTerrain is { } terrain && camera.TryUnprojectTerrain(point.X * pixelSize, point.Y * pixelSize,
                width, height, terrain, out var front) && altitude + tolerance >= terrain.LocalAltitude(camera, front.ElevationMeters);
        }
    }

    private static float SegmentDistanceSquared(float x, float y, SKPoint a, SKPoint b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float length = dx * dx + dy * dy;
        float t = length == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1);
        float px = a.X + dx * t - x, py = a.Y + dy * t - y;
        return px * px + py * py;
    }

    private static bool Contains(TilePoint[][] rings, float x, float y, out float area)
    {
        bool inside = false;
        double twiceArea = 0;
        foreach (var ring in rings)
        {
            if (ring.Length < 3) continue;
            var a = ring[^1];
            foreach (var b in ring)
            {
                if ((a.Y > y) != (b.Y > y) && x < a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y))
                    inside = !inside;
                twiceArea += (long)a.X * b.Y - (long)b.X * a.Y;
                a = b;
            }
        }
        area = (float)Math.Abs(twiceArea);
        return inside;
    }
}
