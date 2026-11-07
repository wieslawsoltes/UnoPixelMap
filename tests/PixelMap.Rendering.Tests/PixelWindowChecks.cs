using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using Silk.NET.OpenGL;
using SkiaSharp;

internal static partial class PixelWindowChecks
{
    private const int Width = 768, Height = 576, PixelSize = 4;
    private const int ArtWidth = Width / PixelSize, ArtHeight = Height / PixelSize;
    private const int CropLeft = ArtWidth / 6, CropRight = ArtWidth / 2;
    private const int CropTop = ArtHeight / 4, CropBottom = ArtHeight * 3 / 4;
    private static readonly TileId Tile = new(14, 8192, 8192);
    private static readonly RoutePoint Center = new(Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (Tile.Y + .5) / (1 << Tile.Z)))) * 180 / Math.PI,
        (Tile.X + .5) / (1 << Tile.Z) * 360 - 180);
    private static readonly MapTile[] Tiles = [new(Tile, [new("building", "", "", "", 701, 4096,
        GeometryType.Polygon, [new TilePoint[] { new(600, 2046), new(3496, 2046), new(3496, 2050), new(600, 2050) }],
        1000, 0, false, 0, 0, "")])];
    private sealed record Result(string Name, PixelStyle Palette, double Distance, double Bearing,
        double ReferenceError, double MeanLuminanceError, double TemporalError, double CpuGpuError,
        double SubmissionMedianMs, double SubmissionP95Ms, string FirstFrameHash, string ReferenceHash,
        IReadOnlyList<string> FrameHashes, IReadOnlyList<string> CpuHashes);

    internal static void Run(string[] arguments)
    {
        bool legacy = arguments.Contains("--pixel-legacy", StringComparer.Ordinal);
        if (!legacy) CheckMath();
        string output = Path.GetFullPath(arguments.FirstOrDefault(a => a.StartsWith("--pixel-output=", StringComparison.Ordinal))?
            ["--pixel-output=".Length..] ?? Path.Combine(Path.GetTempPath(), "PixelMap.PixelWindows"));
        Directory.CreateDirectory(output);
        using var context = new HeadlessGlContext(Width, Height);
        var results = Measure(context.Gl, output, phases: 16);
        File.WriteAllText(Path.Combine(output, "pixel-windows.json"), JsonSerializer.Serialize(new
        {
            Context = context.Description,
            RendererSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GlMapRenderer).Assembly.Location))).ToLowerInvariant(),
            Reference = "Box average of 4x4 art samples from the unchanged Normal façade shader; central-left 64x72 art-pixel wall crop, excluding sky and geometric silhouettes. Timings measure warm GL submission plus completion, excluding preparation and readback.",
            Width, Height, PixelSize, Phases = 16, Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Pixel-window quality evidence: {output}");
    }

    internal static void RunGl(GL gl)
    {
        CheckMath();
        var results = Measure(gl, null, phases: 4);
        foreach (var result in results)
        {
            Assert(result.CpuGpuError < 2, $"{result.Name} {result.Palette} CPU/GPU footprint filtering diverged: {result.CpuGpuError:F3}.");
            if (result.Name == "near") continue;
            double limit = result.Name == "grazing"
                ? result.Palette == PixelStyle.Night ? 12 : 5
                : result.Palette == PixelStyle.Night ? 8 : 4;
            Assert(result.ReferenceError < limit && result.MeanLuminanceError < 1,
                $"{result.Name} {result.Palette} lost minified window detail or average lamp contribution against spatial supersampling: " +
                $"RMS {result.ReferenceError:F3}, mean luminance {result.MeanLuminanceError:F3}.");
        }
        Console.WriteLine("Passed resolved, distant and grazing Pixel façade fixtures, spatial-reference quality, retained lamp contribution and CPU/GPU footprint parity.");
    }

    private static void CheckMath()
    {
        PixelWindowColorChecks.Run();
        var random = new Random(32017);
        double worstCoverage = 0, worstLamp = 0;
        for (int i = 0; i < 4096; i++)
        {
            float x = (float)(random.NextDouble() * 514 - 257), y = (float)(random.NextDouble() * 34 - 17);
            float w = (float)(.21 + random.NextDouble() * .79), h = (float)(.21 + random.NextDouble() * .79);
            float coverX = PixelWindowFilter.Coverage(x, w, .17f, .76f), coverY = PixelWindowFilter.Coverage(y, h, .23f, .75f);
            double expectedX = Interval(x, w, .17, .76), expectedY = Interval(y, h, .23, .75);
            worstCoverage = Math.Max(worstCoverage, Math.Max(Math.Abs(coverX - expectedX), Math.Abs(coverY - expectedY)));
            if (coverX * coverY < .0001f) continue;
            uint seed = (uint)random.Next(256);
            double expected = 0;
            int firstX = (int)Math.Floor(x - w * .5), lastX = (int)Math.Floor(x + w * .5);
            int firstY = (int)Math.Floor(y - h * .5), lastY = (int)Math.Floor(y + h * .5);
            for (int row = firstY; row <= lastY; row++)
            for (int column = firstX; column <= lastX; column++)
                if (PixelWindowFilter.Rooms[((int)seed + column + row * 16) & 255] == 0)
                    expected += Overlap(x, w, column + .17, column + .76) * Overlap(y, h, row + .23, row + .75) / (w * h);
            float actual = coverX * coverY * PixelWindowFilter.Lamp(
                PixelWindowFilter.WeightedCell(x, w, .17f, .76f) + seed,
                PixelWindowFilter.WeightedCell(y, h, .23f, .75f), 0);
            worstLamp = Math.Max(worstLamp, Math.Abs(actual - expected));
        }
        Assert(worstCoverage < .0002 && worstLamp < .0003,
            $"Analytical window intersections changed at signed or periodic boundaries: {worstCoverage}, {worstLamp}.");
        float density = PixelWindowFilter.Rooms.Count(value => value == 0) / 256f;
        foreach (float x in new[] { -257.7f, -.1f, 0, 15.9f, 255.9f, 1024.3f })
        foreach (float y in new[] { -17.1f, -.1f, 0, 15.9f, 16.1f, 68.3f })
            Assert(Math.Abs(PixelWindowFilter.Lamp(x, y, 8) - density) <= .5f / 255,
                "The coarsest lamp mip lost the original average light density.");
        var material = PixelPalette.Night.Materials[0];
        uint wall = material.DarkWall;
        for (int i = 0; i < 2048; i++)
        {
            float x = (float)(random.NextDouble() * 256), y = (float)(random.NextDouble() * 16);
            int column = (int)x, story = (int)y;
            uint expected = x - column > .17f && x - column < .76f && y - story > .23f && y - story < .75f
                ? PixelWindowFilter.Rooms[(column + story * 16) & 255] == 0 ? material.Lamp : material.Glass : wall;
            Assert(PixelWindowFilter.Sample(wall, material, 0, x, y, .1f, .2f, 1) == expected,
                "Resolved Pixel windows changed their original palette or lamp decisions.");
        }
        Console.WriteLine($"Passed 4,096 signed window-footprint intersections (coverage {worstCoverage:F6}, lamps {worstLamp:F6}), mip energy and 2,048 exact resolved samples.");

        static double Interval(double p, double width, double start, double end)
        {
            double result = 0;
            for (int cell = (int)Math.Floor(p - width * .5); cell <= Math.Floor(p + width * .5); cell++)
                result += Overlap(p, width, cell + start, cell + end);
            return result / width;
        }
        static double Overlap(double p, double width, double start, double end) =>
            Math.Max(0, Math.Min(p + width * .5, end) - Math.Max(p - width * .5, start));
    }

    private static List<Result> Measure(GL gl, string? output, int phases)
    {
        using var scene = new PixelMapRenderer();
        using var referenceScene = new PixelMapRenderer();
        using var cpu = new PixelMapRenderer();
        using var renderer = new GlMapRenderer(gl);
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        var results = new List<Result>();
        foreach (var palette in new[] { PixelStyle.Day, PixelStyle.Night })
        foreach (var (name, distance, bearing) in new[] { ("near", 64d, 0d), ("distant", 320d, 0d), ("grazing", 160d, 75d), ("far", 720d, 20d) })
        {
            double square = 0, luminance = 0, temporal = 0, parity = 0;
            long count = 0, transitions = 0;
            var timings = new List<double>();
            var frameHashes = new List<string>();
            var cpuHashes = new List<string>();
            byte[]? previous = null, previousReference = null;
            string firstHash = "", referenceHash = "";
            for (int phase = 0; phase < phases; phase++)
            {
                var eye = Center.Move(90, phase * 3.8 / phases).Move(180 + bearing, distance);
                var camera = MapCamera.FromEye(eye.Latitude, eye.Longitude, 300, bearing, 90, Width, Height, viewDistanceMeters: 6000);
                using var frame = scene.PrepareGpuFrame(Width, Height, camera, Tiles, palette, PixelSize);
                using var referenceFrame = referenceScene.PrepareGpuFrame(Width, Height, camera, Tiles, palette, 1,
                    null, null, MapRenderStyle.Normal);
                byte[] reference = Downsample(Capture(gl, renderer, referenceFrame));
                // Retain source meshes and finish preceding work before timing the
                // moving view. The measured draw still submits its normal commands.
                Capture(gl, renderer, frame);
                gl.Finish();
                long start = Stopwatch.GetTimestamp();
                gl.Viewport(0, 0, ArtWidth, ArtHeight);
                renderer.DrawArtwork(frame);
                gl.Finish();
                timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                byte[] actual = Read(gl, ArtWidth, ArtHeight);
                cpu.Draw(canvas, Width, Height, camera, Tiles, palette, PixelSize, null, false, false, null, MapRenderStyle.Pixel);
                byte[] cpuPixels = PixelCenters(bitmap.GetPixelSpan());
                frameHashes.Add(Convert.ToHexString(SHA256.HashData(actual)));
                cpuHashes.Add(Convert.ToHexString(SHA256.HashData(cpuPixels)));
                double mean = 0;
                for (int y = CropTop; y < CropBottom; y++)
                for (int x = CropLeft; x < CropRight; x++)
                for (int channel = 0; channel < 3; channel++)
                {
                    int offset = (y * ArtWidth + x) * 4 + channel;
                    double error = actual[offset] - reference[offset];
                    square += error * error;
                    mean += error * (channel == 0 ? .2126 : channel == 1 ? .7152 : .0722);
                    parity += Math.Abs(actual[offset] - cpuPixels[offset]);
                    if (previous is not null)
                    {
                        double step = actual[offset] - previous[offset] - reference[offset] + previousReference![offset];
                        temporal += step * step;
                        transitions++;
                    }
                    count++;
                }
                luminance += Math.Abs(mean / ((CropRight - CropLeft) * (CropBottom - CropTop)));
                previous = actual;
                previousReference = reference;
                if (phase == 0)
                {
                    firstHash = Convert.ToHexString(SHA256.HashData(actual));
                    referenceHash = Convert.ToHexString(SHA256.HashData(reference));
                    if (output is not null)
                    {
                        Save(actual, Path.Combine(output, $"{name}-{palette}.png"));
                        Save(reference, Path.Combine(output, $"{name}-{palette}-reference.png"));
                        Save(cpuPixels, Path.Combine(output, $"{name}-{palette}-cpu.png"));
                    }
                }
            }
            timings.Sort();
            var result = new Result(name, palette, distance, bearing, Math.Sqrt(square / count), luminance / phases,
                Math.Sqrt(temporal / Math.Max(1, transitions)), parity / count, timings[timings.Count / 2],
                timings[(int)Math.Ceiling(timings.Count * .95) - 1], firstHash, referenceHash, frameHashes, cpuHashes);
            results.Add(result);
            Console.WriteLine($"Pixel {name} {palette}: reference RMS {result.ReferenceError:F3}, mean {result.MeanLuminanceError:F3}, temporal {result.TemporalError:F3}, CPU/GPU {result.CpuGpuError:F3}, submit+complete {result.SubmissionMedianMs:F3} ms.");
        }
        Assert(gl.GetError() == GLEnum.NoError, "Pixel-window filtering left a GL error.");
        return results;
    }

    private static byte[] Capture(GL gl, GlMapRenderer renderer, GpuMapFrame frame)
    {
        int width = (frame.Width + frame.PixelSize - 1) / frame.PixelSize;
        int height = (frame.Height + frame.PixelSize - 1) / frame.PixelSize;
        gl.Viewport(0, 0, (uint)width, (uint)height);
        renderer.DrawArtwork(frame);
        return Read(gl, width, height);
    }

    private static byte[] Read(GL gl, int width, int height)
    {
        var bytes = new byte[width * height * 4];
        gl.ReadPixels<byte>(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, bytes.AsSpan());
        var topDown = new byte[bytes.Length];
        for (int y = 0; y < height; y++) bytes.AsSpan((height - 1 - y) * width * 4, width * 4).CopyTo(topDown.AsSpan(y * width * 4));
        return topDown;
    }

    private static byte[] Downsample(ReadOnlySpan<byte> source)
    {
        var bytes = new byte[ArtWidth * ArtHeight * 4];
        for (int y = 0; y < ArtHeight; y++)
        for (int x = 0; x < ArtWidth; x++)
        for (int channel = 0; channel < 4; channel++)
        {
            int sum = 0;
            for (int row = 0; row < PixelSize; row++)
            for (int column = 0; column < PixelSize; column++)
                sum += source[((y * PixelSize + row) * Width + x * PixelSize + column) * 4 + channel];
            bytes[(y * ArtWidth + x) * 4 + channel] = (byte)((sum + PixelSize * PixelSize / 2) / (PixelSize * PixelSize));
        }
        return bytes;
    }

    private static byte[] PixelCenters(ReadOnlySpan<byte> source)
    {
        var bytes = new byte[ArtWidth * ArtHeight * 4];
        for (int y = 0; y < ArtHeight; y++)
        for (int x = 0; x < ArtWidth; x++)
            source.Slice(((y * PixelSize + PixelSize / 2) * Width + x * PixelSize + PixelSize / 2) * 4, 4)
                .CopyTo(bytes.AsSpan((y * ArtWidth + x) * 4));
        return bytes;
    }

    private static void Save(byte[] pixels, string path)
    {
        using var bitmap = new SKBitmap(ArtWidth, ArtHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        pixels.CopyTo(bitmap.GetPixelSpan());
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        png.SaveTo(stream);
    }

    private static void Assert(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
}
