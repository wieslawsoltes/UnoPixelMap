using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

if (args.Contains("--gpu-terrain", StringComparer.Ordinal))
{
    GlTerrainChecks.Run(args);
    return;
}

if (args.Contains("--styles", StringComparer.Ordinal))
{
    RenderStyleChecks.Run();
    return;
}

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
var topModel = Building(31, 62, 0, slab);
var lowModel = Building(32, 15, 0, slab);
var models = new[] { new MapTile(id, [topModel, lowModel]) };
using var testSurface = Render(camera, topModel, lowModel);
Assert(renderer.Pick(128, 85, width, height, camera, models) == topModel, "Roof picking missed the highest part");
Assert(renderer.Pick(128, 115, width, height, camera, models) == topModel, "Wall picking missed the visible façade");
Assert(renderer.Pick(128, 128, width, height, flat, [new MapTile(id, [courtyard])]) is null, "Picked a courtyard hole");
var road = lowModel with { Layer = "transportation", Type = GeometryType.LineString, Name = "Road", Paths = [[new(1948, 2048), new(2148, 2048)]] };
var poi = road with { Layer = "poi", Type = GeometryType.Point, Name = "Library", Paths = [[new(2048, 2048)]] };
var park = lowModel with { Layer = "park", Name = "Garden", Paths = [Box(-190, -190, 190, 190)] };
var groundFeatures = new[] { new MapTile(id, [park, road, poi]) };
Assert(renderer.Pick(129, 128, width, height, flat, groundFeatures) == poi, "Point picking priority changed");
Assert(renderer.Pick(150, 131, width, height, flat, groundFeatures) == road, "Road picking tolerance changed");
Assert(renderer.Pick(150, 151, width, height, flat, groundFeatures) == park, "Area picking failed");
// A tower wholly inside its tile casts tens of meters beyond the neighboring edge.
// The neighbor's cached terrain must update when the occluder arrives, exactly once
// even if the vector tile provider includes the same buffered feature on both sides.
var edgeCamera = camera with { Longitude = (id.X + 1d) / (1 << id.Z) * 360 - 180 };
var caster = Building(90, 300, 0, new TilePoint[] { new(3910, 1900), new(4010, 1900), new(4010, 2110), new(3910, 2110) });
var west = new MapTile(id, [caster]);
var east = new MapTile(id with { X = id.X + 1 }, []);
var copy = caster with { Paths = caster.Paths.Select(r => r.Select(p => new TilePoint(p.X - 4096, p.Y)).ToArray()).ToArray() };
using var seamRenderer = new PixelMapRenderer();
using var seamBitmap = new SKBitmap(width, height);
using var seamCanvas = new SKCanvas(seamBitmap);
seamRenderer.Draw(seamCanvas, width, height, edgeCamera, [east], pixelSize: 1);
var sunlit = seamBitmap.GetPixel(158, 141);
seamRenderer.Draw(seamCanvas, width, height, edgeCamera, [east, west], pixelSize: 1);
var shaded = seamBitmap.GetPixel(158, 141);
Assert(shaded != sunlit, "A neighboring tile clipped the tower shadow");
seamRenderer.Draw(seamCanvas, width, height, edgeCamera, [east with { Features = [copy] }, west], pixelSize: 1);
Assert(seamBitmap.GetPixel(158, 141) == shaded, "Buffered tile copies darkened a shadow twice");

Console.WriteLine("Passed projection, wrap, drag, anchored zoom, courtyard, raised base, depth order, picking and cross-tile shadow checks.");

ImageryChecks.Run();
NavigationChecks.Run();
PerspectiveChecks.Run();
TerrainCameraChecks.Run();
TerrainRenderingChecks.Run();
GpuDepthChecks.Run();
TerrainNavigationChecks.Run();
RingFixtures.Run();
RenderStyleChecks.Run();

if (args.Contains("--benchmark", StringComparer.Ordinal))
{
    await RenderingBenchmark.RunAsync(args);
}
else
{
    GpuRenderingChecks.Run();
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
