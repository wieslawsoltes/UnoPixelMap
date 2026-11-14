using System.Numerics;
using System.Text.Json;
using PixelMap.Rendering;

/// <summary>Independent spatial integrals for the production WGSL façade fixture.
/// Native checks establish the reference; browser readback verifies the shader.</summary>
internal static class WebGpuFacadeChecks
{
    private sealed record Sample(string Name, bool Roblox, Vector2 Room, Vector2 Footprint, uint Seed,
        bool Night, Vector3 Wall, Vector3 Glass, Vector3 Lamp, double Tolerance = .012);
    private readonly record struct Weights(double Coverage, double Lamps, double Tint);
    private sealed record Expected(string Name, double[] Albedo, double Roughness, double[] Emission,
        double Metallic, double Tolerance);
    private sealed record Continuity(string Name, int LeftIndex, int RightIndex, double Tolerance);
    private static readonly Vector3 Wall = new(.34f, .27f, .19f);
    private static readonly Vector3 Glass = new(.075f, .17f, .26f);
    private static readonly Vector3 Lamp = new(1f, .8f, .36f);

    internal static void Run()
    {
        CheckSpatialReference();
        CheckPartitionEnergy();
        CheckResolvedRooms();
        Console.WriteLine($"Validated {Cases().Count} independent WebGPU façade reference cases: signed/repeating window edges, lamp energy, linear albedo, squared roughness, tint moments and resolved rooms. Actual WGSL verification requires the browser readback fixture.");
    }

    internal static void WriteFixture(string path)
    {
        Run();
        var samples = Cases();
        var inputs = new List<float>(samples.Count * 20);
        var expected = new List<Expected>(samples.Count);
        foreach (var sample in samples)
        {
            Append(sample);
            expected.Add(Shade(sample, Integrate(sample)));
        }
        // Mip-filtered lamps are an approximation above one room per axis.
        // These probes compare neighboring GPU results directly; they must not
        // be presented as exact matches to the independent spatial integral.
        var continuity = new List<Continuity>();
        foreach (var pair in ContinuityCases())
            continuity.Add(new(pair.Name, Append(pair.Left), Append(pair.Right), .003));

        int Append(Sample sample)
        {
            int index = inputs.Count / 20;
            inputs.AddRange([sample.Room.X, sample.Room.Y, sample.Footprint.X, sample.Footprint.Y,
                sample.Roblox ? 1 : 0, sample.Seed, sample.Night ? 1 : 0, 0,
                sample.Wall.X, sample.Wall.Y, sample.Wall.Z, 0,
                sample.Glass.X, sample.Glass.Y, sample.Glass.Z, 0,
                sample.Lamp.X, sample.Lamp.Y, sample.Lamp.Z, 0]);
            return index;
        }
        var fixture = new
        {
            width = inputs.Count / 20, height = 1, strideFloats = 20,
            wgsl = WebGpuMapShaders.ColorSpace + WebGpuFacadeShading.Wgsl + FixtureShader,
            lightingCommon = WebGpuMapShaders.Common,
            mips = PixelWindowFilter.Levels.Select(level => new
                { width = level.Width, height = level.Height, base64 = Convert.ToBase64String(level.Pixels) }),
            inputs, expected, continuity,
            reference = "Double-precision intersections with individual periodic windows and original binary room lamps; linear-light area integrals, coverage-weighted squared roughness, and exact Roblox pane-tint first moment. No production filtering helper is evaluated by this oracle.",
            continuityReference = "GPU output pairs at room footprints 1, 2 and 4 +/- 0.0001; horizontal, vertical and diagonal transitions include window edges, cell seams and signed texture-repeat phases. Each material channel must remain within 0.003 across a pair. These probes establish continuity of the mip approximation, not exact intermediate-scale spatial integration."
        };
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(fixture, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
        Console.WriteLine($"Wrote {samples.Count} production-WGSL façade reference cases, {continuity.Count} GPU continuity pairs and the actual RG8 room mip chain: {path}");
    }

    private static IEnumerable<(string Name, Sample Left, Sample Right)> ContinuityCases()
    {
        const float epsilon = .0001f;
        foreach (bool roblox in new[] { false, true })
        foreach (float span in new[] { 1f, 2, 4 })
        foreach (int axis in new[] { 0, 1, 2 })
        {
            var phases = new[]
            {
                Vector2.Zero,
                roblox ? new Vector2(.18f, .24f) : new Vector2(.17f, .23f),
                roblox ? new Vector2(.82f, .78f) : new Vector2(.76f, .75f),
                new Vector2(255.9999f, 15.9999f),
                new Vector2(-.0001f, -.0001f),
                new Vector2(-256, -16)
            };
            for (int phase = 0; phase < phases.Length; phase++)
            {
                string name = $"{(roblox ? "roblox" : "normal")}-continuity-span{span}-axis{axis}-phase{phase}";
                Vector2 Footprint(float value) => axis switch
                {
                    0 => new(value, .37f),
                    1 => new(.37f, value),
                    _ => new(value)
                };
                var left = new Sample(name + "-left", roblox, phases[phase], Footprint(span - epsilon),
                    255, true, Wall, Glass, Lamp);
                yield return (name, left, left with { Name = name + "-right", Footprint = Footprint(span + epsilon) });
            }
        }
    }

    private static List<Sample> Cases()
    {
        var result = new List<Sample>();
        // Edge intersections include window edges, cell seams, negative rooms,
        // the 256-column and 16-story periods, and the old 0.2 LOD transition.
        foreach (bool roblox in new[] { false, true })
        foreach (float x in new[] { -.03f, .17f, .18f, .465f, .76f, .82f, 1.03f, 255.97f, -257.18f, 16384.17f })
        foreach (float y in new[] { .23f, .24f, .51f, .75f, .78f, 15.97f, -17.03f })
        foreach (var footprint in new[] { new Vector2(.009f, .017f), new Vector2(.199f, .201f), new Vector2(.93f, .87f) })
            result.Add(new($"{(roblox ? "roblox" : "normal")}-edge-{result.Count}", roblox,
                new(x, y), footprint, 255, true, Wall, Glass, Lamp, roblox ? .0004 : .012));

        var random = new Random(75891);
        for (int i = 0; i < 96; i++)
        {
            bool roblox = (i & 1) != 0;
            result.Add(new($"{(roblox ? "roblox" : "normal")}-random-{i}", roblox,
                new((float)(random.NextDouble() * 1024 - 512), (float)(random.NextDouble() * 96 - 48)),
                new((float)(.01 + random.NextDouble() * .99), (float)(.01 + random.NextDouble() * .99)),
                (uint)random.Next(256), i % 3 != 0, Wall, Glass, Lamp, roblox ? .0004 : .012));
        }

        // Complete lamp periods have an exact mean independently of camera
        // phase. The tolerance includes the RG8 mip's half-byte rounding.
        foreach (bool roblox in new[] { false, true })
        foreach (var room in new[] { new Vector2(-257.93f, -17.01f), new Vector2(.17f, .23f), new Vector2(255.97f, 15.99f) })
        foreach (var footprint in new[] { new Vector2(256, 16), new Vector2(512, 32) })
            result.Add(new($"{(roblox ? "roblox" : "normal")}-far-energy-{result.Count}", roblox,
                room, footprint, 127, true, Wall, Glass, Lamp, roblox ? .0004 : .002));

        // Roblox's gradient must also integrate through several complete rooms
        // and strongly anisotropic footprints, without a fract() colour jump.
        foreach (var footprint in new[] { new Vector2(.03f, 8.7f), new Vector2(9.3f, .07f), new Vector2(3.7f, 4.6f) })
        foreach (float phase in new[] { -.01f, 0, .01f, .49f, .99f, 1.01f })
            result.Add(new($"roblox-tint-moment-{result.Count}", true, new(phase, phase), footprint,
                0, false, Wall, Glass, Lamp, .0004));

        foreach (uint seed in new uint[] { 0, 127, 255 })
        foreach (bool night in new[] { false, true })
        foreach (var room in new[] { new Vector2(.46f, .49f), new Vector2(.04f, .04f) })
            result.Add(new($"normal-resolved-{result.Count}", false, room, new(.01f),
                seed, night, Wall, Glass, Lamp, .0004));
        return result;
    }

    // Enumerate intersected rooms rather than using the production periodic
    // primitive, centroid weights, mip lookup, or any copied float algorithm.
    private static Weights Integrate(Sample sample)
    {
        double width = Math.Max(.0001, sample.Footprint.X), height = Math.Max(.0001, sample.Footprint.Y);
        double left = sample.Room.X - width * .5, right = sample.Room.X + width * .5;
        double top = sample.Room.Y - height * .5, bottom = sample.Room.Y + height * .5;
        double startX = sample.Roblox ? .18 : .17, endX = sample.Roblox ? .82 : .76;
        double startY = sample.Roblox ? .24 : .23, endY = sample.Roblox ? .78 : .75;
        double windows = 0, lamps = 0, tint = 0;
        for (int y = (int)Math.Floor(top); y <= Math.Floor(bottom); y++)
        {
            double first = Math.Max(top, y + startY), last = Math.Min(bottom, y + endY);
            if (last <= first) continue;
            for (int x = (int)Math.Floor(left); x <= Math.Floor(right); x++)
            {
                double across = Math.Max(0, Math.Min(right, x + endX) - Math.Max(left, x + startX));
                double area = across * (last - first);
                windows += area;
                if (PixelWindowFilter.Rooms[(unchecked((int)sample.Seed) + x + y * 16) & 255] == 0)
                    lamps += area;
                // Integrate the linear pane gradient over the clipped interval.
                tint += across * (.12 * (last - first) + .11 *
                    ((last - y) * (last - y) - (first - y) * (first - y)));
            }
        }
        double areaTotal = width * height;
        return new(windows / areaTotal, lamps / areaTotal, tint / areaTotal);
    }

    private static Expected Shade(Sample sample, Weights weights)
    {
        double[] wall = Linear(sample.Wall), glass = Linear(sample.Glass), lamp = Linear(sample.Lamp);
        var albedo = new double[3];
        var emission = new double[3];
        if (sample.Roblox)
        {
            wall = Linear(Vector3.Clamp(sample.Wall * 1.12f + new Vector3(10f / 255), Vector3.Zero, Vector3.One));
            var tint = Linear(new Vector3(225, 246, 250) / 255);
            for (int c = 0; c < 3; c++)
                albedo[c] = wall[c] * (1 - weights.Coverage) + glass[c] * weights.Coverage +
                    (tint[c] - glass[c]) * weights.Tint;
        }
        else
        {
            for (int c = 0; c < 3; c++)
            {
                albedo[c] = wall[c] * (1 - weights.Coverage) + glass[c] * (weights.Coverage - weights.Lamps) + lamp[c] * weights.Lamps;
                emission[c] = sample.Night ? lamp[c] * 2.5 * weights.Lamps : 0;
            }
        }
        double wallRoughness = sample.Roblox ? .32 : .82, glassRoughness = sample.Roblox ? .16 : .18;
        return new(sample.Name, albedo, Math.Sqrt(wallRoughness * wallRoughness * (1 - weights.Coverage) +
            glassRoughness * glassRoughness * weights.Coverage), emission,
            (sample.Roblox ? .2 : .28) * weights.Coverage, sample.Tolerance);
    }

    private static double[] Linear(Vector3 value) => [Decode(value.X), Decode(value.Y), Decode(value.Z)];
    private static double Decode(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);

    private static void CheckSpatialReference()
    {
        const int side = 768;
        double worst = 0;
        // A midpoint supersampler is deliberately independent of intersection
        // enumeration. Only the original point window/lamp decisions are used.
        foreach (var sample in Cases().Where((value, index) => index % 37 == 0 &&
            value.Footprint.X <= 1 && value.Footprint.Y <= 1).Take(24))
        {
            double windows = 0, lamps = 0, tint = 0;
            for (int y = 0; y < side; y++)
            {
                double py = sample.Room.Y + ((y + .5) / side - .5) * sample.Footprint.Y;
                int story = (int)Math.Floor(py);
                double v = py - story;
                if (!(v > (sample.Roblox ? .24 : .23) && v < (sample.Roblox ? .78 : .75))) continue;
                for (int x = 0; x < side; x++)
                {
                    double px = sample.Room.X + ((x + .5) / side - .5) * sample.Footprint.X;
                    int column = (int)Math.Floor(px);
                    double u = px - column;
                    if (!(u > (sample.Roblox ? .18 : .17) && u < (sample.Roblox ? .82 : .76))) continue;
                    windows++;
                    if (PixelWindowFilter.Rooms[(unchecked((int)sample.Seed) + column + story * 16) & 255] == 0) lamps++;
                    tint += .12 + v * .22;
                }
            }
            double count = side * side;
            var integral = Integrate(sample);
            worst = Math.Max(worst, Math.Abs(integral.Coverage - windows / count));
            worst = Math.Max(worst, Math.Abs(integral.Lamps - lamps / count));
            worst = Math.Max(worst, Math.Abs(integral.Tint - tint / count));
        }
        Assert(worst < .0028, $"Façade spatial-integral oracle disagrees with independent 768² point supersampling: {worst:F7}.");
    }

    private static void CheckPartitionEnergy()
    {
        foreach (bool roblox in new[] { false, true })
        {
            double coverage = 0, lamps = 0, tint = 0;
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 256; x++)
            {
                var value = Integrate(new("partition", roblox, new(x + .131f, y + .917f), Vector2.One,
                    255, true, Wall, Glass, Lamp));
                coverage += value.Coverage;
                lamps += value.Lamps;
                tint += value.Tint;
            }
            double duty = roblox ? .64 * .54 : .59 * .52;
            double density = PixelWindowFilter.Rooms.Count(value => value == 0) / 256d;
            Near(coverage / 4096, duty, .0000001, "Periodic window area was not conserved by a phase-shifted pixel partition.");
            Near(lamps / 4096, duty * density, .0000001, "Periodic lamp energy was not conserved by a phase-shifted pixel partition.");
            Near(tint / 4096, duty * (.12 + (roblox ? .51 : .49) * .22), .0000001,
                "The periodic pane-gradient first moment did not conserve its average tint.");
        }
    }

    private static void CheckResolvedRooms()
    {
        foreach (var sample in Cases().Where(value => value.Name.StartsWith("normal-resolved-", StringComparison.Ordinal)))
        {
            var weights = Integrate(sample);
            Near(weights.Coverage, sample.Room.X > .17 ? 1 : 0, .0000001,
                "A resolved window interior or opaque wall has incorrect coverage.");
            if (weights.Coverage == 0) continue;
            double pointLamp = PixelWindowFilter.Rooms[sample.Seed & 255] == 0 ? 1 : 0;
            Near(weights.Lamps, pointLamp, .0000001, "Resolved lamp occupancy changed inside a complete window.");
        }
    }

    private static void Near(double actual, double expected, double tolerance, string message) =>
        Assert(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message} Expected {expected:R}, got {actual:R}.");
    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private const string FixtureShader = """
        @group(0) @binding(0) var rooms:texture_2d<f32>;
        @group(0) @binding(1) var roomSampler:sampler;
        struct FacadeCase {
            roomFootprint:vec4f,
            settings:vec4f,
            wall:vec4f,
            glass:vec4f,
            lamp:vec4f,
        }
        @group(0) @binding(2) var<storage,read> cases:array<FacadeCase>;
        struct Result {
            @location(0) albedoRoughness:vec4f,
            @location(1) emissionMetallic:vec4f,
        }
        @vertex fn vs(@builtin(vertex_index) i:u32)->@builtin(position) vec4f {
            let p=vec2f(f32((i<<1u)&2u),f32(i&2u));
            return vec4f(p*2.0-1.0,0.0,1.0);
        }
        @fragment fn fs(@builtin(position) p:vec4f)->Result {
            let sample=cases[u32(p.x)];
            var surface:FacadeMaterial;
            if(sample.settings.x>0.5){
                surface=robloxFacade(sample.roomFootprint.xy,sample.roomFootprint.zw,sample.wall.rgb,sample.glass.rgb);
            }else{
                surface=normalFacade(sample.roomFootprint.xy,sample.roomFootprint.zw,u32(sample.settings.y),
                    sample.wall.rgb,sample.glass.rgb,sample.lamp.rgb,sample.settings.z>0.5);
            }
            var result:Result;
            result.albedoRoughness=vec4f(surface.albedo,surface.roughness);
            result.emissionMetallic=vec4f(surface.emission,surface.metallic);
            return result;
        }
        """;
}
