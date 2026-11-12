using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using PixelMap.Rendering;

internal static class WebGpuBloomChecks
{
    internal static void Run()
    {
        ExactAllocationPlans();
        AttachmentBudgetBoundaries();
        var down = Taps(WebGpuBloomShaders.DownsampleKernel);
        var up = Taps(WebGpuBloomShaders.UpsampleKernel);
        KernelInvariants(down, 13, 1.5f);
        KernelInvariants(up, 9, .5f);
        RadianceAndHue(down, up);
        ImpulseTransport(down, up);
        Console.WriteLine("Passed HDR bloom checks: exact odd/degenerate pyramid allocations, optional photograph and quality budgets, normalized symmetric kernels, constant radiance, HDR exposure/hue linearity and interior impulse energy/spread. Shader execution is verified separately in browser CI.");
    }

    private static void ExactAllocationPlans()
    {
        // Independent inventories: resolved color8 + depth4 per sample,
        // plus multisample color8 per sample when present, optional photo8.
        Assert(WebGpuBloomLayout.Plan(1, 1, 1, false, 6) == new WebGpuAttachmentPlan(1, 1, 1, false, 0, 12, 0),
            "A 1x1 scene allocated a redundant bloom or photograph target.");
        Assert(WebGpuBloomLayout.Plan(7, 5, 4, false, 6) == new WebGpuAttachmentPlan(7, 5, 4, false, 3, 1960, 264),
            "Odd bloom extents must be 4x3, 2x2, 1x1 with only two reconstruction targets.");
        Assert(WebGpuBloomLayout.Plan(1, 9, 4, true, 6) == new WebGpuAttachmentPlan(1, 9, 4, true, 4, 576, 168),
            "A narrow scene must retain 1x5, 1x3, 1x2, 1x1 bloom levels.");
        Assert(WebGpuBloomLayout.Plan(2, 2, 4, false, 6).TotalBytes == 232,
            "A single bloom level must not allocate an unused reconstruction texture.");
        foreach (int width in new[] { 1, 3, 17, 1920, 3841 })
        foreach (int height in new[] { 1, 5, 19, 1080, 2161 })
        foreach (int samples in new[] { 1, 4 })
        foreach (int levels in new[] { 0, 1, 5, 6 })
        {
            var plain = WebGpuBloomLayout.Plan(width, height, samples, false, levels);
            var photo = WebGpuBloomLayout.Plan(width, height, samples, true, levels);
            Assert(photo.TotalBytes - plain.TotalBytes == (long)width * height * 8,
                "Photograph storage must cost exactly one optional RGBA16F image.");
            Assert(plain.BloomLevels <= levels && plain.BloomBytes >= 0 && plain.TotalBytes >= plain.SceneBytes,
                "An allocation plan overflowed or exceeded the requested level count.");
            if (levels == 0) Assert(plain.BloomBytes == 0 && plain.BloomLevels == 0,
                "Pixel/Minecraft/Balanced allocation plans must contain no bloom targets.");
        }
        foreach (var quality in Enum.GetValues<WebGpuQuality>())
        {
            var options = WebGpuRenderOptions.For(quality);
            Assert(options.BloomLevels == quality switch { WebGpuQuality.Balanced => 0, WebGpuQuality.High => 5, _ => 6 },
                "The advertised quality preset changed its bounded bloom depth.");
        }
    }

    private static void AttachmentBudgetBoundaries()
    {
        const long budget = 256L * 1024 * 1024;
        var random = new Random(4931);
        for (int i = 0; i < 600; i++)
        {
            uint width = (uint)random.Next(1, 32769), height = (uint)random.Next(1, 32769);
            int samples = (i & 1) == 0 ? 1 : 4, levels = i % 3 == 0 ? 0 : i % 3 == 1 ? 5 : 6;
            bool photograph = (i & 2) != 0;
            long pixels = i % 5 == 0 ? 1 : i % 5 == 1 ? 65 : 8_294_400;
            var plan = WebGpuBloomLayout.Fit(width, height, 8192, pixels, budget, samples, photograph, levels);
            Assert(plan.Width >= 1 && plan.Height >= 1 && plan.Width <= width && plan.Height <= height &&
                plan.Width <= 8192 && plan.Height <= 8192 && (long)plan.Width * plan.Height <= pixels && plan.TotalBytes <= budget,
                "Scene fitting violated a dimension, pixel, or exact byte budget.");
        }
        var exact = WebGpuBloomLayout.Plan(1921, 1081, 4, true, 6);
        var atBoundary = WebGpuBloomLayout.Fit(1921, 1081, 8192, 8_294_400, exact.TotalBytes, 4, true, 6);
        Assert(atBoundary == exact, "An exactly fitting odd scene lost resolution.");
        var belowBoundary = WebGpuBloomLayout.Fit(1921, 1081, 8192, 8_294_400, exact.TotalBytes - 1, 4, true, 6);
        Assert(belowBoundary.TotalBytes < exact.TotalBytes && belowBoundary.Width < exact.Width && belowBoundary.Height < exact.Height,
            "A one-byte budget reduction must replan the actual attachment inventory.");
        var thin = WebGpuBloomLayout.Fit(1, uint.MaxValue, 8192, 17, budget, 4, false, 6);
        Assert(thin.Width == 1 && thin.Height == 17, "Clamping an axis to one must not evade the scene pixel budget.");
        bool rejected = false;
        try { WebGpuBloomLayout.Fit(1, 1, 8192, 1, 55, 4, false, 6); }
        catch (NotSupportedException) { rejected = true; }
        Assert(rejected, "An impossible minimum attachment budget was accepted.");
    }

    private static Vector3[] Taps(string shader)
    {
        var matches = Regex.Matches(shader, @"vec3f\((-?[\d.]+),(-?[\d.]+),([\d.]+)\)");
        return matches.Select(m => new Vector3(float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))).ToArray();
    }

    private static void KernelInvariants(Vector3[] taps, int count, float variance)
    {
        Assert(taps.Length == count, "The CPU invariant checks did not locate the submitted WGSL kernel.");
        Assert(taps.All(t => t.Z > 0) && taps.Sum(t => t.Z) == 1,
            "The HDR kernel must be a normalized nonnegative filter.");
        Assert(taps.Sum(t => t.X * t.Z) == 0 && taps.Sum(t => t.Y * t.Z) == 0,
            "The bloom kernel moves highlight centers.");
        Assert(taps.Sum(t => t.X * t.X * t.Z) == variance && taps.Sum(t => t.Y * t.Y * t.Z) == variance,
            "The bloom kernel's spatial support is not isotropic.");
        foreach (var tap in taps)
            Assert(taps.Contains(new(-tap.X, tap.Y, tap.Z)) && taps.Contains(new(tap.Y, tap.X, tap.Z)),
                "Bloom must be symmetric under axis reflection and exchange.");
    }

    private static void RadianceAndHue(Vector3[] down, Vector3[] up)
    {
        foreach (var extent in new[] { (1, 1), (1, 17), (37, 23), (128, 96) })
        foreach (var radiance in new[] { Vector3.Zero, new Vector3(.08f), new Vector3(.25f, 8, 2000) })
        {
            var input = new Image(extent.Item1, extent.Item2, radiance);
            foreach (int levels in new[] { 1, 5, 6 })
            {
                var filtered = Filter(input, levels, down, up);
                Assert(filtered.Pixels.All(p => Near(p, radiance, .0001f)),
                    "Bloom changed uniform radiance, including at clamped odd/one-pixel boundaries.");
            }
        }
        var dim = new Image(37, 23, Vector3.Zero);
        for (int y = 0; y < dim.Height; y++)
        for (int x = 0; x < dim.Width; x++)
            dim[x, y] = new Vector3(.3f, .08f, .02f) * ((x + y) % 5);
        var bright = new Image(dim.Width, dim.Height, Vector3.Zero);
        for (int i = 0; i < dim.Pixels.Length; i++) bright.Pixels[i] = dim.Pixels[i] * 16;
        var a = Filter(dim, 5, down, up); var b = Filter(bright, 5, down, up);
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            Assert(Near(a.Pixels[i] * 16, b.Pixels[i], .0001f),
                "Exposure and linear HDR scattering no longer commute across the old threshold.");
            Assert(MathF.Abs(a.Pixels[i].X * .08f - a.Pixels[i].Y * .3f) < .00001f &&
                MathF.Abs(a.Pixels[i].X * .02f - a.Pixels[i].Z * .3f) < .00001f,
                "Bloom changed the chromaticity of a constant-hue signal.");
        }
    }

    private static void ImpulseTransport(Vector3[] down, Vector3[] up)
    {
        // Even dimensions and an interior support avoid boundary extension and
        // odd-grid resampling. This checks the normalized scattering kernel's
        // energy independently of the GPU fixture's shader/pipeline execution.
        var image = new Image(128, 128, Vector3.Zero);
        var emission = new Vector3(12, 3, 1);
        image[64, 64] = emission;
        var filtered = Filter(image, 3, down, up);
        Vector3 sum = Vector3.Zero;
        double radius = 0;
        for (int y = 0; y < filtered.Height; y++)
        for (int x = 0; x < filtered.Width; x++)
        {
            var color = filtered[x, y]; sum += color;
            Assert(float.IsFinite(color.X) && color.X >= 0 && color.X <= emission.X,
                "The bloom filter produced a negative, nonfinite or overshooting impulse.");
            radius += color.X * ((x - 64) * (x - 64) + (y - 64) * (y - 64));
        }
        Assert(Near(sum, emission, .0001f), "The interior impulse gained or lost radiance across the normalized pyramid.");
        Assert(Math.Sqrt(radius / sum.X) > 8 && filtered[80, 64].X > 0,
            "The bloom pyramid did not produce a broad highlight footprint.");
        var scattered = Vector3.Lerp(emission, sum, .055f);
        Assert(Near(scattered, emission, .0001f), "Final scatter composition added energy to the normalized impulse.");
    }

    // Independent scalar image sampling of the weights declared in production
    // WGSL. Browser CI separately executes those shaders and checks GPU outputs.
    private static Image Filter(Image original, int maximumLevels, Vector3[] down, Vector3[] up)
    {
        var levels = new List<Image>();
        var source = original;
        for (int i = 0; i < maximumLevels && (source.Width > 1 || source.Height > 1); i++)
        {
            var target = new Image((source.Width + 1) / 2, (source.Height + 1) / 2, Vector3.Zero);
            for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
                target[x, y] = Convolve(source, (x + .5f) / target.Width, (y + .5f) / target.Height, down);
            levels.Add(target); source = target;
        }
        if (levels.Count == 0) return original;
        float scatter = float.Parse(Regex.Match(WebGpuBloomShaders.UpsampleKernel, @"BLOOM_SCATTER:f32=([\d.]+)").Groups[1].Value,
            CultureInfo.InvariantCulture);
        Assert(scatter > 0 && scatter < 1, "Pyramid reconstruction must be a convex scale combination.");
        for (int i = levels.Count - 2; i >= 0; i--)
        {
            var detail = levels[i]; var target = new Image(detail.Width, detail.Height, Vector3.Zero);
            for (int y = 0; y < target.Height; y++)
            for (int x = 0; x < target.Width; x++)
                target[x, y] = Vector3.Lerp(detail[x, y],
                    Convolve(source, (x + .5f) / target.Width, (y + .5f) / target.Height, up), scatter);
            source = target;
        }
        var result = new Image(original.Width, original.Height, Vector3.Zero);
        for (int y = 0; y < result.Height; y++)
        for (int x = 0; x < result.Width; x++) result[x, y] = source.Sample((x + .5f) / result.Width, (y + .5f) / result.Height);
        return result;
    }

    private static Vector3 Convolve(Image source, float x, float y, Vector3[] taps)
    {
        var color = Vector3.Zero;
        foreach (var tap in taps) color += source.Sample(x + tap.X / source.Width, y + tap.Y / source.Height) * tap.Z;
        return color;
    }

    private sealed class Image(int width, int height, Vector3 color)
    {
        internal readonly int Width = width, Height = height;
        internal readonly Vector3[] Pixels = Enumerable.Repeat(color, width * height).ToArray();
        internal Vector3 this[int x, int y] { get => Pixels[y * Width + x]; set => Pixels[y * Width + x] = value; }
        internal Vector3 Sample(float u, float v)
        {
            float x = u * Width - .5f, y = v * Height - .5f;
            int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
            int x0 = Math.Clamp(ix, 0, Width - 1), x1 = Math.Clamp(ix + 1, 0, Width - 1);
            int y0 = Math.Clamp(iy, 0, Height - 1), y1 = Math.Clamp(iy + 1, 0, Height - 1);
            return Vector3.Lerp(Vector3.Lerp(this[x0, y0], this[x1, y0], x - ix),
                Vector3.Lerp(this[x0, y1], this[x1, y1], x - ix), y - iy);
        }
    }

    private static bool Near(Vector3 a, Vector3 b, float relative) =>
        Vector3.Distance(a, b) <= relative * Math.Max(1, b.Length());
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
