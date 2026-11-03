using SkiaSharp;

namespace PixelMap.Rendering;

// H holds homogeneous screen coordinates. Z and U remain physical attributes until rasterization.
internal readonly record struct H(float X, float Y, float W, float Z = 0, float U = 0)
{
    public SKPoint Point => new(X / W, Y / W);
    public static H Lerp(H a, H b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
        a.W + (b.W - a.W) * t, a.Z + (b.Z - a.Z) * t, a.U + (b.U - a.U) * t);
}

internal readonly record struct TileTransform(float X, float Y, float XX, float XY, float YX, float YY,
    float PX = 0, float PY = 0, float PW = 1, float CX = 0, float CY = 0, float InverseFocal = 0,
    float SinPitch = 0, float CosPitch = 1, float TargetZ = 0, float Far = 6)
{
    public bool Perspective => InverseFocal != 0;
    public float Weight(float x, float y) => PW + PX * x + PY * y;
    public H Vertex(float x, float y, float z = 0, float u = 0)
    {
        float dz = -z * CosPitch * InverseFocal;
        return new(X + XX*x + XY*y + CX*dz, Y + YX*x + YY*y + CY*dz - z*SinPitch, Weight(x,y) + dz, z, u);
    }
    public SKPoint At(float x, float y) => Perspective ? Vertex(x,y).Point : new(X + XX*x + XY*y, Y + YX*x + YY*y);
    public SKMatrix Matrix => new(XX, XY, X, YX, YY, Y, PX, PY, PW);
    public TileTransform Translate(float dx, float dy) => this with {
        X = X + dx*PW, XX = XX + dx*PX, XY = XY + dx*PY, Y = Y + dy*PW, YX = YX + dy*PX, YY = YY + dy*PY,
        CX = CX + dx, CY = CY + dy };
    public bool Invert(float x, float y, out SKPoint p, out float determinant)
    {
        float a = XX - x*PX, b = XY - x*PY, d = YX - y*PX, e = YY - y*PY;
        float cx = x*PW - X, cy = y*PW - Y;
        determinant = a*e - b*d;
        p = determinant == 0 ? default : new((cx*e - cy*b)/determinant, (cy*a - cx*d)/determinant);
        float w = Weight(p.X,p.Y);
        return determinant != 0 && w >= MapCamera.Near && w <= Far;
    }

    internal float MaximumScale(ReadOnlySpan<SKPoint> polygon)
    {
        float maximum = 0;
        foreach (var point in polygon)
        {
            var h = Vertex(point.X, point.Y);
            float squaredWeight = h.W * h.W;
            float a = (XX * h.W - h.X * PX) / squaredWeight;
            float b = (XY * h.W - h.X * PY) / squaredWeight;
            float c = (YX * h.W - h.Y * PX) / squaredWeight;
            float d = (YY * h.W - h.Y * PY) / squaredWeight;
            float sum = a * a + b * b + c * c + d * d, determinant = a * d - b * c;
            float singular = MathF.Sqrt(.5f * (sum + MathF.Sqrt(Math.Max(0, sum * sum - 4 * determinant * determinant))));
            maximum = Math.Max(maximum, singular);
        }
        return maximum;
    }

    // Clip in tile coordinates against homogeneous plane inequalities before division.
    // This also bounds the number of terrain blocks when the horizon intersects the display.
    public int VisiblePolygon(float width, float height, Span<SKPoint> result, SKRect? bounds = null)
    {
        var area = bounds ?? new SKRect(0, 0, TileGeometry.Extent, TileGeometry.Extent);
        result[0] = new(area.Left, area.Top);
        result[1] = new(area.Right, area.Top);
        result[2] = new(area.Right, area.Bottom);
        result[3] = new(area.Left, area.Bottom);
        if (!Perspective) return 4;
        int count = 4;
        Span<SKPoint> tmp = stackalloc SKPoint[16];
        Span<H> projected = stackalloc H[16];
        Span<H> nextProjected = stackalloc H[16];
        Span<float> distances = stackalloc float[16];
        for (int i = 0; i < count; i++) projected[i] = Vertex(result[i].X, result[i].Y);
        for (int plane = 0; plane < 6 && count != 0; plane++)
        {
            int inside = 0, outside = 0;
            for (int i = 0; i < count; i++)
            {
                float distance = Plane(plane, projected[i], width, height, Far);
                distances[i] = distance;
                if (distance >= 0) inside++;
                if (distance < 0) outside++;
            }
            if (inside == count) continue;
            if (outside == count) return 0;

            int n = 0;
            var a = result[count - 1];
            float da = distances[count - 1];
            for (int i = 0; i < count; i++)
            {
                var b = result[i];
                float db = distances[i];
                if ((da < 0) != (db < 0))
                {
                    float t = da/(da-db);
                    var intersection = new SKPoint(a.X+(b.X-a.X)*t, a.Y+(b.Y-a.Y)*t);
                    tmp[n] = intersection;
                    // Re-evaluate each new tile-space vertex with the original arithmetic.
                    // Interpolating homogeneous coordinates would round differently.
                    nextProjected[n++] = Vertex(intersection.X, intersection.Y);
                }
                if (db >= 0)
                {
                    tmp[n] = b;
                    nextProjected[n++] = projected[i];
                }
                a = b; da = db;
            }
            tmp[..n].CopyTo(result);
            nextProjected[..n].CopyTo(projected);
            count = n;
        }
        return count;
    }

    internal static float Plane(int plane, H v, float w, float h, float far) => plane switch
    {
        0 => v.W - (float)MapCamera.Near, 1 => far - v.W,
        2 => v.X, 3 => w*v.W-v.X, 4 => v.Y, _ => h*v.W-v.Y
    };
}
