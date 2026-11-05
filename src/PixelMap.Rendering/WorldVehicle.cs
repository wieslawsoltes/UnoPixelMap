using PixelMap.Routing;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>Physical, metre-sized models for close free-roaming cameras. The map's
/// homogeneous clipping and terrain datum also apply to these animated models.</summary>
internal sealed class WorldVehicle : IDisposable
{
    private readonly record struct Point(float Forward, float Right, float Up);
    private readonly record struct Face(H A, H B, H C, H D, uint Color, float Distance);
    private readonly List<Face> faces = new(256);
    private readonly SKPath path = new();
    private readonly SKPaint paint = new() { Style = SKPaintStyle.Fill };
    private double originX, originY, originAltitude, cameraAltitude, targetZ, metres, focal;
    private double headingSin, headingCos, bearingSin, bearingCos, pitchSin, pitchCos;
    private float width, height, near, far;
    private bool perspective;
    private MapRenderStyle style;

    internal void Draw(SKCanvas canvas, MapCamera camera, int viewportWidth, int viewportHeight, int pixel,
        TravelMode mode, VehiclePose pose, MapRenderStyle renderStyle, TerrainOptions? terrain,
        TerrainSurface? terrainMesh, Func<float, float, double, bool> visible)
    {
        style = renderStyle;
        width = viewportWidth / (float)pixel;
        height = viewportHeight / (float)pixel;
        perspective = camera.Perspective;
        near = (float)camera.NearLimit(viewportHeight);
        far = (float)camera.FarLimit(viewportHeight);
        focal = MapCamera.Focal(viewportHeight) / pixel;
        double world = camera.WorldSize / pixel;
        originX = MapCamera.WrapOffset((pose.Point.Longitude + 180) / 360 - camera.CenterX) * world;
        originY = (MapCamera.MercatorY(pose.Point.Latitude) - camera.CenterY) * world;
        metres = world / (MapCamera.EarthCircumference *
            Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180));
        originAltitude = terrain?.RenderAltitude(camera, pose.Point.Latitude, pose.Point.Longitude,
            pose.AltitudeMeters, terrainMesh) ?? pose.AltitudeMeters;
        cameraAltitude = camera.GroundAltitudeMeters;
        targetZ = camera.TargetPixels / pixel;
        (headingSin, headingCos) = Math.SinCos(pose.Bearing * Math.PI / 180);
        (bearingSin, bearingCos) = Math.SinCos(camera.Bearing * Math.PI / 180);
        (pitchSin, pitchCos) = Math.SinCos(camera.PitchRadians);
        paint.IsAntialias = MapRendering.Smooth(style);
        faces.Clear();
        float phase = pose.Moving ? (float)Math.Sin(pose.AnimationSeconds * 9) : 0;
        switch (mode)
        {
            case TravelMode.Drive: Car(); break;
            case TravelMode.Boat: Boat(); break;
            case TravelMode.Flight: Plane(); break;
            case TravelMode.Train: Train(); break;
            case TravelMode.Bike: Bike(phase); break;
            default: Walker(phase); break;
        }
        faces.Sort(static (left, right) => right.Distance.CompareTo(left.Distance));
        foreach (var face in faces) DrawFace(canvas, face, visible);
    }

    private H Project(Point point)
    {
        double x = originX + (point.Forward * headingSin + point.Right * headingCos) * metres;
        double y = originY + (-point.Forward * headingCos + point.Right * headingSin) * metres;
        double u = x * bearingCos + y * bearingSin, v = -x * bearingSin + y * bearingCos;
        double altitude = originAltitude + point.Up;
        double z = (altitude - cameraAltitude) * metres - targetZ;
        double weight = perspective ? 1 - (v * pitchSin + z * pitchCos) / focal : 1;
        return new((float)(width * .5 * weight + u), (float)(height * .5 * weight + v * pitchCos - z * pitchSin),
            (float)weight, (float)altitude, (float)(-(v * pitchSin + z * pitchCos)));
    }

    private void Quad(Point a, Point b, Point c, Point d, uint color, float shade = 1)
    {
        var pa = Project(a); var pb = Project(b); var pc = Project(c); var pd = Project(d);
        float distance = perspective ? pa.W + pb.W + pc.W + pd.W : pa.U + pb.U + pc.U + pd.U;
        faces.Add(new(pa, pb, pc, pd, PixelPalette.Mix(0xff000000, color, shade), distance));
    }

    private void Box(float aft, float fore, float left, float right, float bottom, float top, uint color)
    {
        Quad(new(aft, left, bottom), new(fore, left, bottom), new(fore, left, top), new(aft, left, top), color, .72f);
        Quad(new(fore, right, bottom), new(aft, right, bottom), new(aft, right, top), new(fore, right, top), color, .86f);
        Quad(new(aft, right, bottom), new(aft, left, bottom), new(aft, left, top), new(aft, right, top), color, .64f);
        Quad(new(fore, left, bottom), new(fore, right, bottom), new(fore, right, top), new(fore, left, top), color, .9f);
        Quad(new(aft, left, top), new(fore, left, top), new(fore, right, top), new(aft, right, top), color);
        Quad(new(aft, right, bottom), new(fore, right, bottom), new(fore, left, bottom), new(aft, left, bottom), color, .55f);
    }

    private void DrawFace(SKCanvas canvas, Face face, Func<float, float, double, bool> visible)
    {
        Span<H> polygon = stackalloc H[12];
        Span<H> clipped = stackalloc H[12];
        polygon[0] = face.A; polygon[1] = face.B; polygon[2] = face.C; polygon[3] = face.D;
        int count = 4;
        for (int plane = 0; plane < 6 && count >= 3; plane++)
        {
            int next = 0;
            var previous = polygon[count - 1];
            float before = TileTransform.Plane(plane, previous, width, height, far, near);
            for (int i = 0; i < count; i++)
            {
                var current = polygon[i];
                float after = TileTransform.Plane(plane, current, width, height, far, near);
                if ((before >= 0) != (after >= 0)) clipped[next++] = H.Lerp(previous, current, before / (before - after));
                if (after >= 0) clipped[next++] = current;
                previous = current;
                before = after;
            }
            count = next;
            clipped[..count].CopyTo(polygon);
        }
        if (count < 3) return;
        // Subdivide only where a large face crosses scenery. This keeps close camera
        // occlusion bounded without constructing another full-screen depth buffer.
        paint.Color = new(face.Color);
        path.Rewind();
        for (int i = 1; i + 1 < count; i++) AddVisible(polygon[0], polygon[i], polygon[i + 1], 0, visible);
        canvas.DrawPath(path, paint);
    }

    private void AddVisible(H a, H b, H c, int level, Func<float, float, double, bool> visible)
    {
        var center = H.Lerp(H.Lerp(a, b, .5f), c, 1f / 3);
        bool va = visible(a.X / a.W, a.Y / a.W, a.Z + .025);
        bool vb = visible(b.X / b.W, b.Y / b.W, b.Z + .025);
        bool vc = visible(c.X / c.W, c.Y / c.W, c.Z + .025);
        bool vm = visible(center.X / center.W, center.Y / center.W, center.Z + .025);
        if (va && vb && vc && vm)
        {
            path.MoveTo(a.Point); path.LineTo(b.Point); path.LineTo(c.Point); path.Close();
            return;
        }
        float extent = Math.Max(Math.Abs(a.X / a.W - b.X / b.W), Math.Abs(a.Y / a.W - b.Y / b.W));
        extent = Math.Max(extent, Math.Max(Math.Abs(a.X / a.W - c.X / c.W), Math.Abs(a.Y / a.W - c.Y / c.W)));
        if (level >= 6 || extent <= 2)
        {
            if (vm) { path.MoveTo(a.Point); path.LineTo(b.Point); path.LineTo(c.Point); path.Close(); }
            return;
        }
        if (!va && !vb && !vc && !vm && level >= 1) return;
        var ab = H.Lerp(a, b, .5f); var bc = H.Lerp(b, c, .5f); var ca = H.Lerp(c, a, .5f);
        AddVisible(a, ab, ca, level + 1, visible);
        AddVisible(ab, b, bc, level + 1, visible);
        AddVisible(ca, bc, c, level + 1, visible);
        AddVisible(ab, bc, ca, level + 1, visible);
    }

    private void Car()
    {
        Box(-2.2f, 2.3f, -.86f, .86f, .36f, .96f, 0xffed9351);
        Box(-1.15f, .95f, -.73f, .73f, .96f, 1.52f, 0xff81bfcd);
        Box(-.95f, .6f, -.75f, .75f, 1.5f, 1.61f, 0xfff4bc68);
        for (int axle = 0; axle < 2; axle++)
        for (int side = -1; side <= 1; side += 2)
        {
            float end = axle == 0 ? -1.35f : 1.4f;
            Box(end - .34f, end + .34f, side * .88f - .14f, side * .88f + .14f, .02f, .7f, 0xff263742);
            Box(end - .17f, end + .17f, side * 1.025f - .025f, side * 1.025f + .025f, .2f, .52f, 0xffa9b7ba);
            Box(2.28f, 2.32f, side * .57f - .19f, side * .57f + .19f, .65f, .84f, 0xfffff2bc);
            Box(-2.23f, -2.19f, side * .59f - .17f, side * .59f + .17f, .65f, .83f, 0xffe95c59);
        }
        Studs(-.7f, .3f, -.5f, .5f, 1.61f);
    }

    private void Walker(float phase)
    {
        bool toy = style == MapRenderStyle.Roblox;
        float half = toy ? .3f : .25f;
        Box(-.15f, .15f, -half, half, .72f, 1.4f, 0xff4ab6ca);
        Box(-.16f, .18f, -.18f, .18f, 1.42f, 1.8f, 0xffefc58f);
        Box(-.18f, .16f, -.2f, .2f, 1.75f, 1.86f, 0xff495c67);
        for (int side = -1; side <= 1; side += 2)
        {
            float stride = phase * side * .22f;
            Box(-.12f + stride, .12f + stride, side * .14f - .11f, side * .14f + .11f, .08f, .76f, 0xff3c526e);
            Box(-.15f + stride, .24f + stride, side * .14f - .12f, side * .14f + .12f, 0, .16f, 0xff283c46);
            Box(-.12f - stride, .12f - stride, side * (half + .11f) - .1f, side * (half + .11f) + .1f, .8f, 1.38f, 0xffefc58f);
            Box(.178f, .185f, side * .085f - .023f, side * .085f + .023f, 1.61f, 1.66f, 0xff2c424e);
        }
    }

    private void Bike(float phase)
    {
        for (int end = -1; end <= 1; end += 2)
        {
            Box(end * .68f - .29f, end * .68f + .29f, -.055f, .055f, .02f, .62f, 0xff273d48);
            Box(end * .68f - .19f, end * .68f + .19f, -.06f, .06f, .12f, .52f, 0xffbcc5c4);
        }
        Box(-.6f, .7f, -.06f, .06f, .36f, .53f, 0xff5fc997);
        Box(-.2f, -.07f, -.07f, .07f, .48f, .94f, 0xff5fc997);
        Box(.58f, .69f, -.32f, .32f, .95f, 1.03f, 0xff283b43);
        Box(-.28f, .16f, -.19f, .19f, .93f, 1.46f, 0xffec8d61);
        Box(.05f, .36f, -.16f, .16f, 1.45f, 1.79f, 0xffefc58f);
        for (int side = -1; side <= 1; side += 2)
            Box(-.18f + phase * side * .12f, .1f + phase * side * .12f, side * .2f - .08f, side * .2f + .08f, .43f, 1.02f, 0xff39576b);
    }

    private void Boat()
    {
        Box(-3.7f, 2.9f, -1.15f, 1.15f, .04f, .75f, 0xfff3e9c9);
        Quad(new(2.9f, -1.15f, .75f), new(4.1f, 0, .75f), new(2.9f, 1.15f, .75f), new(2.9f, -1.15f, .75f), 0xfff3e9c9);
        Quad(new(2.9f, -1.15f, .04f), new(4.1f, 0, .15f), new(4.1f, 0, .75f), new(2.9f, -1.15f, .75f), 0xff77a7be, .8f);
        Quad(new(4.1f, 0, .15f), new(2.9f, 1.15f, .04f), new(2.9f, 1.15f, .75f), new(4.1f, 0, .75f), 0xff77a7be);
        Box(-1.7f, .8f, -.85f, .85f, .75f, 2.05f, 0xff80b9c9);
        Box(-1.9f, 1, -.95f, .95f, 2.05f, 2.22f, 0xffefbd7c);
        Box(-2.4f, -2.3f, -.04f, .04f, .75f, 3.2f, 0xff435b67);
        Box(-2.42f, -2.28f, 0, .6f, 2.72f, 3.15f, 0xffe77f66);
        Studs(-1.5f, .6f, -.7f, .7f, 2.22f);
    }

    private void Plane()
    {
        Box(-5.2f, 4.5f, -.56f, .56f, -.3f, .55f, 0xfff4ecd6);
        Box(2.4f, 3.7f, -.5f, .5f, .56f, .85f, 0xff79b8ca);
        Box(-1.6f, .6f, -6, 6, -.1f, .12f, 0xffc2d9dc);
        Box(-5.3f, -3.8f, -2.3f, 2.3f, .18f, .36f, 0xffeb9b69);
        Box(-5.1f, -3.9f, -.09f, .09f, .4f, 2.1f, 0xffda7b58);
        for (int side = -1; side <= 1; side += 2)
        {
            Box(-.8f, 1.1f, side * 2.3f - .25f, side * 2.3f + .25f, -.6f, -.05f, 0xff586e7c);
            Box(-1.55f, -.85f, side * 5.95f - .08f, side * 5.95f + .08f, .12f, .24f,
                side < 0 ? 0xffef7067 : 0xff77dca9);
        }
    }

    private void Train()
    {
        Box(-8, 8, -1.35f, 1.35f, .45f, 3.35f, 0xffd37b9d);
        Box(-7.7f, 7.7f, -1.27f, 1.27f, 3.35f, 3.58f, 0xfff0b1bf);
        for (int end = -1; end <= 1; end += 2)
        {
            Box(end * 5.7f - 1, end * 5.7f + 1, -1.4f, 1.4f, .05f, .73f, 0xff334752);
            Box(end * 7.98f - .025f, end * 7.98f + .025f, -1.05f, 1.05f, 1.9f, 2.95f, 0xff84bed1);
        }
        for (int window = -6; window <= 6; window += 2)
        for (int side = -1; side <= 1; side += 2)
            Box(window - .58f, window + .58f, side * 1.355f - .025f, side * 1.355f + .025f, 1.8f, 2.83f, 0xff8ac2cc);
        Studs(-6, 6, -.75f, .75f, 3.58f);
    }

    private void Studs(float aft, float fore, float left, float right, float top)
    {
        if (style != MapRenderStyle.Roblox) return;
        for (float f = aft; f <= fore; f += .6f)
        for (float r = left; r <= right; r += .6f)
            Box(f - .11f, f + .11f, r - .11f, r + .11f, top, top + .08f, 0xffe8c99b);
    }

    public void Dispose() { path.Dispose(); paint.Dispose(); }
}
