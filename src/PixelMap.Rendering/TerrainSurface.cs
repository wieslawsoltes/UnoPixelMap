using PixelMap.Data;
using SkiaSharp;
using System.Buffers;

namespace PixelMap.Rendering;

/// <summary>A bounded, screen-adaptive height field. Artwork is baked independently of
/// the camera, then sampled with perspective-correct texture coordinates into the same
/// art-pixel depth buffer as buildings. No elevation-source lookup occurs in raster loops.</summary>
internal sealed class TerrainSurface : IDisposable
{
    internal const int TextureSize = 256;
    private const int Segments = 16;
    private const int Side = Segments + 1;
    private const int MaximumPatches = 64;
    private const int MaximumTextures = 96;
    private const int MaximumMeshes = 256;
    private readonly Dictionary<TileId, Texture> textures = [];
    private readonly Dictionary<TileId, Mesh> meshes = [];
    private readonly Dictionary<(TileId Id, int Copy), GpuTerrainMesh> gpuMeshes = [];
    private readonly Dictionary<TileId, (double Low, double High)> bounds = [];
    private readonly List<Patch> patches = [];
    private readonly List<float[]> stitched = [];
    private readonly Dictionary<TileId, int> heightPatches = [];
    private readonly List<int> heightZooms = [];
    private readonly Vertex[] projected = new Vertex[Side * Side];
    private readonly float[] blockHeights = new float[Side * Side * 2];
    private readonly float[] patchFloors = new float[MaximumPatches];
    private ElevationSnapshot? elevation;
    private float[] groundAltitude = [], groundHeight = [];
    private float[] depthX = [], depthY = [];
    private uint[] photograph = [];
    private bool[] photographic = [];
    private int width, height;
    private long frame;
    private float meters;
    private float near;
    private MapCamera camera;
    private TerrainOptions options = new(ElevationSnapshot.Empty);
    private TileProjection projection;
    private GpuMapFrame? gpuScene;
    private GpuTerrainMesh[] previousGpuSelection = [];
    internal long HeightRevision { get; private set; }

    private sealed class Texture(SKBitmap bitmap, SKRect[] earthAreas) : IDisposable
    {
        internal readonly SKBitmap Bitmap = bitmap;
        internal readonly SKRect[] EarthAreas = earthAreas;
        internal GpuTerrainTexture? Gpu;
        internal long Frame;
        public void Dispose() => Bitmap.Dispose();
    }

    private sealed class Mesh
    {
        internal readonly float[] Heights = new float[Side * Side];
        internal readonly float[] Light = new float[Side * Side];
        internal long Frame;
    }

    private readonly record struct Patch(TileId Id, int Copy, TileTransform Transform, float Priority)
    {
        internal double Size => 1d / (1 << Id.Z);
        internal double Left => Id.X * Size + Copy;
        internal double Top => Id.Y * Size;
    }

    private readonly record struct Vertex(H Screen, float U, float V, float Height, float Light, int Face = 0)
    {
        internal static Vertex Lerp(Vertex a, Vertex b, float t) => new(H.Lerp(a.Screen, b.Screen, t),
            a.U + (b.U - a.U) * t, a.V + (b.V - a.V) * t,
            a.Height + (b.Height - a.Height) * t, a.Light + (b.Light - a.Light) * t, a.Face);
    }

    internal void ClearTextures()
    {
        foreach (var entry in textures.Values) entry.Dispose();
        textures.Clear();
    }

    internal unsafe void Draw(SKBitmap bitmap, BuildingRasterizer depth, MapCamera view, int logicalWidth,
        int logicalHeight, int pixelSize, TerrainOptions terrain, bool preservePhotograph,
        Func<TileId, SKBitmap, SKRect[]> bake)
    {
        Prepare(view, logicalWidth, logicalHeight, pixelSize, bitmap.Width, bitmap.Height, terrain);
        int length = checked(width * height);
        if (groundAltitude.Length < length)
        {
            groundAltitude = new float[length];
            groundHeight = new float[length];
            depthX = new float[length];
            depthY = new float[length];
        }
        groundAltitude.AsSpan(0, length).Fill(float.NaN);
        if (preservePhotograph)
        {
            if (photograph.Length < length) { photograph = new uint[length]; photographic = new bool[length]; }
            photographic.AsSpan(0, length).Clear();
        }
        var pixels = new Span<uint>((void*)bitmap.GetPixels(), length);
        for (int i = 0; i < patches.Count; i++)
        {
            var patch = patches[i];
            var mesh = meshes[patch.Id];
            var heights = stitched[i];
            var texture = GetTexture(patch.Id, bake);
            var texels = new ReadOnlySpan<uint>((void*)texture.Bitmap.GetPixels(), TextureSize * TextureSize);
            if (MapRendering.Blocks(options.RenderStyle))
            {
                DrawBlocks(GetGpuMesh(i), patch.Transform, pixels, texels, texture.EarthAreas, depth, preservePhotograph);
                continue;
            }
            for (int row = 0; row < Side; row++)
            for (int column = 0; column < Side; column++)
            {
                int index = row * Side + column;
                float altitude = (float)terrain.LocalAltitude(camera, heights[index]) * meters;
                projected[index] = new(patch.Transform.Vertex(column * (TileGeometry.Extent / Segments),
                    row * (TileGeometry.Extent / Segments), altitude),
                    column * (TextureSize / (float)Segments), row * (TextureSize / (float)Segments),
                    heights[index], mesh.Light[index]);
            }
            for (int row = 0; row < Segments; row++)
            for (int column = 0; column < Segments; column++)
            {
                int a = row * Side + column, b = a + 1, c = a + Side, d = c + 1;
                Triangle(projected[a], projected[b], projected[d], patch.Transform.Far,
                    pixels, texels, texture.EarthAreas, depth.Heights, depth.InverseDepth, preservePhotograph);
                Triangle(projected[a], projected[d], projected[c], patch.Transform.Far,
                    pixels, texels, texture.EarthAreas, depth.Heights, depth.InverseDepth, preservePhotograph);
            }
        }
        depth.TerrainComplete();
        TrimCaches();
    }

    private void DrawBlocks(GpuTerrainMesh mesh, TileTransform transform, Span<uint> pixels,
        ReadOnlySpan<uint> texels, SKRect[] earthAreas, BuildingRasterizer depth, bool preservePhotograph)
    {
        int count = mesh.Vertices.Length / GpuTerrainMesh.VertexStride;
        var storage = ArrayPool<Vertex>.Shared.Rent(count);
        try
        {
            var vertices = storage.AsSpan(0, count);
            for (int i = 0; i < count; i++)
            {
                int offset = i * GpuTerrainMesh.VertexStride;
                float x = mesh.Vertices[offset], y = mesh.Vertices[offset + 1], raw = mesh.Vertices[offset + 2];
                vertices[i] = new(transform.Vertex(x, y, (float)options.LocalAltitude(camera, raw) * meters),
                    x * TextureSize / TileGeometry.Extent, y * TextureSize / TileGeometry.Extent,
                    raw, mesh.Vertices[offset + 3], TerrainBlockMesh.Face(i, mesh.RenderStyle));
            }
            var indices = mesh.TriangleIndices;
            for (int i = 0; i < indices.Length; i += 3)
                Triangle(vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]], transform.Far,
                    pixels, texels, earthAreas, depth.Heights, depth.InverseDepth, preservePhotograph);
        }
        finally { ArrayPool<Vertex>.Shared.Return(storage); }
    }

    private void Prepare(MapCamera view, int logicalWidth, int logicalHeight, int pixelSize,
        int artWidth, int artHeight, TerrainOptions terrain)
    {
        frame++;
        gpuScene = null;
        camera = view;
        near = (float)camera.NearLimit(logicalHeight);
        options = terrain;
        width = artWidth;
        height = artHeight;
        meters = (float)(camera.WorldSize / pixelSize / (MapCamera.EarthCircumference *
            Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)));
        projection = new(camera, logicalWidth, logicalHeight, pixelSize);
        if (!ReferenceEquals(elevation, terrain.Elevation))
        {
            elevation = terrain.Elevation;
            meshes.Clear();
            bounds.Clear();
        }
        SelectPatches();
        // Process coarser edges first. Fine meshes interpolate those same edges, including
        // corners already shared with an even coarser neighbour, preventing LOD cracks.
        patches.Sort(static (a, b) => a.Id.Z.CompareTo(b.Id.Z));
        heightPatches.Clear();
        heightZooms.Clear();
        for (int i = 0; i < patches.Count; i++)
        {
            var patch = patches[i];
            var mesh = GetMesh(patch.Id);
            if (i == stitched.Count) stitched.Add(new float[Side * Side]);
            mesh.Heights.CopyTo(stitched[i], 0);
            Stitch(i, patch, stitched[i]);
            float minimum = float.PositiveInfinity;
            foreach (float value in stitched[i]) minimum = Math.Min(minimum, value);
            patchFloors[i] = minimum;
            if (heightZooms.Count == 0 || heightZooms[^1] != patch.Id.Z) heightZooms.Add(patch.Id.Z);
            if (!heightPatches.TryGetValue(patch.Id, out int other) ||
                Math.Abs(patch.Left + patch.Size * .5 - camera.CenterX) < Math.Abs(patches[other].Left + patch.Size * .5 - camera.CenterX))
                heightPatches[patch.Id] = i;
        }
    }

    internal TerrainGpuDraw[] PrepareGpu(MapCamera view, int logicalWidth, int logicalHeight,
        int pixelSize, TerrainOptions terrain, Func<TileId, SKBitmap, SKRect[]> bake)
    {
        Prepare(view, logicalWidth, logicalHeight, pixelSize, (logicalWidth + pixelSize - 1) / pixelSize,
            (logicalHeight + pixelSize - 1) / pixelSize, terrain);
        var draws = new TerrainGpuDraw[patches.Count];
        var selection = new GpuTerrainMesh[patches.Count];
        bool changed = previousGpuSelection.Length != patches.Count;
        for (int i = 0; i < patches.Count; i++)
        {
            var patch = patches[i];
            var retained = GetGpuMesh(i);
            selection[i] = retained;
            if (!changed && !ReferenceEquals(previousGpuSelection[i], retained)) changed = true;
            var texture = GetTexture(patch.Id, bake);
            draws[i] = new(retained, texture.Gpu ??= PrepareGpuTexture(texture), patch.Transform);
        }
        if (changed) HeightRevision++;
        previousGpuSelection = selection;
        TrimCaches();
        return draws;
    }

    private GpuTerrainMesh GetGpuMesh(int index)
    {
        var patch = patches[index];
        var source = meshes[patch.Id];
        var heights = stitched[index];
        var key = (patch.Id, patch.Copy);
        gpuMeshes.TryGetValue(key, out var retained);
        if (retained is not null && retained.Frame == frame && retained.RenderStyle == options.RenderStyle) return retained;
        bool blocks = MapRendering.Blocks(options.RenderStyle);
        if (blocks)
            TerrainBlockMesh.WriteHeights(patch.Id, heights, source.Light, SkirtHeight(index), options.RenderStyle, blockHeights);
        bool update = retained is null || retained.RenderStyle != options.RenderStyle;
        if (!update && blocks) update = retained!.HeightLight is null || !blockHeights.AsSpan().SequenceEqual(retained.HeightLight);
        if (!update && !blocks)
            for (int vertex = 0; vertex < heights.Length; vertex++)
                if (retained!.Vertices[vertex * 4 + 2] != heights[vertex] || retained.Vertices[vertex * 4 + 3] != source.Light[vertex])
                { update = true; break; }
        if (update)
        {
            float[] vertices;
            float[]? samples = null;
            if (blocks)
            {
                samples = (float[])blockHeights.Clone();
                vertices = TerrainBlockMesh.Vertices(samples, options.RenderStyle);
            }
            else
            {
                vertices = new float[Side * Side * GpuTerrainMesh.VertexStride];
                for (int row = 0; row < Side; row++)
                for (int column = 0; column < Side; column++)
                {
                    int vertex = row * Side + column, offset = vertex * GpuTerrainMesh.VertexStride;
                    vertices[offset] = column * (TileGeometry.Extent / Segments);
                    vertices[offset + 1] = row * (TileGeometry.Extent / Segments);
                    vertices[offset + 2] = heights[vertex];
                    vertices[offset + 3] = source.Light[vertex];
                }
            }
            retained = new(retained?.ResourceKey ?? new object(), patch.Id, patch.Copy, vertices,
                (retained?.Revision ?? 0) + 1, options.RenderStyle, samples);
            gpuMeshes[key] = retained;
        }
        retained!.Frame = frame;
        return retained;
    }

    private float SkirtHeight(int index)
    {
        var patch = patches[index];
        float minimum = patchFloors[index];
        double epsilon = patch.Size * 1e-7;
        for (int i = 0; i < patches.Count; i++)
        {
            var neighbour = patches[i];
            if (neighbour.Left > patch.Left + patch.Size + epsilon || neighbour.Left + neighbour.Size < patch.Left - epsilon ||
                neighbour.Top > patch.Top + patch.Size + epsilon || neighbour.Top + neighbour.Size < patch.Top - epsilon) continue;
            minimum = Math.Min(minimum, patchFloors[i]);
        }
        float step = MapRendering.HeightStep(options.RenderStyle);
        return MathF.Floor(minimum / step) * step - step;
    }

    private static unsafe GpuTerrainTexture PrepareGpuTexture(Texture source)
    {
        var pixels = new ReadOnlySpan<uint>((void*)source.Bitmap.GetPixels(), TextureSize * TextureSize);
        var texels = new uint[pixels.Length];
        for (int i = 0; i < pixels.Length; i++) texels[i] = pixels[i] & 0xffffff;
        foreach (var area in source.EarthAreas)
        {
            int left = Math.Clamp((int)MathF.Floor(area.Left - .5f), 0, TextureSize);
            int top = Math.Clamp((int)MathF.Floor(area.Top - .5f), 0, TextureSize);
            int right = Math.Clamp((int)MathF.Ceiling(area.Right + .5f), 0, TextureSize);
            int bottom = Math.Clamp((int)MathF.Ceiling(area.Bottom + .5f), 0, TextureSize);
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (area.Contains(x + .5f, y + .5f)) texels[y * TextureSize + x] |= 0xff000000;
        }
        return new(texels);
    }

    internal void ActivateGpu(GpuMapFrame scene)
    {
        gpuScene = scene;
        camera = scene.Camera;
        near = (float)camera.NearLimit(scene.Height);
        options = scene.TerrainOptions;
        width = scene.ArtWidth;
        height = scene.ArtHeight;
        meters = scene.PixelsPerMeter;
        projection = new(camera, scene.Width, scene.Height, scene.PixelSize);
    }

    private void SelectPatches()
    {
        patches.Clear();
        var copies = projection.Copies(new(0, 0, 0));
        // Elevated ridges may extend beyond the zero-height footprint used by tile requests.
        // Limit repeated worlds by the same scene budget, including unusually wide views.
        int centerCopy = (int)Math.Floor(camera.CenterX);
        int first = Math.Max(centerCopy - MaximumPatches / 2, copies.First - 1);
        int last = Math.Min(centerCopy + MaximumPatches / 2 - 1, copies.Last + 1);
        for (int copy = first; copy <= last; copy++) AddPatch(new(0, 0, 0), copy, patches);
        var children = new List<Patch>(4);
        while (patches.Count < MaximumPatches)
        {
            int candidate = -1;
            float priority = 192;
            for (int i = 0; i < patches.Count; i++)
            {
                if (patches[i].Id.Z >= 22 || patches[i].Priority <= priority) continue;
                candidate = i;
                priority = patches[i].Priority;
            }
            if (candidate < 0) break;
            var parent = patches[candidate];
            children.Clear();
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                AddPatch(new(parent.Id.Z + 1, parent.Id.X * 2 + x, parent.Id.Y * 2 + y), parent.Copy, children);
            if (patches.Count - 1 + children.Count > MaximumPatches)
            {
                patches[candidate] = parent with { Priority = 0 };
                continue;
            }
            patches.RemoveAt(candidate);
            patches.AddRange(children);
        }
    }

    private void AddPatch(TileId id, int copy, List<Patch> target)
    {
        var (transform, _) = projection.Get(id, copy);
        var range = options.Relief ? HeightBounds(id) : default;
        // Quantized caps and seam skirts extend beyond the physical DEM extrema.
        double margin = MapRendering.Blocks(options.RenderStyle) ? MapRendering.HeightStep(options.RenderStyle) * 2 : 0;
        float low = (float)options.LocalAltitude(camera, range.Low - margin) * meters;
        float high = (float)options.LocalAltitude(camera, range.High + margin) * meters;
        Span<H> corners = stackalloc H[8];
        for (int corner = 0; corner < corners.Length; corner++)
            corners[corner] = transform.Vertex((corner & 1) == 0 ? 0 : TileGeometry.Extent,
                (corner & 2) == 0 ? 0 : TileGeometry.Extent, (corner & 4) == 0 ? low : high);
        for (int plane = 0; plane < 6; plane++)
        {
            bool outside = true;
            foreach (var corner in corners)
                if (TileTransform.Plane(plane, corner, width, height, transform.Far, transform.Near) >= 0) { outside = false; break; }
            if (outside) return;
        }
        float minimumW = transform.Perspective ? float.PositiveInfinity : 1;
        foreach (var corner in corners) minimumW = Math.Min(minimumW, Math.Max(transform.Near, corner.W));
        // A projected patch's unforeshortened side controls both texture detail and mesh
        // density. Near-plane patches split before rasterization, keeping clipping bounded.
        float priority = MathF.Sqrt(transform.XX * transform.XX + transform.YX * transform.YX) * TileGeometry.Extent / minimumW;
        priority = Math.Max(priority, MathF.Sqrt(transform.XY * transform.XY + transform.YY * transform.YY) * TileGeometry.Extent / minimumW);
        target.Add(new(id, copy, transform, priority));
    }

    private (double Low, double High) HeightBounds(TileId id)
    {
        if (bounds.TryGetValue(id, out var cached)) return cached;
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        bool covered = false;
        double size = 1d / (1 << id.Z), left = id.X * size, top = id.Y * size;
        foreach (var tile in elevation!.Tiles)
        {
            int count = 1 << tile.Id.Z;
            for (int copy = -1; copy <= 1; copy++)
            {
                if (copy < 0 && (id.X != 0 || tile.Id.X != count - 1)) continue;
                if (copy > 0 && (id.X != (1 << id.Z) - 1 || tile.Id.X != 0)) continue;
                double x0 = (left * count - tile.Id.X - copy * count) * tile.Size;
                double y0 = (top * count - tile.Id.Y) * tile.Size;
                double x1 = x0 + size * count * tile.Size, y1 = y0 + size * count * tile.Size;
                if (x1 < -1 || y1 < -1 || x0 > tile.Size + 1 || y0 > tile.Size + 1) continue;
                covered |= x0 >= 0 && y0 >= 0 && x1 <= tile.Size && y1 <= tile.Size && tile.ValidSampleCount == tile.Size * tile.Size;
                int firstX = (int)Math.Clamp(Math.Floor(x0 - 1), 0, tile.Size - 1);
                int firstY = (int)Math.Clamp(Math.Floor(y0 - 1), 0, tile.Size - 1);
                int lastX = (int)Math.Clamp(Math.Ceiling(x1 + 1), 0, tile.Size - 1);
                int lastY = (int)Math.Clamp(Math.Ceiling(y1 + 1), 0, tile.Size - 1);
                if (firstX == 0 && firstY == 0 && lastX == tile.Size - 1 && lastY == tile.Size - 1)
                {
                    low = Math.Min(low, tile.MinimumMeters);
                    high = Math.Max(high, tile.MaximumMeters);
                    continue;
                }
                var heights = tile.Heights;
                for (int y = firstY; y <= lastY; y++)
                for (int x = firstX; x <= lastX; x++)
                {
                    float value = heights[y * tile.Size + x];
                    if (!float.IsFinite(value)) continue;
                    low = Math.Min(low, value);
                    high = Math.Max(high, value);
                }
            }
        }
        if (!double.IsFinite(low)) low = high = 0;
        // Missing samples and classified ocean can produce zero even when the decoded
        // land samples are signed. The spatial water index also catches tiny coastlines.
        if (!covered || elevation.MayContainOcean(id))
        {
            low = Math.Min(low, 0); high = Math.Max(high, 0);
        }
        return bounds[id] = (low, high);
    }

    private Mesh GetMesh(TileId id)
    {
        if (meshes.TryGetValue(id, out var cached)) { cached.Frame = frame; return cached; }
        var mesh = new Mesh { Frame = frame };
        double size = 1d / (1 << id.Z), left = id.X * size, top = id.Y * size;
        double step = size / Segments;
        for (int row = 0; row < Side; row++)
        for (int column = 0; column < Side; column++)
        {
            double x = left + column * step, y = top + row * step;
            int index = row * Side + column;
            mesh.Heights[index] = (float)elevation!.SampleSurfaceMercator(x, y);
            double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y)));
            double span = 2 * step * MapCamera.EarthCircumference * Math.Cos(latitude);
            double dx = (elevation.SampleSurfaceMercator(x + step, y) - elevation.SampleSurfaceMercator(x - step, y)) / Math.Max(1, span);
            double dy = (elevation.SampleSurfaceMercator(x, y + step) - elevation.SampleSurfaceMercator(x, y - step)) / Math.Max(1, span);
            // Northwest light in geographic space stays fixed while the camera orbits.
            mesh.Light[index] = (float)((.45 * dx + .55 * dy + .705) / Math.Sqrt(1 + dx * dx + dy * dy));
        }
        meshes[id] = mesh;
        return mesh;
    }

    private void Stitch(int patchIndex, Patch patch, float[] heights)
    {
        for (int i = 0; i < patchIndex; i++)
        {
            var neighbour = patches[i];
            if (neighbour.Id.Z >= patch.Id.Z) continue;
            double epsilon = patch.Size * 1e-7;
            if (neighbour.Left > patch.Left + patch.Size + epsilon || neighbour.Left + neighbour.Size < patch.Left - epsilon ||
                neighbour.Top > patch.Top + patch.Size + epsilon || neighbour.Top + neighbour.Size < patch.Top - epsilon) continue;
            var source = stitched[i];
            for (int index = 0; index < Side; index++)
            {
                Snap(index, 0);
                Snap(index, Segments);
                if (index is > 0 and < Segments) { Snap(0, index); Snap(Segments, index); }
            }
            void Snap(int column, int row)
            {
                double x = patch.Left + patch.Size * column / Segments;
                double y = patch.Top + patch.Size * row / Segments;
                double u = (x - neighbour.Left) / neighbour.Size, v = (y - neighbour.Top) / neighbour.Size;
                if (u < -1e-7 || u > 1.0000001 || v < -1e-7 || v > 1.0000001) return;
                heights[row * Side + column] = SampleMesh(source, u, v);
            }
        }
    }

    private static float SampleMesh(float[] values, double u, double v)
    {
        float x = (float)Math.Clamp(u * Segments, 0, Segments), y = (float)Math.Clamp(v * Segments, 0, Segments);
        int column = Math.Min(Segments - 1, (int)x), row = Math.Min(Segments - 1, (int)y);
        float a = x - column, b = y - row;
        int index = row * Side + column;
        // The same diagonal as the rasterized mesh, preserving the actual planar surface.
        return a >= b ? values[index] * (1 - a) + values[index + 1] * (a - b) + values[index + Side + 1] * b
            : values[index] * (1 - b) + values[index + Side] * (b - a) + values[index + Side + 1] * a;
    }

    private Texture GetTexture(TileId id, Func<TileId, SKBitmap, SKRect[]> bake)
    {
        if (!textures.TryGetValue(id, out var texture))
        {
            var bitmap = new SKBitmap(TextureSize, TextureSize, SKColorType.Bgra8888, SKAlphaType.Opaque);
            try { textures[id] = texture = new(bitmap, bake(id, bitmap)); }
            catch { bitmap.Dispose(); throw; }
        }
        texture.Frame = frame;
        return texture;
    }

    private void Triangle(Vertex a, Vertex b, Vertex c, float far, Span<uint> pixels, ReadOnlySpan<uint> texels,
        SKRect[] earthAreas, Span<float> depth, Span<float> inverseDepth, bool preservePhotograph)
    {
        Span<Vertex> polygon = stackalloc Vertex[12], scratch = stackalloc Vertex[12];
        polygon[0] = a; polygon[1] = b; polygon[2] = c;
        int count = 3;
        for (int plane = 0; plane < 6; plane++)
        {
            int n = 0;
            var previous = polygon[count - 1];
            float previousDistance = TileTransform.Plane(plane, previous.Screen, width, height, far, near);
            bool allInside = true;
            for (int i = 0; i < count; i++)
                allInside &= TileTransform.Plane(plane, polygon[i].Screen, width, height, far, near) >= 0;
            if (allInside) continue;
            for (int i = 0; i < count; i++)
            {
                var next = polygon[i];
                float distance = TileTransform.Plane(plane, next.Screen, width, height, far, near);
                if ((previousDistance < 0) != (distance < 0)) scratch[n++] = Vertex.Lerp(previous, next, previousDistance / (previousDistance - distance));
                if (distance >= 0) scratch[n++] = next;
                previous = next;
                previousDistance = distance;
            }
            if (n < 3) return;
            scratch[..n].CopyTo(polygon);
            count = n;
        }
        for (int i = 2; i < count; i++)
            Rasterize(polygon[0], polygon[i - 1], polygon[i], pixels, texels, earthAreas, depth, inverseDepth, preservePhotograph);
    }

    private void Rasterize(Vertex a, Vertex b, Vertex c, Span<uint> pixels, ReadOnlySpan<uint> texels,
        SKRect[] earthAreas, Span<float> depth, Span<float> inverseDepth, bool preservePhotograph)
    {
        var p = a.Screen.Point; var q = b.Screen.Point; var r = c.Screen.Point;
        float dx1 = q.X - p.X, dy1 = q.Y - p.Y, dx2 = r.X - p.X, dy2 = r.Y - p.Y;
        float determinant = dx1 * dy2 - dy1 * dx2;
        if (Math.Abs(determinant) < .00001f) return;
        float ai = 1 / a.Screen.W, bi = 1 / b.Screen.W, ci = 1 / c.Screen.W;
        var inverse = Plane(ai, bi, ci);
        var altitude = Plane(a.Screen.Z * ai, b.Screen.Z * bi, c.Screen.Z * ci);
        var texU = Plane(a.U * ai, b.U * bi, c.U * ci);
        var texV = Plane(a.V * ai, b.V * bi, c.V * ci);
        var rawHeight = Plane(a.Height * ai, b.Height * bi, c.Height * ci);
        var light = Plane(a.Light * ai, b.Light * bi, c.Light * ci);
        int first = FirstPixel(Math.Min(p.Y, Math.Min(q.Y, r.Y)), height);
        int last = FirstPixel(Math.Max(p.Y, Math.Max(q.Y, r.Y)), height);
        for (int row = first; row < last; row++)
        {
            float y = row + .5f, left = float.PositiveInfinity, right = float.NegativeInfinity;
            Intersect(p, q); Intersect(q, r); Intersect(r, p);
            int x0 = FirstPixel(left, width), x1 = FirstPixel(right, width);
            if (x0 >= x1) continue;
            float ox = x0 + .5f - p.X, oy = y - p.Y;
            float iw = ai + inverse.X * ox + inverse.Y * oy;
            float zw = a.Screen.Z * ai + altitude.X * ox + altitude.Y * oy;
            float u = a.U * ai + texU.X * ox + texU.Y * oy, v = a.V * ai + texV.X * ox + texV.Y * oy;
            float raw = a.Height * ai + rawHeight.X * ox + rawHeight.Y * oy;
            float shade = a.Light * ai + light.X * ox + light.Y * oy;
            for (int x = x0, offset = row * width + x0; x < x1; x++, offset++, iw += inverse.X,
                zw += altitude.X, u += texU.X, v += texV.X, raw += rawHeight.X, shade += light.X)
            {
                if (camera.Perspective && iw < inverseDepth[offset]) continue;
                float reciprocal = 1 / iw, z = zw * reciprocal;
                if (!camera.Perspective && z < depth[offset]) continue;
                int tx = Math.Clamp((int)(u * reciprocal), 0, TextureSize - 1), ty = Math.Clamp((int)(v * reciprocal), 0, TextureSize - 1);
                float actualHeight = raw * reciprocal;
                float sampleU = u * reciprocal, sampleV = v * reciprocal;
                uint source = MapRendering.Smooth(options.RenderStyle)
                    ? SampleLinear(texels, sampleU, sampleV) : texels[ty * TextureSize + tx];
                uint color = Color(source, actualHeight, shade * reciprocal, a.Face, sampleU / TextureSize, sampleV / TextureSize);
                pixels[offset] = color;
                depth[offset] = z;
                if (camera.Perspective) inverseDepth[offset] = iw;
                groundAltitude[offset] = z;
                groundHeight[offset] = actualHeight;
                depthX[offset] = camera.Perspective ? inverse.X : altitude.X;
                depthY[offset] = camera.Perspective ? inverse.Y : altitude.Y;
                if (preservePhotograph)
                {
                    bool earth = false;
                    foreach (var area in earthAreas)
                        if (area.Contains(tx + .5f, ty + .5f)) { earth = true; break; }
                    photographic[offset] = earth;
                    photograph[offset] = color;
                }
            }
            void Intersect(SKPoint from, SKPoint to)
            {
                if ((from.Y > y) == (to.Y > y)) return;
                float x = from.X + (y - from.Y) * (to.X - from.X) / (to.Y - from.Y);
                left = Math.Min(left, x); right = Math.Max(right, x);
            }
        }
        (float X, float Y) Plane(float x, float y, float z) =>
            (((y - x) * dy2 - (z - x) * dy1) / determinant, (dx1 * (z - x) - dx2 * (y - x)) / determinant);
    }

    private uint Color(uint source, float altitude, float illumination, int face, float u, float v)
    {
        if (options.Hypsometric) source = PixelPalette.Mix(source, ElevationColor(altitude), .64f);
        // Six tonal levels keep smooth geographic light legible as deliberate pixel art.
        float shade = Math.Clamp(.72f + illumination * .4f, .52f, 1.16f);
        if (!MapRendering.Smooth(options.RenderStyle)) shade = MathF.Round(shade * 6) / 6;
        if (options.RenderStyle == MapRenderStyle.Minecraft)
        {
            if (face > 0)
            {
                source = PixelPalette.Mix(source, 0xff7e5e41, .68f);
                shade *= ((face - 1) % 4) switch { 0 => .68f, 1 => .82f, 2 => 1, _ => .88f };
                if (Fraction(altitude * .5f) < .065f) shade *= .78f;
            }
            else
            {
                float x = Fraction(u * 8), y = Fraction(v * 8);
                if (Math.Min(Math.Min(x, 1 - x), Math.Min(y, 1 - y)) < .018f) shade *= .88f;
            }
            shade *= .96f + (((int)MathF.Floor(u * 128) * 13 + (int)MathF.Floor(v * 128) * 7) & 3) * .022f;
        }
        if (options.RenderStyle == MapRenderStyle.Roblox)
        {
            float red = (source >> 16) & 255, green = (source >> 8) & 255, blue = source & 255;
            float mean = red * .2126f + green * .7152f + blue * .0722f;
            source = 0xff000000 | (uint)Math.Clamp(mean + (red - mean) * 1.3f + 9, 0, 255) << 16 |
                (uint)Math.Clamp(mean + (green - mean) * 1.3f + 9, 0, 255) << 8 |
                (uint)Math.Clamp(mean + (blue - mean) * 1.3f + 9, 0, 255);
            if (face > 0)
                shade *= (((face - 1) % 4) switch { 0 => .74f, 1 => .88f, 2 => 1.04f, _ => .93f }) * (face <= 4 ? 1.08f : .94f);
            else
            {
                float x = Fraction(u * 32) - .5f, y = Fraction(v * 32) - .5f;
                float radius = MathF.Sqrt(x * x + y * y);
                float rim = Smooth(.13f, .16f, radius) - Smooth(.16f, .19f, radius);
                shade *= 1 + rim * (x + y < 0 ? .11f : -.10f);
            }
        }
        if (Math.Abs(shade - 1) < .001f) return source;
        uint r = (uint)Math.Clamp((int)(((source >> 16) & 255) * shade), 0, 255);
        uint g = (uint)Math.Clamp((int)(((source >> 8) & 255) * shade), 0, 255);
        uint b = (uint)Math.Clamp((int)((source & 255) * shade), 0, 255);
        return 0xff000000 | r << 16 | g << 8 | b;
    }

    private static float Fraction(float value) => value - MathF.Floor(value);

    private static float Smooth(float start, float end, float value)
    {
        float amount = Math.Clamp((value - start) / (end - start), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    private static uint SampleLinear(ReadOnlySpan<uint> texels, float u, float v)
    {
        float x = Math.Clamp(u - .5f, 0, TextureSize - 1), y = Math.Clamp(v - .5f, 0, TextureSize - 1);
        int left = (int)x, top = (int)y, right = Math.Min(TextureSize - 1, left + 1), bottom = Math.Min(TextureSize - 1, top + 1);
        float dx = x - left, dy = y - top;
        uint a = texels[top * TextureSize + left], b = texels[top * TextureSize + right];
        uint c = texels[bottom * TextureSize + left], d = texels[bottom * TextureSize + right];
        uint result = 0xff000000;
        for (int shift = 0; shift < 24; shift += 8)
        {
            float upper = ((a >> shift) & 255) * (1 - dx) + ((b >> shift) & 255) * dx;
            float lower = ((c >> shift) & 255) * (1 - dx) + ((d >> shift) & 255) * dx;
            result |= (uint)MathF.Floor(upper * (1 - dy) + lower * dy + .5f) << shift;
        }
        return result;
    }

    internal static uint ElevationColor(float altitude)
    {
        ReadOnlySpan<float> stops = [-500, 0, 500, 1500, 3000, 4500, 6000];
        ReadOnlySpan<uint> colors = [0xff426a83, 0xff719c78, 0xffa6b47c, 0xffc2ac80, 0xffa79691, 0xffd9d9d0, 0xfff0eee5];
        for (int i = 1; i < stops.Length; i++)
            if (altitude <= stops[i]) return PixelPalette.Mix(colors[i - 1], colors[i], Math.Clamp((altitude - stops[i - 1]) / (stops[i] - stops[i - 1]), 0, 1));
        return colors[^1];
    }

    internal void RestorePhotographs(Span<uint> pixels, float opacity)
    {
        for (int i = 0; i < width * height; i++)
            if (photographic[i]) pixels[i] = PixelPalette.Mix(pixels[i], photograph[i], opacity);
    }

    internal bool TrySample(int x, int y, out float altitude, out float inverse)
    {
        if (gpuScene is { } scene) return scene.Depth.TryTerrain(x, y, out altitude, out inverse, out _);
        altitude = inverse = 0;
        if ((uint)x >= (uint)width || (uint)y >= (uint)height || !float.IsFinite(groundAltitude[y * width + x])) return false;
        altitude = groundAltitude[y * width + x];
        var lens = projection.Get(new(0, 0, 0), 0).Transform;
        inverse = camera.Perspective ? (lens.CosPitch + (y + .5f - lens.CY) * lens.SinPitch * lens.InverseFocal) /
            (lens.CosPitch - (altitude - lens.TargetZ) * lens.InverseFocal) : 1;
        return true;
    }

    // Compare overlays against the same terrain triangle at their subpixel position.
    // A building overwrites altitude along with depth, so its pixels retain their ordinary
    // depth test. This avoids slope-dependent bias that could expose hidden buildings.
    internal bool TryDepthAt(int x, int y, float screenX, float screenY, float storedAltitude,
        float storedDepth, out float continuousDepth)
    {
        continuousDepth = storedDepth;
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return false;
        int offset = y * width + x;
        if (storedAltitude != groundAltitude[offset]) return false;
        continuousDepth += depthX[offset] * (screenX - x - .5f) + depthY[offset] * (screenY - y - .5f);
        return float.IsFinite(continuousDepth);
    }

    internal bool TryHeight(double x, double y, out double altitude)
    {
        if (gpuScene is { } scene) return scene.TryHeight(x, y, out altitude);
        altitude = 0;
        if (!double.IsFinite(x) || !double.IsFinite(y) || y is < 0 or > 1) return false;
        x -= Math.Floor(x);
        for (int level = heightZooms.Count - 1; level >= 0; level--)
        {
            int zoom = heightZooms[level], count = 1 << zoom;
            int column = Math.Min(count - 1, (int)(x * count)), row = Math.Min(count - 1, (int)(y * count));
            var id = new TileId(zoom, column, row);
            if (!heightPatches.TryGetValue(id, out int index)) continue;
            if (MapRendering.Blocks(options.RenderStyle))
                altitude = TerrainBlockMesh.Sample(GetGpuMesh(index).HeightLight!, x * count - column, y * count - row, options.RenderStyle);
            else altitude = SampleMesh(stitched[index], x * count - column, y * count - row);
            return true;
        }
        return false;
    }

    internal bool TryPick(double screenX, double screenY, int logicalWidth, int logicalHeight, int pixelSize, out TerrainHit hit)
    {
        hit = default;
        int x = (int)Math.Floor(screenX / pixelSize), y = (int)Math.Floor(screenY / pixelSize);
        if (!TrySample(x, y, out float altitude, out float inverse)) return false;
        float physicalHeight = gpuScene is { } scene && scene.Depth.TryTerrain(x, y, out _, out _, out float gpuHeight)
            ? gpuHeight : groundHeight[y * width + x];
        double sx = x + .5 - logicalWidth * .5 / pixelSize, sy = y + .5 - logicalHeight * .5 / pixelSize;
        var (sp, cp) = Math.SinCos(camera.PitchRadians);
        double z = altitude - camera.TargetPixels / pixelSize;
        double u = sx / inverse;
        double v = !camera.Perspective || Math.Abs(cp) >= Math.Abs(sp) ? (sy / inverse + z * sp) / cp
            : (MapCamera.Focal(logicalHeight) / pixelSize * (1 - 1 / inverse) - z * cp) / sp;
        var (sin, cos) = Math.SinCos(camera.Bearing * Math.PI / 180);
        double world = camera.WorldSize / pixelSize;
        double wx = camera.CenterX + (u * cos - v * sin) / world, wy = camera.CenterY + (u * sin + v * cos) / world;
        if (wy is < 0 or > 1) return false;
        wx -= Math.Floor(wx);
        hit = new(Math.Atan(Math.Sinh(Math.PI * (1 - 2 * wy))) * 180 / Math.PI,
            wx * 360 - 180, physicalHeight);
        return true;
    }

    private void TrimCaches()
    {
        if (textures.Count > MaximumTextures)
            foreach (var entry in textures.OrderBy(entry => entry.Value.Frame).Take(textures.Count - MaximumTextures).ToArray())
            { entry.Value.Dispose(); textures.Remove(entry.Key); }
        if (meshes.Count > MaximumMeshes)
            foreach (var entry in meshes.OrderBy(entry => entry.Value.Frame).Take(meshes.Count - MaximumMeshes).ToArray()) meshes.Remove(entry.Key);
        if (gpuMeshes.Count > MaximumMeshes)
            foreach (var entry in gpuMeshes.OrderBy(entry => entry.Value.Frame).Take(gpuMeshes.Count - MaximumMeshes).ToArray()) gpuMeshes.Remove(entry.Key);
        if (bounds.Count > MaximumMeshes * 8) bounds.Clear();
    }

    private static int FirstPixel(float edge, int limit) => (int)Math.Clamp(MathF.Ceiling(edge - .5f), 0, limit);

    public void Dispose()
    {
        ClearTextures();
        meshes.Clear(); bounds.Clear(); patches.Clear(); stitched.Clear(); heightPatches.Clear(); heightZooms.Clear();
        gpuMeshes.Clear(); previousGpuSelection = []; gpuScene = null;
        groundAltitude = []; groundHeight = []; depthX = []; depthY = []; photograph = []; photographic = [];
    }
}
