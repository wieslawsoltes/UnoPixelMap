using System.Reflection;
using System.Runtime.CompilerServices;
using PixelMap.Rendering;
using SkiaSharp;

internal static class GpuDepthChecks
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Run()
    {
        var assembly = typeof(PixelMapRenderer).Assembly;
        var homogeneous = assembly.GetType("PixelMap.Rendering.H")!;
        var depth = assembly.GetType("PixelMap.Rendering.GpuSceneDepth")!;
        var vertex = depth.GetNestedType("Vertex", BindingFlags.NonPublic)!;
        var triangle = depth.GetNestedType("Triangle", BindingFlags.NonPublic)!;
        var vertexConstructor = vertex.GetConstructors(Members).Single();
        var triangleConstructor = triangle.GetConstructors(Members).Single();
        var evaluateDepth = triangle.GetMethod("EvaluateDepth", Members)!;
        var evaluate = triangle.GetMethod("Evaluate", Members)!;
        var contains = triangle.GetMethod("Contains", Members)!;
        var preparedPlanes = triangle.GetField("preparedPlanes", Members)!;
        var size = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(triangle);
        Assert((int)size.Invoke(null, null)! == 84, "Depth planes and one source-order key exceeded their per-triangle storage budget.");
        int comparisons = 0;
        foreach (bool perspective in new[] { false, true })
        foreach (bool terrain in new[] { false, true })
        foreach (float weight in new[] { .0005f, .125f, 1f, 4.75f })
        foreach (bool reverse in new[] { false, true })
        {
            var a = Point(11, 13, perspective ? weight : 1, 8.5f, 1000.25f);
            var b = Point(83, 31, perspective ? weight * 1.7f : 1, -32.25f, -16.5f);
            var c = Point(29, 101, perspective ? weight * .65f : 1, 286.75f, 2320.125f);
            if (reverse) (b, c) = (c, b);
            object value = triangleConstructor.Invoke([Vertex(a), Vertex(b), Vertex(c), terrain, perspective, 0]);
            Assert((bool)triangle.GetField("Valid", Members)!.GetValue(value)!, "A depth fixture became degenerate.");
            int prepared = (terrain || perspective ? 0 : 1) | (terrain || !perspective ? 0 : 2) | (terrain ? 0 : 4);
            Assert((byte)preparedPlanes.GetValue(value)! == prepared, "A triangle eagerly prepared an unused depth plane.");
            contains.Invoke(value, [20.5f, 21.5f]);
            Assert((byte)preparedPlanes.GetValue(value)! == prepared, "Containment prepared interpolation coefficients.");
            foreach (var sample in new[] { new SKPoint(20.5f, 21.5f), new(39.17f, 48.83f), new(63.5f, 51.5f), new(10.25f, 97.75f) })
            {
                EvaluateReference(a, b, c, sample, out float altitude, out float inverse, out float physical);
                float actual = (float)evaluateDepth.Invoke(value, [sample.X, sample.Y, perspective])!;
                prepared |= perspective ? 1 : 3;
                Assert((byte)preparedPlanes.GetValue(value)! == prepared, "A depth query prepared unrelated height channels.");
                Assert(Exact(actual, perspective ? inverse : altitude),
                    "Selecting the visible depth channel changed interpolation arithmetic.");
                var gradients = Gradients(value);
                Assert(Exact((float)evaluateDepth.Invoke(value, [sample.X, sample.Y, perspective])!, actual) &&
                    gradients.SequenceEqual(Gradients(value)), "A cached interpolation plane was prepared again.");
                if (terrain)
                {
                    object[] arguments = [sample.X, sample.Y, 0f, 0f, 0f];
                    evaluate.Invoke(value, arguments);
                    prepared = 7;
                    Assert((byte)preparedPlanes.GetValue(value)! == prepared, "A terrain query omitted a required interpolation plane.");
                    Assert(Exact((float)arguments[2], altitude) && Exact((float)arguments[3], inverse) &&
                        Exact((float)arguments[4], physical), "Terrain depth or physical-height interpolation changed.");
                }
                comparisons++;
            }
            if (!terrain)
            {
                string unused = perspective ? "altitude" : "inverse";
                foreach (string field in new[] { unused + "X", unused + "Y", "rawX", "rawY" })
                    Assert((float)triangle.GetField(field, Members)!.GetValue(value)! == 0,
                        "A building triangle prepared an unused interpolation plane.");
            }
        }
        Console.WriteLine($"Passed {comparisons} exact GPU depth interpolation comparisons across projection, terrain, near-plane weights and winding.");
        ClipCodes();

        int[] Gradients(object value) => new[] { "inverseX", "inverseY", "altitudeX", "altitudeY", "rawX", "rawY" }
            .Select(name => BitConverter.SingleToInt32Bits((float)triangle.GetField(name, Members)!.GetValue(value)!)).ToArray();

        object Vertex(SampleVertex sample)
        {
            object screen = Activator.CreateInstance(homogeneous, [sample.X, sample.Y, sample.W, sample.Z, 0f])!;
            return vertexConstructor.Invoke([screen, sample.Height, -1]);
        }
    }

    private static void ClipCodes()
    {
        var assembly = typeof(PixelMapRenderer).Assembly;
        var homogeneous = assembly.GetType("PixelMap.Rendering.H")!;
        var transform = assembly.GetType("PixelMap.Rendering.TileTransform")!;
        var plane = transform.GetMethod("Plane", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var renderer = new PixelMapRenderer();
        int comparisons = 0;
        foreach (var camera in new[] { new MapCamera(0, 0, 16, 23, 55),
            MapCamera.FromEye(0, 0, 1.7, 23, 90, 391, 233, viewDistanceMeters: 6000) })
        {
            using var frame = renderer.PrepareGpuFrame(391, 233, camera, [], pixelSize: 3);
            object depth = typeof(GpuMapFrame).GetProperty("Depth", Members)!.GetValue(frame)!;
            var type = depth.GetType();
            var outCode = type.GetMethod("OutCode", Members)!;
            object lens = type.GetField("lens", Members)!.GetValue(depth)!;
            float near = (float)transform.GetProperty("Near", Members)!.GetValue(lens)!;
            float far = (float)transform.GetProperty("Far", Members)!.GetValue(lens)!;
            float width = (int)type.GetField("width", Members)!.GetValue(depth)!;
            float height = (int)type.GetField("height", Members)!.GetValue(depth)!;
            foreach (float weight in new[] { -1f, -0f, 0f, MathF.BitDecrement(near), near, MathF.BitIncrement(near),
                1f, MathF.BitDecrement(far), far, MathF.BitIncrement(far), float.NaN, float.NegativeInfinity, float.PositiveInfinity })
            foreach (float x in new[] { -.001f, -0f, 0f, .5f, 1f, 1.001f })
            foreach (float y in new[] { -.001f, -0f, 0f, .5f, 1f, 1.001f })
            {
                object vertex = Activator.CreateInstance(homogeneous, [x * width * weight, y * height * weight, weight, 0f, 0f])!;
                int expected = 0;
                for (int side = 0; side < 6; side++)
                    if ((float)plane.Invoke(null, [side, vertex, width, height, far, near])! < 0) expected |= 1 << side;
                Assert((int)outCode.Invoke(depth, [vertex, far])! == expected,
                    "Direct clipping codes changed a homogeneous plane or exact boundary.");
                comparisons++;
            }
        }
        Console.WriteLine($"Passed {comparisons} exact direct clipping-code comparisons across viewport and near/far boundaries.");
    }

    private static SampleVertex Point(float x, float y, float weight, float altitude, float height) =>
        new(x * weight, y * weight, weight, altitude, height);

    // Keep the original full interpolation arithmetic as the independent reference.
    private static void EvaluateReference(SampleVertex va, SampleVertex vb, SampleVertex vc, SKPoint sample,
        out float altitude, out float inverse, out float physical)
    {
        var a = new SKPoint(va.X / va.W, va.Y / va.W);
        var b = new SKPoint(vb.X / vb.W, vb.Y / vb.W);
        var c = new SKPoint(vc.X / vc.W, vc.Y / vc.W);
        float dx1 = b.X - a.X, dy1 = b.Y - a.Y, dx2 = c.X - a.X, dy2 = c.Y - a.Y;
        float determinant = dx1 * dy2 - dx2 * dy1;
        float ai = 1 / va.W, bi = 1 / vb.W, ci = 1 / vc.W;
        (float inverseX, float inverseY) = Plane(ai, bi, ci);
        float z = va.Z * ai;
        (float altitudeX, float altitudeY) = Plane(z, vb.Z * bi, vc.Z * ci);
        float raw = va.Height * ai;
        (float rawX, float rawY) = Plane(raw, vb.Height * bi, vc.Height * ci);
        float ox = sample.X - a.X, oy = sample.Y - a.Y;
        inverse = ai + inverseX * ox + inverseY * oy;
        float reciprocal = 1 / inverse;
        altitude = (z + altitudeX * ox + altitudeY * oy) * reciprocal;
        physical = (raw + rawX * ox + rawY * oy) * reciprocal;

        (float X, float Y) Plane(float x, float y, float z) =>
            (((y - x) * dy2 - (z - x) * dy1) / determinant, (dx1 * (z - x) - dx2 * (y - x)) / determinant);
    }

    private static bool Exact(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private readonly record struct SampleVertex(float X, float Y, float W, float Z, float Height);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
