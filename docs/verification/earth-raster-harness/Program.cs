using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

string output = args[0];
int count = int.Parse(args.ElementAtOrDefault(1) ?? "32");
Directory.CreateDirectory(output);
var tiles = new List<(string Name, ImageryTile Tile)>();
var assembly = typeof(ImageryTile).Assembly;
foreach (string resource in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
{
    const string prefix = "PixelMap.Imagery.";
    if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".jpg", StringComparison.Ordinal)) continue;
    string name = resource[prefix.Length..];
    var parts = name[..^4].Split('-').Select(int.Parse).ToArray();
    using var stream = assembly.GetManifestResourceStream(resource)!;
    using var encoded = new MemoryStream();
    stream.CopyTo(encoded);
    tiles.Add((name, new(new(parts[0], parts[1], parts[2]), encoded.ToArray())));
}
foreach (var (name, size) in new[] { ("smooth", 256), ("checker",256), ("gradient",256), ("noise",256), ("irregular",197), ("tiny",1), ("big",512) })
{
    int h = name == "irregular" ? 341 : size;
    using var bitmap = new SKBitmap(size, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
    var random = new Random(7257);
    for (int y = 0; y < h; y++)
    for (int x = 0; x < size; x++)
        bitmap.SetPixel(x, y, name switch {
            "smooth" => new((byte)(60+x/8), (byte)(75+y/8), (byte)(80+(x+y)/16)),
            "checker" => (x+y)%2==0 ? new(13, 27, 51) : new(194, 233, 255),
            "noise" => new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)),
            _ => new((byte)x, (byte)y, (byte)(x^y)) });
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    tiles.Add((name, new(new(14, 6000, 8000), encoded.ToArray())));
    tiles.Add((name+"-128", new(new(12, 5000, 1000), encoded.ToArray())));
}
var type = typeof(PixelMapRenderer).Assembly.GetType("PixelMap.Rendering.EarthRaster")!;
var method = type.GetMethod("Get", BindingFlags.Public | BindingFlags.Instance)!;
var samples = new List<object>();
var controls = new List<object>();
for (int sample = -9; sample < count; sample++)
{
    using var raster = (IDisposable)Activator.CreateInstance(type, true)!;
    var get = (Func<ImageryTile, bool, SKImage?>)method.CreateDelegate(typeof(Func<ImageryTile, bool, SKImage?>), raster);
    foreach (var (name, tile) in tiles)
    {
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var image = get(tile, false);
        if (image is null) throw new InvalidOperationException(name);
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocation;
        if (sample >= 0 || sample == -9) samples.Add(new { Name = name, Sample = sample, Milliseconds = elapsed, AllocatedBytes = allocated });
        if (sample == -9)
        {
            var img = get(tile, false)!;
            using var bitmap = SKBitmap.FromImage(img);
            controls.Add(new {Name=name, bitmap.Width, bitmap.Height, Hash=Convert.ToHexStringLower(SHA256.HashData(bitmap.GetPixelSpan()))});
        }
    }
}
var result = new {
    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Processors = Environment.ProcessorCount,
    Scope = "EarthRaster.Get through one bound delegate, including JPEG/PNG decode, linear-light resampling, palette lookup, dithering and decoded-image cache insertion/replacement. Fixture construction, delegate creation, output hashing and result allocation are excluded. Fresh EarthRaster instance per pass; static palette LUT remains warm after nine untimed passes. Sample -9 is the first cold pass, reported separately and excluded from warm summaries. No rendering or GPU submission is measured.",
    DataHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(ImageryTile).Assembly.Location))),
    SkiaHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(SKImage).Assembly.Location))),
    RendererHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(PixelMapRenderer).Assembly.Location))),
    HarnessHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))),
    Controls = controls, Samples = samples
};
File.WriteAllText(Path.Combine(output,"earth-cost.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented=true }));
Console.WriteLine(output);
