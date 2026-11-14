using System.Numerics;
using System.Text.Json;
using PixelMap.Rendering;
using Silk.NET.WebGPU;

/// <summary>Independent discrete art decisions and byte-color references. Native
/// checks validate their invariants; browser CI executes the actual WGSL and the
/// production resolve against them, including dark encoded colors and photos.</summary>
internal static class WebGpuPixelArtChecks
{
    private sealed record Probe(string Name, int Kind, Vector4 A, Vector4 B, uint Seed = 0);
    private sealed record Expected(string Name, double[] Values, double Tolerance);
    private sealed record Region(string Name, int Start, int Width, int Height, int Group);
    private static readonly Vector3 Sun = Vector3.Normalize(new(-.45f, -.55f, .82f));

    internal static void Run()
    {
        CheckAttachments();
        CheckPalette();
        var (probes, regions) = Probes();
        double[]? first = null;
        foreach (var region in regions)
        {
            var mask = probes.Skip(region.Start).Take(region.Width * region.Height).Select(p => Reference(p)[0]).ToArray();
            Assert(mask.Count(value => value == 1) > 0 && mask.Count(value => value == 2) > 0,
                "Readable façades must retain both window clusters and ledges.");
            Assert(mask.Count(value => value == 0) > mask.Length / 2,
                "Masonry remains the majority of the readable façade.");
            if (first is not null) Assert(first.SequenceEqual(mask), "Nested detail groups preserve the exact coherent pixel cluster.");
            first = mask;
            for (int y = 0; y < region.Height; y++)
            for (int x = 0; x < region.Width; x++)
            {
                if (mask[y * region.Width + x] != 1) continue;
                bool across = x > 0 && mask[y * region.Width + x - 1] == 1 || x + 1 < region.Width && mask[y * region.Width + x + 1] == 1;
                bool vertical = y > 0 && mask[(y - 1) * region.Width + x] == 1 || y + 1 < region.Height && mask[(y + 1) * region.Width + x] == 1;
                Assert(across && vertical, "No resolved window becomes an isolated one-pixel sparkle or stripe.");
            }
        }
        var tiny = probes.Where(p => p.Name.StartsWith("unresolved-", StringComparison.Ordinal));
        Assert(tiny.All(p => Reference(p)[0] == 0), "Unresolved façades must reduce to quiet material planes.");
        Assert(probes.Where(p => p.Kind == 2).Select(p => Reference(p)[0]).Distinct().Count() == 3,
            "Near roofs retain a base plane, service panel and readable highlight.");
        Assert(probes.Where(p => p.Name.StartsWith("roof-boundary-", StringComparison.Ordinal))
                .Select(p => Reference(p)[0]).SequenceEqual(new double[] {0, 16, 15, 0, 0, 16, 15, 0}),
            "Roof rectangle boundaries retain strict inside/outside decisions in both signed periods.");
        Console.WriteLine($"Passed {probes.Count} pixel-art reference probes: world-light ramps, nested connected façade clusters, quiet distant walls, sparse roof detail, selective edges, encoded-color bounds and exact 33.3%/40% attachment reductions at unchanged extent. Browser CI verifies production WGSL and color resolve.");
    }

    internal static void WriteFixture(string path)
    {
        Run();
        var (probes, regions) = Probes();
        var inputs = new List<float>(probes.Count * 12);
        foreach (var p in probes)
        {
            inputs.AddRange([p.A.X, p.A.Y, p.A.Z, p.A.W, p.B.X, p.B.Y, p.B.Z, p.B.W, p.Kind, p.Seed, 0, 0]);
        }
        var colors = new List<byte>();
        void Add(uint color) => colors.AddRange([(byte)(color >> 16), (byte)(color >> 8), (byte)color, 255]);
        // Every encoded byte, including dark values that would be damaged by an
        // accidental linear-unorm attachment, plus every authored material/ink.
        for (uint value = 0; value < 256; value++) Add(0xff000000 | value * 0x010101);
        foreach (var palette in PixelPalettes())
        {
            var table = new uint[palette.Materials.Length * WebGpuPixelArt.PaletteWidth];
            WebGpuPixelArt.WritePalette(palette.Materials, table);
            foreach (uint color in table) Add(color);
            foreach (uint color in palette.Inks) Add(color);
            Add(palette.Ground);
        }
        var displayPalette = new uint[PixelPalettes()[0].Materials.Length * WebGpuPixelArt.PaletteWidth];
        WebGpuPixelArt.WritePalette(PixelPalettes()[0].Materials, displayPalette);
        var fixture = new
        {
            width = probes.Count, height = 1, strideFloats = 12,
            wgsl = WebGpuPixelArtShading.Wgsl + FixtureShader,
            resolveWgsl = WebGpuMapShaders.Resolve,
            inputs,
            expected = probes.Select(p => new Expected(p.Name, Reference(p), p.Kind is 3 or 5 ? 1.01 / 255 : .0001)),
            regions, colors, displayPalette = displayPalette.Take(WebGpuPixelArt.PaletteWidth)
                .Select(c => new[] {(int)((c >> 16) & 255), (int)((c >> 8) & 255), (int)(c & 255)}).ToArray(),
            reference = "Independent double-precision world-normal sectors, enumeration of permitted cluster sizes and cell rectangles, and byte material/terrain/sky ramps. No production WGSL helper is evaluated to form expected results.",
            stability = "Façade detail is anchored to wall-local rooms and roof detail to model coordinates. Lighting uses world normals and the sun; camera position/bearing is absent from those decisions. Nested 1/2/4-room groups retain the same connected projected motif. The real map integration suite separately exercises camera and style transitions.",
            work = new { finalFacadePaletteLookups = 1, maximumResolvedLampLookups = 1, unresolvedLampLookups = 0,
                pixelSceneBytesPerPixel = 8, previousSceneBytesPerPixel = 12,
                pixelPhotographSceneBytesPerPixel = 12, previousPhotographSceneBytesPerPixel = 20 }
        };
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(fixture, new JsonSerializerOptions
            { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
        Console.WriteLine($"Wrote independent pixel-art WGSL/encoded-color fixture: {path}");
    }

    private static PixelPalette[] PixelPalettes() => [PixelPalette.For(PixelStyle.Day, MapRenderStyle.Pixel),
        PixelPalette.For(PixelStyle.Dusk, MapRenderStyle.Pixel), PixelPalette.For(PixelStyle.Night, MapRenderStyle.Pixel)];

    private static void CheckAttachments()
    {
        foreach (var (width, height) in new[] {(1, 1), (389, 223), (1200, 683), (3840, 2160)})
        foreach (bool photograph in new[] {false, true})
        {
            var actual = WebGpuPixelArt.Attachments(width, height, photograph);
            var before = WebGpuBloomLayout.Plan(width, height, 1, photograph, 0);
            Assert(actual.Width == before.Width && actual.Height == before.Height && actual.Samples == 1 && actual.BloomLevels == 0,
                "LDR specialization cannot reduce the art resolution, introduce AA or change the projection.");
            Assert(actual.TotalBytes == (long)width * height * (photograph ? 12 : 8), "Pixel attachment accounting matches actual formats.");
            Assert(actual.TotalBytes * (photograph ? 5 : 3) == before.TotalBytes * (photograph ? 3 : 2),
                "The reduced byte budget is derived from format sizes, not a claimed GPU timing result.");
        }
        foreach (var style in Enum.GetValues<MapRenderStyle>())
            Assert(WebGpuPixelArt.SceneFormat(style) == (style == MapRenderStyle.Pixel ? TextureFormat.Rgba8Unorm : TextureFormat.Rgba16float),
                "Normal, Minecraft and Roblox retain their HDR scene representation.");
        var keys = new[] {WebGpuPixelArt.PipelineIndex(TextureFormat.Rgba8Unorm, 1),
            WebGpuPixelArt.PipelineIndex(TextureFormat.Rgba16float, 1), WebGpuPixelArt.PipelineIndex(TextureFormat.Rgba16float, 4)};
        Assert(keys.Distinct().Count() == 3, "Pixel/Minecraft/Balanced transitions cannot alias an incompatible color pipeline.");
        foreach (var quality in Enum.GetValues<WebGpuQuality>())
        {
            var options = WebGpuRenderOptions.For(quality);
            Assert(options.SampleCount == (quality == WebGpuQuality.Balanced ? 1 : 4), "Continuous-style antialiasing remains intact.");
            Assert(options.MaximumScenePixels == (quality == WebGpuQuality.Balanced ? 2_073_600 : quality == WebGpuQuality.Ultra ? 8_294_400 : 3_686_400),
                "Continuous-style resolution budgets remain intact.");
        }
    }

    private static void CheckPalette()
    {
        foreach (var palette in PixelPalettes().Concat(new[] {PixelPalette.Day, PixelPalette.Dusk, PixelPalette.Night}))
        {
            var table = new uint[palette.Materials.Length * WebGpuPixelArt.PaletteWidth];
            WebGpuPixelArt.WritePalette(palette.Materials, table);
            for (int row = 0; row < palette.Materials.Length; row++)
            {
                var m = palette.Materials[row];
                ReadOnlySpan<uint> original = [m.Roof, m.LightWall, m.DarkWall, m.Rim, m.Trim, m.Glass, m.Lamp];
                Assert(table.AsSpan(row * WebGpuPixelArt.PaletteWidth, 7).SequenceEqual(original),
                    "Original material columns are byte-identical; non-Pixel shaders keep their exact inputs.");
                var ramp = table.AsSpan(row * WebGpuPixelArt.PaletteWidth + WebGpuPixelArt.WallRampStart, 4);
                Assert(ramp[0] == m.DarkWall && ramp[3] == m.LightWall, "World-light ramp retains both authored endpoints.");
                for (int channel = 0; channel < 3; channel++)
                {
                    uint a = (m.DarkWall >> (channel * 8)) & 255, b = (m.LightWall >> (channel * 8)) & 255;
                    foreach (uint color in ramp)
                    {
                        uint value = (color >> (channel * 8)) & 255;
                        Assert(value >= Math.Min(a, b) && value <= Math.Max(a, b), "Ramp never overshoots its encoded material range.");
                    }
                }
            }
            Assert(table.All(color => color >> 24 == 255), "Opaque material semantics survive the format specialization.");
        }
    }

    private static (List<Probe> Probes, List<Region> Regions) Probes()
    {
        var result = new List<Probe>();
        var regions = new List<Region>();
        for (int i = 0; i < 72; i++)
        {
            double angle = (i + .37) * Math.PI / 36;
            result.Add(new($"world-facing-{i}", 0, new((float)Math.Cos(angle), (float)Math.Sin(angle), 0, 0), new(Sun, 0)));
        }
        foreach (int group in new[] {1, 2, 4})
        {
            regions.Add(new($"connected-group-{group}", result.Count, 24, 24, group));
            for (int y = 0; y < 24; y++)
            for (int x = 0; x < 24; x++)
                result.Add(new($"facade-{group}-{x}-{y}", 1,
                    new((x + .5f) * group / 8, (y + .5f) * group / 8, group / 8f, group / 8f), new(32, 48, 0, 0)));
        }
        foreach (float footprint in new[] {.08f, .219f, .221f, .399f, .441f, .799f, .881f, 1.4f})
        foreach (float phase in new[] {-.801f, -.199f, .199f, .201f, .279f, .281f, .729f, .731f, .759f, .761f, 15.6f})
            result.Add(new($"phase-footprint-{result.Count}", 1, new(phase, phase, footprint, footprint), new(32, 48, 0, 0)));
        foreach (float phase in new[] {.2f, .5f, .7f, 4.5f, -2.5f})
        {
            result.Add(new($"unresolved-distance-{phase}", 1, new(phase, phase, 1.1f, 1.2f), new(64, 64, 0, 0)));
            result.Add(new($"unresolved-small-building-{phase}", 1, new(phase, phase, .1f, .1f), new(1, 1, 0, 0)));
        }
        foreach (uint seed in new uint[] {0, 5, 15})
        foreach (var point in new[] {new Vector2(0, 0), new Vector2(3, 2.1f), new Vector2(4, 4), new Vector2(-8, -6.05f), new Vector2(16, 13.95f)})
        foreach (float footprint in new[] {.1f, .6f, 2f})
            result.Add(new($"roof-{result.Count}", 2, new(point, footprint, footprint), default, seed));
        // Exact normalized decimal cutoffs can round to opposite sides in the
        // independent double reference and WGSL float32. Probe both sides with
        // a physical 1 cm offset instead; keep the semantic tolerance strict.
        foreach (var (period, x, lowerOutside, lowerInside, upperInside, upperOutside) in new[]
        {
            ("positive", 16f, 9.49f, 9.51f, 13.99f, 14.01f),
            ("negative", -8f, -.51f, -.49f, -6.01f, -5.99f)
        })
        foreach (var (edge, y) in new[]
        {
            ("lower-outside", lowerOutside), ("lower-inside", lowerInside),
            ("upper-inside", upperInside), ("upper-outside", upperOutside)
        })
            result.Add(new($"roof-boundary-{period}-{edge}", 2, new(x, y, .1f, .1f), default, 5));
        foreach (float light in new[] {-.5f, .1f, .3f, .705f, .9f, 1f})
        foreach (var color in new[] {new Vector3(8, 16, 24), new Vector3(52, 94, 115), new Vector3(123, 177, 138), new Vector3(226, 197, 151)})
            result.Add(new($"terrain-{result.Count}", 3, new(color / 255, light), default));
        foreach (var direction in new[] {Vector2.UnitX, Vector2.UnitY, -Vector2.UnitX, -Vector2.UnitY})
        foreach (float length in new[] {.2f, 1.999f, 2.001f, 20f})
            result.Add(new($"selective-edge-{result.Count}", 4, new(direction, length, 0), new(Sun, 0)));
        foreach (float position in new[] {0f, .1f, .249f, .251f, .499f, .501f, .749f, .751f, 1f})
            result.Add(new($"banded-sky-{position}", 5, new(new Vector3(23, 43, 64) / 255, position), new(new Vector3(65, 91, 112) / 255, 0)));
        return (result, regions);
    }

    private static double[] Reference(Probe p)
    {
        double Part(double value) => value - Math.Floor(value);
        double RoundByte(double value) => Math.Floor(Math.Clamp(value * 255, 0, 255) + .5) / 255;
        if (p.Kind == 0 || p.Kind == 4)
        {
            double nx = p.Kind == 0 ? p.A.X : p.A.Y, ny = p.Kind == 0 ? p.A.Y : p.A.X;
            double angle = Math.Atan2(ny, nx) - Math.Atan2(p.B.Y, p.B.X);
            double facing = Math.Cos(angle);
            if (p.Kind == 4) return [facing > .15 ? 3 : 4, p.A.Z >= 2 ? 1 : 0, 0, 1];
            int band = facing < -.5 ? 0 : facing < 0 ? 1 : facing < .5 ? 2 : 3;
            return [band, 0, 0, 1];
        }
        if (p.Kind == 1)
        {
            // Enumerate the bounded candidate footprints instead of copying the
            // shader's vector select sequence. Rectangle intersections are in
            // room coordinates, with an independent floor/cell decomposition.
            double Group(double footprint, double limit) => new[] {1d, 2, 4}.FirstOrDefault(value => footprint / value <= limit, 4);
            double gx = Group(p.A.Z, .22), gy = Group(p.A.W, .20);
            double cx = Math.Floor(p.A.X / gx), cy = Math.Floor(p.A.Y / gy);
            double part = 0, x = p.A.X - cx * gx, y = p.A.Y - cy * gy;
            if (p.A.Z / gx <= .22 && p.A.W / gy <= .20 && p.B.X / gx >= 1.25 && p.B.Y / gy >= 1.5)
            {
                bool across = x >= .20 * gx && x < .76 * gx;
                if (across && y >= .28 * gy && y < .73 * gy) part = 1;
                double ledge = Math.Clamp(p.A.W / gy, .07, .16);
                if (across && y >= (.28 - ledge) * gy && y < .28 * gy) part = 2;
            }
            return [part, cx, cy, gx * 4 + gy];
        }
        if (p.Kind == 2)
        {
            double part = 0;
            if (p.A.Z / 12 <= .12 && p.A.W / 10 <= .12)
            {
                double x = Part((p.A.X + (p.Seed & 3) * 3) / 12), y = Part((p.A.Y + ((p.Seed >> 2) & 3) * 2.5) / 10);
                if (x >= .20 && x < .65 && y >= .20 && y < .65)
                    part = y < .20 + Math.Max(.04, p.A.W / 10) ? 16 : 15;
            }
            return [part, 0, 0, 1];
        }
        if (p.Kind == 3)
        {
            double[] scale = p.A.W < .20 ? [.78, .84, .94] : p.A.W < .50 ? [.89, .94, .99] : p.A.W >= .84 ? [1.05, 1.02, .97] : [1, 1, 1];
            return [RoundByte(p.A.X * scale[0]), RoundByte(p.A.Y * scale[1]), RoundByte(p.A.Z * scale[2]), 1];
        }
        double t = Math.Min(3, Math.Floor(Math.Clamp(p.A.W, 0, 1) * 4)) / 3;
        return [RoundByte(p.A.X * (1 - t) + p.B.X * t), RoundByte(p.A.Y * (1 - t) + p.B.Y * t), RoundByte(p.A.Z * (1 - t) + p.B.Z * t), 1];
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private const string FixtureShader = """
        struct Probe {a:vec4f,b:vec4f,meta:vec4f,}
        @group(0) @binding(0) var<storage,read> probes:array<Probe>;
        @vertex fn vs(@builtin(vertex_index) id:u32)->@builtin(position) vec4f {
            let p=vec2f(f32((id<<1u)&2u),f32(id&2u));return vec4f(p*2.0-1.0,0.0,1.0);
        }
        @fragment fn fs(@builtin(position) position:vec4f)->@location(0) vec4f {
            let p=probes[u32(position.x)];let kind=u32(p.meta.x+0.5);
            if(kind==0u){return vec4f(f32(pixelWallBand(p.a.xyz,p.b.xyz)),0.0,0.0,1.0);}
            if(kind==1u){return pixelFacadeCell(p.a.xy,p.a.zw,p.b.xy);}
            if(kind==2u){return vec4f(f32(pixelRoofPart(p.a.xy,p.a.zw,u32(p.meta.y+0.5))),0.0,0.0,1.0);}
            if(kind==3u){return vec4f(pixelTerrainColor(p.a.xyz,p.a.w),1.0);}
            if(kind==4u){return vec4f(f32(pixelEdgePart(p.a.xy,p.b.xyz)),select(0.0,1.0,pixelEdgeVisible(p.a.z)),0.0,1.0);}
            return vec4f(pixelSkyColor(p.a.xyz,p.b.xyz,p.a.w),1.0);
        }
        """;
}
