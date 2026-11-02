using System.Diagnostics;
using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

const int width = 256, height = 256;
var id = new TileId(14, 8192, 8192);
double centerX = (id.X + .5) / (1 << id.Z);
double centerY = (id.Y + .5) / (1 << id.Z);
var camera = new MapCamera(Math.Atan(Math.Sinh(Math.PI * (1 - 2 * centerY))) * 180 / Math.PI,
    centerX * 360 - 180, 16, 0, 55);
using var renderer = new PixelMapRenderer();

foreach (var latitude in new[] { -75d, 0, 70 })
foreach (var longitude in new[] { -179.99, 179.99 })
foreach (var bearing in new[] { 0d, 45, 130, 290 })
{
    var c = new MapCamera(latitude, longitude, 12, bearing, 50);
    var p = c.Unproject(245, 150, 900, 600);
    var q = c.Project(p.Latitude, p.Longitude, 900, 600);
    var z = c.ZoomAt(2.5, 245, 150, 900, 600).Project(p.Latitude, p.Longitude, 900, 600);
    var drag = c.PanBy(8, 11).Project(latitude, longitude, 900, 600);
    Assert(Math.Abs(q.X - 245) + Math.Abs(q.Y - 150) + Math.Abs(z.X - 245) + Math.Abs(z.Y - 150)
        + Math.Abs(drag.X - 458) + Math.Abs(drag.Y - 311) < 1e-6, "Camera anchor or inverse changed");
}
var wrapped = (camera with { Longitude = 179.999, Zoom = 8 }).VisibleTiles(900, 600);
Assert(wrapped.Distinct().Count() == wrapped.Length && wrapped.Any(t => t.X == 0) && wrapped.Any(t => t.X == 255),
    "Antimeridian coverage must contain both sides exactly once");

var courtyard = Building(1, 20, 0, Box(-160, -160, 160, 160), Box(-64, -64, 64, 64).Reverse().ToArray());
var flat = camera with { Pitch = 0 };
using var plain = Render(flat);
using var court = Render(flat, courtyard);
Assert(court.GetPixel(128, 128) == plain.GetPixel(128, 128), "Courtyard roof filled over its hole");
Assert(court.GetPixel(72, 128) != plain.GetPixel(72, 128), "Courtyard test did not render its outer roof");

var slab = Box(-128, -36, 128, 36);
using var raised = Render(camera, Building(8, 62, 42, slab));
using var solid = Render(camera, Building(8, 62, 0, slab));
Assert(raised.GetPixel(128, 132) != solid.GetPixel(128, 132), "Wall filled the open area below min_height");

var tall = Building(131, 95, 5, Box(-90, -100, 65, 80));
var shortPart = Building(17, 24, 0, Box(-150, -50, 55, 140));
var oblique = camera with { Bearing = 31 };
using var frontFirst = Render(oblique, tall, shortPart);
using var backFirst = Render(oblique, shortPart, tall);
Assert(frontFirst.GetPixelSpan().SequenceEqual(backFirst.GetPixelSpan()), "Parts changed visible pixels when submitted in reverse order");
Console.WriteLine("Passed projection, wrap, drag, anchored zoom, courtyard, raised base and depth order checks.");

if (args.Contains("--benchmark", StringComparer.Ordinal))
{
    const int w = 1100, h = 650, samples = 48;
    using var http = new HttpClient();
    var source = new VectorTileSource(http);
    using var bitmap = new SKBitmap(w, h);
    using var canvas = new SKCanvas(bitmap);
    foreach (var (pitch, bearing) in new[] { (0d, 0d), (55d, -15d), (65d, 135d) })
    {
        var c = new MapCamera(40.7087, -74.0104, 16.5, bearing, pitch);
        var tiles = new List<MapTile>();
        foreach (var tile in c.VisibleTiles(w, h)) tiles.Add(await source.GetTileAsync(tile));
        using var instance = new PixelMapRenderer();
        for (int i = 0; i < 32; i++) instance.Draw(canvas, w, h, c with { Bearing = bearing + i % 7 }, tiles);
        var timings = new double[samples];
        for (int i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            instance.Draw(canvas, w, h, c with { Bearing = bearing + i % 7 }, tiles);
            timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(timings);
        Console.WriteLine($"CPU raster canvas, {w}×{h}, pixel size 2, pitch {pitch:F0}°, bearing {bearing:F0}° + [0..6]°: " +
            $"median {timings[samples / 2]:F2} ms, p95 {timings[(int)(samples * .95)]:F2} ms ({samples} frames)");
    }
}

SKBitmap Render(MapCamera c, params MapFeature[] features)
{
    var bmp = new SKBitmap(width, height);
    using var canvas = new SKCanvas(bmp);
    renderer.Draw(canvas, width, height, c, [new MapTile(id, features)], pixelSize: 1);
    return bmp;
}

static TilePoint[] Box(int x0, int y0, int x1, int y1) =>
    [new(2048 + x0, 2048 + y0), new(2048 + x1, 2048 + y0), new(2048 + x1, 2048 + y1), new(2048 + x0, 2048 + y1)];

static MapFeature Building(ulong id, double top, double bottom, params TilePoint[][] paths) =>
    new("building", "", "", "", id, 4096, GeometryType.Polygon, paths, top, bottom, false, 0, 0, "");

static void Assert(bool passed, string message)
{
    if (!passed) throw new InvalidOperationException(message);
}
