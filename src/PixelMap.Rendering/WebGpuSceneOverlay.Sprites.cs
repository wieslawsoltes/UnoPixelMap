using PixelMap.Routing;
using SkiaSharp;

namespace PixelMap.Rendering;

public sealed partial class WebGpuSceneOverlay
{
    private const uint SpriteInk = 0xff23343d, SpriteDark = 0xff3a5058, SpriteWindow = 0xff98ced4, SpriteWhite = 0xfff5efd5;
    private H spriteAnchor;
    private float spriteForwardX, spriteForwardY, spriteRightX, spriteRightY, spriteUpX, spriteUpY;

    private void AppendSpriteVehicle(TravelMode mode, VehiclePose pose)
    {
        double altitude = RenderAltitude(pose.Point, pose.AltitudeMeters);
        spriteAnchor = Project(pose.Point, altitude);
        var ground = GroundPoint(pose.Point, .5);
        if (!frame!.Camera.Perspective && frame.NavigationTerrain is null && pose.AltitudeMeters > 0)
            spriteAnchor = Offset(ground, 0, -VehicleEffects.Lift(frame.Camera, pose.Point, pose.AltitudeMeters, frame.Width, frame.Height, 1))
                with { Z = (float)((altitude - frame.Camera.GroundAltitudeMeters) * frame.PixelsPerMeter) };
        if (!InFrustum(spriteAnchor, 44 * frame.PixelSize)) return;
        float pixel = frame.PixelSize;
        if (!MapRendering.Smooth(frame.RenderStyle))
            spriteAnchor = spriteAnchor with {
                X = MathF.Round(spriteAnchor.X / spriteAnchor.W / pixel) * pixel * spriteAnchor.W,
                Y = MathF.Round(spriteAnchor.Y / spriteAnchor.W / pixel) * pixel * spriteAnchor.W };
        if (InFrustum(ground) && ground.Y / ground.W - spriteAnchor.Y / spriteAnchor.W > 2 * pixel)
            Block(ground, -4 * pixel, -pixel, 8 * pixel, 2 * pixel, 0x5432454c);
        var axes = OverlayAxes(pose.Point, pose.AltitudeMeters, pose.Bearing, mode != TravelMode.Flight);
        spriteForwardX = axes.Forward.X * pixel; spriteForwardY = axes.Forward.Y * pixel;
        spriteRightX = axes.Right.X * pixel; spriteRightY = axes.Right.Y * pixel;
        spriteUpX = axes.Up.X * pixel; spriteUpY = axes.Up.Y * pixel;
        int phase = (int)((particleTime % 10) * 8);
        switch (mode)
        {
            case TravelMode.Drive: SpriteCar(phase); break;
            case TravelMode.Boat: SpriteBoat(phase, pose.Moving); break;
            case TravelMode.Flight: SpritePlane(phase); break;
            case TravelMode.Train: SpriteTrain(phase); break;
            case TravelMode.Bike: SpriteCyclist(phase); break;
            default: SpriteWalker(phase); break;
        }
        Block(spriteAnchor, -pixel, -10 * pixel, 3 * pixel, pixel, 0xfff9c76a, hidden: true);
    }

    private VehicleAxes OverlayAxes(RoutePoint point, double absoluteAltitude, double bearing, bool followsSurface)
    {
        if (frame!.Camera.Perspective || frame.NavigationTerrain is not null)
        {
            double altitude = RenderAltitude(point, absoluteAltitude);
            var origin = Project(point, altitude).Point;
            var forward = point.Move(bearing, 1);
            var right = point.Move(bearing + 90, 1);
            double clearance = absoluteAltitude - PhysicalSurface(point);
            double At(RoutePoint target) => RenderAltitude(target,
                followsSurface && frame.NavigationTerrain is not null ? PhysicalSurface(target) + clearance : absoluteAltitude);
            var f = Project(forward, At(forward)).Point;
            var r = Project(right, At(right)).Point;
            var up = Project(point, altitude + 1).Point;
            double fx = f.X - origin.X, fy = f.Y - origin.Y, rx = r.X - origin.X, ry = r.Y - origin.Y;
            double scale = Math.Max(Math.Sqrt(fx * fx + fy * fy), Math.Sqrt(rx * rx + ry * ry));
            if (scale > 1e-9 && double.IsFinite(scale))
                return new(new((float)(fx / scale), (float)(fy / scale)), new((float)(rx / scale), (float)(ry / scale)),
                    new((float)((up.X - origin.X) / scale), (float)((up.Y - origin.Y) / scale)));
        }
        var (sin, cos) = Math.SinCos((bearing - frame!.Camera.Bearing) * Math.PI / 180);
        float tilt = (float)Math.Max(.48, pitchCos);
        return new(new((float)sin, -(float)cos * tilt), new((float)cos, (float)sin * tilt), new(0, (float)(-.20 - .80 * pitchSin)));
    }

    private H SpriteP(float forward, float side, float z = 0) => Offset(spriteAnchor,
        forward * spriteForwardX + side * spriteRightX + z * spriteUpX,
        forward * spriteForwardY + side * spriteRightY + z * spriteUpY);

    private void SpriteQuad(H a, H b, H c, H d, uint color)
    {
        Quad(a, b, c, d, color);
        Quad(a, b, c, d, color, hidden: true, opacity: 125f / 255);
    }
    private void SpriteDeck(float aft, float fore, float left, float right, float z, uint color) =>
        SpriteQuad(SpriteP(aft, left, z), SpriteP(fore, left, z), SpriteP(fore, right, z), SpriteP(aft, right, z), color);
    private void SpriteBox(float aft, float fore, float left, float right, float baseZ, float top, uint side, uint roof)
    {
        float lateral = spriteRightY < 0 ? left : right, end = spriteForwardY < 0 ? aft : fore;
        SpriteQuad(SpriteP(aft, lateral, baseZ), SpriteP(fore, lateral, baseZ), SpriteP(fore, lateral, top), SpriteP(aft, lateral, top), side);
        SpriteQuad(SpriteP(end, left, baseZ), SpriteP(end, right, baseZ), SpriteP(end, right, top), SpriteP(end, left, top), side);
        SpriteDeck(aft, fore, left, right, top, roof);
    }
    private void SpriteLine(H a, H b, uint color, float width = 1)
    {
        Stroke(a, b, width * frame!.PixelSize, color);
        Stroke(a, b, width * frame.PixelSize, color, hidden: true, opacity: 125f / 255);
    }
    private void SpritePixel(H point, uint color, int width = 1, int height = 1)
    {
        float pixel = frame!.PixelSize;
        float x = MathF.Round(point.X / point.W / pixel - width * .5f) * pixel;
        float y = MathF.Round(point.Y / point.W / pixel - height * .5f) * pixel;
        point = point with { X = x * point.W, Y = y * point.W };
        Block(point, 0, 0, width * pixel, height * pixel, color);
        Block(point, 0, 0, width * pixel, height * pixel, color, hidden: true, opacity: 125f / 255);
    }

    private void SpriteCar(int phase)
    {
        SpriteDeck(-6, 6, -3.6f, 3.6f, 0, SpriteInk);
        for (int f = -4; f <= 4; f += 8)
        for (int s = -1; s <= 1; s += 2)
        {
            SpriteLine(SpriteP(f - 1, s * 3.3f), SpriteP(f + 1, s * 3.3f), SpriteInk, 2.3f);
            SpritePixel(SpriteP(f + (phase & 1) * .4f, s * 3.5f, .4f), 0xff889899);
        }
        SpriteBox(-6, 6.6f, -2.9f, 2.9f, .8f, 2.1f, 0xffae553e, 0xffeb9b55);
        SpriteBox(-3.3f, 2.7f, -2.35f, 2.35f, 2.1f, 3.8f, 0xff37575e, SpriteWindow);
        SpriteDeck(-2.2f, .7f, -2.35f, 2.35f, 4, 0xfff6c477);
        SpritePixel(SpriteP(6, -1.8f, 1.7f), 0xffffffcf, 2); SpritePixel(SpriteP(6, 1.8f, 1.7f), 0xffffffcf, 2);
        SpritePixel(SpriteP(-6, -2.1f, 1.5f), 0xffe45c51); SpritePixel(SpriteP(-6, 2.1f, 1.5f), 0xffe45c51);
    }

    private void SpriteBoat(int phase, bool moving)
    {
        SpriteQuad(SpriteP(-7,-2.8f), SpriteP(2,-3.8f), SpriteP(8,0), SpriteP(-7,2.8f), 0xff244f65);
        SpriteQuad(SpriteP(-6,-2.8f,1), SpriteP(3,-3.2f,1), SpriteP(8,0,1), SpriteP(3,3.2f,1), SpriteWhite);
        SpriteDeck(-6, 3, -2.8f, 2.8f, 1, 0xffe6dcc0);
        SpriteBox(-3, 2, -2, 2, 1, 3.8f, 0xff537f90, 0xffe8aa60);
        SpriteDeck(-2, 1, -1.7f, 1.7f, 4, 0xfff4ddad);
        SpriteLine(SpriteP(-3,0,2), SpriteP(-3,0,6), SpriteInk); SpritePixel(SpriteP(-3,-.6f,5), 0xffe77062, 2);
        if (moving)
        {
            SpriteLine(SpriteP(-7,-2), SpriteP(-10 - (phase & 1),-4), 0xffb8f3df);
            SpriteLine(SpriteP(-7,2), SpriteP(-10 - (phase & 1),4), 0xffdff9ef);
        }
    }

    private void SpritePlane(int phase)
    {
        SpriteQuad(SpriteP(-4,-1,1), SpriteP(1,-1,1), SpriteP(-3,-10,1), SpriteP(-5,-10,1), 0xffa6bdce);
        SpriteQuad(SpriteP(1,1,1), SpriteP(-4,1,1), SpriteP(-5,10,1), SpriteP(-3,10,1), 0xffc1d8dc);
        SpriteQuad(SpriteP(-8,-4,2), SpriteP(-6,0,2), SpriteP(-8,4,2), SpriteP(-9,0,2), 0xffe89461);
        SpriteQuad(SpriteP(-9,-1), SpriteP(3,-1.8f,2), SpriteP(10,0,2), SpriteP(3,1.8f,2), 0xfff8f0d9);
        SpriteBox(-6, 5, -1.3f, 1.3f, 1, 2.6f, SpriteDark, SpriteWhite);
        SpriteDeck(3.1f, 5.3f, -1.25f, 1.25f, 2.8f, 0xff529bb2);
        SpriteLine(SpriteP(-7,0,2), SpriteP(-8,0,5.2f), 0xffc2674b, 2);
        SpritePixel(SpriteP(-4,-9.6f,1), (phase & 7) < 2 ? 0xffff705e : 0xffa64140);
        SpritePixel(SpriteP(-4,9.6f,1), (phase & 7) < 2 ? 0xffb0ffd2 : 0xff539c82);
        // Twin nacelles stay aligned with the fuselage during a bank or map rotation.
        SpriteLine(SpriteP(-2,-4.4f,.4f), SpriteP(.4f,-4.4f,.4f), SpriteInk, 1.3f);
        SpriteLine(SpriteP(-2,4.4f,.4f), SpriteP(.4f,4.4f,.4f), SpriteInk, 1.3f);
    }

    private void SpriteTrain(int phase)
    {
        SpriteDeck(-11, 9, -2.8f, 2.8f, 0, SpriteInk);
        SpriteBox(-10.5f, -.8f, -2.4f, 2.4f, .8f, 3.9f, 0xff823d5d, 0xffd986a1);
        SpriteBox(.3f, 9, -2.4f, 2.4f, .8f, 3.9f, 0xff743e60, 0xffed9db1);
        float near = spriteRightY < 0 ? -2.45f : 2.45f;
        for (int i = -9; i < 9; i += 3) SpritePixel(SpriteP(i,near,2.4f), SpriteWindow, 2, 1);
        SpriteDeck(6, 8.1f, -2, 2, 4, 0xff78a5b5);
        for (int i = -9; i < 6; i += 4)
            SpritePixel(SpriteP(i + (phase & 1) * .3f, near, .3f), SpriteDark, 2);
        SpriteLine(SpriteP(-6,0,4.2f), SpriteP(-4,0,5.7f), SpriteInk); SpriteLine(SpriteP(-4,0,5.7f), SpriteP(-2.8f,0,4.2f), SpriteInk);
        SpritePixel(SpriteP(8.8f,0,2), 0xffffd99e, 2);
    }

    private void SpriteCyclist(int phase)
    {
        float pedal = (phase & 3) < 2 ? 1 : -1;
        for (int i = -3; i <= 3; i += 6)
        {
            SpriteLine(SpriteP(i - 1,0,.6f), SpriteP(i + 1,0,1.8f), SpriteInk, 2);
            SpritePixel(SpriteP(i,0,1.1f), 0xffc8d1b8);
        }
        SpriteLine(SpriteP(-3,0,1), SpriteP(-1,0,3), 0xff4fbd93); SpriteLine(SpriteP(-1,0,3), SpriteP(3,0,1), 0xff4fbd93);
        SpriteLine(SpriteP(2,0,3.5f), SpriteP(3,0,1), SpriteInk);
        SpriteLine(SpriteP(-.3f,0,3), SpriteP(pedal,1,1), SpriteDark); SpriteLine(SpriteP(-.3f,0,3), SpriteP(-pedal,-1,1), SpriteInk);
        SpriteLine(SpriteP(-.8f,0,3.5f), SpriteP(1,0,5.7f), 0xff47ae86, 2.8f);
        SpritePixel(SpriteP(1.3f,0,6.8f), 0xffe9ad79, 2, 2); SpritePixel(SpriteP(1.1f,0,7.4f), 0xffefc85c, 3);
    }
    private void SpriteWalker(int phase)
    {
        float step = (phase & 3) < 2 ? 1.4f : -1.4f;
        SpriteLine(SpriteP(0,-.7f,3), SpriteP(step,-.8f,.2f), SpriteDark, 1.5f);
        SpriteLine(SpriteP(0,.7f,3), SpriteP(-step,.8f,.2f), SpriteInk, 1.5f);
        SpriteLine(SpriteP(0,0,3), SpriteP(0,0,5.8f), 0xff75ad65, 3);
        SpriteLine(SpriteP(0,1.1f,5), SpriteP(step,1.25f,3.2f), 0xffcc9365);
        SpritePixel(SpriteP(.1f,0,6.8f), 0xffe8ae7c, 3, 3);
        SpritePixel(SpriteP(0,0,7.8f), 0xff5d493e, 3);
        SpritePixel(SpriteP(-.8f,-.3f,4.8f), 0xffcd9f58, 2, 2);
    }

}
