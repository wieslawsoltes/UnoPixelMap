using PixelMap.Data;

namespace PixelMap.Rendering;

/// <summary>One bounded topology for each block style. The GPU expands the same
/// indexed cell descriptors from its retained height array. CPU rendering and sparse
/// depth queries use these immutable expanded vertices.</summary>
internal static class TerrainBlockMesh
{
    internal const int Segments = 8;
    internal const int SampleStride = GpuTerrainMesh.Side;
    internal static readonly ushort[] CubeIndices = CreateIndices(20);
    internal static readonly ushort[] BeveledIndices = CreateIndices(36);

    internal static void WriteHeights(TileId id, ReadOnlySpan<float> heights, ReadOnlySpan<float> light,
        float skirtHeight, MapRenderStyle style, Span<float> result)
    {
        result.Clear();
        float step = MapRendering.HeightStep(style);
        for (int y = 0; y < Segments + 2; y++)
        for (int x = 0; x < Segments + 2; x++)
        {
            int target = (y * SampleStride + x) * 2;
            bool interior = x is > 0 and <= Segments && y is > 0 and <= Segments;
            int source = (Math.Clamp(y - 1, 0, Segments - 1) * 2 + 1) * GpuTerrainMesh.Side +
                Math.Clamp(x - 1, 0, Segments - 1) * 2 + 1;
            result[target] = interior ? MathF.Floor(heights[source] / step + .5f) * step : skirtHeight;
            result[target + 1] = light[source];
        }
        double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (id.Y + .5) / (1 << id.Z))));
        double cellMeters = MapCamera.EarthCircumference * Math.Cos(latitude) / ((1 << id.Z) * (double)Segments);
        // The unused last texel carries the stable, physical bevel width for this tile.
        result[^2] = style == MapRenderStyle.Roblox ? (float)Math.Min(.14, cellMeters * .04) : 0;
    }

    internal static float[] Vertices(float[] heights, MapRenderStyle style)
    {
        int perCell = style == MapRenderStyle.Roblox ? 36 : 20;
        var result = new float[Segments * Segments * perCell * GpuTerrainMesh.VertexStride];
        for (int vertex = 0; vertex < result.Length / GpuTerrainMesh.VertexStride; vertex++)
        {
            var point = Vertex(vertex, heights, style);
            int offset = vertex * GpuTerrainMesh.VertexStride;
            result[offset] = point.X;
            result[offset + 1] = point.Y;
            result[offset + 2] = point.Height;
            result[offset + 3] = point.Light;
        }
        return result;
    }

    internal static (float X, float Y, float Height, float Light, int Face) Vertex(int vertex,
        ReadOnlySpan<float> heights, MapRenderStyle style)
    {
        bool beveled = style == MapRenderStyle.Roblox;
        int perCell = beveled ? 36 : 20;
        int cell = vertex / perCell, local = vertex % perCell;
        int x = cell % Segments, y = cell / Segments;
        int sample = ((y + 1) * SampleStride + x + 1) * 2;
        float top = heights[sample], light = heights[sample + 1];
        float bevel = heights[^2];
        int face = local / 4, corner = local % 4;
        float px, py, height;
        if (face == 0)
        {
            (px, py) = Corner(corner);
            if (beveled) { px = .04f + px * .92f; py = .04f + py * .92f; }
            height = top;
        }
        else
        {
            bool bevelFace = beveled && face <= 4;
            int edge = (face - 1) % 4;
            int endpoint = corner is 0 or 3 ? edge : (edge + 1) % 4;
            (px, py) = Corner(endpoint);
            bool upper = corner < 2;
            if (bevelFace)
            {
                if (upper) { px = .04f + px * .92f; py = .04f + py * .92f; }
                height = upper ? top : top - bevel;
            }
            else
            {
                int neighbour = edge switch { 0 => -SampleStride, 1 => 1, 2 => SampleStride, _ => -1 };
                float lower = Math.Min(top, heights[sample + neighbour * 2]);
                height = upper ? top - bevel : lower - bevel;
            }
        }
        const float span = TileGeometry.Extent / Segments;
        return ((x + px) * span, (y + py) * span, height, light, face);
    }

    internal static int Face(int vertex, MapRenderStyle style) =>
        vertex % (style == MapRenderStyle.Roblox ? 36 : 20) / 4;

    internal static float Sample(ReadOnlySpan<float> heights, double u, double v, MapRenderStyle style)
    {
        float x = (float)Math.Clamp(u * Segments, 0, Segments), y = (float)Math.Clamp(v * Segments, 0, Segments);
        int column = Math.Min(Segments - 1, (int)x), row = Math.Min(Segments - 1, (int)y);
        float top = heights[((row + 1) * SampleStride + column + 1) * 2];
        if (style != MapRenderStyle.Roblox) return top;
        float edge = Math.Min(Math.Min(x - column, column + 1 - x), Math.Min(y - row, row + 1 - y));
        return top - heights[^2] * Math.Clamp(1 - edge / .04f, 0, 1);
    }

    private static (float X, float Y) Corner(int corner) => corner switch
    {
        0 => (0, 0), 1 => (1, 0), 2 => (1, 1), _ => (0, 1)
    };

    private static ushort[] CreateIndices(int perCell)
    {
        var indices = new ushort[Segments * Segments * perCell / 4 * 6];
        int offset = 0;
        for (int vertex = 0; vertex < Segments * Segments * perCell; vertex += 4)
        {
            indices[offset++] = (ushort)vertex;
            indices[offset++] = (ushort)(vertex + 1);
            indices[offset++] = (ushort)(vertex + 2);
            indices[offset++] = (ushort)vertex;
            indices[offset++] = (ushort)(vertex + 2);
            indices[offset++] = (ushort)(vertex + 3);
        }
        return indices;
    }
}
