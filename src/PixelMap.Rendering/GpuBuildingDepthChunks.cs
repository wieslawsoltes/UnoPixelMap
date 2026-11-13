using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>Small immutable bounds over consecutive source primitives. Shared by
/// foundation-only mesh snapshots; no camera, projected data or source geometry is retained.</summary>
internal sealed class GpuBuildingDepthChunks(int count)
{
    internal const int MinimumVertices = 512;
    private const int TrianglesPerChunk = 64, EdgeSegmentsPerChunk = 64;
    private readonly Model?[] models = new Model?[count];

    internal Model Get(GpuBuildingMesh mesh, int index)
    {
        if (Volatile.Read(ref models[index]) is { } cached) return cached;
        var result = Build(mesh, mesh.Ranges[index]);
        return Interlocked.CompareExchange(ref models[index], result, null) ?? result;
    }

    internal sealed record Model(Chunk[] Triangles, Chunk[] Edges);
    internal readonly record struct Chunk(int First, int Count, SKRect Bounds, bool HasBottom, bool HasTop);

    private static Model Build(GpuBuildingMesh mesh, GpuBuildingRange range)
    {
        var triangles = new Chunk[(range.IndexCount + TrianglesPerChunk * 3 - 1) / (TrianglesPerChunk * 3)];
        for (int chunk = 0; chunk < triangles.Length; chunk++)
        {
            int first = range.FirstIndex + chunk * TrianglesPerChunk * 3;
            int count = Math.Min(TrianglesPerChunk * 3, range.FirstIndex + range.IndexCount - first);
            var bounds = new SKRect(float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity);
            bool bottom = false, top = false;
            for (int index = first; index < first + count; index++)
            {
                ref readonly var vertex = ref mesh.Vertices[mesh.Indices[index]];
                Include(ref bounds, vertex.X, vertex.Y);
                if (vertex.Bottom) bottom = true; else top = true;
            }
            triangles[chunk] = new(first, count, bounds, bottom, top);
        }
        var edges = new Chunk[(range.EdgeCount + EdgeSegmentsPerChunk * 2 - 1) / (EdgeSegmentsPerChunk * 2)];
        for (int chunk = 0; chunk < edges.Length; chunk++)
        {
            int first = range.FirstEdge + chunk * EdgeSegmentsPerChunk * 2;
            int count = Math.Min(EdgeSegmentsPerChunk * 2, range.FirstEdge + range.EdgeCount - first);
            var bounds = new SKRect(float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int index = first; index < first + count; index++)
            {
                int offset = index * GpuBuildingMesh.EdgeStride;
                Include(ref bounds, mesh.RoofEdges[offset], mesh.RoofEdges[offset + 1]);
            }
            edges[chunk] = new(first, count, bounds, false, true);
        }
        return new(triangles, edges);

        static void Include(ref SKRect bounds, float x, float y)
        {
            bounds.Left = Math.Min(bounds.Left, x); bounds.Top = Math.Min(bounds.Top, y);
            bounds.Right = Math.Max(bounds.Right, x); bounds.Bottom = Math.Max(bounds.Bottom, y);
        }
    }
}
