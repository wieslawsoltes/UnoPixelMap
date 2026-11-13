namespace PixelMap.Rendering;

internal sealed partial class GpuSceneDepth
{
    private void AddChunkedBuilding(GpuBuildingDraw draw, in Group group, GpuBuildingRange range)
    {
        var mesh = draw.Mesh;
        var transform = draw.Transform;
        var source = mesh.Vertices;
        var chunks = mesh.DepthChunks.Get(mesh, group.Model);
        var projected = storage.ProjectedVertices;
        if (projected.Length < range.VertexCount)
            storage.ProjectedVertices = projected = new Vertex[range.VertexCount];
        var stamps = storage.ProjectedVertexStamps;
        if (stamps.Length < range.VertexCount)
            storage.ProjectedVertexStamps = stamps = new int[range.VertexCount];
        if (++storage.ProjectionStamp <= 0)
        {
            Array.Clear(stamps);
            storage.ProjectionStamp = 1;
        }
        int stamp = storage.ProjectionStamp;
        float top = group.Top, bottom = group.Bottom, foundation = group.Foundation;
        float modelTop = range.Model.Top, modelBottom = range.Model.Bottom;
        float rise = top * transform.SinPitch, baseRise = bottom * transform.SinPitch;
        float eye = perspective ? transform.TargetZ + transform.CosPitch / transform.InverseFocal : float.PositiveInfinity;
        foreach (var chunk in chunks.Triangles)
        {
            // Use the same conservative screen bounds as demand preparation. Do
            // not reject on an analytic depth or near-plane corner test: crossing
            // and non-finite boxes retain the original exact primitive clipper.
            if (!Bounds(chunk.Bounds, chunk.HasBottom ? bottom : top, chunk.HasTop ? top : bottom,
                transform, rejectOutside: false, out _, out _)) continue;
            for (int index = chunk.First, end = index + chunk.Count; index < end; index += 3)
            {
                int first = (int)mesh.Indices[index];
                ref readonly var vertex = ref source[first];
                int face = vertex.Face;
                if (face == 1 && top >= eye || face == 2 && (!perspective || bottom <= eye)) continue;
                if (face == 0 && !perspective &&
                    (rise - baseRise < .35f || -(transform.XX * vertex.Dx + transform.XY * vertex.Dy) < .01f)) continue;
                ref var a = ref Project(first);
                ref var b = ref Project((int)mesh.Indices[index + 1]);
                ref var c = ref Project((int)mesh.Indices[index + 2]);
                if (face == 0 && perspective && Orientation(a.Screen, b.Screen, c.Screen) >= -.00001f) continue;
                AddTriangle(ref a, ref b, ref c, transform.Far, false);
            }
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
        foreach (var chunk in chunks.Edges)
        {
            if (!Bounds(chunk.Bounds, top, top, transform, rejectOutside: false, out _, out _)) continue;
            for (int index = chunk.First, end = index + chunk.Count; index < end; index += 2)
            {
                int a = index * GpuBuildingMesh.EdgeStride, b = a + GpuBuildingMesh.EdgeStride;
                AddEdge(transform.Vertex(mesh.RoofEdges[a], mesh.RoofEdges[a + 1], top),
                    transform.Vertex(mesh.RoofEdges[b], mesh.RoofEdges[b + 1], top), transform.Far);
            }
        }

        ref Vertex Project(int index)
        {
            int local = index - range.FirstVertex;
            if (stamps[local] != stamp)
            {
                ref readonly var vertex = ref source[index];
                float altitude = ((vertex.Bottom ? modelBottom : modelTop) + foundation) * pixelsPerMeter;
                projected[local] = new(transform.Vertex(vertex.X, vertex.Y, altitude));
                stamps[local] = stamp;
            }
            return ref projected[local];
        }
    }
}
