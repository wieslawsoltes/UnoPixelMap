using PixelMap.Routing;

namespace PixelMap.Rendering;

public sealed partial class WebGpuSceneOverlay
{
    private readonly record struct ModelPoint(float Forward, float Right, float Up);
    private readonly record struct ModelFace(H A, H B, H C, H D, uint Color, float Distance);
    private readonly List<ModelFace> modelFaces = new(256);
    private double modelX, modelY, modelAltitude, headingSin, headingCos;
    private MapRenderStyle vehicleStyle;

    private void AppendWorldVehicle(TravelMode mode, VehiclePose pose)
    {
        var camera = frame!.Camera;
        vehicleStyle = frame.RenderStyle;
        modelX = MapCamera.WrapOffset((pose.Point.Longitude + 180) / 360 - camera.CenterX) * worldSize;
        modelY = (MapCamera.MercatorY(pose.Point.Latitude) - camera.CenterY) * worldSize;
        modelAltitude = RenderAltitude(pose.Point, pose.AltitudeMeters);
        (headingSin, headingCos) = Math.SinCos(pose.Bearing * Math.PI / 180);
        modelFaces.Clear();
        float phase = pose.Moving ? (float)Math.Sin(pose.AnimationSeconds * 9) : 0;
        switch (mode)
        {
            case TravelMode.Drive: WorldCar(); break;
            case TravelMode.Boat: WorldBoat(); break;
            case TravelMode.Flight: WorldPlane(); break;
            case TravelMode.Train: WorldTrain(); break;
            case TravelMode.Bike: WorldBike(phase); break;
            default: WorldWalker(phase); break;
        }
        // Overlay models share the map depth buffer. Sort their small face lists for
        // internal painter order while the GPU handles exact scenery occlusion.
        modelFaces.Sort(static (a, b) => b.Distance.CompareTo(a.Distance));
        foreach (var face in modelFaces) Quad(face.A, face.B, face.C, face.D, face.Color);
    }

    private H ModelProject(ModelPoint point)
    {
        double east = modelX + (point.Forward * headingSin + point.Right * headingCos) * meters;
        double south = modelY + (-point.Forward * headingCos + point.Right * headingSin) * meters;
        double u = east * bearingCos + south * bearingSin, v = -east * bearingSin + south * bearingCos;
        double altitude = modelAltitude + point.Up;
        double local = altitude - frame!.Camera.GroundAltitudeMeters;
        double z = local * meters - frame.Camera.TargetPixels;
        double w = frame.Camera.Perspective ? 1 - (v * pitchSin + z * pitchCos) / focal : 1;
        return new((float)(frame.Width * .5 * w + u), (float)(frame.Height * .5 * w + v * pitchCos - z * pitchSin),
            (float)w, (float)(local * frame.PixelsPerMeter), (float)(-(v * pitchSin + z * pitchCos)));
    }

    private void ModelQuad(ModelPoint a, ModelPoint b, ModelPoint c, ModelPoint d, uint color, float shade = 1)
    {
        var pa = ModelProject(a); var pb = ModelProject(b); var pc = ModelProject(c); var pd = ModelProject(d);
        float distance = frame!.Camera.Perspective ? pa.W + pb.W + pc.W + pd.W : pa.U + pb.U + pc.U + pd.U;
        modelFaces.Add(new(pa, pb, pc, pd, PixelPalette.Mix(0xff000000, color, shade), distance));
    }

    private void ModelBox(float aft, float fore, float left, float right, float bottom, float top, uint color)
    {
        ModelQuad(new(aft, left, bottom), new(fore, left, bottom), new(fore, left, top), new(aft, left, top), color, .72f);
        ModelQuad(new(fore, right, bottom), new(aft, right, bottom), new(aft, right, top), new(fore, right, top), color, .86f);
        ModelQuad(new(aft, right, bottom), new(aft, left, bottom), new(aft, left, top), new(aft, right, top), color, .64f);
        ModelQuad(new(fore, left, bottom), new(fore, right, bottom), new(fore, right, top), new(fore, left, top), color, .9f);
        ModelQuad(new(aft, left, top), new(fore, left, top), new(fore, right, top), new(aft, right, top), color);
        ModelQuad(new(aft, right, bottom), new(fore, right, bottom), new(fore, left, bottom), new(aft, left, bottom), color, .55f);
    }

    private void WorldCar()
    {
        ModelBox(-2.2f, 2.3f, -.86f, .86f, .36f, .96f, 0xffed9351);
        ModelBox(-1.15f, .95f, -.73f, .73f, .96f, 1.52f, 0xff81bfcd);
        ModelBox(-.95f, .6f, -.75f, .75f, 1.5f, 1.61f, 0xfff4bc68);
        for (int axle = 0; axle < 2; axle++)
        for (int side = -1; side <= 1; side += 2)
        {
            float end = axle == 0 ? -1.35f : 1.4f;
            ModelBox(end - .34f, end + .34f, side * .88f - .14f, side * .88f + .14f, .02f, .7f, 0xff263742);
            ModelBox(end - .17f, end + .17f, side * 1.025f - .025f, side * 1.025f + .025f, .2f, .52f, 0xffa9b7ba);
            ModelBox(2.28f, 2.32f, side * .57f - .19f, side * .57f + .19f, .65f, .84f, 0xfffff2bc);
            ModelBox(-2.23f, -2.19f, side * .59f - .17f, side * .59f + .17f, .65f, .83f, 0xffe95c59);
        }
        WorldStuds(-.7f, .3f, -.5f, .5f, 1.61f);
    }

    private void WorldWalker(float phase)
    {
        bool toy = vehicleStyle == MapRenderStyle.Roblox;
        float half = toy ? .3f : .25f;
        ModelBox(-.15f, .15f, -half, half, .72f, 1.4f, 0xff4ab6ca);
        ModelBox(-.16f, .18f, -.18f, .18f, 1.42f, 1.8f, 0xffefc58f);
        ModelBox(-.18f, .16f, -.2f, .2f, 1.75f, 1.86f, 0xff495c67);
        for (int side = -1; side <= 1; side += 2)
        {
            float stride = phase * side * .22f;
            ModelBox(-.12f + stride, .12f + stride, side * .14f - .11f, side * .14f + .11f, .08f, .76f, 0xff3c526e);
            ModelBox(-.15f + stride, .24f + stride, side * .14f - .12f, side * .14f + .12f, 0, .16f, 0xff283c46);
            ModelBox(-.12f - stride, .12f - stride, side * (half + .11f) - .1f, side * (half + .11f) + .1f, .8f, 1.38f, 0xffefc58f);
            ModelBox(.178f, .185f, side * .085f - .023f, side * .085f + .023f, 1.61f, 1.66f, 0xff2c424e);
        }
    }

    private void WorldBike(float phase)
    {
        for (int end = -1; end <= 1; end += 2)
        {
            ModelBox(end * .68f - .29f, end * .68f + .29f, -.055f, .055f, .02f, .62f, 0xff273d48);
            ModelBox(end * .68f - .19f, end * .68f + .19f, -.06f, .06f, .12f, .52f, 0xffbcc5c4);
        }
        ModelBox(-.6f, .7f, -.06f, .06f, .36f, .53f, 0xff5fc997);
        ModelBox(-.2f, -.07f, -.07f, .07f, .48f, .94f, 0xff5fc997);
        ModelBox(.58f, .69f, -.32f, .32f, .95f, 1.03f, 0xff283b43);
        ModelBox(-.28f, .16f, -.19f, .19f, .93f, 1.46f, 0xffec8d61);
        ModelBox(.05f, .36f, -.16f, .16f, 1.45f, 1.79f, 0xffefc58f);
        for (int side = -1; side <= 1; side += 2)
            ModelBox(-.18f + phase * side * .12f, .1f + phase * side * .12f, side * .2f - .08f, side * .2f + .08f, .43f, 1.02f, 0xff39576b);
    }

    private void WorldBoat()
    {
        ModelBox(-3.7f, 2.9f, -1.15f, 1.15f, .04f, .75f, 0xfff3e9c9);
        ModelQuad(new(2.9f, -1.15f, .75f), new(4.1f, 0, .75f), new(2.9f, 1.15f, .75f), new(2.9f, -1.15f, .75f), 0xfff3e9c9);
        ModelQuad(new(2.9f, -1.15f, .04f), new(4.1f, 0, .15f), new(4.1f, 0, .75f), new(2.9f, -1.15f, .75f), 0xff77a7be, .8f);
        ModelQuad(new(4.1f, 0, .15f), new(2.9f, 1.15f, .04f), new(2.9f, 1.15f, .75f), new(4.1f, 0, .75f), 0xff77a7be);
        ModelBox(-1.7f, .8f, -.85f, .85f, .75f, 2.05f, 0xff80b9c9);
        ModelBox(-1.9f, 1, -.95f, .95f, 2.05f, 2.22f, 0xffefbd7c);
        ModelBox(-2.4f, -2.3f, -.04f, .04f, .75f, 3.2f, 0xff435b67);
        ModelBox(-2.42f, -2.28f, 0, .6f, 2.72f, 3.15f, 0xffe77f66);
        WorldStuds(-1.5f, .6f, -.7f, .7f, 2.22f);
    }

    private void WorldPlane()
    {
        ModelBox(-5.2f, 4.5f, -.56f, .56f, -.3f, .55f, 0xfff4ecd6);
        ModelBox(2.4f, 3.7f, -.5f, .5f, .56f, .85f, 0xff79b8ca);
        ModelBox(-1.6f, .6f, -6, 6, -.1f, .12f, 0xffc2d9dc);
        ModelBox(-5.3f, -3.8f, -2.3f, 2.3f, .18f, .36f, 0xffeb9b69);
        ModelBox(-5.1f, -3.9f, -.09f, .09f, .4f, 2.1f, 0xffda7b58);
        for (int side = -1; side <= 1; side += 2)
        {
            ModelBox(-.8f, 1.1f, side * 2.3f - .25f, side * 2.3f + .25f, -.6f, -.05f, 0xff586e7c);
            ModelBox(-1.55f, -.85f, side * 5.95f - .08f, side * 5.95f + .08f, .12f, .24f,
                side < 0 ? 0xffef7067 : 0xff77dca9);
        }
    }

    private void WorldTrain()
    {
        ModelBox(-8, 8, -1.35f, 1.35f, .45f, 3.35f, 0xffd37b9d);
        ModelBox(-7.7f, 7.7f, -1.27f, 1.27f, 3.35f, 3.58f, 0xfff0b1bf);
        for (int end = -1; end <= 1; end += 2)
        {
            ModelBox(end * 5.7f - 1, end * 5.7f + 1, -1.4f, 1.4f, .05f, .73f, 0xff334752);
            ModelBox(end * 7.98f - .025f, end * 7.98f + .025f, -1.05f, 1.05f, 1.9f, 2.95f, 0xff84bed1);
        }
        for (int window = -6; window <= 6; window += 2)
        for (int side = -1; side <= 1; side += 2)
            ModelBox(window - .58f, window + .58f, side * 1.355f - .025f, side * 1.355f + .025f, 1.8f, 2.83f, 0xff8ac2cc);
        WorldStuds(-6, 6, -.75f, .75f, 3.58f);
    }

    private void WorldStuds(float aft, float fore, float left, float right, float top)
    {
        if (vehicleStyle != MapRenderStyle.Roblox) return;
        for (float f = aft; f <= fore; f += .6f)
        for (float r = left; r <= right; r += .6f)
            ModelBox(f - .11f, f + .11f, r - .11f, r + .11f, top, top + .08f, 0xffe8c99b);
    }

}
