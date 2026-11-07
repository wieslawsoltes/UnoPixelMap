using LibTessDotNet;
using SkiaSharp;
using System.Runtime.InteropServices;

namespace PixelMap.Rendering;

/// <summary>Lossless building attributes. Heights and along-edge coordinates select
/// existing model/edge values; only integer identity and endpoint flags are packed.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct GpuBuildingVertex(float x, float y, float length, float light,
    float perspectiveLight, float dx, float dy, uint packed)
{
    internal const int Size = 32;
    internal readonly float X = x, Y = y, Length = length, Light = light,
        PerspectiveLight = perspectiveLight, Dx = dx, Dy = dy;
    internal readonly uint Packed = packed;
    internal int Model => (int)(Packed >> 4);
    internal int Face => (int)(Packed & 3);
    internal bool Bottom => (Packed & 4) != 0;
    internal float Along => (Packed & 8) != 0 ? Length : 0;
}

/// <summary>One indexed building batch per vector tile. Roofs retain the source's
/// even/odd fill, including holes, disconnected footprints and intersections.
/// Compact cap staging permits exact final allocations without retaining growing
/// float lists, their copies, or the CPU rasterizer's prepared edge tables.</summary>
internal sealed class GpuBuildingMesh
{
    internal const int EdgeStride = 4;
    internal const int ModelStride = 8;
    internal readonly object ResourceKey;
    internal readonly TileGeometry Source;
    internal readonly GpuBuildingVertex[] Vertices;
    internal readonly uint[] Indices;
    internal readonly float[] RoofEdges;
    internal readonly float[] Models;
    internal readonly GpuBuildingRange[] Ranges;
    internal readonly long Revision;
    internal readonly long FoundationRevision;
    internal long Frame;

    private readonly record struct Cap(SKPoint[] Positions, int[] Elements, int ElementCount, int IndexCount);

    internal GpuBuildingMesh(TileGeometry tile)
    {
        ResourceKey = new object();
        Source = tile;
        Revision = 1;
        int count = tile.Buildings.Count;
        // Four flag bits leave 28 exact bits for a model ID. The model table's
        // checked array length also bounds every packed index below that limit.
        Models = new float[checked(count * ModelStride)];
        Ranges = new GpuBuildingRange[count];
        var caps = new Cap[count];
        int vertices = 0, indices = 0, edges = 0;
        for (int modelIndex = 0; modelIndex < count; modelIndex++)
        {
            var model = tile.Buildings[modelIndex];
            int firstIndex = indices, firstEdge = edges, firstVertex = vertices;
            int metadata = modelIndex * ModelStride;
            Models[metadata + 1] = model.Seed & 255;
            Models[metadata + 2] = model.Seed % (uint)PixelPalette.Day.Materials.Length;
            Models[metadata + 3] = model.Top;
            Models[metadata + 4] = model.Bounds.Width;
            Models[metadata + 5] = model.Bounds.Height;
            Models[metadata + 6] = model.Bottom;
            int wallEdges = CountEdges(model);
            int wallIndexCount = model.Top > model.Bottom ? checked(wallEdges * 6) : 0;
            var cap = caps[modelIndex] = PrepareCap(model);
            checked
            {
                vertices += (model.Top > model.Bottom ? wallEdges * 4 : 0) + cap.Positions.Length * 2;
                indices += wallIndexCount + cap.IndexCount;
                edges += wallEdges * 2;
            }
            Ranges[modelIndex] = new(model, firstIndex, indices - firstIndex,
                firstEdge, edges - firstEdge, firstVertex, vertices - firstVertex, wallIndexCount);
        }
        Vertices = new GpuBuildingVertex[vertices];
        Indices = new uint[indices];
        RoofEdges = new float[checked(edges * EdgeStride)];
        for (int modelIndex = 0; modelIndex < count; modelIndex++)
        {
            var range = Ranges[modelIndex];
            int vertex = range.FirstVertex, index = range.FirstIndex, edge = range.FirstEdge * EdgeStride;
            AddWalls(range.Model, modelIndex, ref vertex, ref index, ref edge);
            AddCaps(modelIndex, caps[modelIndex], ref vertex, ref index);
            // Release each compact tessellation after its final output is emitted.
            caps[modelIndex] = default;
            System.Diagnostics.Debug.Assert(vertex == range.FirstVertex + range.VertexCount &&
                index == range.FirstIndex + range.IndexCount && edge == (range.FirstEdge + range.EdgeCount) * EdgeStride);
        }
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

    private static int CountEdges(Building model)
    {
        int count = 0;
        foreach (var ring in model.Feature.Paths)
        {
            if (ring.Length < 3) continue;
            var previous = ring[^1];
            foreach (var point in ring)
            {
                var edge = Building.PrepareVertex(previous, point, model.Unit, model.Feature.Extent);
                previous = point;
                if (edge.TileCut || edge.PerspectiveLength <= .001f) continue;
                count = checked(count + 1);
            }
        }
        return count;
    }

    private void AddWalls(Building model, int modelIndex, ref int vertex, ref int index, ref int rim)
    {
        uint packed = (uint)modelIndex << 4;
        foreach (var ring in model.Feature.Paths)
        {
            if (ring.Length < 3) continue;
            var previous = ring[^1];
            foreach (var point in ring)
            {
                var edge = Building.PrepareVertex(previous, point, model.Unit, model.Feature.Extent);
                var a = new SKPoint(previous.X * model.Unit, previous.Y * model.Unit);
                var b = edge.Point;
                previous = point;
                if (edge.TileCut || edge.PerspectiveLength <= .001f) continue;
                float dx = b.X - a.X, dy = b.Y - a.Y;
                if (model.Top > model.Bottom)
                {
                    uint first = (uint)vertex;
                    Vertices[vertex++] = Wall(a, edge, dx, dy, packed);
                    Vertices[vertex++] = Wall(b, edge, dx, dy, packed | 8);
                    Vertices[vertex++] = Wall(b, edge, dx, dy, packed | 12);
                    Vertices[vertex++] = Wall(a, edge, dx, dy, packed | 4);
                    Indices[index++] = first; Indices[index++] = first + 1; Indices[index++] = first + 2;
                    Indices[index++] = first; Indices[index++] = first + 2; Indices[index++] = first + 3;
                }
                AddPosition(a, model.Top, modelIndex, ref rim);
                AddPosition(b, model.Top, modelIndex, ref rim);
            }
        }
    }

    private static GpuBuildingVertex Wall(SKPoint point, BuildingVertex edge, float dx, float dy, uint packed) =>
        new(point.X, point.Y, edge.Length, edge.Light, edge.PerspectiveLight, dx, dy, packed);

    private static Cap PrepareCap(Building model)
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
        if (!hasContour) return new([], [], 0, 0);
        tessellator.Tessellate(WindingRule.EvenOdd, ElementType.Polygons, 3, normal: new Vec3(0, 0, 1));
        var points = new SKPoint[tessellator.VertexCount];
        for (int i = 0; i < points.Length; i++)
        {
            var position = tessellator.Vertices[i].Position;
            points[i] = new(position.X, position.Y);
        }
        int count = 0;
        var elements = tessellator.Elements;
        for (int i = 0; i < tessellator.ElementCount; i++)
            if (elements[i * 3] >= 0 && elements[i * 3 + 1] >= 0 && elements[i * 3 + 2] >= 0)
                count = checked(count + 6);
        // Elements has three entries per polygon; retaining it and XY alone does
        // not retain Tess's topology graph or its ContourVertex.Data references.
        return new(points, elements, tessellator.ElementCount, count);
    }

    private void AddCaps(int modelIndex, Cap cap, ref int vertex, ref int index)
    {
        uint first = (uint)vertex, packed = (uint)modelIndex << 4;
        foreach (var point in cap.Positions)
        {
            Vertices[vertex++] = new(point.X, point.Y, 0, 0, 0, 0, 0, packed | 1);
            Vertices[vertex++] = new(point.X, point.Y, 0, 0, 0, 0, 0, packed | 6);
        }
        for (int i = 0; i < cap.ElementCount * 3; i += 3)
        {
            int a = cap.Elements[i], b = cap.Elements[i + 1], c = cap.Elements[i + 2];
            if (a < 0 || b < 0 || c < 0) continue;
            // Preserve the original interleaved roof/underside triangle order and
            // outward winding after the tile-Y to GL clip-Y inversion.
            Indices[index++] = first + (uint)a * 2; Indices[index++] = first + (uint)c * 2; Indices[index++] = first + (uint)b * 2;
            Indices[index++] = first + (uint)a * 2 + 1; Indices[index++] = first + (uint)b * 2 + 1; Indices[index++] = first + (uint)c * 2 + 1;
        }
    }

    private void AddPosition(SKPoint point, float height, int model, ref int offset)
    {
        RoofEdges[offset++] = point.X; RoofEdges[offset++] = point.Y;
        RoofEdges[offset++] = height; RoofEdges[offset++] = model;
    }
}

internal readonly record struct GpuBuildingRange(Building Model, int FirstIndex, int IndexCount,
    int FirstEdge, int EdgeCount, int FirstVertex, int VertexCount, int WallIndexCount);
