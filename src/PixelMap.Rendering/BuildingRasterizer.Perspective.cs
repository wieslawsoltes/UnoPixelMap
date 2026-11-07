using System.Runtime.CompilerServices;
using SkiaSharp;

namespace PixelMap.Rendering;

internal sealed partial class BuildingRasterizer
{
    private float perspectiveFar, perspectiveNear;
    private bool usingPerspective;
    private TileTransform depthLens;
    private ProjectionContext[] projectionContexts = new ProjectionContext[8];
    private FrustumPlane[] frustumPlanes = new FrustumPlane[48];
    private int projectionCount, projectionIndex = -1;
    private float[] inverseDepth = [];
    private readonly List<H> contour = [], clippedContour = [];
    private readonly List<SKPoint> roofPoints = [];
    private readonly List<(int End, float MinimumY, float MaximumY)> roofRings = [];
    private H[] topVertices = [], bottomVertices = [];
    private bool[] visibleRings = [];
    private const int DepthCell = 8;
    private int depthColumns;
    private float[] coarseDepth = [];
    private bool[] depthDirty = [];

    private readonly record struct ProjectionContext(TileTransform Transform, int PlaneStart,
        float MetersPerUnit, float EyeAltitude);

    private readonly struct FrustumPlane(float x, float y, float z, float constant)
    {
        internal readonly float X = x, Y = y, Z = z, Constant = constant;
        internal readonly float AbsX = Math.Abs(x), AbsY = Math.Abs(y), AbsZ = Math.Abs(z);
    }

    private void DrawPerspective(Span<uint> pixels, in Building model, in TileTransform t, Material material, float foundationMeters)
    {
        perspectiveFar = t.Far;
        perspectiveNear = t.Near;
        depthLens = t;
        float foundation = foundationMeters * pixelsPerMeter;
        float top = (model.Top + foundationMeters) * pixelsPerMeter, bottom = (model.Bottom + foundationMeters) * pixelsPerMeter;
        var bounds = model.Bounds;
        ref readonly var context = ref ProjectionFor(t);
        if (Outside(bounds,bottom,top,context.PlaneStart)) return;
        var screenBounds = ProjectedBounds(bounds, bottom, top, t, out float nearest);
        if (Occluded(screenBounds, nearest)) return;

        float metersPerUnit = context.MetersPerUnit;
        float eyeAltitude = context.EyeAltitude;
        float cap = top < eyeAltitude ? top : bottom > eyeAltitude ? bottom : float.NaN;
        bool hasCap = float.IsFinite(cap), topCap = cap == top;
        var prepared = model.Vertices;
        if (topVertices.Length < prepared.Length)
        {
            int capacity = Math.Max(prepared.Length, topVertices.Length * 2);
            topVertices = new H[capacity];
            bottomVertices = new H[capacity];
        }
        roofPoints.Clear(); roofRings.Clear();
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        int ringEnd = 0, ringIndex = 0;
        var rings = model.Feature.Paths;
        if (visibleRings.Length < rings.Length) visibleRings = new bool[rings.Length];
        foreach (var ring in rings)
        {
            int ringStart = ringEnd;
            ringEnd += ring.Length;
            // A vector feature can contain hundreds of disconnected footprints. Reject
            // only complete ring prisms, retaining enclosing rings and courtyard holes.
            bool visible = ring.Length >= 3 && (rings.Length == 1 ||
                !Outside(model.BoundsForRing(ringIndex), bottom, top, context.PlaneStart));
            visibleRings[ringIndex++] = visible;
            if (!visible) continue;
            for (int i = ringStart; i < ringEnd; i++)
            {
                var point = prepared[i].Point;
                topVertices[i] = t.Vertex(point.X, point.Y, top);
                bottomVertices[i] = t.Vertex(point.X, point.Y, bottom);
            }
            contour.Clear();
            int previous = ringEnd - 1;
            for (int i = ringStart; i < ringEnd; i++)
            {
                var edge = prepared[i];
                if (hasCap) contour.Add(topCap ? topVertices[i] : bottomVertices[i]);
                if (!edge.TileCut && edge.PerspectiveLength > .001f && top > bottom)
                {
                    uint wall = PixelPalette.Mix(material.DarkWall, material.LightWall, edge.PerspectiveLight);
                    float along = edge.PerspectiveLength * metersPerUnit;
                    PerspectiveWall(pixels, topVertices[previous], topVertices[i] with { U = along },
                        bottomVertices[i] with { U = along }, bottomVertices[previous], material, wall, model.Seed, foundation);
                }
                previous = i;
            }
            if (contour.Count == 0) continue;
            // Clip each ring before perspective division; concave rings and holes continue
            // to participate in a single even/odd scan conversion for the horizontal cap.
            ClipRing(contour, clippedContour, 0);
            ClipRing(clippedContour, contour, 1);
            if (contour.Count < 3) continue;
            float ringTop = float.PositiveInfinity, ringBottom = float.NegativeInfinity;
            foreach (var v in contour)
            {
                var p = v.Point; roofPoints.Add(p);
                ringTop = Math.Min(ringTop, p.Y); ringBottom = Math.Max(ringBottom, p.Y);
            }
            minY = Math.Min(minY, ringTop); maxY = Math.Max(maxY, ringBottom);
            roofRings.Add((roofPoints.Count, ringTop, ringBottom));
        }
        if (roofPoints.Count == 0) return;
        if (intersections.Length < roofPoints.Count) intersections = new float[roofPoints.Count];
        float divisor = t.CosPitch - (cap-t.TargetZ)*t.InverseFocal;
        if (Math.Abs(divisor) < 1e-7f) return;
        uint color = cap == top ? material.Roof : material.DarkWall;
        InvalidateDepth(screenBounds);
        for (int y = FirstPixel(minY,height), last = FirstPixel(maxY,height); y < last; y++)
        {
            float scan = y + .5f;
            float iw = (t.CosPitch + (scan-t.CY)*t.SinPitch*t.InverseFocal)/divisor;
            int hits = 0, first = 0;
            foreach (var (end, ringTop, ringBottom) in roofRings)
            {
                if (scan < ringTop || scan >= ringBottom) { first = end; continue; }
                var a = roofPoints[end-1];
                for (int i = first; i < end; i++)
                {
                    var b = roofPoints[i];
                    if ((a.Y>scan)!=(b.Y>scan)) intersections[hits++] = a.X + (scan-a.Y)*(b.X-a.X)/(b.Y-a.Y);
                    a=b;
                }
                first=end;
            }
            if (hits == 2 && intersections[0] > intersections[1]) (intersections[0],intersections[1]) = (intersections[1],intersections[0]);
            else if (hits > 2) intersections.AsSpan(0,hits).Sort();
            for (int i=1;i<hits;i+=2)
            {
                int x0=FirstPixel(intersections[i-1],width), x1=FirstPixel(intersections[i],width);
                for (int p=y*width+x0, end=y*width+x1; p<end; p++)
                {
                    if (iw < inverseDepth[p]) continue;
                    inverseDepth[p]=iw; depth[p]=cap; pixels[p]=color;
                }
            }
        }
        if (cap != top) return;
        ringEnd = 0;
        ringIndex = 0;
        foreach (var ring in rings)
        {
            int ringStart = ringEnd;
            ringEnd += ring.Length;
            if (!visibleRings[ringIndex++]) continue;
            int previous = ringEnd - 1;
            for (int i = ringStart; i < ringEnd; i++)
            {
                if (!prepared[i].TileCut)
                    PerspectiveEdge(pixels, topVertices[previous], topVertices[i], cap, material);
                previous = i;
            }
        }
    }

    private void BeginOcclusion(int w, int h)
    {
        projectionCount = 0;
        projectionIndex = -1;
        depthColumns = (w + DepthCell - 1) / DepthCell;
        int cells = depthColumns * ((h + DepthCell - 1) / DepthCell);
        if (coarseDepth.Length < cells)
        {
            coarseDepth = new float[cells];
            depthDirty = new bool[cells];
        }
        else
        {
            Array.Clear(coarseDepth, 0, cells);
            Array.Clear(depthDirty, 0, cells);
        }
    }

    private SKRect ProjectedBounds(SKRect area, float bottom, float top, TileTransform transform, out float nearest)
    {
        var screen = new SKRect(float.PositiveInfinity, float.PositiveInfinity, float.NegativeInfinity, float.NegativeInfinity);
        nearest = 0;
        for (int corner = 0; corner < 8; corner++)
        {
            var vertex = transform.Vertex((corner & 1) == 0 ? area.Left : area.Right,
                (corner & 2) == 0 ? area.Top : area.Bottom, (corner & 4) == 0 ? bottom : top);
            if (vertex.W < transform.Near)
            {
                nearest = float.PositiveInfinity;
                return new(0, 0, width, height);
            }
            nearest = Math.Max(nearest, 1 / vertex.W);
            var point = vertex.Point;
            screen.Left = Math.Min(screen.Left, point.X);
            screen.Right = Math.Max(screen.Right, point.X);
            screen.Top = Math.Min(screen.Top, point.Y);
            screen.Bottom = Math.Max(screen.Bottom, point.Y);
        }
        return screen;
    }

    // Each cell stores the farthest visible sample. An entire model can be discarded
    // only when every intersecting cell is already nearer than its nearest possible point.
    private bool Occluded(SKRect bounds, float nearest)
    {
        if (!float.IsFinite(nearest)) return false;
        var cells = DepthCells(bounds);
        for (int y = cells.Top; y < cells.Bottom; y++)
        for (int x = cells.Left; x < cells.Right; x++)
        {
            int cell = y * depthColumns + x;
            if (depthDirty[cell])
            {
                float minimum = float.PositiveInfinity;
                int yEnd = Math.Min(height, (y + 1) * DepthCell);
                int xEnd = Math.Min(width, (x + 1) * DepthCell);
                for (int py = y * DepthCell; py < yEnd && minimum > 0; py++)
                for (int px = x * DepthCell; px < xEnd; px++)
                    minimum = Math.Min(minimum, inverseDepth[py * width + px]);
                coarseDepth[cell] = minimum;
                depthDirty[cell] = false;
            }
            if (coarseDepth[cell] <= nearest + 1e-5f) return false;
        }
        return true;
    }

    private void InvalidateDepth(SKRect bounds)
    {
        var cells = DepthCells(bounds);
        for (int y = cells.Top; y < cells.Bottom; y++)
        for (int x = cells.Left; x < cells.Right; x++)
            depthDirty[y * depthColumns + x] = true;
    }

    private SKRectI DepthCells(SKRect bounds) => new(
        (int)Math.Clamp(MathF.Floor(bounds.Left / DepthCell), 0, depthColumns),
        (int)Math.Clamp(MathF.Floor(bounds.Top / DepthCell), 0, (height + DepthCell - 1) / DepthCell),
        (int)Math.Clamp(MathF.Ceiling(bounds.Right / DepthCell), 0, depthColumns),
        (int)Math.Clamp(MathF.Ceiling(bounds.Bottom / DepthCell), 0, (height + DepthCell - 1) / DepthCell));

    private ref readonly ProjectionContext ProjectionFor(in TileTransform t)
    {
        if (projectionIndex >= 0 && projectionContexts[projectionIndex].Transform == t)
            return ref projectionContexts[projectionIndex];
        // The front-to-back model order interleaves tiles. Retain each tile's lens and
        // frustum for the frame instead of rebuilding them at every tile transition.
        for (int i = 0; i < projectionCount; i++)
        {
            if (projectionContexts[i].Transform != t) continue;
            projectionIndex = i;
            return ref projectionContexts[i];
        }
        if (projectionCount == projectionContexts.Length)
        {
            Array.Resize(ref projectionContexts, projectionContexts.Length * 2);
            Array.Resize(ref frustumPlanes, projectionContexts.Length * 6);
        }
        projectionIndex = projectionCount++;
        int first = projectionIndex * 6;
        float pz=-t.CosPitch*t.InverseFocal,xz=t.CX*pz,yz=t.CY*pz-t.SinPitch;
        frustumPlanes[first] = new(t.PX,t.PY,pz,t.PW-t.Near);
        frustumPlanes[first+1] = new(-t.PX,-t.PY,-pz,t.Far-t.PW);
        frustumPlanes[first+2] = new(t.XX,t.XY,xz,t.X);
        frustumPlanes[first+3] = new(width*t.PX-t.XX,width*t.PY-t.XY,width*pz-xz,width*t.PW-t.X);
        frustumPlanes[first+4] = new(t.YX,t.YY,yz,t.Y);
        frustumPlanes[first+5] = new(height*t.PX-t.YX,height*t.PY-t.YY,height*pz-yz,height*t.PW-t.Y);
        float rightX = t.XX - t.CX*t.PX, rightY = t.XY - t.CX*t.PY;
        float metersPerUnit = MathF.Sqrt(rightX*rightX + rightY*rightY) / pixelsPerMeter;
        float eyeAltitude = t.TargetZ + t.CosPitch / t.InverseFocal;
        projectionContexts[projectionIndex] = new(t, first, metersPerUnit, eyeAltitude);
        return ref projectionContexts[projectionIndex];
    }

    private bool Outside(SKRect bounds,float bottom,float top,int firstPlane)
    {
        float x=bounds.MidX,y=bounds.MidY,z=(bottom+top)*.5f;
        float rx=bounds.Width*.5f,ry=bounds.Height*.5f,rz=(top-bottom)*.5f;
        foreach(var p in frustumPlanes.AsSpan(firstPlane, 6))
            if(p.X*x+p.Y*y+p.Z*z+p.Constant+p.AbsX*rx+p.AbsY*ry+p.AbsZ*rz<-.01f)return true;
        return false;
    }

    private void ClipRing(List<H> source, List<H> target, int plane)
    {
        target.Clear(); if (source.Count == 0) return;
        var a=source[^1]; float da=TileTransform.Plane(plane,a,width,height,perspectiveFar,perspectiveNear);
        foreach (var b in source)
        {
            float db=TileTransform.Plane(plane,b,width,height,perspectiveFar,perspectiveNear);
            if ((da<0)!=(db<0)) target.Add(H.Lerp(a,b,da/(da-db)));
            if (db>=0) target.Add(b);
            a=b;da=db;
        }
    }

    private void PerspectiveWall(Span<uint> pixels, H a, H b, H c, H d, Material material, uint color, uint seed, float foundation)
    {
        if (Inside(a) && Inside(b) && Inside(c) && Inside(d))
        {
            WallTriangle(pixels, a, b, c, material, color, seed, foundation);
            WallTriangle(pixels, a, c, d, material, color, seed, foundation);
            return;
        }
        Span<H> polygon = stackalloc H[12], scratch = stackalloc H[12];
        Span<float> distances = stackalloc float[12];
        polygon[0]=a;polygon[1]=b;polygon[2]=c;polygon[3]=d;
        int count=4;
        for (int plane=0;plane<6 && count>0;plane++)
        {
            int inside = 0, outside = 0;
            for (int i = 0; i < count; i++)
            {
                float distance = TileTransform.Plane(plane, polygon[i], width, height, perspectiveFar, perspectiveNear);
                distances[i] = distance;
                if (distance >= 0) inside++;
                if (distance < 0) outside++;
            }
            if (inside == count) continue;
            if (outside == count) return;
            int n=0; var prev=polygon[count-1]; float da=distances[count-1];
            for(int i=0;i<count;i++)
            {
                var next=polygon[i]; float db=distances[i];
                if ((da<0)!=(db<0)) scratch[n++]=H.Lerp(prev,next,da/(da-db));
                if (db>=0) scratch[n++]=next;
                prev=next; da=db;
            }
            scratch[..n].CopyTo(polygon);count=n;
        }
        for (int i=2;i<count;i++) WallTriangle(pixels,polygon[0],polygon[i-1],polygon[i],material,color,seed,foundation);
    }

    private bool Inside(H point) => point.W - perspectiveNear >= 0 && perspectiveFar - point.W >= 0 &&
        point.X >= 0 && width * point.W - point.X >= 0 && point.Y >= 0 && height * point.W - point.Y >= 0;

    private void WallTriangle(Span<uint> pixels,H a,H b,H c,Material mat,uint color,uint seed,float foundation)
    {
        var p=a.Point; var q=b.Point; var r=c.Point;
        float dx1=q.X-p.X, dy1=q.Y-p.Y, dx2=r.X-p.X, dy2=r.Y-p.Y;
        float det=dx1*dy2-dy1*dx2;
        if (det>=-.00001f) return; // back face in the MVT winding convention
        float ai=1/a.W, bi=1/b.W, ci=1/c.W;
        var screenBounds = new SKRect(Math.Min(p.X, Math.Min(q.X, r.X)), Math.Min(p.Y, Math.Min(q.Y, r.Y)),
            Math.Max(p.X, Math.Max(q.X, r.X)), Math.Max(p.Y, Math.Max(q.Y, r.Y)));
        if (Occluded(screenBounds, Math.Max(ai, Math.Max(bi, ci)))) return;
        var iw=Plane(ai,bi,ci); var zw=Plane(a.Z*ai,b.Z*bi,c.Z*ci); var uw=Plane(a.U*ai,b.U*bi,c.U*ci);
        float floor=3.2f+(seed&3)*.22f, bay=3.2f+((seed>>2)&3)*.6f;
        bool finiteAttributes = false;
        bool filterWindows = renderStyle == MapRenderStyle.Pixel && !HasResolvedWindowFootprint(
            p, ai, a.U * ai, a.Z * ai, iw, uw, zw, width, height, bay, (double)pixelsPerMeter * floor,
            Math.Min(a.W, Math.Min(b.W, c.W)), perspectiveNear, out finiteAttributes);
        var windowColors = filterWindows ? new PixelWindowFilter.Colors(color, mat) : default;
        bool constantFootprint = filterWindows && finiteAttributes && iw.X == 0 && iw.Y == 0;
        var footprint = default(PixelWindowFilter.SamplingFootprint);
        if (constantFootprint)
        {
            // With finite attributes and constant reciprocal W, the quotient terms
            // multiplied by d(1/W) are exactly zero. Retain the scalar denominator
            // association while preparing the filter's LOD/blend only once.
            float footprintX = (Math.Abs(uw.X) + Math.Abs(uw.Y)) / (ai * bay);
            float footprintY = (Math.Abs(zw.X) + Math.Abs(zw.Y)) / (ai * pixelsPerMeter * floor);
            constantFootprint = float.IsFinite(footprintX) && float.IsFinite(footprintY);
            if (constantFootprint) footprint = new(footprintX, footprintY);
        }
        int y0=FirstPixel(Math.Min(p.Y,Math.Min(q.Y,r.Y)),height);
        int y1=FirstPixel(Math.Max(p.Y,Math.Max(q.Y,r.Y)),height);
        for(int y=y0;y<y1;y++)
        {
            float row=y+.5f, min=float.PositiveInfinity, max=float.NegativeInfinity;
            Hit(p,q);Hit(q,r);Hit(r,p);
            if (min>=max) continue;
            int x0=FirstPixel(min,width), x1=FirstPixel(max,width);
            float offX=x0+.5f-p.X, offY=row-p.Y;
            float inv=ai+iw.X*offX+iw.Y*offY, zOver=a.Z*ai+zw.X*offX+zw.Y*offY, uOver=a.U*ai+uw.X*offX+uw.Y*offY;
            if (constantFootprint && zw.X == 0 && zOver != 0 && x0 < x1 &&
                TryFilteredWindowRow(pixels, y * width + x0, x1 - x0, inv, zOver, uOver, uw.X,
                    seed, foundation, floor, bay, in windowColors, in footprint)) continue;
            for(int x=x0,pos=y*width+x0;x<x1;x++,pos++,inv+=iw.X,zOver+=zw.X,uOver+=uw.X)
            {
                if (inv<inverseDepth[pos]) continue;
                float z=zOver/inv; uint texel=color;
                if (filterWindows)
                {
                    float column = uOver / (inv * bay), story = (z - foundation) / (pixelsPerMeter * floor);
                    if (constantFootprint)
                        texel = PixelWindowFilter.Sample(in windowColors, in footprint, seed, column, story, pixelsPerMeter * inv);
                    else
                    {
                        float along = uOver / inv;
                        // U/W and Z/W are affine on the screen. The quotient derivative
                        // measures one art pixel along both façade axes, including a
                        // grazing wall whose bays are much narrower than its stories.
                        float footprintX = (Math.Abs(uw.X - along * iw.X) + Math.Abs(uw.Y - along * iw.Y)) / (inv * bay);
                        float footprintY = (Math.Abs(zw.X - z * iw.X) + Math.Abs(zw.Y - z * iw.Y)) / (inv * pixelsPerMeter * floor);
                        texel = PixelWindowFilter.Sample(in windowColors, seed, column, story, footprintX, footprintY, pixelsPerMeter * inv);
                    }
                }
                else if (pixelsPerMeter*inv > .40f)
                {
                    float story=(z-foundation)/(pixelsPerMeter*floor), column=uOver/(inv*bay);
                    int level=(int)MathF.Floor(story), col=(int)MathF.Floor(column);
                    float wy=story-level, wx=column-col;
                    if (wy>.23f && wy<.75f && wx>.17f && wx<.76f)
                        texel=Rooms[((int)seed+col+level*16)&255]==0 ? mat.Lamp : mat.Glass;
                }
                inverseDepth[pos]=inv; depth[pos]=z; pixels[pos]=texel;
            }
            void Hit(SKPoint v,SKPoint e)
            {
                if ((v.Y>row)==(e.Y>row)) return;
                float x=v.X+(row-v.Y)*(e.X-v.X)/(e.Y-v.Y);min=Math.Min(min,x);max=Math.Max(max,x);
            }
        }
        InvalidateDepth(screenBounds);
        (float X,float Y) Plane(float v0,float v1,float v2) =>
            (((v1-v0)*dy2-(v2-v0)*dy1)/det,(dx1*(v2-v0)-dx2*(v1-v0))/det);
    }

    // Keep the prepared filter's mip-row state and inlined pixel arithmetic out
    // of WallTriangle's frame: resolved walls should not pay its register pressure.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryFilteredWindowRow(Span<uint> pixels, int start, int count,
        float inv, float zOver, float uOver, float du, uint seed, float foundation, float floor, float bay,
        in PixelWindowFilter.Colors colors, in PixelWindowFilter.SamplingFootprint footprint)
    {
        float z = zOver / inv, story = (z - foundation) / (pixelsPerMeter * floor);
        if (!float.IsFinite(z) || !float.IsFinite(story)) return false;
        // I and Z/I are identical across this row. Zero Z numerators stay on the
        // general path: repeated += 0 can change the sign of a negative zero.
        var sampling = new PixelWindowFilter.SamplingRow(footprint, seed, story, pixelsPerMeter * inv);
        float columnScale = inv * bay;
        for (int pos = start, end = start + count; pos < end; pos++, uOver += du)
        {
            if (inv < inverseDepth[pos]) continue;
            uint texel = PixelWindowFilter.Sample(colors, footprint, sampling, uOver / columnScale);
            inverseDepth[pos] = inv; depth[pos] = z; pixels[pos] = texel;
        }
        return true;
    }

    /// <summary>Prove that every possible scanline sample uses the original point
    /// window lookup. The viewport rectangle contains the rasterized triangle; affine
    /// extrema bound I, U/I and Z/I before the quotient derivatives are evaluated.
    /// Ignoring cancellation makes this conservative for oblique and clipped walls.</summary>
    internal static bool HasResolvedWindowFootprint(SKPoint origin, float inverse, float uOver, float zOver,
        (float X, float Y) iw, (float X, float Y) uw, (float X, float Y) zw,
        int viewportWidth, int viewportHeight, float bay, double storyHeight, float minimumWeight, float nearWeight) =>
        HasResolvedWindowFootprint(origin, inverse, uOver, zOver, iw, uw, zw, viewportWidth, viewportHeight,
            bay, storyHeight, minimumWeight, nearWeight, out _);

    private static bool HasResolvedWindowFootprint(SKPoint origin, float inverse, float uOver, float zOver,
        (float X, float Y) iw, (float X, float Y) uw, (float X, float Y) zw,
        int viewportWidth, int viewportHeight, float bay, double storyHeight, float minimumWeight, float nearWeight,
        out bool finiteAttributes)
    {
        finiteAttributes = false;
        // Clipping at the physical near plane and exceptional numeric inputs keep
        // the general per-pixel path. Pixel centers must also remain exact floats.
        if (viewportWidth <= 0 || viewportHeight <= 0 || viewportHeight >= 1 << 23 ||
            !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(inverse) || inverse <= 0 ||
            !float.IsFinite(uOver) || !float.IsFinite(zOver) ||
            !float.IsFinite(iw.X) || !float.IsFinite(iw.Y) ||
            !float.IsFinite(uw.X) || !float.IsFinite(uw.Y) ||
            !float.IsFinite(zw.X) || !float.IsFinite(zw.Y) ||
            !float.IsFinite(bay) || !double.IsFinite(storyHeight) || bay <= 0 || storyHeight <= 0 ||
            !float.IsFinite(minimumWeight) || !float.IsFinite(nearWeight) || nearWeight < 0 ||
            minimumWeight <= nearWeight * 1.001f) return false;

        // Use a full float epsilon (twice unit roundoff). The gamma bound covers
        // the initial plane evaluation and at most width rounded additions per row.
        const double epsilon = 1.1920928955078125e-7;
        double accumulated = (viewportWidth + 16d) * epsilon;
        if (accumulated >= .01) return false;
        double gamma = accumulated / (1 - accumulated);
        double left = .5 - origin.X, right = viewportWidth - .5 - origin.X;
        double top = .5 - origin.Y, bottom = viewportHeight - .5 - origin.Y;
        var inverseRange = Range(inverse, iw);
        var uRange = Range(uOver, uw);
        var zRange = Range(zOver, zw);
        double minimum = inverseRange.Low;
        double maximum = inverseRange.High;
        if (minimum <= 1e-20 || maximum >= float.MaxValue ||
            Math.Max(Math.Abs(uRange.Low), Math.Abs(uRange.High)) >= float.MaxValue ||
            Math.Max(Math.Abs(zRange.Low), Math.Abs(zRange.High)) >= float.MaxValue ||
            minimum * bay <= 1e-30 || minimum * storyHeight <= 1e-30 ||
            maximum * bay >= float.MaxValue || maximum * storyHeight >= float.MaxValue) return false;
        double along = Math.Max(Math.Abs(uRange.Low), Math.Abs(uRange.High)) / minimum;
        double altitude = Math.Max(Math.Abs(zRange.Low), Math.Abs(zRange.High)) / minimum;
        if (along >= float.MaxValue / (1 + 16 * epsilon) || altitude >= float.MaxValue / (1 + 16 * epsilon)) return false;
        finiteAttributes = true;
        double inverseSlope = Math.Abs((double)iw.X) + Math.Abs((double)iw.Y);
        // Bound |dU - (U/I)dI| / I and its vertical counterpart. The remaining
        // margin covers division, products, sums, and the two story-scale products.
        double rounding = 1 + 16 * epsilon;
        double horizontal = (Math.Abs((double)uw.X) + Math.Abs((double)uw.Y) + along * inverseSlope + 1e-30) *
            rounding / (minimum * bay);
        double vertical = (Math.Abs((double)zw.X) + Math.Abs((double)zw.Y) + altitude * inverseSlope + 1e-30) *
            rounding / (minimum * storyHeight);
        return horizontal <= PixelWindowFilter.FilterStart && vertical <= PixelWindowFilter.FilterStart;

        (double Low, double High) Range(float value, (float X, float Y) gradient)
        {
            double x0 = gradient.X * left, x1 = gradient.X * right;
            double y0 = gradient.Y * top, y1 = gradient.Y * bottom;
            double scale = Math.Abs((double)value) + Math.Max(Math.Abs(x0), Math.Abs(x1)) +
                Math.Max(Math.Abs(y0), Math.Abs(y1)) + viewportWidth * Math.Abs((double)gradient.X);
            // Cancellation in double must not hide overflowing float products or
            // partial sums in the actual row initializer.
            if (scale >= float.MaxValue / (1 + gamma)) return (double.NegativeInfinity, double.PositiveInfinity);
            double error = gamma * scale + 1e-30;
            return (value + Math.Min(x0, x1) + Math.Min(y0, y1) - error,
                value + Math.Max(x0, x1) + Math.Max(y0, y1) + error);
        }
    }

    private void PerspectiveEdge(Span<uint> pixels,H a,H b,float elevation,Material material)
    {
        float lo=0, hi=1;
        for(int plane=0;plane<6;plane++)
        {
            float v=TileTransform.Plane(plane,a,width,height,perspectiveFar,perspectiveNear), w=TileTransform.Plane(plane,b,width,height,perspectiveFar,perspectiveNear);
            if(v<0&&w<0)return;
            if(v>=0&&w>=0)continue;
            float t=v/(v-w);
            if(v<0)lo=Math.Max(lo,t);else hi=Math.Min(hi,t);
        }
        if(hi<lo)return;
        var c=H.Lerp(a,b,lo);var d=H.Lerp(a,b,hi);var p=c.Point;var q=d.Point;
        float dx=q.X-p.X,dy=q.Y-p.Y,iw=1/c.W,end=1/d.W;
        int n=Math.Max(1,(int)MathF.Ceiling(Math.Max(Math.Abs(dx),Math.Abs(dy))));
        uint color=dx>=0?material.Rim:material.Trim;
        for(int i=0;i<=n;i++)
        {
            float t=i/(float)n;int x=(int)MathF.Floor(p.X+dx*t),y=(int)MathF.Floor(p.Y+dy*t);
            if((uint)x>=(uint)width||(uint)y>=(uint)height)continue;
            int pos=y*width+x;float z=iw+(end-iw)*t;
            if(z < inverseDepth[pos]-1e-6f)continue;
            inverseDepth[pos]=Math.Max(z,inverseDepth[pos]);depth[pos]=elevation;pixels[pos]=color;
        }
    }
}
