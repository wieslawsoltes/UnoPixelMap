using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>Depth-tested orthographic extrusion at the pixel-art resolution.</summary>
internal sealed partial class BuildingRasterizer
{
    // Apartment/office lights repeat in two-bay groups with a different phase per model.
    private static readonly byte[] Rooms = CreateRooms();
    private static byte[] CreateRooms()
    {
        var rooms = new byte[256];
        var random = new Random(859);
        for (int i = 0; i < rooms.Length; i += 2)
        {
            rooms[i] = (byte)random.Next(4);
            rooms[i + 1] = random.Next(3) == 0 ? (byte)random.Next(4) : rooms[i];
        }
        return rooms;
    }

    private float[] depth = [];
    private SKPoint[] vertices = [];
    private float[] intersections = [];
    private float[] ringMinimumY = [], ringMaximumY = [];
    private int width, height;
    private float vertical, pixelsPerMeter;

    public void Begin(int w, int h, float sinPitch, float scale, bool perspective = false)
    {
        width = w; height = h; vertical = sinPitch; pixelsPerMeter = scale;
        usingPerspective = perspective;
        if (perspective)
        {
            if (inverseDepth.Length < w*h) inverseDepth = new float[w*h];
            else Array.Clear(inverseDepth, 0, w*h);
            BeginOcclusion(w, h);
        }
        if (depth.Length < w * h) depth = new float[w * h];
        else Array.Clear(depth, 0, w * h);
    }

    public bool Covers(float sx, float sy) => CoversAt(sx, sy, 4);

    public bool CoversAt(float sx, float sy, float altitudeMeters)
    {
        int x = (int)MathF.Floor(sx), y = (int)MathF.Floor(sy);
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return false;
        int offset = y*width+x;
        if (!usingPerspective) return depth[offset] > pixelsPerMeter*altitudeMeters;
        if (inverseDepth[offset] <= 0) return false;
        float plane = depthLens.CosPitch - (altitudeMeters*pixelsPerMeter-depthLens.TargetZ)*depthLens.InverseFocal;
        if (Math.Abs(plane) < 1e-7f) return false;
        float inv = (depthLens.CosPitch + (sy-depthLens.CY)*depthLens.SinPitch*depthLens.InverseFocal)/plane;
        return inverseDepth[offset] > inv+1e-5f;
    }

    public void Draw(Span<uint> pixels, in Building building, in TileTransform transform, Material material)
    {
        if (transform.Perspective) { DrawPerspective(pixels, building, transform, material); return; }
        float top = building.Top * pixelsPerMeter;
        float bottom = building.Bottom * pixelsPerMeter;
        float rise = top * vertical, baseRise = bottom * vertical;
        var bounds = building.Bounds;
        var middle = transform.At(bounds.MidX, bounds.MidY);
        float rx = (Math.Abs(transform.XX) * bounds.Width + Math.Abs(transform.XY) * bounds.Height) * .5f;
        float ry = (Math.Abs(transform.YX) * bounds.Width + Math.Abs(transform.YY) * bounds.Height) * .5f;
        if (middle.X + rx < 0 || middle.X - rx >= width || middle.Y + ry - baseRise < 0 || middle.Y - ry - rise >= height) return;
        if (rx < .28f && ry + rise < .5f) return;
        var rings = building.Feature.Paths;
        int count = 0;
        foreach (var ring in rings) count += ring.Length;
        var prepared = rise - baseRise >= .35f ? building.Vertices : null;
        if (vertices.Length < count)
        {
            vertices = new SKPoint[Math.Max(count, vertices.Length * 2)];
            intersections = new float[vertices.Length];
        }

        if (visibleRings.Length < rings.Length) visibleRings = new bool[rings.Length];
        if (ringMinimumY.Length < rings.Length)
        {
            ringMinimumY = new float[rings.Length];
            ringMaximumY = new float[rings.Length];
        }
        int end = 0, ringIndex = 0;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        float tileWorldScale = prepared is not null
            ? MathF.Sqrt(transform.XX * transform.XX + transform.XY * transform.XY) : 0;
        foreach (var ring in rings)
        {
            int start = end;
            float left = float.PositiveInfinity, right = float.NegativeInfinity;
            float ringTop = float.PositiveInfinity, ringBottom = float.NegativeInfinity;
            foreach (var point in ring)
            {
                var projected = transform.At(point.X * building.Unit, point.Y * building.Unit);
                vertices[end++] = projected;
                left = Math.Min(left, projected.X); right = Math.Max(right, projected.X);
                ringTop = Math.Min(ringTop, projected.Y); ringBottom = Math.Max(ringBottom, projected.Y);
            }
            bool visible = ring.Length >= 3 && right >= 0 && left < width &&
                ringBottom - baseRise >= 0 && ringTop - rise < height;
            visibleRings[ringIndex] = visible;
            ringMinimumY[ringIndex] = ringTop;
            ringMaximumY[ringIndex++] = ringBottom;
            if (!visible) continue;
            minY = Math.Min(minY, ringTop); maxY = Math.Max(maxY, ringBottom);
            if (prepared is null) continue;
            int previous = ring.Length - 1;
            for (int i = 0; i < ring.Length; i++)
            {
                var a = vertices[start + previous]; var b = vertices[start + i];
                var edge = prepared[start + i];
                previous = i;
                // MVT windings face the solid to the right for outer rings and courtyard rings alike.
                if (a.X - b.X < .01f) continue;
                if (edge.TileCut) continue;
                uint wall = PixelPalette.Mix(material.DarkWall, material.LightWall, edge.Light);
                float along = edge.Length * tileWorldScale;
                Wall(pixels, a, b, rise, baseRise, wall, material, along, building.Seed);
            }
        }

        // A ray through a fixed pixel has a constant depth ordering by elevation. Storing
        // elevation therefore resolves intersecting walls, courtyards and overhanging parts.
        int first = FirstPixel(minY - rise, height), last = FirstPixel(maxY - rise, height);
        for (int y = first; y < last; y++)
        {
            float row = y + .5f + rise;
            int hits = 0, index = 0;
            ringIndex = 0;
            foreach (var ring in rings)
            {
                bool crosses = visibleRings[ringIndex] && row >= ringMinimumY[ringIndex] && row < ringMaximumY[ringIndex];
                ringIndex++;
                if (!crosses) { index += ring.Length; continue; }
                var a = vertices[index + ring.Length - 1];
                for (int i = 0; i < ring.Length; i++)
                {
                    var b = vertices[index++];
                    if ((a.Y > row) != (b.Y > row))
                        intersections[hits++] = a.X + (row - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    a = b;
                }
            }
            if (hits == 2 && intersections[0] > intersections[1])
                (intersections[0], intersections[1]) = (intersections[1], intersections[0]);
            else if (hits > 2)
                intersections.AsSpan(0, hits).Sort();
            for (int i = 1; i < hits; i += 2)
            {
                int begin = FirstPixel(intersections[i - 1], width);
                int limit = FirstPixel(intersections[i], width);
                int pos = y * width + begin;
                for (int x = begin; x < limit; x++, pos++)
                {
                    if (top < depth[pos]) continue;
                    depth[pos] = top;
                    pixels[pos] = material.Roof;
                }
            }
        }

        if (rx < 3 || ry < 1.5f) return;
        int offset = 0;
        ringIndex = 0;
        foreach (var ring in rings)
        {
            if (!visibleRings[ringIndex++]) { offset += ring.Length; continue; }
            int prev = ring.Length - 1;
            for (int i = 0; i < ring.Length; i++)
            {
                var a = vertices[offset + prev]; var b = vertices[offset + i];
                var pa = ring[prev]; var pb = ring[i];
                prev = i;
                if (IsTileCut(pa.X, pa.Y, pb.X, pb.Y, building.Feature.Extent)) continue;
                a.Y -= rise; b.Y -= rise;
                uint color = b.X >= a.X ? material.Rim : material.Trim;
                Edge(pixels, a, b, top, color);
            }
            offset += ring.Length;
        }
    }

    private static bool IsTileCut(int ax, int ay, int bx, int by, int extent) =>
        ax == bx && (ax <= 0 || ax >= extent) || ay == by && (ay <= 0 || ay >= extent);

    private void Wall(Span<uint> pixels, SKPoint a, SKPoint b, float rise, float baseRise,
        uint color, Material material, float along, uint seed)
    {
        float span = a.X - b.X;
        float dy = (a.Y - b.Y) / span;
        float floor = Math.Max(.01f, pixelsPerMeter * vertical * (3.2f + (seed & 3) * .22f));
        float bay = pixelsPerMeter * (3.2f + ((seed >> 2) & 3) * .6f) * span / Math.Max(.01f, along);
        bool windows = floor >= 1.65f && bay >= 1.45f && rise - baseRise > floor * 1.6f;
        bool ribbon = (seed & 8) != 0 && rise > 9 * floor;
        bool pilasters = (seed & 32) != 0 && rise > 7 * floor;
        uint rib = PixelPalette.Mix(color, material.Rim, .32f);
        float invFloor = 1 / floor, invVertical = 1 / vertical;
        int first = FirstPixel(b.X, width), last = FirstPixel(a.X, width);
        for (int x = first; x < last; x++)
        {
            float alongEdge = x + .5f - b.X;
            float ground = b.Y + alongEdge * dy;
            int y0 = FirstPixel(ground - rise, height), y1 = FirstPixel(ground - baseRise, height);
            int column = (int)(alongEdge / Math.Max(.01f, bay));
            float windowX = alongEdge - column * bay;
            bool glass = windows && windowX > bay * (ribbon ? .05f : .19f) && windowX < bay * (ribbon ? .95f : .68f);
            uint pier = pilasters && bay > 1.9f && (column & 3) == 0 && windowX < .9f ? rib : color;
            int phase = (int)(seed & 255) + column;
            int pos = y0 * width + x;
            for (int y = y0; y < y1; y++, pos += width)
            {
                float h = ground - (y + .5f);
                float altitude = h * invVertical;
                if (altitude < depth[pos]) continue;
                depth[pos] = altitude;
                uint texel = pier;
                if (glass)
                {
                    int story = (int)(h * invFloor);
                    float within = h - story * floor;
                    if (within >= floor * .22f && within < floor * .75f)
                        texel = Rooms[(phase + story * 16) & 255] == 0 ? material.Lamp : material.Glass;
                }
                pixels[pos] = texel;
            }
        }
    }

    private void Edge(Span<uint> pixels, SKPoint a, SKPoint b, float elevation, uint color)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, lo = 0, hi = 1;
        if (!Clip(-dx, a.X, ref lo, ref hi) || !Clip(dx, width - a.X, ref lo, ref hi) ||
            !Clip(-dy, a.Y, ref lo, ref hi) || !Clip(dy, height - a.Y, ref lo, ref hi)) return;
        int steps = Math.Max(1, (int)MathF.Ceiling((hi - lo) * Math.Max(Math.Abs(dx), Math.Abs(dy))));
        for (int i = 0; i <= steps; i++)
        {
            float t = lo + (hi - lo) * i / steps;
            int x = (int)MathF.Floor(a.X + dx * t), y = (int)MathF.Floor(a.Y + dy * t);
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
            int p = y * width + x;
            if (elevation < depth[p]) continue;
            depth[p] = elevation;
            pixels[p] = color;
        }
    }

    private static bool Clip(float p, float q, ref float lo, ref float hi)
    {
        if (p == 0) return q >= 0;
        float t = q / p;
        if (p < 0) lo = Math.Max(lo, t); else hi = Math.Min(hi, t);
        return lo <= hi;
    }

    private static int FirstPixel(float edge, int limit) =>
        (int)Math.Clamp(MathF.Ceiling(edge - .5f), 0, limit);
}
