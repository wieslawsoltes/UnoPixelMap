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
                && (CurrentTerrain is { Relief: true } || camera.Zoom >= 12 && camera.Pitch > 3);
            long revision = aligned ? frame : -1;
            if (aligned && lastView.GpuGeneration == GeometryGpuGeneration && activeGpuDepth is { } gpuDepth)
                revision = gpuDepth.Revision;
            (routeOverlay ??= new()).Draw(canvas, width, height, MapRendering.PixelSize(renderStyle, pixelSize), camera, plan, vehicle,
                aligned ? buildings : null, Math.Max(1, lastView.PixelSize), revision, navigationTerrain, aligned ? reliefSurface : null, renderStyle);
        }
    }

    /// <summary>Draw a free-roaming, world-scale vehicle and its effects without a route.</summary>
    public void DrawVehicle(SKCanvas canvas, int width, int height, MapCamera camera,
        TravelMode mode, VehiclePose vehicle, int pixelSize = 2)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (width <= 0 || height <= 0) return;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            bool aligned = lastPicture is not null && lastView.Camera == camera &&
                lastView.Width == width && lastView.Height == height;
            (routeOverlay ??= new()).DrawVehicle(canvas, width, height, MapRendering.PixelSize(renderStyle, pixelSize),
                camera, mode, vehicle, aligned ? buildings : null, Math.Max(1, lastView.PixelSize),
                navigationTerrain, aligned ? reliefSurface : null, renderStyle);
        }
    }
}

internal sealed class RouteOverlay : IDisposable
{
    private readonly record struct Vertex(double X, double Y, double Distance, double Elevation = 0);
    private readonly record struct Stroke(SKPoint A, SKPoint B, double Start, double End, bool Hidden, float WeightA, float WeightB);
    private readonly record struct View(int Width, int Height, int Pixel, MapCamera Camera, long Revision, TerrainOptions? Terrain, MapRenderStyle RenderStyle);

    private RoutePlan? plan;
    private TerrainOptions? meshTerrain;
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
    private readonly WorldVehicle worldVehicle = new();
    private TravelMode? freeMode;
    private readonly List<SKPoint> stops = [];
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    internal void Draw(SKCanvas canvas, int w, int h, int pixel, MapCamera camera, RoutePlan next, VehiclePose? vehicle,
        BuildingRasterizer? depth, int depthPixel, long revision, TerrainOptions? terrain, TerrainSurface? terrainMesh, MapRenderStyle renderStyle)
    {
        freeMode = null;
        paint.IsAntialias = MapRendering.Smooth(renderStyle);
        bool changedPlan = !ReferenceEquals(plan, next);
        if (changedPlan || meshTerrain != terrain)
        {
            plan = next;
            meshTerrain = terrain;
            PrepareMesh(next);
            if (terrain is { Relief: true }) DrapeMesh(terrain);
            view = null;
            if (changedPlan) effects.Reset();
        }
        if (vertices.Length == 0) return;
        var key = new View(w, h, pixel, camera, revision, terrain, renderStyle);
        if (view != key)
        {
            PrepareView(key, next, depth, depthPixel, terrainMesh);
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
            DrawVehicleCore(canvas, w, h, pixel, camera, next.Mode, sample, depth, depthPixel, terrain, terrainMesh, renderStyle, false);
        else effects.Reset();
        canvas.Restore();
    }

    internal void DrawVehicle(SKCanvas canvas, int w, int h, int pixel, MapCamera camera, TravelMode mode,
        VehiclePose sample, BuildingRasterizer? depth, int depthPixel, TerrainOptions? terrain,
        TerrainSurface? terrainMesh, MapRenderStyle renderStyle)
    {
        if (plan is not null || freeMode != mode)
        {
            effects.Reset();
            plan = null;
            freeMode = mode;
            view = null;
            vertices = [];
            lineImage?.Dispose();
            lineImage = null;
        }
        canvas.Save();
        canvas.ClipRect(new(0, 0, w, h));
        canvas.Scale(pixel);
        DrawVehicleCore(canvas, w, h, pixel, camera, mode, sample, depth, depthPixel, terrain, terrainMesh, renderStyle, true);
        canvas.Restore();
    }

    private void DrawVehicleCore(SKCanvas canvas, int w, int h, int pixel, MapCamera camera, TravelMode mode,
        VehiclePose sample, BuildingRasterizer? depth, int depthPixel, TerrainOptions? terrain,
        TerrainSurface? terrainMesh, MapRenderStyle renderStyle, bool worldScale)
    {
        bool Visible(float x, float y, double altitude) => depth is null ||
            !depth.CoversAt(x * pixel / depthPixel, y * pixel / depthPixel, (float)(altitude - camera.GroundAltitudeMeters));
        paint.IsAntialias = MapRendering.Smooth(renderStyle);
        effects.Advance(mode, sample);
        effects.Draw(canvas, camera, w, h, pixel, mode, paint, Visible, terrain, terrainMesh);
        if (worldScale)
        {
            worldVehicle.Draw(canvas, camera, w, h, pixel, mode, sample, renderStyle, terrain, terrainMesh, Visible);
            return;
        }
        bool inFrustum = VehicleEffects.Position(camera,sample.Point,sample.AltitudeMeters,w,h,pixel,out float x,out float y,out float lift,terrain,terrainMesh);
        if (inFrustum && x > -32 && x < w / pixel + 32 && y - lift > -32 && y - lift < h / pixel + 32)
        {
            double renderedAltitude = terrain?.RenderAltitude(camera, sample.Point.Latitude, sample.Point.Longitude, sample.AltitudeMeters, terrainMesh) ?? sample.AltitudeMeters;
            bool visible = Visible(x, y - lift, renderedAltitude + (terrain is { Relief: true } ? 2 : 0));
            double surface = terrain?.Elevation.SampleSurfaceMeters(sample.Point.Latitude, sample.Point.Longitude) ?? 0;
            double footAltitude = terrain?.RenderAltitude(camera, sample.Point.Latitude, sample.Point.Longitude, surface, terrainMesh) ?? 0;
            SKPoint? ground = camera.TryProject(sample.Point.Latitude, sample.Point.Longitude, footAltitude, w, h, out var foot)
                ? new((float)(foot.X / pixel), (float)(foot.Y / pixel)) : null;
            if (terrain is { Relief: true } && ground is { } shadow && !Visible(shadow.X, shadow.Y, footAltitude + 2))
                ground = null;
            var axes = VehicleEffects.Axes(camera, sample.Point, sample.AltitudeMeters, sample.Bearing, w, h, terrain, mode != TravelMode.Flight, terrainMesh);
            sprite.Draw(canvas, new(x, y - lift), ground, mode, axes, effects.Time, sample.Moving, visible, renderStyle);
        }
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

    private void DrapeMesh(TerrainOptions terrain)
    {
        if (vertices.Length == 0) return;
        // Sample long provider segments as well as their endpoints. Storage and traversal
        // stay bounded for continental and transoceanic routes.
        double length = 0;
        for (int i = 1; i < vertices.Length; i++)
            length += Math.Sqrt(Math.Pow(vertices[i].X - vertices[i - 1].X, 2) + Math.Pow(vertices[i].Y - vertices[i - 1].Y, 2));
        double cell = 1d / ((1 << Math.Min(terrain.Elevation.MaximumZoom, 20)) * 256d);
        double spacing = Math.Max(cell * .75, length / 32768);
        var draped = new List<Vertex>(Math.Min(65536, vertices.Length + 32768));
        draped.Add(Sample(vertices[0]));
        for (int i = 1; i < vertices.Length; i++)
        {
            var a = vertices[i - 1]; var b = vertices[i];
            double span = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            int count = Math.Clamp((int)Math.Ceiling(span / spacing), 1, 32768);
            for (int j = 1; j <= count; j++)
            {
                double t = j / (double)count;
                draped.Add(Sample(new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
                    a.Distance + (b.Distance - a.Distance) * t)));
            }
        }
        vertices = draped.ToArray();
        Vertex Sample(Vertex point) => point with { Elevation = terrain.Elevation.SampleSurfaceMercator(point.X, point.Y) };
    }

    private void PrepareView(View v, RoutePlan route, BuildingRasterizer? depth, int depthPixel, TerrainSurface? terrainMesh)
    {
        strokes.Clear(); stops.Clear(); shown.Rewind(); behind.Rewind();
        foreach (var item in completed) item.Path.Dispose();
        completed.Clear();
        int width = (v.Width + v.Pixel - 1) / v.Pixel, height = (v.Height + v.Pixel - 1) / v.Pixel;
        var cam = v.Camera;
        double world = cam.WorldSize / v.Pixel;
        var (sin, cos) = Math.SinCos(cam.Bearing * Math.PI / 180);
        double tilt = Math.Cos(cam.PitchRadians);
        double centerX = cam.CenterX, centerY = cam.CenterY;
        double groundZ = -cam.TargetPixels / v.Pixel;
        double metresToPixel = world / (MapCamera.EarthCircumference * Math.Cos(cam.Latitude * Math.PI / 180));
        bool relief = v.Terrain is { Relief: true };
        double LocalAltitude(Vertex point)
        {
            if (!relief) return 0;
            double surface = terrainMesh is not null && terrainMesh.TryHeight(point.X, point.Y, out double height)
                ? height : point.Elevation;
            return v.Terrain!.LocalAltitude(cam, surface) + 2;
        }
        double pitchSin = Math.Sin(cam.PitchRadians);
        double focal = MapCamera.Focal(v.Height), far = cam.FarLimit(v.Height), near = cam.NearLimit(v.Height);
        var cells = cam.TileBounds(v.Width, v.Height, 1);
        float depthRatio = v.Pixel / (float)depthPixel;
        (double X, double Y, double W) Project(Vertex p, int copy, double altitude)
        {
            double x = (p.X + copy - centerX) * world, y = (p.Y - centerY) * world;
            double u=x*cos+y*sin, forward=-x*sin+y*cos;
            double z = groundZ + altitude * metresToPixel;
            double w=cam.Perspective ? 1-(forward*pitchSin+z*tilt)*v.Pixel/focal : 1;
            return (v.Width*.5/v.Pixel*w+u, v.Height*.5/v.Pixel*w+forward*tilt-z*pitchSin, w);
        }
        for (int i = 1; i < vertices.Length; i++)
        {
            var a = vertices[i - 1]; var b = vertices[i];
            double altitudeA = LocalAltitude(a), altitudeB = LocalAltitude(b);
            int first = (int)Math.Ceiling(cells.Left - Math.Max(a.X, b.X));
            int last = (int)Math.Floor(cells.Right + 1 - Math.Min(a.X, b.X));
            for (int wrap = first; wrap <= last; wrap++)
            {
                var ha = Project(a, wrap, altitudeA); var hb = Project(b, wrap, altitudeB);
                double from=0,to=1,dw=hb.W-ha.W;
                if (!Clip(-dw,ha.W-near,ref from,ref to) || !Clip(dw,far-ha.W,ref from,ref to)) continue;
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
                    double station = from + (to - from) * t * aw / ((1 - t) * bw + t * aw);
                    float altitude = (float)(altitudeA + (altitudeB - altitudeA) * station);
                    float px = (float)(sa.X + dx * t) * depthRatio, py = (float)(sa.Y + dy * t) * depthRatio;
                    bool covered = relief ? depth?.CoversAt(px, py, altitude) == true : depth?.Covers(px, py) == true;
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
            double surface = v.Terrain?.Elevation.SampleSurfaceMeters(p.Latitude, p.Longitude) ?? 0;
            double altitude = v.Terrain?.RenderAltitude(cam, p.Latitude, p.Longitude, surface, terrainMesh) ?? 0;
            bool inFrustum=cam.TryProject(p.Latitude,p.Longitude,altitude,v.Width,v.Height,out var screen);
            if (relief && depth?.CoversAt((float)screen.X / depthPixel, (float)screen.Y / depthPixel,
                (float)(altitude - cam.GroundAltitudeMeters + 2)) == true) inFrustum = false;
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
        lineImage?.Dispose(); partial.Dispose(); shown.Dispose(); behind.Dispose(); paint.Dispose(); sprite.Dispose(); worldVehicle.Dispose();
        foreach (var chunk in completed) chunk.Path.Dispose();
    }
}
