using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>Immutable, order-preserving model streams for a physical camera. Source
/// vertices stay resident, and unchanged visibility reuses the same index/edge uploads.</summary>
internal sealed class GpuBuildingSelection(byte[] models, uint[] indices, float[] roofEdges)
{
    private const byte Walls = 1, Roof = 2, Underside = 4, Rim = 8;
    internal readonly byte[] Models = models;
    internal readonly uint[] Indices = indices;
    internal readonly float[] RoofEdges = roofEdges;

    internal static GpuBuildingSelection Prepare(GpuBuildingMesh mesh, TileTransform transform,
        MapCamera camera, TerrainOptions terrain, float meters, int width, int height,
        GpuBuildingSelection? previous, ref byte[] scratch)
    {
        if (scratch.Length != mesh.Ranges.Length) scratch = new byte[mesh.Ranges.Length];
        var frustum = new Frustum(transform, width, height);
        float eye = transform.TargetZ + transform.CosPitch / transform.InverseFocal;
        float ground = (float)camera.GroundAltitudeMeters;
        float exaggeration = terrain.Relief ? (float)terrain.HeightScale : 0;
        int indexCount = 0, edgeFloats = 0;
        for (int i = 0; i < mesh.Ranges.Length; i++)
        {
            var range = mesh.Ranges[i];
            var model = range.Model;
            // Match the uniforms and model texture arithmetic. The conservative plane
            // margin also admits float contraction differences between GL drivers.
            float foundation = (mesh.Models[i * GpuBuildingMesh.ModelStride] - ground) * exaggeration;
            float top = (model.Top + foundation) * meters, bottom = (model.Bottom + foundation) * meters;
            byte selected = 0;
            if (frustum.Intersects(model.Bounds, bottom, top))
            {
                if (range.WallIndexCount > 0) selected |= Walls;
                float tolerance = (Math.Abs(top) + Math.Abs(bottom) + Math.Abs(eye) + 1) * .00001f;
                if (top < eye + tolerance) selected |= Roof;
                if (bottom > eye - tolerance) selected |= Underside;
                if ((selected & Roof) != 0 && frustum.Intersects(model.Bounds, top, top)) selected |= Rim;
            }
            scratch[i] = selected;
            int capIndices = (range.IndexCount - range.WallIndexCount) / 2;
            if ((selected & Walls) != 0) indexCount += range.WallIndexCount;
            if ((selected & Roof) != 0) indexCount += capIndices;
            if ((selected & Underside) != 0) indexCount += capIndices;
            if ((selected & Rim) != 0) edgeFloats += range.EdgeCount * GpuBuildingMesh.EdgeStride;
        }
        if (previous is not null && scratch.AsSpan().SequenceEqual(previous.Models)) return previous;

        var indices = indexCount == mesh.Indices.Length ? mesh.Indices : new uint[indexCount];
        var edges = edgeFloats == mesh.RoofEdges.Length ? mesh.RoofEdges : new float[edgeFloats];
        int indexOffset = 0, edgeOffset = 0;
        for (int i = 0; i < mesh.Ranges.Length; i++)
        {
            var range = mesh.Ranges[i];
            byte selected = scratch[i];
            if (!ReferenceEquals(indices, mesh.Indices))
            {
                if ((selected & Walls) != 0)
                {
                    mesh.Indices.AsSpan(range.FirstIndex, range.WallIndexCount).CopyTo(indices.AsSpan(indexOffset));
                    indexOffset += range.WallIndexCount;
                }
                int end = range.FirstIndex + range.IndexCount;
                for (int cap = range.FirstIndex + range.WallIndexCount; cap < end; cap += 6)
                {
                    if ((selected & Roof) != 0)
                    {
                        mesh.Indices.AsSpan(cap, 3).CopyTo(indices.AsSpan(indexOffset));
                        indexOffset += 3;
                    }
                    if ((selected & Underside) != 0)
                    {
                        mesh.Indices.AsSpan(cap + 3, 3).CopyTo(indices.AsSpan(indexOffset));
                        indexOffset += 3;
                    }
                }
            }
            if (!ReferenceEquals(edges, mesh.RoofEdges) && (selected & Rim) != 0)
            {
                int length = range.EdgeCount * GpuBuildingMesh.EdgeStride;
                mesh.RoofEdges.AsSpan(range.FirstEdge * GpuBuildingMesh.EdgeStride, length).CopyTo(edges.AsSpan(edgeOffset));
                edgeOffset += length;
            }
        }
        return new((byte[])scratch.Clone(), indices, edges);
    }

    /// <summary>Conservative homogeneous planes built from the actual GL uniforms.
    /// Expanding by two art pixels retains the one-pixel roof raster and clip boundaries.</summary>
    private readonly struct Frustum
    {
        private readonly Plane near, far, left, right, top, bottom;

        internal Frustum(TileTransform t, int width, int height)
        {
            float zWeight = -t.CosPitch * t.InverseFocal;
            var x = new Plane(t.XX, t.XY, t.CX * zWeight, t.X);
            var y = new Plane(t.YX, t.YY, t.CY * zWeight - t.SinPitch, t.Y);
            var w = new Plane(t.PX, t.PY, zWeight, t.PW);
            near = w with { D = w.D - t.Near };
            far = (w * -1) with { D = t.Far - w.D };
            left = x + w * 2;
            right = w * (width + 2) + x * -1;
            top = y + w * 2;
            bottom = w * (height + 2) + y * -1;
        }

        internal bool Intersects(SKRect bounds, float low, float high) =>
            near.Intersects(bounds, low, high) && far.Intersects(bounds, low, high) &&
            left.Intersects(bounds, low, high) && right.Intersects(bounds, low, high) &&
            top.Intersects(bounds, low, high) && bottom.Intersects(bounds, low, high);
    }

    private readonly record struct Plane(double X, double Y, double Z, double D)
    {
        internal bool Intersects(SKRect bounds, float low, float high)
        {
            double x = X * (X < 0 ? bounds.Left : bounds.Right);
            double y = Y * (Y < 0 ? bounds.Top : bounds.Bottom);
            double z = Z * (Z < 0 ? low : high);
            double margin = (Math.Abs(x) + Math.Abs(y) + Math.Abs(z) + Math.Abs(D) + 1) * .00001;
            return !(x + y + z + D < -margin);
        }

        public static Plane operator +(Plane a, Plane b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.D + b.D);
        public static Plane operator *(Plane p, double scale) => new(p.X * scale, p.Y * scale, p.Z * scale, p.D * scale);
    }
}
