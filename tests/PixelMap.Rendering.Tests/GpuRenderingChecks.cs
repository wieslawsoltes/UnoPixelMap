using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

/// <summary>Opting into GPU rendering must remain exact on a CPU-backed destination.</summary>
internal static class GpuRenderingChecks
{
    internal static void Run()
    {
        var id = RingFixtures.Tile;
        var building = new MapFeature("building", "", "", "", 971, 4096, GeometryType.Polygon,
            [[new(1900, 1900), new(2210, 1900), new(2210, 2200), new(1900, 2200)],
                [new(1990, 1980), new(1990, 2090), new(2120, 2090), new(2120, 1980)]],
            80, 8, false, 0, 0, "");
        IReadOnlyList<MapTile> tiles = [new(id, [building])];
        IReadOnlyList<ImageryTile> imagery = [MakeImage(id)];
        using var expected = new PixelMapRenderer();
        using var optional = new PixelMapRenderer();
        int checks = 0;
        foreach (var (width, height) in new[] { (320, 240), (391, 233), (320, 240) })
        {
            using var cpuBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var optionalBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var cpuCanvas = new SKCanvas(cpuBitmap);
            using var optionalCanvas = new SKCanvas(optionalBitmap);
            foreach (bool earth in new[] { false, true })
            foreach (var (pitch, perspective) in new[] { (0d, false), (65d, true) })
            foreach (var style in Enum.GetValues<PixelStyle>())
            foreach (int pixelSize in new[] { 2, 3 })
            {
                var camera = RingFixtures.Camera with { Pitch = pitch, Perspective = perspective };
                var photos = earth ? imagery : null;
                foreach (bool enabled in new[] { false, true, true, false, true })
                {
                    cpuCanvas.Clear(SKColors.Magenta);
                    optionalCanvas.Clear(SKColors.Magenta);
                    expected.Draw(cpuCanvas, width, height, camera, tiles, style, pixelSize, photos);
                    optional.Draw(optionalCanvas, width, height, camera, tiles, style, pixelSize, photos, enabled);
                    Assert(!optional.IsGpuRenderingActive, "A CPU canvas reported an active GPU backend.");
                    Assert(cpuBitmap.GetPixelSpan().SequenceEqual(optionalBitmap.GetPixelSpan()),
                        $"GPU fallback changed pixels at {width}×{height}, pitch {pitch}, {style}, Earth {earth}, pixel size {pixelSize}, enabled {enabled}.");
                    checks++;
                }
            }
        }
        Console.WriteLine($"Passed {checks} exact GPU fallback, toggle, cached-redraw and resize checks on CPU canvases.");
    }

    private static ImageryTile MakeImage(TileId id)
    {
        using var bitmap = new SKBitmap(32, 32, SKColorType.Bgra8888, SKAlphaType.Opaque);
        for (int y = 0; y < bitmap.Height; y++)
        for (int x = 0; x < bitmap.Width; x++)
            bitmap.SetPixel(x, y, new((byte)(40 + x * 5), (byte)(80 + y * 4), (byte)(30 + (x ^ y) * 6)));
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(id, png.ToArray());
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
