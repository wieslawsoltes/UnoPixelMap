using SkiaSharp;
using PixelMap.Data;

namespace PixelMap.Rendering;

/// <summary>A retained geometry frame prepared for <see cref="GlMapRenderer"/>. The
/// frame owns its label display list. Tile geometry and artwork are shared immutable
/// snapshots, so preparing another camera does not change a pending frame.</summary>
public sealed class GpuMapFrame : IDisposable
{
    internal GpuMapFrame(int width, int height, int pixelSize, MapCamera camera,
        TerrainOptions terrainOptions, PixelPalette palette, float pixelsPerMeter,
        TerrainGpuDraw[] terrain, GpuBuildingDraw[] buildings)
    {
        Width = width;
        Height = height;
        PixelSize = pixelSize;
        Camera = camera;
        TerrainOptions = terrainOptions;
        Palette = palette;
        PixelsPerMeter = pixelsPerMeter;
        Terrain = terrain;
        Buildings = buildings;
        ArtWidth = (width + pixelSize - 1) / pixelSize;
        ArtHeight = (height + pixelSize - 1) / pixelSize;
        var zooms = new SortedSet<int>();
        foreach (var draw in terrain)
        {
            var mesh = draw.Mesh;
            zooms.Add(mesh.Id.Z);
            double center = (mesh.Id.X + .5) / (1 << mesh.Id.Z) + mesh.Copy;
            if (!heightMeshes.TryGetValue(mesh.Id, out var previous) ||
                Math.Abs(center - camera.CenterX) < Math.Abs((previous.Id.X + .5) / (1 << previous.Id.Z) + previous.Copy - camera.CenterX))
                heightMeshes[mesh.Id] = mesh;
        }
        heightZooms = zooms.Reverse().ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public int PixelSize { get; }
    public MapRenderStyle RenderStyle => TerrainOptions.RenderStyle;
    public MapCamera Camera { get; }
    public int TerrainPatchCount => Terrain.Length;
    public int TerrainTriangleCount => Terrain.Sum(draw => draw.Mesh.TriangleIndices.Length / 3);
    public int BuildingTriangleCount => Buildings.Sum(draw => draw.Mesh.Indices.Length / 3);
    internal int ArtWidth { get; }
    internal int ArtHeight { get; }
    internal TerrainGpuDraw[] Terrain { get; }
    internal GpuBuildingDraw[] Buildings { get; }
    internal PixelPalette Palette { get; }
    internal TerrainOptions TerrainOptions { get; }
    internal TerrainOptions? NavigationTerrain { get; init; }
    internal float PixelsPerMeter { get; }
    internal float MinimumAltitude { get; set; }
    internal float MaximumAltitude { get; set; }
    internal bool PreservePhotograph { get; set; }
    internal SKPicture? Overlay { get; set; }
    private GpuSceneDepth? depth;
    internal GpuSceneDepth Depth
    {
        get => depth!;
        set
        {
            if (ReferenceEquals(depth, value)) return;
            value.Retain();
            depth?.Release();
            depth = value;
        }
    }
    internal bool IsDisposed { get; private set; }
    internal MapTile[] Tiles { get; set; } = [];
    internal ImageryTile[] Imagery { get; set; } = [];
    private readonly Dictionary<TileId, GpuTerrainMesh> heightMeshes = [];
    private readonly int[] heightZooms;

    internal bool TryHeight(double x, double y, out double altitude)
    {
        altitude = 0;
        if (!double.IsFinite(x) || !double.IsFinite(y) || y is < 0 or > 1) return false;
        x -= Math.Floor(x);
        foreach (int zoom in heightZooms)
        {
            int count = 1 << zoom;
            int column = Math.Min(count - 1, (int)(x * count)), row = Math.Min(count - 1, (int)(y * count));
            if (!heightMeshes.TryGetValue(new(zoom, column, row), out var mesh)) continue;
            if (mesh.HeightLight is { } blocks)
            {
                altitude = TerrainBlockMesh.Sample(blocks, x * count - column, y * count - row, mesh.RenderStyle);
                return true;
            }
            float localX = (float)Math.Clamp((x * count - column) * GpuTerrainMesh.Segments, 0, GpuTerrainMesh.Segments);
            float localY = (float)Math.Clamp((y * count - row) * GpuTerrainMesh.Segments, 0, GpuTerrainMesh.Segments);
            int cx = Math.Min(GpuTerrainMesh.Segments - 1, (int)localX), cy = Math.Min(GpuTerrainMesh.Segments - 1, (int)localY);
            float a = localX - cx, b = localY - cy;
            int i = (cy * GpuTerrainMesh.Side + cx) * GpuTerrainMesh.VertexStride + 2;
            var values = mesh.Vertices;
            int right = GpuTerrainMesh.VertexStride, down = GpuTerrainMesh.Side * GpuTerrainMesh.VertexStride;
            altitude = a >= b
                ? values[i] * (1 - a) + values[i + right] * (a - b) + values[i + down + right] * b
                : values[i] * (1 - b) + values[i + down] * (b - a) + values[i + down + right] * a;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Overlay?.Dispose();
        Overlay = null;
        depth?.Release();
    }
}

internal readonly record struct TerrainGpuDraw(GpuTerrainMesh Mesh, GpuTerrainTexture Texture,
    TileTransform Transform);

internal sealed class GpuTerrainMesh(object resourceKey, PixelMap.Data.TileId id, int copy,
    float[] vertices, long revision, MapRenderStyle renderStyle = MapRenderStyle.Pixel, float[]? heightLight = null)
{
    internal const int VertexStride = 4;
    internal const int Segments = 16;
    internal const int Side = Segments + 1;
    internal readonly object ResourceKey = resourceKey;
    internal readonly PixelMap.Data.TileId Id = id;
    internal readonly int Copy = copy;
    internal readonly float[] Vertices = vertices;
    internal readonly long Revision = revision;
    internal readonly MapRenderStyle RenderStyle = renderStyle;
    internal readonly float[]? HeightLight = heightLight;
    internal ushort[] TriangleIndices => RenderStyle switch
    {
        MapRenderStyle.Minecraft => TerrainBlockMesh.CubeIndices,
        MapRenderStyle.Roblox => TerrainBlockMesh.BeveledIndices,
        _ => Indices
    };
    internal long Frame;
    internal static readonly ushort[] Indices = CreateIndices();

    private static ushort[] CreateIndices()
    {
        var indices = new ushort[Segments * Segments * 6];
        int index = 0;
        for (int row = 0; row < Segments; row++)
        for (int column = 0; column < Segments; column++)
        {
            int a = row * Side + column, b = a + 1, c = a + Side, d = c + 1;
            indices[index++] = (ushort)a;
            indices[index++] = (ushort)b;
            indices[index++] = (ushort)d;
            indices[index++] = (ushort)a;
            indices[index++] = (ushort)d;
            indices[index++] = (ushort)c;
        }
        return indices;
    }
}

internal sealed class GpuTerrainTexture(uint[] texels)
{
    internal const int Size = TerrainSurface.TextureSize;
    internal readonly uint[] Texels = texels;
}

internal readonly record struct GpuBuildingDraw(GpuBuildingMesh Mesh, TileTransform Transform,
    float TileUnitsToMeters, GpuBuildingSelection? Selection = null);
