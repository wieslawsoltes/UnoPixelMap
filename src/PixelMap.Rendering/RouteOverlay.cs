using PixelMap.Routing;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>World-space simulation sample. Bearing is clockwise from north, progress is the
/// fraction of route distance. AnimationSeconds advances with the simulation clock.</summary>
public readonly record struct VehiclePose(RoutePoint Point, double Bearing, double AnimationSeconds = 0,
    double AltitudeMeters = 0, bool Moving = true, double Progress = double.NaN, double SpeedMetersPerSecond = 0, long Continuity = 0);

public sealed partial class PixelMapRenderer
{
    private RouteOverlay? routeOverlay;

    /// <summary>Composite directions and a moving vehicle after drawing the map. Vehicle motion
    /// reuses both the last map frame and the static route geometry.</summary>
    public void DrawNavigation(SKCanvas canvas, int width, int height, MapCamera camera, RoutePlan plan,
        VehiclePose? vehicle = null, int pixelSize = 2)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(plan);
        if (width <= 0 || height <= 0) return;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            bool aligned = lastPicture != null && lastView.Camera == camera && lastView.Width == width && lastView.Height == height
                && camera.Zoom >= 12 && camera.Pitch > 3;
            (routeOverlay ??= new()).Draw(canvas, width, height, Math.Clamp(pixelSize, 1, 8), camera, plan, vehicle,
                aligned ? buildings : null, Math.Max(1, lastView.PixelSize), aligned ? frame : -1);
        }
    }
}

internal sealed class RouteOverlay : IDisposable
{
    private readonly record struct Vertex(double X, double Y, double Distance);
    private readonly record struct Stroke(SKPoint A, SKPoint B, double Start, double End, bool Hidden, float WeightA, float WeightB);
    private readonly record struct View(int Width, int Height, int Pixel, MapCamera Camera, long Revision);

    private RoutePlan? plan;
    private Vertex[] vertices = [];
    private readonly List<Stroke> strokes = [];
    private readonly SKPaint paint = new() { IsAntialias = false, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
    private readonly SKPath partial = new();
    private readonly SKPath shown = new();
    private readonly SKPath behind = new();
    private readonly List<(SKPath Path, double End)> completed = [];
    private SKImage? lineImage;
    private View? view;
    private readonly VehicleEffects effects = new();
    private readonly VehicleSprite sprite = new();
    private readonly List<SKPoint> stops = [];
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    internal void Draw(SKCanvas canvas, int w, int h, int pixel, MapCamera camera, RoutePlan next, VehiclePose? vehicle,
        BuildingRasterizer? depth, int depthPixel, long revision)
    {
        if (!ReferenceEquals(plan, next))
        {
            plan = next;
            PrepareMesh(next);
            view = null;
            effects.Reset();
        }
        if (vertices.Length == 0) return;
        var key = new View(w, h, pixel, camera, revision);
        if (view != key)
        {
            PrepareView(key, next, depth, depthPixel);
            view = key;
        }

        canvas.Save();
        canvas.ClipRect(new(0, 0, w, h));
        canvas.Scale(pixel);
        if (lineImage is not null)
            canvas.DrawImage(lineImage, 0, 0, Nearest);
        if (vehicle is { } pose && double.IsFinite(pose.Progress))
            DrawProgress(canvas, Math.Clamp(pose.Progress, 0, 1));
        for (int i = 0; i < stops.Count; i++)
            DrawStop(canvas, stops[i], i == 0, i == stops.Count - 1, pixel);

        if (vehicle is { } sample)
        {
            bool Visible(float x, float y, double altitude) => depth is null ||
                !depth.CoversAt(x * pixel / depthPixel, y * pixel / depthPixel, (float)altitude);
            effects.Advance(next.Mode, sample);
            effects.Draw(canvas, camera, w, h, pixel, next.Mode, paint, Visible);
            bool inFrustum = VehicleEffects.Position(camera,sample.Point,sample.AltitudeMeters,w,h,pixel,out float x,out float y,out float lift);
            if (inFrustum && x > -32 && x < w / pixel + 32 && y - lift > -32 && y - lift < h / pixel + 32)
            {
                bool visible = Visible(x, y - lift, sample.AltitudeMeters);
                SKPoint? ground = camera.TryProject(sample.Point.Latitude, sample.Point.Longitude, 0, w, h, out var foot)
                    ? new((float)(foot.X / pixel), (float)(foot.Y / pixel)) : null;
                var axes = VehicleEffects.Axes(camera, sample.Point, sample.AltitudeMeters, sample.Bearing, w, h);
                sprite.Draw(canvas, new(x, y - lift), ground, next.Mode, axes, effects.Time, sample.Moving, visible);
            }
        }
        else effects.Reset();
        canvas.Restore();
    }

    private void PrepareMesh(RoutePlan route)
    {
        var mesh = new List<Vertex>(route.Geometry.Count);
        double x = route.Geometry.Count == 0 ? 0 : (route.Geometry[0].Longitude + 180) / 360;
        double distance = 0;
        RoutePoint? last = null;
        foreach (var leg in route.Legs)
        {
            // Match RouteTrack.Sample's conversion from provider distance to geometry distance
            // per leg, including providers which snap the two ends of a waypoint differently.
            int first = mesh.Count;
            double local = 0;
            for (int i = 0; i < leg.Geometry.Count; i++)
            {
                var p = leg.Geometry[i];
                if (i > 0) local += leg.Geometry[i - 1].DistanceTo(p);
                if (last == p) continue;
                x += MapCamera.WrapOffset((p.Longitude + 180) / 360 - x);
                mesh.Add(new(x, MapCamera.MercatorY(p.Latitude), local));
                last = p;
            }
            for (int i = first; i < mesh.Count; i++)
            {
                double station = distance + (local > 0 ? mesh[i].Distance / local * leg.DistanceMeters : 0);
                mesh[i] = mesh[i] with { Distance = route.DistanceMeters > 0 ? station / route.DistanceMeters : 0 };
            }
            distance += leg.DistanceMeters;
        }
        vertices = mesh.ToArray();
    }

    private void PrepareView(View v, RoutePlan route, BuildingRasterizer? depth, int depthPixel)
    {
        strokes.Clear(); stops.Clear(); shown.Rewind(); behind.Rewind();
        foreach (var item in completed) item.Path.Dispose();
        completed.Clear();
        int width = (v.Width + v.Pixel - 1) / v.Pixel, height = (v.Height + v.Pixel - 1) / v.Pixel;
        var cam = v.Camera;
        double world = cam.WorldSize / v.Pixel;
        var (sin, cos) = Math.SinCos(cam.Bearing * Math.PI / 180);
        double tilt = Math.Cos(cam.PitchRadians);
        var cells = cam.TileBounds(v.Width, v.Height, 1);
        float depthRatio = v.Pixel / (float)depthPixel;
        (double X, double Y, double W) Project(Vertex p, int copy)
        {
            double x = (p.X + copy - cam.CenterX) * world, y = (p.Y - cam.CenterY) * world;
            double u=x*cos+y*sin, forward=-x*sin+y*cos, z=-cam.TargetPixels/v.Pixel;
            double w=cam.Perspective ? 1-(forward*Math.Sin(cam.PitchRadians)+z*tilt)*v.Pixel/MapCamera.Focal(v.Height) : 1;
            return (v.Width*.5/v.Pixel*w+u, v.Height*.5/v.Pixel*w+forward*tilt-z*Math.Sin(cam.PitchRadians), w);
        }
        for (int i = 1; i < vertices.Length; i++)
        {
            var a = vertices[i - 1]; var b = vertices[i];
            int first = (int)Math.Ceiling(cells.Left - Math.Max(a.X, b.X));
            int last = (int)Math.Floor(cells.Right + 1 - Math.Min(a.X, b.X));
            for (int wrap = first; wrap <= last; wrap++)
            {
                var ha = Project(a, wrap); var hb = Project(b, wrap);
                double from=0,to=1,dw=hb.W-ha.W;
                if (!Clip(-dw,ha.W-MapCamera.Near,ref from,ref to) || !Clip(dw,cam.FarLimit(v.Height)-ha.W,ref from,ref to)) continue;
                double aw=ha.W+dw*from, bw=ha.W+dw*to;
                var sa=(X:(ha.X+(hb.X-ha.X)*from)/aw,Y:(ha.Y+(hb.Y-ha.Y)*from)/aw);
                var sb=(X:(ha.X+(hb.X-ha.X)*to)/bw,Y:(ha.Y+(hb.Y-ha.Y)*to)/bw);
                double dx = sb.X - sa.X, dy = sb.Y - sa.Y, lo = 0, hi = 1;
                if (!Clip(-dx, sa.X + 5, ref lo, ref hi) || !Clip(dx, width + 5 - sa.X, ref lo, ref hi) ||
                    !Clip(-dy, sa.Y + 5, ref lo, ref hi) || !Clip(dy, height + 5 - sa.Y, ref lo, ref hi)) continue;
                int samples = depth is null ? 1 : Math.Max(1, (int)Math.Ceiling((hi - lo) * Math.Max(Math.Abs(dx), Math.Abs(dy)) / 2.5));
                double start = lo;
                bool hidden = false;
                for (int n = 0; n < samples; n++)
                {
                    double t = lo + (hi - lo) * (n + .5) / samples;
                    bool covered = depth?.Covers((float)(sa.X + dx * t) * depthRatio, (float)(sa.Y + dy * t) * depthRatio) == true;
                    if (n != 0 && hidden != covered)
                    {
                        double edge = lo + (hi - lo) * n / samples;
                        Append(start, edge, hidden);
                        start = edge;
                    }
                    hidden = covered;
                }
                Append(start, hi, hidden);

                void Append(double t0, double t1, bool obscured)
                {
                    var p = new SKPoint((float)(sa.X + dx * t0), (float)(sa.Y + dy * t0));
                    var q = new SKPoint((float)(sa.X + dx * t1), (float)(sa.Y + dy * t1));
                    if (p == q) return;
                    var path = obscured ? behind : shown;
                    Segment(path, p, q);
                    double m0=from+(to-from)*t0*aw/((1-t0)*bw+t0*aw);
                    double m1=from+(to-from)*t1*aw/((1-t1)*bw+t1*aw);
                    strokes.Add(new(p,q,a.Distance+(b.Distance-a.Distance)*m0,a.Distance+(b.Distance-a.Distance)*m1,
                        obscured,(float)(ha.W+dw*m0),(float)(ha.W+dw*m1)));
                }
            }
        }
        // Sort clipped runs by route station (world copies can share a station). Completed
        // chunks replay as native paths; only the current chunk needs rebuilding during travel.
        strokes.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        for (int i = 0; i < strokes.Count; i += 96)
        {
            var path = new SKPath();
            double end = 0;
            for (int j = i; j < Math.Min(i + 96, strokes.Count); j++)
            {
                var s = strokes[j]; end = Math.Max(end, s.End);
                if (!s.Hidden) Segment(path, s.A, s.B);
            }
            completed.Add((path, end));
        }

        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var draw = new SKCanvas(bitmap))
        {
            draw.Clear(SKColors.Transparent);
            float body = Math.Max(1, 3.8f / v.Pixel);
            Pen(0x80344c54, body + .5f); draw.DrawPath(behind, paint);
            Pen(0xd9d8f6dc, Math.Max(1, body * .48f)); draw.DrawPath(behind, paint);
            Pen(0xff213439, body + Math.Max(1, 3f / v.Pixel)); draw.DrawPath(shown, paint);
            Pen(RouteColor(route.Mode), body); draw.DrawPath(shown, paint);
        }
        bitmap.SetImmutable();
        lineImage?.Dispose();
        lineImage = SKImage.FromBitmap(bitmap);
        AddStop(route.Geometry[0]);
        for (int i = 0; i + 1 < route.Legs.Count; i++)
            if (route.Legs[i].Geometry.Count > 0) AddStop(route.Legs[i].Geometry[^1]);
        AddStop(route.Geometry[^1]);

        void AddStop(RoutePoint p)
        {
            bool inFrustum=cam.TryProject(p.Latitude,p.Longitude,0,v.Width,v.Height,out var screen);
            stops.Add(inFrustum ? new((float)Math.Round(screen.X/v.Pixel),(float)Math.Round(screen.Y/v.Pixel)) : new(-100,-100));
        }
    }

    private void DrawProgress(SKCanvas canvas, double fraction)
    {
        if (fraction <= 0) return;
        Pen(0xff609d92, Math.Max(1, 2.2f / view!.Value.Pixel));
        int first = 0;
        foreach (var chunk in completed)
        {
            if (fraction < chunk.End) break;
            canvas.DrawPath(chunk.Path, paint);
            first += 96;
        }
        partial.Rewind();
        for (int j = first; j < strokes.Count; j++)
        {
            var s = strokes[j];
            if (s.Start > fraction) break;
            if (s.Hidden) continue;
            float t = s.End <= fraction || s.End <= s.Start ? 1 : (float)((fraction - s.Start) / (s.End - s.Start));
            t=t*s.WeightB/((1-t)*s.WeightA+t*s.WeightB);
            var end = new SKPoint(s.A.X + (s.B.X - s.A.X) * t, s.A.Y + (s.B.Y - s.A.Y) * t);
            Segment(partial, s.A, end);
        }
        canvas.DrawPath(partial, paint);
    }

    private void DrawStop(SKCanvas canvas, SKPoint p, bool origin, bool finish, int pixel)
    {
        if (p.X < -16 || p.Y < -16 || p.X > view!.Value.Width / pixel + 16 || p.Y > view.Value.Height / pixel + 16) return;
        paint.Style = SKPaintStyle.Fill;
        void Block(float x, float y, int w, int h, uint color)
        { paint.Color = new(color); canvas.DrawRect(p.X + x, p.Y + y, w, h, paint); }
        if (!origin && !finish)
        {
            Block(-3, -3, 6, 6, 0xff263d43); Block(-2, -2, 4, 4, 0xfff0bb71); return;
        }
        Block(-1, -10, 2, 11, 0xff20343c); Block(-3, 0, 6, 2, 0xff26393c);
        Block(1, -10, 8, 6, 0xff254148);
        if (origin) { Block(1, -9, 6, 3, 0xff7de0ad); Block(1, -9, 4, 1, 0xffd7f7c9); }
        else
        {
            Block(1, -9, 6, 4, 0xfff6edd1);
            Block(1, -9, 2, 2, 0xff344154); Block(5, -9, 2, 2, 0xff344154); Block(3, -7, 2, 2, 0xff344154);
        }
    }

    private void Pen(uint color, float width) { paint.Style = SKPaintStyle.Stroke; paint.Color = new(color); paint.StrokeWidth = width; }
    private static void Segment(SKPath path, SKPoint p, SKPoint q) { path.MoveTo(p); path.LineTo(q); }
    private static bool Clip(double p, double q, ref double lo, ref double hi)
    {
        if (p == 0) return q >= 0;
        double t = q / p;
        if (p < 0) lo = Math.Max(lo, t); else hi = Math.Min(hi, t);
        return lo <= hi;
    }

    internal static uint RouteColor(TravelMode mode) => mode switch
    {
        TravelMode.Walk => 0xffb4df79, TravelMode.Bike => 0xff67dba8, TravelMode.Drive => 0xff65cfee,
        TravelMode.Train => 0xffe9a2e1, TravelMode.Boat => 0xff85dcea, _ => 0xffffc37a
    };

    public void Dispose()
    {
        lineImage?.Dispose(); partial.Dispose(); shown.Dispose(); behind.Dispose(); paint.Dispose(); sprite.Dispose();
        foreach (var chunk in completed) chunk.Path.Dispose();
    }
}
