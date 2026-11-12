using PixelMap.Routing;

namespace PixelMap.Rendering;

public sealed partial class WebGpuSceneOverlay
{
    private readonly record struct Particle(RoutePoint Point, double Bearing, double Altitude, double Born, int Serial);
    private const int ParticleCapacity = 56;
    private readonly Particle[] particles = new Particle[ParticleCapacity];
    private VehiclePose? previousParticlePose;
    private TravelMode? particleMode;
    private RoutePlan? particlePlan;
    private int particleHead, particleCount, particleSerial;
    private double particleRemainder, particleTime;

    private void ResetParticles()
    {
        previousParticlePose = null;
        particleMode = null;
        particlePlan = null;
        particleHead = particleCount = particleSerial = 0;
        particleRemainder = particleTime = 0;
    }

    private void AdvanceParticles(TravelMode mode, VehiclePose pose)
    {
        if (particleMode != mode || !ReferenceEquals(particlePlan, routePlan))
        {
            ResetParticles();
            particleMode = mode;
            particlePlan = routePlan;
        }
        if (previousParticlePose is not { } previous) { previousParticlePose = pose; return; }
        previousParticlePose = pose;
        double elapsed = pose.AnimationSeconds - previous.AnimationSeconds;
        double distance = previous.Point.DistanceTo(pose.Point);
        double nominal = mode switch
        {
            TravelMode.Walk => 3, TravelMode.Bike => 15, TravelMode.Boat => 25,
            TravelMode.Flight => 340, TravelMode.Train => 100, _ => 60
        };
        double speed = pose.SpeedMetersPerSecond > 0 ? pose.SpeedMetersPerSecond : nominal;
        if (!double.IsFinite(elapsed) || !double.IsFinite(distance) || pose.Continuity != previous.Continuity ||
            elapsed == 0 && distance > .02 || elapsed < 0 || pose.Moving && elapsed > 4 ||
            distance > Math.Max(60, speed * Math.Max(elapsed, .1) * 5 + 30) ||
            double.IsFinite(previous.Progress) && double.IsFinite(pose.Progress) && pose.Progress < previous.Progress - .00001)
        {
            ResetParticles();
            particleMode = mode;
            particlePlan = routePlan;
            previousParticlePose = pose;
            return;
        }
        // Consume the timestamp while stopped without advancing the visual clock.
        // Resumption therefore cannot emit a burst for time spent at rest.
        if (!pose.Moving || elapsed == 0) return;
        particleTime += elapsed;
        double life = ParticleLifetime(mode);
        while (particleCount != 0 && particleTime - particles[(particleHead - particleCount + ParticleCapacity) % ParticleCapacity].Born >= life)
            particleCount--;
        if (distance < .02) return;
        double spacing = mode switch { TravelMode.Walk => .30, TravelMode.Bike => .22, TravelMode.Drive => .18, _ => .13 };
        for (double at = spacing - particleRemainder; at <= elapsed; at += spacing)
        {
            double t = at / elapsed;
            var point = previous.Point.InterpolateTo(pose.Point, t);
            double bearing = previous.Bearing + 360 * MapCamera.WrapOffset((pose.Bearing - previous.Bearing) / 360) * t;
            particles[particleHead] = new(point, bearing,
                previous.AltitudeMeters + (pose.AltitudeMeters - previous.AltitudeMeters) * t,
                particleTime - elapsed + at, particleSerial++);
            particleHead = (particleHead + 1) % ParticleCapacity;
            particleCount = Math.Min(ParticleCapacity, particleCount + 1);
        }
        particleRemainder = (particleRemainder + elapsed) % spacing;
    }

    private void AppendParticles(TravelMode mode)
    {
        double life = ParticleLifetime(mode);
        float pixel = frame!.PixelSize;
        for (int i = 0; i < particleCount; i++)
        {
            var particle = particles[(particleHead - particleCount + i + ParticleCapacity) % ParticleCapacity];
            float age = (float)((particleTime - particle.Born) / life);
            if (age is < 0 or >= 1) continue;
            double absolute = frame.NavigationTerrain is not null && mode != TravelMode.Flight ? PhysicalSurface(particle.Point) : particle.Altitude;
            var anchor = Project(particle.Point, RenderAltitude(particle.Point, absolute) + .6);
            if (!InFrustum(anchor, 40)) continue;
            var axes = OverlayAxes(particle.Point, absolute, particle.Bearing, mode != TravelMode.Flight);
            byte alpha = (byte)(185 * (1 - age));
            float sign = (particle.Serial & 1) == 0 ? -1 : 1;
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
            void Puff(float back, float side, float rise, float size, uint color)
            {
                float x = -axes.Forward.X * back + axes.Right.X * side;
                float y = -axes.Forward.Y * back + axes.Right.Y * side - rise;
                Block(anchor, MathF.Round(x) * pixel, MathF.Round(y) * pixel, size * pixel,
                    Math.Max(1, size * .65f) * pixel, (color & 0x00ffffff) | ((uint)alpha << 24));
            }
        }
    }

    private static double ParticleLifetime(TravelMode mode) => mode switch
    {
        TravelMode.Flight => 6.5, TravelMode.Boat => 4, TravelMode.Walk => 3, TravelMode.Train => 2.6, _ => 1.7
    };
}
