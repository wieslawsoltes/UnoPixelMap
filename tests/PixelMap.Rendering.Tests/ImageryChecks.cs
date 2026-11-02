using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

internal static class ImageryChecks
{
    private const int Size = 512;
    internal static void Run()
    {
        using var renderer = new PixelMapRenderer();
        using var bitmap = new SKBitmap(Size, Size);
        using var canvas = new SKCanvas(bitmap);
        var id = new TileId(14, 8192, 8192);
        var center = Geo(id, .5, .5);
        var camera = new MapCamera(center.Latitude, center.Longitude, 14);
        var source = MakeTile(id, static (x, y) => (x < 128, y < 128) switch
        {
            (true, true) => new(36, 95, 128), (false, true) => new(60, 104, 48),
            (true, false) => new(183, 114, 74), _ => new(228, 229, 218)
        });

        foreach (var pitch in new[] { 0d, 55d })
        {
            var c = camera with { Pitch = pitch, Bearing = 31 };
            Draw(c, [], [source]);
            var sea = At(c, id, .28, .28); var trees = At(c, id, .72, .28);
            var soil = At(c, id, .28, .72); var ice = At(c, id, .72, .72);
            Assert(sea.Blue > sea.Red + 25 && trees.Green > trees.Red && trees.Green > trees.Blue &&
                   soil.Red > soil.Blue + 30 && ice.Red > 175, "Image quad orientation or biome tones changed");
        }

        var edge = Geo(id, 1, .5);
        var edgeCamera = camera with { Latitude = edge.Latitude, Longitude = edge.Longitude, Pitch = 0 };
        var east = id with { X = id.X + 1 };
        Draw(edgeCamera, [new(east, [])], null);
        var fallback = At(edgeCamera, east, .2, .5);
        Draw(edgeCamera, [new(east, [])], [source, new(east, [1, 2, 3])]);
        Assert(At(edgeCamera, east, .2, .5) == fallback && At(edgeCamera, id, .8, .5) != fallback,
            "Missing or undecodable imagery failed to use the local vector fallback");

        var solid = MakeTile(new(3, 0, 4), static (_, _) => new(36, 95, 128));
        var antimeridian = new MapCamera(Geo(solid.Id, 0, .5).Latitude, 179.99, 3, 0, 0);
        Draw(antimeridian, [], [solid]);
        var wrapped = At(antimeridian, solid.Id, .2, .5);
        Assert(wrapped.Blue > wrapped.Red + 25, "Raster tile did not wrap around the antimeridian");

        var highModel = new MapFeature("building", "", "", "", 11, 4096, GeometryType.Polygon,
            [[new(1600, 1700), new(2400, 1700), new(2400, 2350), new(1600, 2350)]], 95, 0, false, 0, 0, "");
        var models = new[] { new MapTile(id, [highModel]) };
        var plan = camera with { Pitch = 0 };
        Draw(plan, [], [source]);
        var photo = At(plan, id, .45, .45);
        Draw(plan, models, [source]);
        var tint = At(plan, id, .45, .45);
        Draw(plan, models, null);
        var roof = At(plan, id, .45, .45);
        Assert(tint != roof && tint != photo && Distance(tint, photo) < Distance(tint, roof), "Plan footprints obscured the imagery");
        var iso = camera with { Pitch = 55 };
        Draw(iso, models, [source]);
        Assert(renderer.Pick(256, 239, Size, Size, iso, models) == highModel, "Isometric building selection failed with imagery");
        var isoPixel = bitmap.GetPixel(256, 239);
        Draw(iso, [], [source]);
        Assert(bitmap.GetPixel(256, 239) != isoPixel, "Isometric model did not composite over imagery");

        var gradient = MakeTile(id, static (x, y) => new((byte)x, (byte)y, (byte)(x ^ y)));
        Draw(plan, [], [gradient]);
        var colors = bitmap.Pixels.Distinct().Count();
        Assert(colors is >= 24 and <= 89, $"Expected <=89 deliberate Earth inks, got {colors}");
        var tileColor = MakeTile(id, static (_, _) => new(79, 112, 86));
        Draw(edgeCamera, [], [tileColor, tileColor with { Id = east }]);
        for (int y = 248; y < 264; y++)
        for (int x = 248; x < 256; x++)
            Assert(bitmap.GetPixel(x, y) == bitmap.GetPixel(x + 8, y), "Ordered texture phase jumped at a geographic tile edge");

        // A fresh renderer and a different request order must reproduce exactly the same image.
        var first = bitmap.GetPixelSpan().ToArray();
        using var fresh = new PixelMapRenderer();
        fresh.Draw(canvas, Size, Size, edgeCamera, [], pixelSize: 1, imagery: [tileColor with { Id = east }, tileColor]);
        Assert(first.AsSpan().SequenceEqual(bitmap.GetPixelSpan()), "Raster output depends on request order or renderer lifetime");
        Console.WriteLine($"Passed Earth quadrants, pitch, wrap, missing tile, model composition, palette ({colors} inks on RGB fixture), seam phase and determinism.");
        return;

        void Draw(MapCamera c, IReadOnlyList<MapTile> vectors, IReadOnlyList<ImageryTile>? photos) =>
            renderer.Draw(canvas, Size, Size, c, vectors, pixelSize: 1, imagery: photos);

        SKColor At(MapCamera c, TileId tile, double u, double v)
        {
            var p = Geo(tile, u, v);
            var screen = c.Project(p.Latitude, p.Longitude, Size, Size);
            return bitmap.GetPixel((int)screen.X, (int)screen.Y);
        }
    }

    private static int Distance(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
    private static void Assert(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }

    private static (double Latitude, double Longitude) Geo(TileId id, double u, double v)
    {
        int n = 1 << id.Z;
        return (Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (id.Y + v) / n))) * 180 / Math.PI, (id.X + u) * 360 / n - 180);
    }

    private static ImageryTile MakeTile(TileId id, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque);
        for (int y = 0; y < 256; y++)
        for (int x = 0; x < 256; x++)
            bitmap.SetPixel(x, y, pixel(x, y));
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(id, png.ToArray());
    }
}
