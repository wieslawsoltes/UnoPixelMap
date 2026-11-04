using LibTessDotNet;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>One indexed building batch per vector tile. Roofs use the source's even/odd
/// fill rule, including courtyards, disconnected footprints and intersecting contours.
/// Model heights occupy a small independent texture, keeping terrain updates separate
/// from the retained vertex and index buffers.</summary>
internal sealed class GpuBuildingMesh
{
    internal const int VertexStride = 12;
    internal const int ModelStride = 8;
    internal readonly object ResourceKey;
    internal readonly TileGeometry Source;
    internal readonly float[] Vertices;
    internal readonly uint[] Indices;
    internal readonly float[] RoofEdges;
    internal readonly float[] Models;
    internal readonly GpuBuildingRange[] Ranges;
    internal readonly long Revision;
    internal readonly long FoundationRevision;
    internal long Frame;

    internal GpuBuildingMesh(TileGeometry tile)
    {
        ResourceKey = new object();
        Source = tile;
        Revision = 1;
        var vertices = new List<float>();
        var indices = new List<uint>();
        var edges = new List<float>();
        var ranges = new List<GpuBuildingRange>(tile.Buildings.Count);
        Models = new float[tile.Buildings.Count * ModelStride];
        for (int modelIndex = 0; modelIndex < tile.Buildings.Count; modelIndex++)
        {
            var model = tile.Buildings[modelIndex];
            int firstIndex = indices.Count, firstEdge = edges.Count / VertexStride, firstVertex = vertices.Count / VertexStride;
            int metadata = modelIndex * ModelStride;
            Models[metadata + 1] = model.Seed & 255;
            Models[metadata + 2] = model.Seed % (uint)PixelPalette.Day.Materials.Length;
            Models[metadata + 3] = model.Top;
            Models[metadata + 4] = model.Bounds.Width;
            Models[metadata + 5] = model.Bounds.Height;
            Models[metadata + 6] = model.Bottom;
            AddWalls(model, modelIndex, vertices, indices, edges);
            int wallIndexCount = indices.Count - firstIndex;
            AddCaps(model, modelIndex, vertices, indices);
            ranges.Add(new(model, firstIndex, indices.Count - firstIndex,
                firstEdge, edges.Count / VertexStride - firstEdge, firstVertex, vertices.Count / VertexStride - firstVertex,
                wallIndexCount));
        }
        Vertices = vertices.ToArray();
        Indices = indices.ToArray();
        RoofEdges = edges.ToArray();
        Ranges = ranges.ToArray();
    }

    private GpuBuildingMesh(GpuBuildingMesh source, float[] models, long foundationRevision)
    {
        ResourceKey = source.ResourceKey;
        Source = source.Source;
        Vertices = source.Vertices;
        Indices = source.Indices;
        RoofEdges = source.RoofEdges;
        Models = models;
        Ranges = source.Ranges;
        Revision = source.Revision;
        FoundationRevision = foundationRevision;
        Frame = source.Frame;
    }

    internal GpuBuildingMesh WithFoundations(float[] models) => new(this, models, FoundationRevision + 1);

    private static void AddWalls(Building model, int modelIndex, List<float> vertices,
        List<uint> indices, List<float> edges)
    {
        var prepared = model.Vertices;
        int start = 0;
        foreach (var ring in model.Feature.Paths)
        {
            int end = start + ring.Length;
            if (ring.Length >= 3)
            {
                int previous = end - 1;
                for (int current = start; current < end; current++)
                {
                    var edge = prepared[current];
                    var a = prepared[previous].Point;
                    var b = edge.Point;
                    previous = current;
                    if (edge.TileCut || edge.PerspectiveLength <= .001f) continue;
                    float dx = b.X - a.X, dy = b.Y - a.Y;
                    if (model.Top > model.Bottom)
                    {
                        uint index = (uint)(vertices.Count / VertexStride);
                        AddVertex(vertices, a, model.Top, modelIndex, 0, edge, dx, dy, model.Top - model.Bottom, 0);
                        AddVertex(vertices, b, model.Top, modelIndex, edge.Length, edge, dx, dy, model.Top - model.Bottom, 0);
                        AddVertex(vertices, b, model.Bottom, modelIndex, edge.Length, edge, dx, dy, model.Top - model.Bottom, 0);
                        AddVertex(vertices, a, model.Bottom, modelIndex, 0, edge, dx, dy, model.Top - model.Bottom, 0);
                        indices.Add(index); indices.Add(index + 1); indices.Add(index + 2);
                        indices.Add(index); indices.Add(index + 2); indices.Add(index + 3);
                    }
                    AddVertex(edges, a, model.Top, modelIndex, 0, edge, dx, dy, model.Top - model.Bottom, 3);
                    AddVertex(edges, b, model.Top, modelIndex, edge.Length, edge, dx, dy, model.Top - model.Bottom, 3);
                }
            }
            start = end;
        }
    }

    private static void AddCaps(Building model, int modelIndex, List<float> vertices, List<uint> indices)
    {
        var tessellator = new Tess();
        bool hasContour = false;
        foreach (var ring in model.Feature.Paths)
        {
            if (ring.Length < 3) continue;
            var contour = new ContourVertex[ring.Length];
            for (int i = 0; i < ring.Length; i++)
                contour[i].Position = new Vec3(ring[i].X * model.Unit, ring[i].Y * model.Unit, 0);
            tessellator.AddContour(contour, ContourOrientation.Original);
            hasContour = true;
        }
        if (!hasContour) return;
        tessellator.Tessellate(WindingRule.EvenOdd, ElementType.Polygons, 3, normal: new Vec3(0, 0, 1));
        uint first = (uint)(vertices.Count / VertexStride);
        for (int i = 0; i < tessellator.VertexCount; i++)
        {
            var position = tessellator.Vertices[i].Position;
            var point = new SKPoint(position.X, position.Y);
            AddVertex(vertices, point, model.Top, modelIndex, 0, default, 0, 0, model.Top - model.Bottom, 1);
            AddVertex(vertices, point, model.Bottom, modelIndex, 0, default, 0, 0, model.Top - model.Bottom, 2);
        }
        for (int i = 0; i < tessellator.ElementCount; i++)
        {
            int a = tessellator.Elements[i * 3], b = tessellator.Elements[i * 3 + 1], c = tessellator.Elements[i * 3 + 2];
            if (a < 0 || b < 0 || c < 0) continue;
            indices.Add(first + (uint)a * 2); indices.Add(first + (uint)b * 2); indices.Add(first + (uint)c * 2);
            indices.Add(first + (uint)a * 2 + 1); indices.Add(first + (uint)c * 2 + 1); indices.Add(first + (uint)b * 2 + 1);
        }
    }

    private static void AddVertex(List<float> target, SKPoint point, float height, int model,
        float along, BuildingVertex edge, float dx, float dy, float heightSpan, int face)
    {
        target.Add(point.X); target.Add(point.Y); target.Add(height); target.Add(model);
        target.Add(along); target.Add(edge.Length); target.Add(edge.Light); target.Add(edge.PerspectiveLight);
        target.Add(dx); target.Add(dy); target.Add(heightSpan); target.Add(face);
    }
}

internal readonly record struct GpuBuildingRange(Building Model, int FirstIndex, int IndexCount,
    int FirstEdge, int EdgeCount, int FirstVertex, int VertexCount, int WallIndexCount);
