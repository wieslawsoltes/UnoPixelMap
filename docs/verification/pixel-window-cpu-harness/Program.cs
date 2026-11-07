using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

// Public APIs only: the identical compiled harness loads either frozen renderer.
const int width = 768, height = 576, phases = 16;
string output = Path.GetFullPath(Value("--output=") ?? "cpu-window-output");
string variant = Value("--variant=") ?? "unspecified";
int block = int.Parse(Value("--block=") ?? "0", CultureInfo.InvariantCulture);
int warmups = int.Parse(Value("--warmups=") ?? "32", CultureInfo.InvariantCulture);
int samples = int.Parse(Value("--samples=") ?? "64", CultureInfo.InvariantCulture);
if (warmups < phases || samples < 1 || samples > 4096) throw new ArgumentOutOfRangeException(nameof(args));
Directory.CreateDirectory(output);
var tile = new TileId(14, 8192, 8192);
var center = new RoutePoint(Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (tile.Y + .5) / (1 << tile.Z)))) * 180 / Math.PI,
    (tile.X + .5) / (1 << tile.Z) * 360 - 180);
MapTile[] tiles = [new(tile, [new("building", "", "", "", 701, 4096, GeometryType.Polygon,
    [new TilePoint[] { new(600, 2046), new(3496, 2046), new(3496, 2050), new(600, 2050) }],
    1000, 0, false, 0, 0, "")])];
var controls = new List<object>();
var measurements = new List<Measurement>();
foreach (var (name, distance, bearing, pixelSize) in new[]
{
    ("near", 64d, 0d, 4), ("distant", 320d, 0d, 4), ("grazing", 160d, 75d, 4),
    ("far", 720d, 20d, 4), ("near-native", 64d, 0d, 1)
})
{
    var cameras = Enumerable.Range(0, phases).Select(phase =>
    {
        var eye = center.Move(90, phase * 3.8 / phases).Move(180 + bearing, distance);
        return MapCamera.FromEye(eye.Latitude, eye.Longitude, 300, bearing, 90, width, height, viewDistanceMeters: 6000);
    }).ToArray();
    for (int i = 0; i < cameras.Length; i++)
        if (cameras[i] == cameras[(i + cameras.Length - 1) % cameras.Length])
            throw new InvalidOperationException("A repeated camera would hide CPU rasterization behind the retained picture cache.");
    using var renderer = new PixelMapRenderer();
    using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
    using var canvas = new SKCanvas(bitmap);
    var canvasHashes = new List<string>();
    var artworkHashes = new List<string>();
    foreach (var camera in cameras)
    {
        renderer.Draw(canvas, width, height, camera, tiles, PixelStyle.Night, pixelSize, null, false, false, null, MapRenderStyle.Pixel);
        canvas.Flush();
        canvasHashes.Add(Hash(bitmap.GetPixelSpan()));
        artworkHashes.Add(Hash(Artwork(bitmap.GetPixelSpan(), pixelSize)));
        if (canvasHashes.Count == 1)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(Path.Combine(output, name + ".png"));
            encoded.SaveTo(stream);
        }
    }
    // Warm every moving pose and both facade triangles before measuring. The final
    // warm camera differs from the first measured camera, including at cycle wraps.
    for (int i = 0; i < warmups; i++)
    {
        var camera = cameras[i % phases];
        renderer.Draw(canvas, width, height, camera, tiles, PixelStyle.Night, pixelSize, null, false, false, null, MapRenderStyle.Pixel);
        canvas.Flush();
    }
    for (int sample = 0; sample < samples; sample++)
    {
        int phase = (sample + warmups) % phases;
        var camera = cameras[phase];
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        double cpu = NativeTime.ThreadCpu();
        long start = Stopwatch.GetTimestamp();
        renderer.Draw(canvas, width, height, camera, tiles, PixelStyle.Night, pixelSize, null, false, false, null, MapRenderStyle.Pixel);
        canvas.Flush();
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        double cpuMilliseconds = NativeTime.ThreadCpu() - cpu;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocation;
        measurements.Add(new(name, pixelSize, variant, block, sample, phase, milliseconds, cpuMilliseconds, allocatedBytes));
    }
    string finalHash = Hash(bitmap.GetPixelSpan());
    int finalPhase = (samples - 1 + warmups) % phases;
    if (finalHash != canvasHashes[finalPhase]) throw new InvalidOperationException("A warm moving draw changed its control image.");
    controls.Add(new
    {
        Scene = name, PixelSize = pixelSize, DistanceMeters = distance, Bearing = bearing,
        CameraSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(cameras)),
        FullCanvasHashes = canvasHashes, ArtworkHashes = artworkHashes, FinalPhase = finalPhase, FinalHash = finalHash
    });
    var values = measurements.Where(value => value.Scene == name).Select(value => value.Milliseconds).Order().ToArray();
    Console.WriteLine($"CPU {variant} block {block} {name} p{pixelSize}: {values[values.Length / 2]:F4} ms median.");
}

var options = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(Path.Combine(output, "cpu-window.json"), JsonSerializer.Serialize(new
{
    Variant = variant, Block = block, Width = width, Height = height, Phases = phases, Warmups = warmups, Samples = samples,
    Palette = "Night", RenderStyle = "Pixel", Runtime = RuntimeInformation.FrameworkDescription,
    OperatingSystem = RuntimeInformation.OSDescription, Processors = Environment.ProcessorCount,
    RendererSha256 = Hash(File.ReadAllBytes(typeof(PixelMapRenderer).Assembly.Location)),
    DataSha256 = Hash(File.ReadAllBytes(typeof(MapTile).Assembly.Location)),
    RoutingSha256 = Hash(File.ReadAllBytes(typeof(RoutePoint).Assembly.Location)),
    SkiaSha256 = Hash(File.ReadAllBytes(typeof(SKBitmap).Assembly.Location)),
    HarnessSha256 = Hash(File.ReadAllBytes(typeof(Measurement).Assembly.Location)),
    Scope = "Complete CPU PixelMapRenderer.Draw plus SKCanvas.Flush into an RGBA8888 opaque SKBitmap; includes scene preparation, building rasterization and nearest artwork presentation. One label-free wall, flat terrain, no GPU path. Camera changes every draw. Fixture construction, image hashing, PNG capture, and result allocation are untimed. Managed allocation is per current thread; thread CPU uses CLOCK_THREAD_CPUTIME_ID. This is a whole-renderer baseline/candidate comparison, not an isolated filter-branch benchmark.",
    Controls = controls, Measurements = measurements
}, options));

string? Value(string prefix) => args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static byte[] Artwork(ReadOnlySpan<byte> pixels, int pixelSize)
{
    int artWidth = width / pixelSize, artHeight = height / pixelSize;
    var result = new byte[artWidth * artHeight * 4];
    for (int y = 0; y < artHeight; y++)
    for (int x = 0; x < artWidth; x++)
        pixels.Slice(((y * pixelSize + pixelSize / 2) * width + x * pixelSize + pixelSize / 2) * 4, 4)
            .CopyTo(result.AsSpan((y * artWidth + x) * 4));
    return result;
}

internal sealed record Measurement(string Scene, int PixelSize, string Variant, int Block, int Sample, int Phase,
    double Milliseconds, double ThreadCpuMilliseconds, long AllocatedBytes);

internal static class NativeTime
{
    internal static double ThreadCpu()
    {
        if (!OperatingSystem.IsLinux() || clock_gettime(3, out var time) != 0)
            throw new PlatformNotSupportedException("Thread CPU measurement requires Linux CLOCK_THREAD_CPUTIME_ID.");
        return time.Seconds * 1000d + time.Nanoseconds / 1e6;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Timespec { internal long Seconds, Nanoseconds; }
    [DllImport("libc")] private static extern int clock_gettime(int id, out Timespec result);
}
