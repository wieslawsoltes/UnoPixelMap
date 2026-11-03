using SkiaSharp;

namespace PixelMap.Rendering;

internal sealed partial class BuildingRasterizer
{
    private float perspectiveFar;
    private bool usingPerspective;
    private TileTransform depthLens;
    private TileTransform cullLens;
    private (int Width,int Height) cullSize;
    private readonly (float X,float Y,float Z,float Constant)[] planes = new (float,float,float,float)[6];
    private float[] inverseDepth = [];
    private readonly List<H> contour = [], clippedContour = [];
    private readonly List<SKPoint> roofPoints = [];
    private readonly List<int> roofEnds = [];
    private const int DepthCell = 8;
    private int depthColumns;
    private float[] coarseDepth = [];
    private bool[] depthDirty = [];

    private void DrawPerspective(Span<uint> pixels, in Building model, in TileTransform t, Material material)
    {
        perspectiveFar = t.Far;
        depthLens = t;
        float top = model.Top * pixelsPerMeter, bottom = model.Bottom * pixelsPerMeter;
        var bounds = model.Bounds;
        if (Outside(bounds,bottom,top,t)) return;
        var screenBounds = ProjectedBounds(bounds, bottom, top, t, out float nearest);
        if (Occluded(screenBounds, nearest)) return;

        float rightX = t.XX - t.CX*t.PX, rightY = t.XY - t.CX*t.PY;
        float metersPerUnit = MathF.Sqrt(rightX*rightX + rightY*rightY) / pixelsPerMeter;
        float eyeAltitude = t.TargetZ + t.CosPitch / t.InverseFocal;
        float cap = top < eyeAltitude ? top : bottom > eyeAltitude ? bottom : float.NaN;
        roofPoints.Clear(); roofEnds.Clear();
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        foreach (var ring in model.Feature.Paths)
        {
            if (ring.Length < 3) continue;
            contour.Clear();
            var last = ring[^1];
            foreach (var point in ring)
            {
                float ax = last.X*model.Unit, ay = last.Y*model.Unit, bx = point.X*model.Unit, by = point.Y*model.Unit;
                if (float.IsFinite(cap)) contour.Add(t.Vertex(bx, by, cap));
                if (!IsTileCut(last.X, last.Y, point.X, point.Y, model.Feature.Extent))
                {
                    float dx = bx-ax, dy = by-ay, len = MathF.Sqrt(dx*dx+dy*dy);
                    if (len > .001f && top > bottom)
                    {
                        float light = Math.Clamp(.5f + (dx*.75f - dy*.55f)/(2*len), 0, 1);
                        uint wall = PixelPalette.Mix(material.DarkWall, material.LightWall, light);
                        PerspectiveWall(pixels, t.Vertex(ax,ay,top), t.Vertex(bx,by,top,len*metersPerUnit),
                            t.Vertex(bx,by,bottom,len*metersPerUnit), t.Vertex(ax,ay,bottom), material, wall, model.Seed);
                    }
                }
                last = point;
            }
            if (contour.Count == 0) continue;
            // Clip each ring before perspective division; concave rings and holes continue
            // to participate in a single even/odd scan conversion for the horizontal cap.
            ClipRing(contour, clippedContour, 0);
            ClipRing(clippedContour, contour, 1);
            if (contour.Count < 3) continue;
            foreach (var v in contour)
            {
                var p = v.Point; roofPoints.Add(p);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            roofEnds.Add(roofPoints.Count);
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
            foreach (int end in roofEnds)
            {
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
        foreach (var ring in model.Feature.Paths)
        {
            if (ring.Length < 3) continue;
            var a=ring[^1];
            foreach (var b in ring)
            {
                if (!IsTileCut(a.X,a.Y,b.X,b.Y,model.Feature.Extent))
                    PerspectiveEdge(pixels,t.Vertex(a.X*model.Unit,a.Y*model.Unit,cap),
                        t.Vertex(b.X*model.Unit,b.Y*model.Unit,cap),cap,material);
                a=b;
            }
        }
    }

    private void BeginOcclusion(int w, int h)
    {
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
            if (vertex.W < MapCamera.Near)
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

    private bool Outside(SKRect bounds,float bottom,float top,in TileTransform t)
    {
        if (cullLens != t || cullSize != (width,height))
        {
            cullLens=t; cullSize=(width,height);
            float pz=-t.CosPitch*t.InverseFocal,xz=t.CX*pz,yz=t.CY*pz-t.SinPitch;
            planes[0]=(t.PX,t.PY,pz,t.PW-(float)MapCamera.Near);
            planes[1]=(-t.PX,-t.PY,-pz,t.Far-t.PW);
            planes[2]=(t.XX,t.XY,xz,t.X);
            planes[3]=(width*t.PX-t.XX,width*t.PY-t.XY,width*pz-xz,width*t.PW-t.X);
            planes[4]=(t.YX,t.YY,yz,t.Y);
            planes[5]=(height*t.PX-t.YX,height*t.PY-t.YY,height*pz-yz,height*t.PW-t.Y);
        }
        float x=bounds.MidX,y=bounds.MidY,z=(bottom+top)*.5f;
        float rx=bounds.Width*.5f,ry=bounds.Height*.5f,rz=(top-bottom)*.5f;
        foreach(var p in planes)
            if(p.X*x+p.Y*y+p.Z*z+p.Constant+Math.Abs(p.X)*rx+Math.Abs(p.Y)*ry+Math.Abs(p.Z)*rz<-.01f)return true;
        return false;
    }

    private void ClipRing(List<H> source, List<H> target, int plane)
    {
        target.Clear(); if (source.Count == 0) return;
        var a=source[^1]; float da=TileTransform.Plane(plane,a,width,height,perspectiveFar);
        foreach (var b in source)
        {
            float db=TileTransform.Plane(plane,b,width,height,perspectiveFar);
            if ((da<0)!=(db<0)) target.Add(H.Lerp(a,b,da/(da-db)));
            if (db>=0) target.Add(b);
            a=b;da=db;
        }
    }

    private void PerspectiveWall(Span<uint> pixels, H a, H b, H c, H d, Material material, uint color, uint seed)
    {
        Span<H> polygon = stackalloc H[12], scratch = stackalloc H[12];
        polygon[0]=a;polygon[1]=b;polygon[2]=c;polygon[3]=d;
        int count=4;
        for (int plane=0;plane<6 && count>0;plane++)
        {
            int n=0; var prev=polygon[count-1]; float da=TileTransform.Plane(plane,prev,width,height,perspectiveFar);
            for(int i=0;i<count;i++)
            {
                var next=polygon[i]; float db=TileTransform.Plane(plane,next,width,height,perspectiveFar);
                if ((da<0)!=(db<0)) scratch[n++]=H.Lerp(prev,next,da/(da-db));
                if (db>=0) scratch[n++]=next;
                prev=next; da=db;
            }
            scratch[..n].CopyTo(polygon);count=n;
        }
        for (int i=2;i<count;i++) WallTriangle(pixels,polygon[0],polygon[i-1],polygon[i],material,color,seed);
    }

    private void WallTriangle(Span<uint> pixels,H a,H b,H c,Material mat,uint color,uint seed)
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
            for(int x=x0,pos=y*width+x0;x<x1;x++,pos++,inv+=iw.X,zOver+=zw.X,uOver+=uw.X)
            {
                if (inv<inverseDepth[pos]) continue;
                float z=zOver/inv; uint texel=color;
                if (pixelsPerMeter*inv > .40f)
                {
                    float story=z/(pixelsPerMeter*floor), column=uOver/(inv*bay);
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

    private void PerspectiveEdge(Span<uint> pixels,H a,H b,float elevation,Material material)
    {
        float lo=0, hi=1;
        for(int plane=0;plane<6;plane++)
        {
            float v=TileTransform.Plane(plane,a,width,height,perspectiveFar), w=TileTransform.Plane(plane,b,width,height,perspectiveFar);
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
