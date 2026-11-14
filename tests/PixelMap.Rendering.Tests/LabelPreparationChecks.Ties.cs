using System.Reflection;
using System.Runtime.InteropServices;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

internal static partial class LabelPreparationChecks
{
    private static void RunTieSelectionChecks()
    {
        int cases = 0, reduced = 0, fallbacks = 0;
        using var expectedOverlay = new WebGpuSceneOverlay();
        using var actualOverlay = new WebGpuSceneOverlay();
        foreach (int pixels in new[] { 2, 3, 4 })
        foreach (bool reverse in new[] { false, true })
        foreach (string kind in new[] { "hidden", "single-visible", "buffered", "collision", "clipped",
                     "visible", "case-sensitive", "different-size", "rounded-boundary" })
        {
            using var renderer = new PixelMapRenderer();
            TestLabelFont.Apply(renderer);
            using var tile = new TileGeometry(new(Tile, []));
            var transform = new TileTransform(0, 0, 1f / pixels, 0, 0, 1f / pixels);
            Field<List<(TileGeometry Tile, TileTransform Transform, float Scale)>>(renderer, "visible").Add((tile, transform, 1));
            tile.Labels.Add(new("Reference", new(450, 480), -1, false));
            // These rejected candidates make an accidental global fallback observable.
            // Their anchors are on screen, but their label rectangles cross its top.
            for (int index = 0; index < 180; index++)
                tile.Labels.Add(new("Clipped background " + index % 7, new(15 + index * .125f, 5), 30, true));
            var anchor = new SKPoint(650, kind == "clipped" ? 5 : 300);
            var first = new Place("First road", anchor, 0, true);
            var second = new Place("Second road", anchor, 0, true);
            if (kind is "buffered" or "single-visible")
            {
                first = new(kind == "buffered" ? "Buffered road" : "Hidden road", anchor, 0, true);
                second = new(kind == "buffered" ? "Buffered road" : "Visible road",
                    new(anchor.X, MathF.BitIncrement(anchor.Y)), 0, true);
            }
            else if (kind == "case-sensitive")
            {
                first = new("Same Road", anchor, 0, true);
                second = new("SAME ROAD", anchor, 0, true);
            }
            else if (kind == "different-size") second = new(first.Text, anchor, 0, false);
            else if (kind == "rounded-boundary")
            {
                first = new("Boundary road", new(anchor.X, MathF.BitDecrement(300.5f)), 0, true);
                second = new(first.Text, new(anchor.X, MathF.BitIncrement(300.5f)), 0, true);
            }
            if (kind == "collision") tile.Labels.Add(new("Earlier visible label", anchor, -1, false));
            tile.Labels.Add(first); tile.Labels.Add(second);
            if (reverse) tile.Labels.Reverse();
            var a = Project(first.Point); var b = Project(second.Point);
            float Distance(SKPoint point)
            {
                float dx = point.X - Width * .5f, dy = point.Y - Height * .5f;
                return dx * dx + dy * dy;
            }
            Assert(Distance(a) == Distance(b), $"The {kind}/p{pixels} fixture did not create an equal-distance tie.");
            if (kind is "buffered" or "single-visible")
                Assert(a != b && MathF.Round(a.X) == MathF.Round(b.X) && MathF.Round(a.Y) == MathF.Round(b.Y),
                    "Buffered-copy roundoff did not preserve one rounded placement.");
            if (kind == "rounded-boundary") Assert(MathF.Round(a.Y) != MathF.Round(b.Y), "The tie did not cross a rounding boundary.");
            int queries = 0;
            float firstY = transform.At(first.Point.X, first.Point.Y).Y;
            bool Covered(float x, float y, float altitude)
            {
                queries++;
                if (kind == "hidden") return Math.Abs(x * pixels - anchor.X) < 1 && Math.Abs(y * pixels - anchor.Y) < 1;
                return kind == "single-visible" && y == firstY;
            }
            typeof(BuildingRasterizer).GetField("gpuSceneQuery", Members)!.SetValue(Field<BuildingRasterizer>(renderer, "buildings"),
                (Func<float, float, float, bool>)Covered);
            using var expected = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var actual = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var expectedCanvas = new SKCanvas(expected);
            using var actualCanvas = new SKCanvas(actual);
            var colors = reverse ? PixelPalette.Night : PixelPalette.Day;
            expectedCanvas.Clear(SKColors.Transparent);
            LegacyDraw(renderer, expectedCanvas, 16, pixels, colors);
            int eager = queries;
            queries = 0;
            actualCanvas.Clear(SKColors.Transparent);
            var prepare = typeof(PixelMapRenderer).GetMethod("PrepareLabels", Members)!
                .CreateDelegate<Action<SKCanvas?, int, int, double, int, PixelPalette, WebGpuSceneOverlay?>>(renderer);
            prepare(actualCanvas, Width, Height, 16, pixels, colors, null);
            Assert(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()),
                $"Tie selection changed exact glyph pixels: {kind}, p{pixels}, reverse{reverse}.");
            Assert(queries <= eager, "A compatibility restart queried a candidate twice.");
            if (kind is "visible" or "case-sensitive" or "different-size" or "rounded-boundary")
            {
                Assert(queries == eager && eager > 180, "A genuine visible tie did not use the original eager ordering.");
                fallbacks++;
            }
            else
            {
                Assert(queries * 4 < eager, $"An irrelevant tie forced eager depth queries: {kind}, {eager} -> {queries}.");
                reduced++;
            }
            var camera = new MapCamera(0, 0, 16, 0, 0, false);
            var terrain = new TerrainOptions(ElevationSnapshot.Empty, Relief: false)
                { RenderStyle = pixels == 3 ? MapRenderStyle.Normal : MapRenderStyle.Pixel };
            using var frame = new GpuMapFrame(Width, Height, pixels, camera, terrain, colors, 1, [], []);
            expectedOverlay.Begin(frame, 1.75f);
            LegacyDraw(renderer, null, 16, pixels, colors, expectedOverlay);
            expectedOverlay.Complete(null, null, TravelMode.Walk, null);
            actualOverlay.Begin(frame, 1.75f);
            prepare(null, Width, Height, 16, pixels, colors, actualOverlay);
            actualOverlay.Complete(null, null, TravelMode.Walk, null);
            EqualOverlay(expectedOverlay, actualOverlay, $"synthetic {kind}/p{pixels}/reverse{reverse}");
            cases++;

            SKPoint Project(SKPoint point)
            {
                var result = transform.At(point.X, point.Y);
                return new(result.X * pixels, result.Y * pixels);
            }
        }
        Assert(expectedOverlay.Atlas!.GetPixelSpan().SequenceEqual(actualOverlay.Atlas!.GetPixelSpan()),
            "Tie selection changed packed label atlas texels.");
        Console.WriteLine($"Passed {cases} exact tie images and WebGPU label buffers: {reduced} irrelevant ties avoided eager depth; " +
            $"{fallbacks} visible ambiguities preserved the original sort winner; atlas texels match exactly.");
    }

    private static void RunRetainedCityChecks(List<MapTile> tiles)
    {
        int cases = 0;
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        {
            using var renderer = new PixelMapRenderer();
            TestLabelFont.Apply(renderer);
            using var expected = new WebGpuSceneOverlay();
            using var actual = new WebGpuSceneOverlay();
            var camera = new MapCamera(40.7087, -74.0104, 16.5, 23, 58, true);
            using var first = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 3,
                null, null, style, prepareLegacyOverlay: false);
            Check(first);
            byte[] retainedVertices = MemoryMarshal.AsBytes(actual.ScreenVertices).ToArray();
            uint[] retainedIndices = actual.ScreenIndices.ToArray();
            using var second = renderer.PrepareGpuFrame(Width, Height, camera with { Bearing = 24.4 }, tiles.AsEnumerable().Reverse().ToArray(),
                PixelStyle.Day, 3, null, null, style, prepareLegacyOverlay: false);
            using var third = renderer.PrepareGpuFrame(Width, Height, camera.PanBy(24, -15), tiles.Skip(4).Concat(tiles.Take(4)).ToArray(),
                PixelStyle.Night, 3, null, null, style, prepareLegacyOverlay: false);
            foreach (var frame in new[] { second, third, first, third, second, first })
            {
                Check(frame);
                if (ReferenceEquals(frame, first))
                    Assert(retainedVertices.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(actual.ScreenVertices)) &&
                        retainedIndices.AsSpan().SequenceEqual(actual.ScreenIndices),
                        $"Displaying newer/reordered frames changed retained {style} label geometry.");
            }
            Assert(expected.Atlas!.GetPixelSpan().SequenceEqual(actual.Atlas!.GetPixelSpan()),
                $"Reordered/retained {style} frames changed exact label atlas texels.");

            void Check(GpuMapFrame frame)
            {
                renderer.PrepareWebGpuOverlay(frame, actual, labelRasterScale: 1.75f);
                expected.Begin(frame, 1.75f);
                LegacyDraw(renderer, null, frame.Camera.Zoom, frame.PixelSize, frame.Palette, expected);
                expected.Complete(null, null, TravelMode.Walk, null);
                EqualOverlay(expected, actual, $"retained/reordered {style} frame {cases}");
                cases++;
            }
        }
        Console.WriteLine($"Passed {cases} exact real-city WebGPU label buffers across four styles, tile reorder, palette changes and retained frames.");
    }

    private static void EqualOverlay(WebGpuSceneOverlay expected, WebGpuSceneOverlay actual, string context)
    {
        Assert(expected.LabelCount == actual.LabelCount && expected.ScreenIndices.SequenceEqual(actual.ScreenIndices) &&
            MemoryMarshal.AsBytes(expected.ScreenVertices).SequenceEqual(MemoryMarshal.AsBytes(actual.ScreenVertices)),
            $"Label selection changed exact WebGPU buffers: {context}.");
    }
}
