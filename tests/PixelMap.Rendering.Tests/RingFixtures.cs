using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

/// <summary>Compound footprints whose individual rings cross different clipping planes.</summary>
internal static class RingFixtures
{
    internal static readonly TileId Tile = new(14, 8192, 8192);

    internal static MapCamera Camera => new(
        Math.Atan(Math.Sinh(-Math.PI / (1 << Tile.Z))) * 180 / Math.PI,
        180d / (1 << Tile.Z), 16, 31, 55);

    internal static MapTile Create(MapCamera camera, int width, int height)
    {
        var features = new List<MapFeature>
        {
            Building(801, 65, 0, Box(-160, -100, -30, 110),
                Box(-18000, -16000, -17800, -15800), Box(15000, 17000, 15200, 17200)),
            Building(802, 42, 0, Box(-50000, -50000, 50000, 50000),
                Box(-45000, -45000, 45000, 45000).Reverse().ToArray())
        };

        // Put a disconnected footprint exactly across each ground-visible viewport edge.
        var edgeCamera = camera.Perspective && camera.Pitch > 45
            ? camera with { Pitch = 45 } : camera;
        foreach (var edge in new[] { (0d, height / 2d), ((double)width, height / 2d),
            (width / 2d, 0d), (width / 2d, (double)height) })
        {
            if (!edgeCamera.TryUnproject(edge.Item1, edge.Item2, width, height, out var point))
                throw new InvalidOperationException("The edge fixture must intersect the ground.");
            var center = FromCoordinate(point.Latitude, point.Longitude);
            features.Add(Building((ulong)(803 + features.Count), 28, 5,
                Around(center, 24), Box(22000, -22000, 22100, -21900)));
        }

        var lens = camera with { Perspective = true, Pitch = camera.Perspective ? camera.Pitch : 65 };
        double pitch = lens.Pitch * Math.PI / 180;
        double focal = height / (2 * Math.Tan(MapCamera.VerticalFieldOfView * Math.PI / 360));
        double pixelsPerMeter = 512 * Math.Pow(2, lens.Zoom)
            / (40_075_016.68557849 * Math.Cos(lens.Latitude * Math.PI / 180));
        double nearV = .9 * focal * Math.Sin(pitch);
        for (int i = 0; i < 3; i++)
        {
            var center = FromView(lens, (i - 1) * 24, nearV);
            double actualV = ViewV(lens, center);
            double weight = .1 + (i - 1) * .00001;
            double top = (focal * (1 - weight) - actualV * Math.Sin(pitch))
                / (pixelsPerMeter * Math.Cos(pitch));
            features.Add(Building((ulong)(820 + i), top, top - 3,
                Around(center, 11), Box(-20000, 16000, -19800, 16200)));

            double farV = (1 - (6 + (i - 1) * .0001)) * focal / Math.Sin(pitch);
            var far = FromView(lens, (i - 1) * 420, farV);
            features.Add(Building((ulong)(830 + i), 2, 0,
                Around(far, 90), Box(20000, 20000, 20200, 20200)));
        }
        return new(Tile, features);
    }

    internal static void Run()
    {
        const int width = 360, height = 240;
        var visible = Building(901, 45, 0, Box(-90, -70, 85, 95));
        var compound = visible with { Paths = [visible.Paths[0],
            Box(-20000, -20000, -19800, -19800), Box(20000, 20000, 20200, 20200)] };
        foreach (var (pitch, perspective) in new[] { (0d, false), (55d, false),
            (45d, true), (65d, true), (72d, true) })
        {
            var camera = Camera with { Pitch = pitch, Perspective = perspective };
            using var single = Render(camera, visible);
            using var multiple = Render(camera, compound);
            Assert(single.GetPixelSpan().SequenceEqual(multiple.GetPixelSpan()),
                $"Distant disconnected rings changed a visible footprint at pitch {pitch}.");
        }
        var flat = Camera with { Pitch = 0 };
        using var empty = Render(flat);
        using var courtyard = Render(flat, Building(902, 42, 0,
            Box(-50000, -50000, 50000, 50000), Box(-45000, -45000, 45000, 45000).Reverse().ToArray()));
        Assert(empty.GetPixelSpan().SequenceEqual(courtyard.GetPixelSpan()),
            "A courtyard enclosing the entire viewport filled its visible hole.");
        Console.WriteLine("Passed disconnected footprint and enclosing courtyard clipping checks.");

        static SKBitmap Render(MapCamera camera, params MapFeature[] features)
        {
            var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            using var renderer = new PixelMapRenderer();
            renderer.Draw(canvas, width, height, camera, [new(Tile, features)], pixelSize: 1);
            return bitmap;
        }
    }

    private static TilePoint FromCoordinate(double latitude, double longitude)
    {
        double x = (longitude + 180) / 360;
        double sine = Math.Sin(latitude * Math.PI / 180);
        double y = .5 - Math.Log((1 + sine) / (1 - sine)) / (4 * Math.PI);
        return new((int)Math.Round((x * (1 << Tile.Z) - Tile.X) * 4096),
            (int)Math.Round((y * (1 << Tile.Z) - Tile.Y) * 4096));
    }

    private static TilePoint FromView(MapCamera camera, double u, double v)
    {
        double bearing = camera.Bearing * Math.PI / 180;
        double unitsPerPixel = 4096d * (1 << Tile.Z) / (512 * Math.Pow(2, camera.Zoom));
        return new((int)Math.Round(2048 + (u * Math.Cos(bearing) - v * Math.Sin(bearing)) * unitsPerPixel),
            (int)Math.Round(2048 + (u * Math.Sin(bearing) + v * Math.Cos(bearing)) * unitsPerPixel));
    }

    private static double ViewV(MapCamera camera, TilePoint point)
    {
        double bearing = camera.Bearing * Math.PI / 180;
        double pixelsPerUnit = 512 * Math.Pow(2, camera.Zoom) / (4096d * (1 << Tile.Z));
        return (-(point.X - 2048) * Math.Sin(bearing) + (point.Y - 2048) * Math.Cos(bearing)) * pixelsPerUnit;
    }

    private static TilePoint[] Box(int x0, int y0, int x1, int y1) =>
        [new(2048 + x0, 2048 + y0), new(2048 + x1, 2048 + y0),
            new(2048 + x1, 2048 + y1), new(2048 + x0, 2048 + y1)];

    private static TilePoint[] Around(TilePoint center, int radius) =>
        Box(center.X - 2048 - radius, center.Y - 2048 - radius,
            center.X - 2048 + radius, center.Y - 2048 + radius);

    private static MapFeature Building(ulong id, double top, double bottom, params TilePoint[][] paths) =>
        new("building", "", "", "", id, 4096, GeometryType.Polygon, paths, top, bottom, false, 0, 0, "");

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
