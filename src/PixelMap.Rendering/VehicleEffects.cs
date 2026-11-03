using PixelMap.Routing;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>Fixed storage for simulation-time footprints, wake, vapor and exhaust.
/// Emission points remain attached to the Earth when the camera orbits or pans.</summary>
internal sealed class VehicleEffects
{
    private readonly record struct Particle(RoutePoint Point, double Bearing, double Altitude, double Born, int Serial);
    private const int Capacity = 56;
    private readonly Particle[] trail = new Particle[Capacity];
    private VehiclePose? previous;
    private int head, count, serial;
    private double remainder;
    internal int Count => count;
    internal double Time { get; private set; }
    internal void Reset() { previous = null; head = count = serial = 0; remainder = Time = 0; }

    internal void Advance(TravelMode mode, VehiclePose pose)
    {
        if (previous is not { } last) { previous = pose; return; }
        previous = pose;
        double dt = pose.AnimationSeconds - last.AnimationSeconds;
        double distance = last.Point.DistanceTo(pose.Point);
        double nominal = mode switch
        {
            TravelMode.Walk => 3, TravelMode.Bike => 15, TravelMode.Boat => 25,
            TravelMode.Flight => 340, TravelMode.Train => 100, _ => 60
        };
        double speed = pose.SpeedMetersPerSecond > 0 ? pose.SpeedMetersPerSecond : nominal;
        if (pose.Continuity != last.Continuity || (dt == 0 && distance > .02) || dt < 0 || (pose.Moving && dt > 4) || distance > Math.Max(60, speed * Math.Max(dt, .1) * 5 + 30) ||
            double.IsFinite(last.Progress) && double.IsFinite(pose.Progress) && pose.Progress < last.Progress - .00001)
        {
            Reset(); previous = pose; return;
        }
        if (!pose.Moving || dt == 0) return;
        Time += dt;
        double life = Lifetime(mode);
        while (count != 0 && Time - trail[(head - count + Capacity) % Capacity].Born >= life) count--;
        if (distance < .02) return;
        double spacing = mode switch { TravelMode.Walk => .30, TravelMode.Bike => .22, TravelMode.Drive => .18, _ => .13 };
        double until = spacing - remainder;
        for (double at = until; at <= dt; at += spacing)
        {
            double t = at / dt;
            var point = last.Point.InterpolateTo(pose.Point, t);
            double bearing = last.Bearing + 360 * MapCamera.WrapOffset((pose.Bearing - last.Bearing) / 360) * t;
            trail[head] = new(point, bearing, last.AltitudeMeters + (pose.AltitudeMeters - last.AltitudeMeters) * t,
                Time - dt + at, serial++);
            head = (head + 1) % Capacity;
            count = Math.Min(Capacity, count + 1);
        }
        remainder = (remainder + dt) % spacing;
    }

    internal void Draw(SKCanvas canvas, MapCamera cam, int w, int h, int pixel, TravelMode mode, SKPaint paint,
        Func<float, float, double, bool> visible)
    {
        double life = Lifetime(mode);
        paint.Style = SKPaintStyle.Fill;
        for (int i = 0; i < count; i++)
        {
            var p = trail[(head - count + i + Capacity) % Capacity];
            float age = (float)((Time - p.Born) / life);
            if (age is < 0 or >= 1) continue;
            if (!Position(cam,p.Point,p.Altitude,w,h,pixel,out float x,out float groundY,out float lift)) continue;
            float y=groundY-lift;
            if (x < -20 || x > w / pixel + 20 || y < -20 || y > h / pixel + 20 || !visible(x, y, p.Altitude + 1)) continue;
            var axes = Axes(cam, p.Point, p.Altitude, p.Bearing, w, h);
            float fx = axes.Forward.X, fy = axes.Forward.Y;
            float rx = axes.Right.X, ry = axes.Right.Y;
            byte alpha = (byte)(185 * (1 - age));
            void Puff(float back, float side, float rise, float size, uint color)
            {
                float px = MathF.Round(x - fx * back + rx * side), py = MathF.Round(y - fy * back + ry * side - rise);
                paint.Color = new SKColor(color).WithAlpha(alpha);
                canvas.DrawRect(px, py, size, Math.Max(1, size * .65f), paint);
            }
            float sign = (p.Serial & 1) == 0 ? -1 : 1;
            switch (mode)
            {
                case TravelMode.Flight:
                    Puff(3, -5 - age * 1.5f, 0, 1 + age * 2, 0xfff2f6ed);
                    Puff(3, 5 + age * 1.5f, 0, 1 + age * 2, 0xffedeee1); break;
                case TravelMode.Boat:
                    Puff(6 + age * 5, -2 - age * 6, 0, 2, 0xffb9ede2);
                    Puff(6 + age * 5, 2 + age * 6, 0, 2, 0xffd5fbf0); break;
                case TravelMode.Train: Puff(4, sign * age, 5 + age * 5, 1 + age * 2, 0xffafbbbd); break;
                case TravelMode.Drive: Puff(4, -1 + age * sign, age * 2, 1 + age, 0xffb7a58d); break;
                case TravelMode.Bike: Puff(3, sign * .4f, 0, 1, 0xff7a8e83); break;
                default: Puff(1, sign * 1.3f, 0, 1, 0xff7a7161); break;
            }
        }
    }

    internal static VehicleAxes Axes(MapCamera camera, RoutePoint point, double altitude, double bearing, int width, int height)
    {
        if (camera.Perspective)
        {
            var origin = camera.ProjectAtAltitude(point.Latitude, point.Longitude, altitude, width, height);
            var forward = point.Move(bearing, 1);
            var right = point.Move(bearing + 90, 1);
            var f = camera.ProjectAtAltitude(forward.Latitude, forward.Longitude, altitude, width, height);
            var r = camera.ProjectAtAltitude(right.Latitude, right.Longitude, altitude, width, height);
            var u = camera.ProjectAtAltitude(point.Latitude, point.Longitude, altitude + 1, width, height);
            double fx = f.X - origin.X, fy = f.Y - origin.Y, rx = r.X - origin.X, ry = r.Y - origin.Y;
            double scale = Math.Max(Math.Sqrt(fx * fx + fy * fy), Math.Sqrt(rx * rx + ry * ry));
            if (scale > 1e-9 && double.IsFinite(scale))
                return new(new((float)(fx / scale), (float)(fy / scale)), new((float)(rx / scale), (float)(ry / scale)),
                    new((float)((u.X - origin.X) / scale), (float)((u.Y - origin.Y) / scale)));
        }
        var (sin, cos) = Math.SinCos((bearing - camera.Bearing) * Math.PI / 180);
        float tilt = (float)Math.Max(.48, Math.Cos(camera.PitchRadians));
        return new(new((float)sin, -(float)cos * tilt), new((float)cos, (float)sin * tilt),
            new(0, (float)(-.20 - .80 * Math.Sin(camera.PitchRadians))));
    }

    internal static bool Position(MapCamera camera,RoutePoint p,double altitude,int w,int h,int pixel,
        out float x,out float groundY,out float lift)
    {
        var ground=camera.Project(p.Latitude,p.Longitude,w,h);
        groundY=(float)(ground.Y/pixel);
        if(camera.Perspective)
        {
            bool inside=camera.TryProject(p.Latitude,p.Longitude,altitude,w,h,out var actual);
            x=(float)(actual.X/pixel); lift=groundY-(float)(actual.Y/pixel);
            return inside;
        }
        x=(float)(ground.X/pixel);lift=Lift(camera,p,altitude,w,h,pixel);return true;
    }

    internal static float Lift(MapCamera camera, RoutePoint p, double altitude, int width, int height, int pixel)
    {
        if (altitude <= 0 || camera.Pitch <= 0) return 0;
        double trueRise = altitude * camera.WorldSize /
            (MapCamera.EarthCircumference * Math.Cos(Math.Clamp(p.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)) *
            Math.Sin(camera.PitchRadians);
        // The route renderer is a navigation overlay. Compress very high flight altitudes
        // so the aircraft and its ground reference remain in the same viewport.
        double cap = Math.Min(110, height * .2);
        return (float)(cap * (1 - Math.Exp(-trueRise / Math.Max(1, cap))) / pixel);
    }

    private static double Lifetime(TravelMode mode) => mode switch
    {
        TravelMode.Flight => 6.5, TravelMode.Boat => 4, TravelMode.Walk => 3, TravelMode.Train => 2.6, _ => 1.7
    };
}
