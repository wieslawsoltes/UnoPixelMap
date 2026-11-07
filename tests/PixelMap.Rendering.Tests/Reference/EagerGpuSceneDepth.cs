// Independent eager depth oracle from ab765d24c3063c1745867772426720fd47152805.
// Keep projection, clipping, raster coverage and submission order unchanged.
using SkiaSharp;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PixelMap.Rendering;

/// <summary>A screen-space primitive index for picking and overlay visibility. Preparing
/// a frame projects vertices and bins triangle bounds. Depth is evaluated only for pixels
/// actually queried by labels, navigation or pointer input, leaving fragment rendering to GL.</summary>
internal sealed class EagerGpuSceneDepth
{
    private const int CellSize = 16;
    private const int MaximumPooledStorage = 2;
    private const int MaximumCachedPixels = 65536;
    private static readonly ConcurrentBag<Storage> pool = [];
    private static int pooledStorage;
    private static long nextRevision;
    internal long Revision { get; } = long.MinValue + Interlocked.Increment(ref nextRevision);
    private readonly int width, height, columns, rows;
    private readonly bool perspective;
    private readonly float pixelsPerMeter;
    private readonly TileTransform lens;
    private readonly List<Triangle> triangles;
    private readonly List<Edge> edges;
    private readonly List<int>?[] cells;
    private readonly Dictionary<int, PixelHit> samples;
    private readonly Storage storage;
    private int references;
    private float minimumAltitude = float.PositiveInfinity, maximumAltitude = float.NegativeInfinity;

    internal EagerGpuSceneDepth(GpuMapFrame frame)
    {
        width = frame.ArtWidth;
        height = frame.ArtHeight;
        columns = (width + CellSize - 1) / CellSize;
        rows = (height + CellSize - 1) / CellSize;
        if (pool.TryTake(out var recycled))
        {
            Interlocked.Decrement(ref pooledStorage);
            storage = recycled;
        }
        else storage = new();
        triangles = storage.Triangles;
        edges = storage.Edges;
        samples = storage.Samples;
        if (storage.Cells.Length < columns * rows) storage.Cells = new List<int>?[columns * rows];
        cells = storage.Cells;
        perspective = frame.Camera.Perspective;
        pixelsPerMeter = frame.PixelsPerMeter;
        lens = new TileProjection(frame.Camera, frame.Width, frame.Height, frame.PixelSize).Get(new(0, 0, 0), 0).Transform;
        foreach (var draw in frame.Terrain) AddTerrain(draw, frame);
        foreach (var draw in frame.Buildings) AddBuildings(draw, frame);
        frame.MinimumAltitude = float.IsFinite(minimumAltitude) ? minimumAltitude - 1 : -1;
        frame.MaximumAltitude = float.IsFinite(maximumAltitude) ? Math.Max(frame.MinimumAltitude + 1, maximumAltitude + 1) : 1;
    }

    internal void Retain() => Interlocked.Increment(ref references);

    internal void Release()
    {
        if (Interlocked.Decrement(ref references) != 0) return;
        // Keep two ordinary moving-frame workspaces. Oversized/offscreen renders release
        // their arrays normally instead of permanently enlarging the shared pool.
        if (triangles.Capacity > 262144 || edges.Capacity > 65536 || samples.EnsureCapacity(0) > 131072 ||
            storage.ProjectedVertices.Length > 131072 || storage.VisibleModels.Length > 16384) return;
        if (Interlocked.Increment(ref pooledStorage) > MaximumPooledStorage)
        {
            Interlocked.Decrement(ref pooledStorage);
            return;
        }
        triangles.Clear();
        edges.Clear();
        samples.Clear();
        foreach (var cell in cells) cell?.Clear();
        pool.Add(storage);
    }

    internal bool CoversAt(float sx, float sy, float altitudeMeters)
    {
        int x = (int)MathF.Floor(sx), y = (int)MathF.Floor(sy);
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return false;
        var sample = Sample(x, y);
        if (!sample.HasSurface) return false;
        float nearest = sample.Depth;
        float sampleY = y + .5f;
        if (sample.SurfaceIsTerrain)
        {
            ref var triangle = ref CollectionsMarshal.AsSpan(triangles)[sample.TerrainTriangle];
            nearest = triangle.EvaluateDepth(sx, sy, perspective);
            sampleY = sy;
        }
        if (!perspective) return nearest > pixelsPerMeter * altitudeMeters;
        float plane = lens.CosPitch - (altitudeMeters * pixelsPerMeter - lens.TargetZ) * lens.InverseFocal;
        if (Math.Abs(plane) < 1e-7f) return false;
        float candidate = (lens.CosPitch + (sampleY - lens.CY) * lens.SinPitch * lens.InverseFocal) / plane;
        return nearest > candidate + 1e-5f;
    }

    internal bool TryTerrain(int x, int y, out float altitude, out float inverse, out float physicalHeight)
    {
        altitude = inverse = physicalHeight = 0;
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return false;
        var sample = Sample(x, y);
        if (sample.TerrainTriangle < 0) return false;
        ref var triangle = ref CollectionsMarshal.AsSpan(triangles)[sample.TerrainTriangle];
        triangle.Evaluate(x + .5f, y + .5f, out altitude, out inverse, out physicalHeight);
        return float.IsFinite(altitude) && inverse > 0;
    }

    private PixelHit Sample(int x, int y)
    {
        int offset = y * width + x;
        if (samples.TryGetValue(offset, out var cached)) return cached;
        float sx = x + .5f, sy = y + .5f;
        float nearest = float.NegativeInfinity, terrainDepth = float.NegativeInfinity;
        int terrainIndex = -1;
        bool surfaceIsTerrain = false, hasSurface = false;
        var preparedTriangles = CollectionsMarshal.AsSpan(triangles);
        if (cells[(y / CellSize) * columns + x / CellSize] is { } candidates)
            foreach (int primitive in candidates)
            {
                float depth;
                bool isTerrain;
                if (primitive >= 0)
                {
                    ref var triangle = ref preparedTriangles[primitive];
                    if (!triangle.Contains(sx, sy)) continue;
                    depth = triangle.EvaluateDepth(sx, sy, perspective);
                    isTerrain = triangle.Terrain;
                }
                else
                {
                    if (!edges[~primitive].TrySample(x, y, out float altitude, out float inverse)) continue;
                    depth = perspective ? inverse : altitude;
                    isTerrain = false;
                }
                if (!float.IsFinite(depth)) continue;
                if (isTerrain && depth >= terrainDepth)
                {
                    terrainDepth = depth;
                    terrainIndex = primitive;
                }
                // Roof-rim DDA samples have the same small inverse-depth tolerance as
                // the CPU edge pass, while retaining the nearer stored depth value.
                float tolerance = perspective && primitive < 0 ? 1e-6f : 0;
                if (depth < nearest - tolerance) continue;
                nearest = Math.Max(nearest, depth);
                hasSurface = true;
                surfaceIsTerrain = isTerrain;
            }
        var result = new PixelHit(nearest, terrainIndex, hasSurface, surfaceIsTerrain);
        if (samples.Count >= MaximumCachedPixels) samples.Clear();
        samples.Add(offset, result);
        return result;
    }

    private void AddTerrain(TerrainGpuDraw draw, GpuMapFrame frame)
    {
        var vertices = draw.Mesh.Vertices;
        int count = vertices.Length / GpuTerrainMesh.VertexStride;
        Vertex[]? storage = count > GpuTerrainMesh.Side * GpuTerrainMesh.Side ? ArrayPool<Vertex>.Shared.Rent(count) : null;
        Span<Vertex> projected = storage is not null ? storage.AsSpan(0, count) : stackalloc Vertex[count];
        try
        {
            for (int i = 0; i < projected.Length; i++)
            {
                int offset = i * GpuTerrainMesh.VertexStride;
                float raw = vertices[offset + 2];
                float altitude = (float)frame.TerrainOptions.LocalAltitude(frame.Camera, raw) * pixelsPerMeter;
                IncludeAltitude(altitude);
                projected[i] = new(draw.Transform.Vertex(vertices[offset], vertices[offset + 1], altitude), raw);
            }
            var indices = draw.Mesh.TriangleIndices;
            for (int i = 0; i < indices.Length; i += 3)
                AddTriangle(ref projected[indices[i]], ref projected[indices[i + 1]], ref projected[indices[i + 2]], draw.Transform.Far, true);
        }
        finally { if (storage is not null) ArrayPool<Vertex>.Shared.Return(storage); }
    }

    private void AddBuildings(GpuBuildingDraw draw, GpuMapFrame frame)
    {
        var mesh = draw.Mesh;
        var transform = draw.Transform;
        var source = mesh.Vertices;
        var projected = storage.ProjectedVertices;
        if (storage.VisibleModels.Length < mesh.Ranges.Length) storage.VisibleModels = new bool[mesh.Ranges.Length];
        var visible = storage.VisibleModels.AsSpan(0, mesh.Ranges.Length);
        visible.Clear();
        for (int modelIndex = 0; modelIndex < mesh.Ranges.Length; modelIndex++)
        {
            var range = mesh.Ranges[modelIndex];
            var model = range.Model;
            float foundation = (float)frame.TerrainOptions.LocalAltitude(frame.Camera, mesh.Models[modelIndex * GpuBuildingMesh.ModelStride]);
            float top = (model.Top + foundation) * pixelsPerMeter, bottom = (model.Bottom + foundation) * pixelsPerMeter;
            IncludeAltitude(top); IncludeAltitude(bottom);
            if (draw.Selection is { } selected && selected.Models[modelIndex] == 0) continue;
            if (Outside(model.Bounds, bottom, top, transform)) continue;
            float rx = (Math.Abs(transform.XX) * model.Bounds.Width + Math.Abs(transform.XY) * model.Bounds.Height) * .5f;
            float ry = (Math.Abs(transform.YX) * model.Bounds.Width + Math.Abs(transform.YY) * model.Bounds.Height) * .5f;
            float rise = top * transform.SinPitch, baseRise = bottom * transform.SinPitch;
            if (!perspective && rx < .28f && ry + (foundation == 0 ? rise : rise - baseRise) < .5f) continue;
            visible[modelIndex] = true;
            if (projected.Length < range.VertexCount)
            {
                projected = new Vertex[range.VertexCount];
                storage.ProjectedVertices = projected;
            }
            float eye = perspective ? transform.TargetZ + transform.CosPitch / transform.InverseFocal : float.PositiveInfinity;
            for (int i = 0; i < range.VertexCount; i++)
            {
                ref readonly var vertex = ref source[range.FirstVertex + i];
                int face = vertex.Face;
                // Caps have separate vertices and their same face test precedes
                // index traversal, so hidden caps never read this scratch entry.
                if (face == 1 && top >= eye || face == 2 && (!perspective || bottom <= eye)) continue;
                float altitude = ((vertex.Bottom ? model.Bottom : model.Top) + foundation) * pixelsPerMeter;
                projected[i] = new(transform.Vertex(vertex.X, vertex.Y, altitude));
            }
            for (int i = range.FirstIndex, end = i + range.IndexCount; i < end; i += 3)
            {
                int first = (int)mesh.Indices[i];
                ref readonly var vertex = ref source[first];
                int face = vertex.Face;
                if (face == 1 && top >= eye || face == 2 && (!perspective || bottom <= eye)) continue;
                if (face == 0)
                {
                    if (!perspective && (rise - baseRise < .35f || -(transform.XX * vertex.Dx + transform.XY * vertex.Dy) < .01f)) continue;
                    var a = projected[first - range.FirstVertex].Screen;
                    var b = projected[(int)mesh.Indices[i + 1] - range.FirstVertex].Screen;
                    var c = projected[(int)mesh.Indices[i + 2] - range.FirstVertex].Screen;
                    if (perspective && Orientation(a, b, c) >= -.00001f) continue;
                }
                AddTriangle(ref projected[first - range.FirstVertex],
                    ref projected[(int)mesh.Indices[i + 1] - range.FirstVertex],
                    ref projected[(int)mesh.Indices[i + 2] - range.FirstVertex], transform.Far, false);
            }
        }
        for (int modelIndex = 0; modelIndex < mesh.Ranges.Length; modelIndex++)
        {
            if (!visible[modelIndex]) continue;
            var range = mesh.Ranges[modelIndex];
            var model = range.Model;
            float foundation = (float)frame.TerrainOptions.LocalAltitude(frame.Camera, mesh.Models[modelIndex * GpuBuildingMesh.ModelStride]);
            float top = (model.Top + foundation) * pixelsPerMeter;
            if (perspective)
            {
                if (top >= transform.TargetZ + transform.CosPitch / transform.InverseFocal) continue;
            }
            else
            {
                float rx = (Math.Abs(transform.XX) * model.Bounds.Width + Math.Abs(transform.XY) * model.Bounds.Height) * .5f;
                float ry = (Math.Abs(transform.YX) * model.Bounds.Width + Math.Abs(transform.YY) * model.Bounds.Height) * .5f;
                if (rx < 3 || ry < 1.5f) continue;
            }
            for (int i = range.FirstEdge, end = i + range.EdgeCount; i < end; i += 2)
            {
                int a = i * GpuBuildingMesh.EdgeStride, b = a + GpuBuildingMesh.EdgeStride;
                AddEdge(transform.Vertex(mesh.RoofEdges[a], mesh.RoofEdges[a + 1], top),
                    transform.Vertex(mesh.RoofEdges[b], mesh.RoofEdges[b + 1], top), transform.Far);
            }
        }
    }

    private bool Outside(SKRect bounds, float bottom, float top, TileTransform transform)
    {
        int common = 63;
        for (int i = 0; i < 8; i++)
        {
            var corner = transform.Vertex((i & 1) == 0 ? bounds.Left : bounds.Right,
                (i & 2) == 0 ? bounds.Top : bounds.Bottom, (i & 4) == 0 ? bottom : top);
            common &= OutCode(corner, transform.Far);
            if (common == 0) return false;
        }
        return true;
    }

    private int OutCode(H vertex, float far)
    {
        int code = 0;
        if (vertex.W - lens.Near < 0) code |= 1;
        if (far - vertex.W < 0) code |= 2;
        if (vertex.X < 0) code |= 4;
        if (width * vertex.W - vertex.X < 0) code |= 8;
        if (vertex.Y < 0) code |= 16;
        if (height * vertex.W - vertex.Y < 0) code |= 32;
        return code;
    }

    private static float Orientation(H a, H b, H c) =>
        a.X * (b.Y * c.W - c.Y * b.W) - a.Y * (b.X * c.W - c.X * b.W) + a.W * (b.X * c.Y - c.X * b.Y);

    private int CachedOutCode(ref Vertex vertex, float far)
    {
        // A vertex may belong only to hidden caps or back-facing walls. Populate its
        // clip code after face rejection and reuse it for the remaining triangles.
        if (vertex.ClipCode < 0) vertex.ClipCode = OutCode(vertex.Screen, far);
        return vertex.ClipCode;
    }

    private void AddTriangle(ref Vertex a, ref Vertex b, ref Vertex c, float far, bool terrain)
    {
        int ca = CachedOutCode(ref a, far), cb = CachedOutCode(ref b, far), cc = CachedOutCode(ref c, far);
        if ((ca & cb & cc) != 0) return;
        if ((ca | cb | cc) == 0)
        {
            AddProjectedTriangle(a, b, c, terrain);
            return;
        }
        Span<Vertex> polygon = stackalloc Vertex[12];
        Span<Vertex> buffer = stackalloc Vertex[12];
        polygon[0] = a; polygon[1] = b; polygon[2] = c;
        int count = 3;
        for (int plane = 0; plane < 6 && count > 0; plane++)
        {
            if (((ca | cb | cc) & (1 << plane)) == 0) continue;
            int output = 0;
            var previous = polygon[count - 1];
            float previousDistance = TileTransform.Plane(plane, previous.Screen, width, height, far, lens.Near);
            for (int i = 0; i < count; i++)
            {
                var current = polygon[i];
                float distance = TileTransform.Plane(plane, current.Screen, width, height, far, lens.Near);
                if ((distance < 0) != (previousDistance < 0))
                    buffer[output++] = Vertex.Lerp(previous, current, previousDistance / (previousDistance - distance));
                if (distance >= 0) buffer[output++] = current;
                previous = current;
                previousDistance = distance;
            }
            buffer[..output].CopyTo(polygon);
            count = output;
        }
        for (int i = 1; i + 1 < count; i++) AddProjectedTriangle(polygon[0], polygon[i], polygon[i + 1], terrain);
    }

    private void AddProjectedTriangle(in Vertex a, in Vertex b, in Vertex c, bool terrain)
    {
        var triangle = new Triangle(a, b, c, terrain, perspective);
        if (!triangle.Valid) return;
        int index = triangles.Count;
        triangles.Add(triangle);
        Bin(index, triangle.Left, triangle.Top, triangle.Right, triangle.Bottom);
    }

    private void AddEdge(H a, H b, float far)
    {
        float lo = 0, hi = 1;
        for (int plane = 0; plane < 6; plane++)
        {
            float from = TileTransform.Plane(plane, a, width, height, far, lens.Near);
            float to = TileTransform.Plane(plane, b, width, height, far, lens.Near);
            if (from < 0 && to < 0) return;
            if (from >= 0 && to >= 0) continue;
            float t = from / (from - to);
            if (from < 0) lo = Math.Max(lo, t); else hi = Math.Min(hi, t);
        }
        if (hi < lo) return;
        var edge = new Edge(a, b, lo, hi, perspective);
        int index = edges.Count;
        edges.Add(edge);
        Bin(~index, edge.Left, edge.Top, edge.Right + 1, edge.Bottom + 1);
    }

    private void Bin(int primitive, float left, float top, float right, float bottom)
    {
        if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom) ||
            right < 0 || bottom < 0 || left >= width || top >= height) return;
        int firstX = Math.Clamp((int)MathF.Floor(left / CellSize), 0, columns - 1);
        int lastX = Math.Clamp((int)MathF.Floor(right / CellSize), 0, columns - 1);
        int firstY = Math.Clamp((int)MathF.Floor(top / CellSize), 0, rows - 1);
        int lastY = Math.Clamp((int)MathF.Floor(bottom / CellSize), 0, rows - 1);
        for (int y = firstY; y <= lastY; y++)
        for (int x = firstX; x <= lastX; x++) (cells[y * columns + x] ??= []).Add(primitive);
    }

    private void IncludeAltitude(float altitude)
    {
        minimumAltitude = Math.Min(minimumAltitude, altitude);
        maximumAltitude = Math.Max(maximumAltitude, altitude);
    }

    private readonly record struct PixelHit(float Depth, int TerrainTriangle, bool HasSurface, bool SurfaceIsTerrain);

    private sealed class Storage
    {
        internal readonly List<Triangle> Triangles = new(32768);
        internal readonly List<Edge> Edges = [];
        internal readonly Dictionary<int, PixelHit> Samples = [];
        internal List<int>?[] Cells = [];
        // Indexed primitives retain value copies, so model scratch can span tiles and frames.
        internal Vertex[] ProjectedVertices = [];
        internal bool[] VisibleModels = [];
    }

    private record struct Vertex(H Screen, float Height = 0, int ClipCode = -1)
    {
        internal static Vertex Lerp(Vertex a, Vertex b, float t) => new(H.Lerp(a.Screen, b.Screen, t), a.Height + (b.Height - a.Height) * t);
    }

    private struct Triangle
    {
        private const byte InversePlane = 1, AltitudePlane = 2, PhysicalHeightPlane = 4;
        private readonly SKPoint a, b, c;
        private readonly float inverse, altitude, raw;
        private float inverseX, inverseY, altitudeX, altitudeY, rawX, rawY;
        private byte preparedPlanes;
        internal readonly bool Terrain, Valid;
        internal readonly float Left, Top, Right, Bottom;

        internal Triangle(in Vertex va, in Vertex vb, in Vertex vc, bool terrain, bool perspective)
        {
            a = va.Screen.Point; b = vb.Screen.Point; c = vc.Screen.Point;
            Terrain = terrain;
            Left = Math.Min(a.X, Math.Min(b.X, c.X)); Right = Math.Max(a.X, Math.Max(b.X, c.X));
            Top = Math.Min(a.Y, Math.Min(b.Y, c.Y)); Bottom = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            float dx1 = b.X - a.X, dy1 = b.Y - a.Y, dx2 = c.X - a.X, dy2 = c.Y - a.Y;
            float determinant = dx1 * dy2 - dx2 * dy1;
            Valid = float.IsFinite(determinant) && Math.Abs(determinant) >= .00001f;
            float ai = 1 / va.Screen.W, bi = 1 / vb.Screen.W, ci = 1 / vc.Screen.W;
            // Reuse the gradient slots for the other vertices until a query needs
            // that plane. Unqueried triangles retain the same storage footprint.
            // Orthographic models have constant W and need only their altitude.
            inverse = ai;
            inverseX = terrain || perspective ? bi : 0;
            inverseY = terrain || perspective ? ci : 0;
            altitude = va.Screen.Z * ai;
            altitudeX = terrain || !perspective ? vb.Screen.Z * bi : 0;
            altitudeY = terrain || !perspective ? vc.Screen.Z * ci : 0;
            raw = va.Height * ai;
            rawX = terrain ? vb.Height * bi : 0;
            rawY = terrain ? vc.Height * ci : 0;
            preparedPlanes = (byte)((terrain || perspective ? 0 : InversePlane) |
                (terrain || !perspective ? 0 : AltitudePlane) | (terrain ? 0 : PhysicalHeightPlane));
        }

        private void PreparePlanes(byte planes)
        {
            byte pending = (byte)(planes & ~preparedPlanes);
            if (pending == 0) return;
            float dx1 = b.X - a.X, dy1 = b.Y - a.Y, dx2 = c.X - a.X, dy2 = c.Y - a.Y;
            float determinant = dx1 * dy2 - dx2 * dy1;
            if ((pending & InversePlane) != 0) (inverseX, inverseY) = Plane(inverse, inverseX, inverseY);
            if ((pending & AltitudePlane) != 0) (altitudeX, altitudeY) = Plane(altitude, altitudeX, altitudeY);
            if ((pending & PhysicalHeightPlane) != 0) (rawX, rawY) = Plane(raw, rawX, rawY);
            preparedPlanes |= pending;
            (float X, float Y) Plane(float x, float y, float z) =>
                (((y - x) * dy2 - (z - x) * dy1) / determinant, (dx1 * (z - x) - dx2 * (y - x)) / determinant);
        }

        internal readonly bool Contains(float x, float y)
        {
            if (x < Left || x >= Right || y < Top || y >= Bottom) return false;
            float left = float.PositiveInfinity, right = float.NegativeInfinity;
            Intersect(a, b, y, ref left, ref right);
            Intersect(b, c, y, ref left, ref right);
            Intersect(c, a, y, ref left, ref right);
            return x >= left && x < right;
        }

        private static void Intersect(SKPoint from, SKPoint to, float y, ref float left, ref float right)
        {
            if ((from.Y > y) == (to.Y > y)) return;
            float x = from.X + (y - from.Y) * (to.X - from.X) / (to.Y - from.Y);
            left = Math.Min(left, x); right = Math.Max(right, x);
        }

        internal void Evaluate(float x, float y, out float z, out float iw, out float physicalHeight)
        {
            PreparePlanes(InversePlane | AltitudePlane | PhysicalHeightPlane);
            float ox = x - a.X, oy = y - a.Y;
            iw = inverse + inverseX * ox + inverseY * oy;
            float reciprocal = 1 / iw;
            z = (altitude + altitudeX * ox + altitudeY * oy) * reciprocal;
            physicalHeight = (raw + rawX * ox + rawY * oy) * reciprocal;
        }

        internal float EvaluateDepth(float x, float y, bool perspective)
        {
            PreparePlanes(perspective ? InversePlane : (byte)(InversePlane | AltitudePlane));
            float ox = x - a.X, oy = y - a.Y;
            float iw = inverse + inverseX * ox + inverseY * oy;
            if (perspective) return iw;
            float reciprocal = 1 / iw;
            return (altitude + altitudeX * ox + altitudeY * oy) * reciprocal;
        }
    }

    private readonly struct Edge
    {
        private readonly SKPoint start, delta, clippedStart, clippedDelta;
        private readonly float lo, range, altitude, inverse, inverseDelta;
        private readonly int steps;
        internal readonly float Left, Top, Right, Bottom;

        internal Edge(H a, H b, float low, float high, bool perspective)
        {
            var c = H.Lerp(a, b, low); var d = H.Lerp(a, b, high);
            var p = c.Point; var q = d.Point;
            clippedStart = p;
            clippedDelta = new(q.X - p.X, q.Y - p.Y);
            Left = Math.Min(p.X, q.X); Right = Math.Max(p.X, q.X);
            Top = Math.Min(p.Y, q.Y); Bottom = Math.Max(p.Y, q.Y);
            altitude = a.Z;
            inverse = 1 / c.W;
            inverseDelta = 1 / d.W - inverse;
            if (perspective)
            {
                start = p; delta = clippedDelta; lo = 0; range = 1;
                steps = Math.Max(1, (int)MathF.Ceiling(Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y))));
            }
            else
            {
                start = a.Point; var end = b.Point;
                delta = new(end.X - start.X, end.Y - start.Y);
                lo = low; range = high - low;
                steps = Math.Max(1, (int)MathF.Ceiling(range * Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y))));
            }
        }

        internal bool TrySample(int x, int y, out float z, out float iw)
        {
            z = altitude; iw = float.NegativeInfinity;
            bool horizontal = Math.Abs(clippedDelta.X) >= Math.Abs(clippedDelta.Y);
            float origin = horizontal ? clippedStart.X : clippedStart.Y;
            float change = horizontal ? clippedDelta.X : clippedDelta.Y;
            float coordinate = horizontal ? x : y;
            int first = 0, last = steps;
            if (Math.Abs(change) > 1e-7f)
            {
                float a = (coordinate - origin) / change, b = (coordinate + 1 - origin) / change;
                first = Math.Max(0, (int)MathF.Floor(Math.Min(a, b) * steps) - 1);
                last = Math.Min(steps, (int)MathF.Ceiling(Math.Max(a, b) * steps) + 1);
            }
            for (int i = first; i <= last; i++)
            {
                float t = lo + range * i / steps;
                if ((int)MathF.Floor(start.X + delta.X * t) != x || (int)MathF.Floor(start.Y + delta.Y * t) != y) continue;
                iw = Math.Max(iw, inverse + inverseDelta * (i / (float)steps));
            }
            return float.IsFinite(iw);
        }
    }
}
