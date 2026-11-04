using PixelMap.Routing;
using SkiaSharp;

namespace PixelMap.Rendering;

internal readonly record struct VehicleAxes(SKPoint Forward, SKPoint Right, SKPoint Up);

/// <summary>Miniature pixel-painted models with world-relative heading. Each vehicle is
/// rasterized on a fixed sprite grid and presented through a nearest-neighbor blit.</summary>
internal sealed class VehicleSprite : IDisposable
{
    private const int Size = 44, Center = Size / 2;
    private const uint Ink = 0xff23343d, Dark = 0xff3a5058, Window = 0xff98ced4, White = 0xfff5efd5;
    private readonly SKBitmap bitmap = new(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul);
    private readonly SKCanvas sprite;
    private readonly SKPaint brush = new() { IsAntialias = false, StrokeCap = SKStrokeCap.Square, StrokeWidth = 1 };
    private readonly SKPath path = new();
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private float gx, gy, rx, ry, ux, uy;
    internal VehicleSprite() => sprite = new(bitmap);

    internal void Draw(SKCanvas canvas, SKPoint position, SKPoint? ground, TravelMode mode,
        VehicleAxes axes, double time, bool moving, bool visible, MapRenderStyle renderStyle = MapRenderStyle.Pixel)
    {
        brush.IsAntialias = MapRendering.Smooth(renderStyle);
        float x = position.X, y = position.Y;
        if (ground is { } shadow && shadow.Y - y > 2)
        {
            brush.Style = SKPaintStyle.Fill;
            brush.Color = new(0x5432454c);
            canvas.DrawRect(MathF.Round(shadow.X - 4), MathF.Round(shadow.Y - 1), 8, 2, brush);
        }
        gx = axes.Forward.X; gy = axes.Forward.Y;
        rx = axes.Right.X; ry = axes.Right.Y;
        ux = axes.Up.X; uy = axes.Up.Y;
        sprite.Clear(SKColors.Transparent);
        int phase = (int)((time % 10) * 8);
        switch (mode)
        {
            case TravelMode.Drive: Car(phase); break;
            case TravelMode.Boat: Boat(phase, moving); break;
            case TravelMode.Flight: Plane(phase); break;
            case TravelMode.Train: Train(phase); break;
            case TravelMode.Bike: Cyclist(phase); break;
            default: Walker(phase); break;
        }
        sprite.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        brush.Color = SKColors.White.WithAlpha(visible ? (byte)255 : (byte)125);
        brush.Style = SKPaintStyle.Fill;
        canvas.DrawImage(image, MathF.Round(x) - Center, MathF.Round(y) - Center, Nearest, brush);
        if (!visible)
        {
            // Keep a small locator above a screened vehicle while allowing the occluding
            // building to remain visible through its silhouette.
            brush.Color = new(0xfff9c76a);
            canvas.DrawRect(MathF.Round(x) - 1, MathF.Round(y) - 10, 3, 1, brush);
        }
    }

    private SKPoint P(float forward, float side, float z = 0) =>
        new(Center + forward * gx + side * rx + z * ux, Center + forward * gy + side * ry + z * uy);

    private void Quad(SKPoint a, SKPoint b, SKPoint c, SKPoint d, uint color)
    {
        path.Rewind(); path.MoveTo(a); path.LineTo(b); path.LineTo(c); path.LineTo(d); path.Close();
        brush.Color = new(color); brush.Style = SKPaintStyle.Fill; sprite.DrawPath(path, brush);
    }
    private void Deck(float aft, float fore, float left, float right, float z, uint color) =>
        Quad(P(aft, left, z), P(fore, left, z), P(fore, right, z), P(aft, right, z), color);

    private void Box(float aft, float fore, float left, float right, float baseZ, float top, uint side, uint roof)
    {
        // Faces farther down-screen are visible; draw these after the opposite face.
        float lateral = ry < 0 ? left : right, end = gy < 0 ? aft : fore;
        Quad(P(aft,lateral,baseZ), P(fore,lateral,baseZ), P(fore,lateral,top), P(aft,lateral,top), side);
        Quad(P(end,left,baseZ), P(end,right,baseZ), P(end,right,top), P(end,left,top), side);
        Deck(aft, fore, left, right, top, roof);
    }
    private void Line(SKPoint a, SKPoint b, uint color, float width = 1)
    {
        brush.Color = new(color); brush.Style = SKPaintStyle.Stroke; brush.StrokeWidth = width;
        sprite.DrawLine(a, b, brush);
    }
    private void Pixel(SKPoint p, uint color, int w = 1, int h = 1)
    {
        brush.Color = new(color); brush.Style = SKPaintStyle.Fill;
        sprite.DrawRect(MathF.Round(p.X - w * .5f), MathF.Round(p.Y - h * .5f), w, h, brush);
    }

    private void Car(int phase)
    {
        Deck(-6, 6, -3.6f, 3.6f, 0, Ink);
        for (int f = -4; f <= 4; f += 8)
        for (int s = -1; s <= 1; s += 2)
        {
            Line(P(f - 1, s * 3.3f), P(f + 1, s * 3.3f), Ink, 2.3f);
            Pixel(P(f + (phase & 1) * .4f, s * 3.5f, .4f), 0xff889899);
        }
        Box(-6, 6.6f, -2.9f, 2.9f, .8f, 2.1f, 0xffae553e, 0xffeb9b55);
        Box(-3.3f, 2.7f, -2.35f, 2.35f, 2.1f, 3.8f, 0xff37575e, Window);
        Deck(-2.2f, .7f, -2.35f, 2.35f, 4, 0xfff6c477);
        Pixel(P(6, -1.8f, 1.7f), 0xffffffcf, 2); Pixel(P(6, 1.8f, 1.7f), 0xffffffcf, 2);
        Pixel(P(-6, -2.1f, 1.5f), 0xffe45c51); Pixel(P(-6, 2.1f, 1.5f), 0xffe45c51);
    }

    private void Boat(int phase, bool moving)
    {
        Quad(P(-7,-2.8f), P(2,-3.8f), P(8,0), P(-7,2.8f), 0xff244f65);
        Quad(P(-6,-2.8f,1), P(3,-3.2f,1), P(8,0,1), P(3,3.2f,1), White);
        Deck(-6, 3, -2.8f, 2.8f, 1, 0xffe6dcc0);
        Box(-3, 2, -2, 2, 1, 3.8f, 0xff537f90, 0xffe8aa60);
        Deck(-2, 1, -1.7f, 1.7f, 4, 0xfff4ddad);
        Line(P(-3,0,2), P(-3,0,6), Ink); Pixel(P(-3,-.6f,5), 0xffe77062, 2);
        if (moving)
        {
            Line(P(-7,-2), P(-10 - (phase & 1),-4), 0xffb8f3df);
            Line(P(-7,2), P(-10 - (phase & 1),4), 0xffdff9ef);
        }
    }

    private void Plane(int phase)
    {
        Quad(P(-4,-1,1), P(1,-1,1), P(-3,-10,1), P(-5,-10,1), 0xffa6bdce);
        Quad(P(1,1,1), P(-4,1,1), P(-5,10,1), P(-3,10,1), 0xffc1d8dc);
        Quad(P(-8,-4,2), P(-6,0,2), P(-8,4,2), P(-9,0,2), 0xffe89461);
        Quad(P(-9,-1), P(3,-1.8f,2), P(10,0,2), P(3,1.8f,2), 0xfff8f0d9);
        Box(-6, 5, -1.3f, 1.3f, 1, 2.6f, Dark, White);
        Deck(3.1f, 5.3f, -1.25f, 1.25f, 2.8f, 0xff529bb2);
        Line(P(-7,0,2), P(-8,0,5.2f), 0xffc2674b, 2);
        Pixel(P(-4,-9.6f,1), (phase & 7) < 2 ? 0xffff705e : 0xffa64140);
        Pixel(P(-4,9.6f,1), (phase & 7) < 2 ? 0xffb0ffd2 : 0xff539c82);
        // Twin nacelles stay aligned with the fuselage during a bank or map rotation.
        Line(P(-2,-4.4f,.4f), P(.4f,-4.4f,.4f), Ink, 1.3f);
        Line(P(-2,4.4f,.4f), P(.4f,4.4f,.4f), Ink, 1.3f);
    }

    private void Train(int phase)
    {
        Deck(-11, 9, -2.8f, 2.8f, 0, Ink);
        Box(-10.5f, -.8f, -2.4f, 2.4f, .8f, 3.9f, 0xff823d5d, 0xffd986a1);
        Box(.3f, 9, -2.4f, 2.4f, .8f, 3.9f, 0xff743e60, 0xffed9db1);
        float near = ry < 0 ? -2.45f : 2.45f;
        for (int i = -9; i < 9; i += 3) Pixel(P(i,near,2.4f), Window, 2, 1);
        Deck(6, 8.1f, -2, 2, 4, 0xff78a5b5);
        for (int i = -9; i < 6; i += 4)
            Pixel(P(i + (phase & 1) * .3f, near, .3f), Dark, 2);
        Line(P(-6,0,4.2f), P(-4,0,5.7f), Ink); Line(P(-4,0,5.7f), P(-2.8f,0,4.2f), Ink);
        Pixel(P(8.8f,0,2), 0xffffd99e, 2);
    }

    private void Cyclist(int phase)
    {
        float pedal = (phase & 3) < 2 ? 1 : -1;
        for (int i = -3; i <= 3; i += 6)
        {
            Line(P(i - 1,0,.6f), P(i + 1,0,1.8f), Ink, 2);
            Pixel(P(i,0,1.1f), 0xffc8d1b8);
        }
        Line(P(-3,0,1), P(-1,0,3), 0xff4fbd93); Line(P(-1,0,3), P(3,0,1), 0xff4fbd93);
        Line(P(2,0,3.5f), P(3,0,1), Ink);
        Line(P(-.3f,0,3), P(pedal,1,1), Dark); Line(P(-.3f,0,3), P(-pedal,-1,1), Ink);
        Line(P(-.8f,0,3.5f), P(1,0,5.7f), 0xff47ae86, 2.8f);
        Pixel(P(1.3f,0,6.8f), 0xffe9ad79, 2, 2); Pixel(P(1.1f,0,7.4f), 0xffefc85c, 3);
    }
    private void Walker(int phase)
    {
        float step = (phase & 3) < 2 ? 1.4f : -1.4f;
        Line(P(0,-.7f,3), P(step,-.8f,.2f), Dark, 1.5f);
        Line(P(0,.7f,3), P(-step,.8f,.2f), Ink, 1.5f);
        Line(P(0,0,3), P(0,0,5.8f), 0xff75ad65, 3);
        Line(P(0,1.1f,5), P(step,1.25f,3.2f), 0xffcc9365);
        Pixel(P(.1f,0,6.8f), 0xffe8ae7c, 3, 3);
        Pixel(P(0,0,7.8f), 0xff5d493e, 3);
        Pixel(P(-.8f,-.3f,4.8f), 0xffcd9f58, 2, 2);
    }

    public void Dispose() { sprite.Dispose(); bitmap.Dispose(); brush.Dispose(); path.Dispose(); }
}
