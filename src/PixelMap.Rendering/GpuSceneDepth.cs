using SkiaSharp;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PixelMap.Rendering;

/// <summary>A sparse screen-space index for picking and overlay visibility. Frame
/// preparation bins conservative terrain/model bounds; a queried cell prepares only
/// its contributing geometry. Exact clipped primitives are retained for later queries,
/// leaving unqueried vertices and all fragment rendering to GL.</summary>
internal sealed class GpuSceneDepth
{
    private const int CellSize = 16;
    private const int MaximumCellsPerProxy = 64;
    private const int CompleteCellAfterQueries = 8;
    private const int MaximumPooledStorage = 2;
    private const int MaximumPooledIndices = 2 * 1024 * 1024;
    private const int MaximumCachedPixels = 65536;
    private static readonly ConcurrentBag<Storage> pool = [];
    private static int pooledStorage;
    private static long nextRevision;
    internal long Revision { get; } = long.MinValue + Interlocked.Increment(ref nextRevision);
    private readonly int width, height, columns, rows;
    private readonly bool perspective;
    private readonly float pixelsPerMeter;
    private readonly TileTransform lens;
    private readonly GpuMapFrame frame;
    private readonly List<Triangle> triangles;
    private readonly List<Edge> edges;
    private readonly List<Group> groups;
    private readonly List<int> largeProxies;
    private readonly List<int>?[] pendingCells;
    private readonly List<ulong>?[] cells;
    private readonly bool[] preparedCells;
    private readonly byte[] cellQueries;
    private readonly Dictionary<int, PixelHit> samples;
    private readonly Storage storage;
    private int preparingOrder;
    private int references;
    private float minimumAltitude = float.PositiveInfinity, maximumAltitude = float.NegativeInfinity;

    internal GpuSceneDepth(GpuMapFrame frame)
    {
        this.frame = frame;
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
        groups = storage.Groups;
        largeProxies = storage.LargeProxies;
        samples = storage.Samples;
        if (storage.Cells.Length < columns * rows)
        {
            storage.Cells = new List<ulong>?[columns * rows];
            storage.PendingCells = new List<int>?[columns * rows];
            storage.PreparedCells = new bool[columns * rows];
            storage.CellQueries = new byte[columns * rows];
        }
        cells = storage.Cells;
        pendingCells = storage.PendingCells;
        preparedCells = storage.PreparedCells;
        cellQueries = storage.CellQueries;
        perspective = frame.Camera.Perspective;
        pixelsPerMeter = frame.PixelsPerMeter;
        lens = new TileProjection(frame.Camera, frame.Width, frame.Height, frame.PixelSize).Get(new(0, 0, 0), 0).Transform;
        for (int i = 0; i < frame.Terrain.Length; i++) IndexTerrain(i);
        for (int i = 0; i < frame.Buildings.Length; i++) IndexBuildings(i);
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
            storage.ProjectedVertices.Length > 131072 || groups.Capacity > 65536 || cells.Length > 16384) return;
        // Screen-filling and changing views can accumulate different cell high-water
        // marks. Limit all retained exact/coarse index arrays to 8 MiB per workspace.
        long indices = largeProxies.Capacity;
        foreach (var cell in cells) { indices += (cell?.Capacity ?? 0) * 2L; cell?.Clear(); }
        foreach (var cell in pendingCells) { indices += cell?.Capacity ?? 0; cell?.Clear(); }
        if (indices > MaximumPooledIndices) return;
        if (Interlocked.Increment(ref pooledStorage) > MaximumPooledStorage)
        {
            Interlocked.Decrement(ref pooledStorage);
            return;
        }
        triangles.Clear();
        edges.Clear();
        groups.Clear();
        largeProxies.Clear();
        samples.Clear();
        Array.Clear(preparedCells);
        Array.Clear(cellQueries);
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
        float modelDepth = float.NegativeInfinity, edgeDepth = float.NegativeInfinity;
        int terrainIndex = -1;
        ulong terrainOrder = 0;
        bool hasSurface = false;
        int cell = (y / CellSize) * columns + x / CellSize;
        if (!preparedCells[cell]) PrepareCell(cell, sx, sy, ++cellQueries[cell] >= CompleteCellAfterQueries);
        var preparedTriangles = CollectionsMarshal.AsSpan(triangles);
        if (cells[cell] is { } candidates)
            foreach (ulong key in candidates)
            {
                int primitive = (int)((uint)key >> 1);
                if ((key & 1) != 0) primitive = ~primitive;
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
                if (isTerrain && (depth > terrainDepth || depth == terrainDepth && key >= terrainOrder))
                {
                    terrainDepth = depth;
                    terrainIndex = primitive;
                    terrainOrder = key;
                }
                else if (!isTerrain)
                {
                    if (primitive < 0) edgeDepth = Math.Max(edgeDepth, depth);
                    else modelDepth = Math.Max(modelDepth, depth);
                }
                nearest = Math.Max(nearest, depth);
                hasSurface = true;
            }
        // Every terrain primitive precedes every model primitive in the eager pass.
        // Once a model covers terrain, no later primitive can restore terrain status.
        // Thus only these maxima matter; cell entries need no ordering or sorting.
        // Rims retain the original small inverse-depth tolerance, without changing
        // the stored maximum. Equal-depth terrain picks still use source order.
        bool surfaceIsTerrain = terrainIndex >= 0 && modelDepth < terrainDepth &&
            edgeDepth < terrainDepth - (perspective ? 1e-6f : 0);
        var result = new PixelHit(nearest, terrainIndex, hasSurface, surfaceIsTerrain);
        if (samples.Count >= MaximumCachedPixels) samples.Clear();
        samples.Add(offset, result);
        return result;
    }

    private void IndexTerrain(int drawIndex)
    {
        var draw = frame.Terrain[drawIndex];
        var bounds = draw.Mesh.Bounds;
        float low = (float)frame.TerrainOptions.LocalAltitude(frame.Camera, bounds.Minimum) * pixelsPerMeter;
        float high = (float)frame.TerrainOptions.LocalAltitude(frame.Camera, bounds.Maximum) * pixelsPerMeter;
        IncludeAltitude(low); IncludeAltitude(high);
        // Terrain did not previously have a box-frustum rejection. Keep that exact
        // clipping behavior, including patches meeting a screen/near-plane boundary.
        if (!Bounds(bounds.Area, low, high, draw.Transform, rejectOutside: false, out var screen, out var cells)) return;
        int group = groups.Count;
        groups.Add(new(drawIndex, -1, 0, low, high, screen, cells));
        IndexProxy(group);
    }

    private void IndexBuildings(int drawIndex)
    {
        var draw = frame.Buildings[drawIndex];
        var mesh = draw.Mesh;
        var transform = draw.Transform;
        for (int modelIndex = 0; modelIndex < mesh.Ranges.Length; modelIndex++)
        {
            var model = mesh.Ranges[modelIndex].Model;
            float foundation = (float)frame.TerrainOptions.LocalAltitude(frame.Camera, mesh.Models[modelIndex * GpuBuildingMesh.ModelStride]);
            float top = (model.Top + foundation) * pixelsPerMeter, bottom = (model.Bottom + foundation) * pixelsPerMeter;
            // GL's normalized depth range includes every submitted model, even when
            // its physical-camera selection or screen-space bounds reject it.
            IncludeAltitude(top); IncludeAltitude(bottom);
            if (draw.Selection is { } selected && selected.Models[modelIndex] == 0) continue;
            if (!Bounds(model.Bounds, bottom, top, transform, rejectOutside: true, out var screen, out var cells)) continue;
            float rx = (Math.Abs(transform.XX) * model.Bounds.Width + Math.Abs(transform.XY) * model.Bounds.Height) * .5f;
            float ry = (Math.Abs(transform.YX) * model.Bounds.Width + Math.Abs(transform.YY) * model.Bounds.Height) * .5f;
            float rise = top * transform.SinPitch, baseRise = bottom * transform.SinPitch;
            if (!perspective && rx < .28f && ry + (foundation == 0 ? rise : rise - baseRise) < .5f) continue;
            int group = groups.Count;
            groups.Add(new(drawIndex, modelIndex, foundation, bottom, top, screen, cells));
            IndexProxy(group);
        }
    }

    private void IndexProxy(int group)
    {
        var bounds = groups[group].Cells;
        if ((bounds.Right - bounds.Left + 1) * (bounds.Bottom - bounds.Top + 1) > MaximumCellsPerProxy)
        {
            // Near-plane crossings and screen-filling terrain/models must not grow
            // a model×viewport-cell index. One overflow entry bounds their storage.
            largeProxies.Add(group);
            return;
        }
        for (int y = bounds.Top; y <= bounds.Bottom; y++)
        for (int x = bounds.Left; x <= bounds.Right; x++)
            (pendingCells[y * columns + x] ??= []).Add(group);
    }

    private void PrepareCell(int cell, float sx, float sy, bool complete)
    {
        int x = cell % columns, y = cell / columns;
        var local = pendingCells[cell];
        if (local is not null)
            foreach (int group in local) PrepareAt(group, sx, sy, complete);
        foreach (int group in largeProxies)
            if (groups[group].Cells.Contains(x, y)) PrepareAt(group, sx, sy, complete);
        preparedCells[cell] = complete;
    }

    private void PrepareAt(int group, float sx, float sy, bool complete)
    {
        var screen = groups[group].Screen;
        if (complete || sx >= screen.Left && sx <= screen.Right && sy >= screen.Top && sy <= screen.Bottom)
            PrepareGroup(group);
    }

    private void PrepareGroup(int index)
    {
        ref var group = ref CollectionsMarshal.AsSpan(groups)[index];
        if (group.FirstTriangle >= 0) return;
        group.FirstTriangle = triangles.Count;
        preparingOrder = index;
        if (group.Model < 0) AddTerrain(frame.Terrain[group.Draw], frame);
        else AddBuilding(frame.Buildings[group.Draw], group);
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
                projected[i] = new(draw.Transform.Vertex(vertices[offset], vertices[offset + 1], altitude), raw);
            }
            var indices = draw.Mesh.TriangleIndices;
            for (int i = 0; i < indices.Length; i += 3)
                AddTriangle(ref projected[indices[i]], ref projected[indices[i + 1]], ref projected[indices[i + 2]], draw.Transform.Far, true);
        }
        finally { if (storage is not null) ArrayPool<Vertex>.Shared.Return(storage); }
    }

    private void AddBuilding(GpuBuildingDraw draw, in Group group)
    {
        var mesh = draw.Mesh;
        var transform = draw.Transform;
        var source = mesh.Vertices;
        var range = mesh.Ranges[group.Model];
        var projected = storage.ProjectedVertices;
        if (projected.Length < range.VertexCount)
            storage.ProjectedVertices = projected = new Vertex[range.VertexCount];
        float top = group.Top, bottom = group.Bottom, foundation = group.Foundation;
        float rise = top * transform.SinPitch, baseRise = bottom * transform.SinPitch;
        float eye = perspective ? transform.TargetZ + transform.CosPitch / transform.InverseFocal : float.PositiveInfinity;
        for (int i = 0; i < range.VertexCount; i++)
        {
            int offset = (range.FirstVertex + i) * GpuBuildingMesh.VertexStride;
            int face = (int)source[offset + 11];
            // Caps have separate vertices and their same face test precedes index
            // traversal, so hidden caps never read an uninitialized scratch entry.
            if (face == 1 && top >= eye || face == 2 && (!perspective || bottom <= eye)) continue;
            float altitude = (source[offset + 2] + foundation) * pixelsPerMeter;
            projected[i] = new(transform.Vertex(source[offset], source[offset + 1], altitude));
        }
        for (int i = range.FirstIndex, end = i + range.IndexCount; i < end; i += 3)
        {
            int first = (int)mesh.Indices[i];
            int offset = first * GpuBuildingMesh.VertexStride;
            int face = (int)source[offset + 11];
            if (face == 1 && top >= eye || face == 2 && (!perspective || bottom <= eye)) continue;
            if (face == 0)
            {
                if (!perspective && (rise - baseRise < .35f || -(transform.XX * source[offset + 8] + transform.XY * source[offset + 9]) < .01f)) continue;
                var a = projected[first - range.FirstVertex].Screen;
                var b = projected[(int)mesh.Indices[i + 1] - range.FirstVertex].Screen;
                var c = projected[(int)mesh.Indices[i + 2] - range.FirstVertex].Screen;
                if (perspective && Orientation(a, b, c) >= -.00001f) continue;
            }
            AddTriangle(ref projected[first - range.FirstVertex],
                ref projected[(int)mesh.Indices[i + 1] - range.FirstVertex],
                ref projected[(int)mesh.Indices[i + 2] - range.FirstVertex], transform.Far, false);
        }
        if (perspective)
        {
            if (top >= eye) return;
        }
        else
        {
            float rx = (Math.Abs(transform.XX) * range.Model.Bounds.Width + Math.Abs(transform.XY) * range.Model.Bounds.Height) * .5f;
            float ry = (Math.Abs(transform.YX) * range.Model.Bounds.Width + Math.Abs(transform.YY) * range.Model.Bounds.Height) * .5f;
            if (rx < 3 || ry < 1.5f) return;
        }
        for (int i = range.FirstEdge, end = i + range.EdgeCount; i < end; i += 2)
        {
            int a = i * GpuBuildingMesh.EdgeStride, b = a + GpuBuildingMesh.EdgeStride;
            AddEdge(transform.Vertex(mesh.RoofEdges[a], mesh.RoofEdges[a + 1], top),
                transform.Vertex(mesh.RoofEdges[b], mesh.RoofEdges[b + 1], top), transform.Far);
        }
    }

    private bool Bounds(SKRect bounds, float bottom, float top, TileTransform transform,
        bool rejectOutside, out SKRect screen, out CellBounds result)
    {
        int common = 63;
        bool crossesNear = false;
        float left = float.PositiveInfinity, upper = float.PositiveInfinity;
        float right = float.NegativeInfinity, lower = float.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            var corner = transform.Vertex((i & 1) == 0 ? bounds.Left : bounds.Right,
                (i & 2) == 0 ? bounds.Top : bounds.Bottom, (i & 4) == 0 ? bottom : top);
            common &= OutCode(corner, transform.Far);
            if (!(corner.W >= lens.Near)) { crossesNear = true; continue; }
            var point = corner.Point;
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) { crossesNear = true; continue; }
            left = Math.Min(left, point.X); upper = Math.Min(upper, point.Y);
            right = Math.Max(right, point.X); lower = Math.Max(lower, point.Y);
        }
        result = default;
        screen = default;
        if (rejectOutside && common != 0) return false;
        if (crossesNear)
        {
            // A box crossing the eye/near plane can project outside every corner.
            // Its bounded overflow proxy is refined by the original exact clipper.
            result = new(0, 0, columns - 1, rows - 1);
            screen = new(0, 0, width, height);
            return true;
        }
        // With positive homogeneous weights a projective box reaches its extrema
        // at a corner. Expand for float projection roundoff and the rim's DDA pixel.
        float marginX = 2 + Math.Max(Math.Abs(left), Math.Abs(right)) * .00002f;
        float marginY = 2 + Math.Max(Math.Abs(upper), Math.Abs(lower)) * .00002f;
        left -= marginX; right += marginX; upper -= marginY; lower += marginY;
        if (right < 0 || lower < 0 || left >= width || upper >= height) return false;
        screen = new(Math.Max(0, left), Math.Max(0, upper), Math.Min(width, right), Math.Min(height, lower));
        // Box corners have not been clipped and can exceed Int32 at a physical
        // near plane. Clamp in floating point before converting to cell indices.
        result = new((int)(Math.Clamp(left, 0, width - 1) / CellSize),
            (int)(Math.Clamp(upper, 0, height - 1) / CellSize),
            (int)(Math.Clamp(right, 0, width - 1) / CellSize),
            (int)(Math.Clamp(lower, 0, height - 1) / CellSize));
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
        Bin(((ulong)preparingOrder << 32) | ((ulong)(uint)index << 1), triangle.Left, triangle.Top, triangle.Right, triangle.Bottom);
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
        Bin(((ulong)preparingOrder << 32) | ((ulong)(uint)index << 1) | 1, edge.Left, edge.Top, edge.Right + 1, edge.Bottom + 1);
    }

    private void Bin(ulong primitive, float left, float top, float right, float bottom)
    {
        if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom) ||
            right < 0 || bottom < 0 || left >= width || top >= height) return;
        int firstX = Math.Clamp((int)MathF.Floor(left / CellSize), 0, columns - 1);
        int lastX = Math.Clamp((int)MathF.Floor(right / CellSize), 0, columns - 1);
        int firstY = Math.Clamp((int)MathF.Floor(top / CellSize), 0, rows - 1);
        int lastY = Math.Clamp((int)MathF.Floor(bottom / CellSize), 0, rows - 1);
        for (int y = firstY; y <= lastY; y++)
        for (int x = firstX; x <= lastX; x++)
        {
            int cell = y * columns + x;
            (cells[cell] ??= []).Add(primitive);
        }
    }

    private void IncludeAltitude(float altitude)
    {
        minimumAltitude = Math.Min(minimumAltitude, altitude);
        maximumAltitude = Math.Max(maximumAltitude, altitude);
    }

    private readonly record struct PixelHit(float Depth, int TerrainTriangle, bool HasSurface, bool SurfaceIsTerrain);

    private readonly record struct CellBounds(int Left, int Top, int Right, int Bottom)
    {
        internal bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
    }

    private struct Group(int draw, int model, float foundation, float bottom, float top, SKRect screen, CellBounds cells)
    {
        internal readonly int Draw = draw, Model = model;
        internal readonly float Foundation = foundation, Bottom = bottom, Top = top;
        internal readonly CellBounds Cells = cells;
        internal readonly SKRect Screen = screen;
        internal int FirstTriangle = -1;
    }

    private sealed class Storage
    {
        internal readonly List<Triangle> Triangles = new(1024);
        internal readonly List<Edge> Edges = [];
        internal readonly List<Group> Groups = [];
        internal readonly List<int> LargeProxies = [];
        internal readonly Dictionary<int, PixelHit> Samples = [];
        internal List<ulong>?[] Cells = [];
        internal List<int>?[] PendingCells = [];
        internal bool[] PreparedCells = [];
        internal byte[] CellQueries = [];
        // Indexed primitives retain value copies, so model scratch can span tiles and frames.
        internal Vertex[] ProjectedVertices = [];
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
