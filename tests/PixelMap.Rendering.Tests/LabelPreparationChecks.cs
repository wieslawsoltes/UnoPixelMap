using System.Reflection;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

/// <summary>Compare deferred label occlusion with the original eager layout,
/// including unstable-sort ties that must keep their original visible winner.</summary>
internal static partial class LabelPreparationChecks
{
    private const int Width = 900, Height = 600;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TileId Tile = new(14, 8192, 8192);

    internal static void Run()
    {
        int cases = 0, reduced = 0, ties = 0;
        long eagerQueries = 0, deferredQueries = 0;
        double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (Tile.Y + .5) / (1 << Tile.Z)))) * 180 / Math.PI;
        double longitude = (Tile.X + .5) / (1 << Tile.Z) * 360 - 180;
        var elevation = new ElevationSnapshot([new(new(0, 0, 0), 2, new float[] { 1500, 1500, 1500, 1500 })]);
        foreach (bool terrainEnabled in new[] { false, true })
        foreach (bool perspective in new[] { false, true })
        foreach (int pixels in new[] { 2, 3, 4 })
        foreach (bool ambiguous in new[] { false, true })
        {
            var terrain = terrainEnabled ? new TerrainOptions(elevation) : null;
            var camera = new MapCamera(latitude, longitude, 15.4, 17, perspective ? 58 : 0, perspective)
                .OnTerrain(terrain, Height);
            var tile = Labels(camera, pixels, ambiguous);
            using var renderer = new PixelMapRenderer();
            TestLabelFont.Apply(renderer);
            using var frame = renderer.PrepareGpuFrame(Width, Height, camera, [tile], PixelStyle.Night, pixels, null, terrain);
            var buildings = Field<BuildingRasterizer>(renderer, "buildings");
            int queries = 0;
            bool Covered(float x, float y, float altitude)
            {
                queries++;
                return ((int)MathF.Floor(x) * 73 + (int)MathF.Floor(y) * 19) % 7 == 0;
            }
            typeof(BuildingRasterizer).GetField("gpuSceneQuery", Members)!.SetValue(buildings,
                (Func<float, float, float, bool>)Covered);
            using var expected = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var actual = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var expectedCanvas = new SKCanvas(expected);
            using var actualCanvas = new SKCanvas(actual);
            expectedCanvas.Clear(SKColors.Transparent);
            LegacyDraw(renderer, expectedCanvas, camera.Zoom, pixels, PixelPalette.Night);
            int before = queries;
            int ink = 0;
            foreach (byte value in expected.GetPixelSpan()) if (value != 0) ink++;
            Assert(ink > 100, $"Label reference has no visible text: terrain{terrainEnabled}, perspective{perspective}, p{pixels}.");
            queries = 0;
            actualCanvas.Clear(SKColors.Transparent);
            var draw = typeof(PixelMapRenderer).GetMethod("DrawLabels", Members)!
                .CreateDelegate<Action<SKCanvas, int, int, double, int, PixelPalette>>(renderer);
            draw(actualCanvas, Width, Height, camera.Zoom, pixels, PixelPalette.Night);
            Assert(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()),
                $"Deferred labels changed exact text pixels: terrain{terrainEnabled}, perspective{perspective}, p{pixels}, ties{ambiguous}.");
            Assert(queries <= before, "Deferred label selection issued more depth queries than the eager layout.");
            if (!ambiguous)
            {
                Assert(before > 100 && queries > 0 && queries * 4 < before,
                    $"Crowded labels did not reject depth work before rasterization ({before} -> {queries}).");
                reduced++;
                eagerQueries += before;
                deferredQueries += queries;
            }
            else
            {
                Assert(queries > 100, "The ambiguous-tie fixture did not exercise the exact eager compatibility path.");
                ties++;
            }
            cases++;
        }
        Console.WriteLine($"Passed {cases} exact eager/deferred label images, {ties} ambiguous-sort tie fallbacks; " +
            $"{reduced} crowded views reduced depth queries from {eagerQueries} to {deferredQueries}.");
        RunTieSelectionChecks();
    }

    internal static async Task RunCityAsync()
    {
        using var http = new HttpClient(new DepthPreparationBenchmark.OfflineHandler());
        var vectors = new VectorTileSource(http);
        var tiles = new List<MapTile>();
        for (int y = 6159; y <= 6161; y++)
        for (int x = 4822; x <= 4824; x++) tiles.Add(await vectors.GetTileAsync(new(14, x, y)));
        var route = RouteDemos.ForMode(TravelMode.Drive);
        var track = new RouteTrack(route);
        int nonempty = 0, cases = 0;
        foreach (string view in new[] { "walk", "chase", "freeflight", "orbit" })
        {
            using var renderer = new PixelMapRenderer();
            TestLabelFont.Apply(renderer);
            long eager = 0, deferred = 0;
            int retainedCandidates = 0, drawnLabels = 0;
            for (int phase = 0; phase < 4; phase++)
            {
                var position = track.Sample(route.DistanceMeters * .35 + phase * 1.5);
                double pitch = view == "walk" ? 90 : view == "chase" ? 82 : 64;
                double altitude = view == "walk" ? 1.7 : view == "chase" ? 7 : 180;
                var camera = view == "orbit"
                    ? new MapCamera(40.7087 + phase * .00001, -74.0104, 16.5, 23 + phase * .6, 58, true)
                    : MapCamera.FromEye(position.Point.Latitude, position.Point.Longitude, altitude,
                        position.Bearing + Math.Sin(phase * Math.PI / 8) * 5, pitch, Width, Height, viewDistanceMeters: 6000);
                using var frame = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 3);
                var buildings = Field<BuildingRasterizer>(renderer, "buildings");
                var original = Field<Func<float, float, float, bool>>(buildings, "gpuSceneQuery");
                int queries = 0;
                bool Covered(float x, float y, float height) { queries++; return original(x, y, height); }
                typeof(BuildingRasterizer).GetField("gpuSceneQuery", Members)!.SetValue(buildings,
                    (Func<float, float, float, bool>)Covered);
                using var expected = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var actual = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var expectedCanvas = new SKCanvas(expected);
                using var actualCanvas = new SKCanvas(actual);
                expectedCanvas.Clear(SKColors.Transparent);
                LegacyDraw(renderer, expectedCanvas, camera.Zoom, 3, PixelPalette.Day);
                eager += queries;
                queries = 0;
                actualCanvas.Clear(SKColors.Transparent);
                typeof(PixelMapRenderer).GetMethod("DrawLabels", Members)!
                    .CreateDelegate<Action<SKCanvas, int, int, double, int, PixelPalette>>(renderer)
                    (actualCanvas, Width, Height, camera.Zoom, 3, PixelPalette.Day);
                deferred += queries;
                retainedCandidates += Field<System.Collections.ICollection>(renderer, "candidates").Count;
                drawnLabels += Field<List<SKRect>>(renderer, "occupied").Count;
                Assert(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()),
                    $"Manhattan label pixels changed in {view}, phase{phase}.");
                bool ink = false;
                foreach (byte value in actual.GetPixelSpan()) if (value != 0) { ink = true; break; }
                if (ink) nonempty++;
                cases++;
            }
            Console.WriteLine($"Exact Manhattan {view} labels: {eager} -> {deferred} depth queries across four moving views; " +
                $"{retainedCandidates} retained candidates, {drawnLabels} placed labels.");
        }
        Assert(nonempty >= 8, "The Manhattan label fixtures did not render enough visible text.");
        Console.WriteLine($"Passed {cases} real-city exact label images, including {nonempty} with visible glyphs.");
        RunRetainedCityChecks(tiles);
    }

    private static MapTile Labels(MapCamera camera, int pixels, bool ambiguous)
    {
        var transform = new TileProjection(camera, Width, Height, pixels).Get(Tile, 0).Transform;
        var random = new Random(719);
        var distances = new HashSet<int>();
        var features = new List<MapFeature>();
        for (int index = 0; index < 600;)
        {
            var point = camera.Unproject(3 + random.NextDouble() * (Width - 6),
                3 + random.NextDouble() * (Height - 6), Width, Height);
            int x = (int)Math.Round(((point.Longitude + 180) / 360 * (1 << Tile.Z) - Tile.X) * 4096);
            int y = (int)Math.Round((MapCamera.MercatorY(point.Latitude) * (1 << Tile.Z) - Tile.Y) * 4096);
            var screen = transform.At(x, y);
            screen.X *= pixels; screen.Y *= pixels;
            float dx = screen.X - Width * .5f, dy = screen.Y - Height * .5f;
            if (!distances.Add(BitConverter.SingleToInt32Bits(dx * dx + dy * dy))) continue;
            features.Add(Label((ulong)index + 1, "Street " + index % 24, new(x, y)));
            // Buffered duplicates at the same position are visually equivalent ties.
            if (index % 9 == 0) features.Add(features[^1]);
            index++;
        }
        if (ambiguous)
        {
            // Distinct names at one position deliberately make legacy List.Sort's
            // tie order observable; hidden candidates elsewhere change its pivots.
            for (int i = 0; i < 17; i++) features.Add(Label((ulong)1000 + (ulong)i, "Colocated " + i, new(2048, 2048)));
        }
        return new(Tile, features);
    }

    private static MapFeature Label(ulong id, string name, TilePoint point) =>
        new("transportation_name", "", "", name, id, 4096, GeometryType.Point, [[point]], null, null, false, 0, 0, "");

    // The pre-optimization implementation is an independent compatibility oracle.
    // It deliberately queries all eligible labels before sorting and collision tests.
    private static void LegacyDraw(PixelMapRenderer renderer, SKCanvas? canvas, double zoom, int pixels, PixelPalette colors,
        WebGpuSceneOverlay? overlay = null)
    {
        var visible = Field<List<(TileGeometry Tile, TileTransform Transform, float Scale)>>(renderer, "visible");
        var buildings = Field<BuildingRasterizer>(renderer, "buildings");
        var camera = Field<MapCamera>(renderer, "terrainCamera");
        var font = Field<SKFont>(renderer, "font");
        var paint = Field<SKPaint>(renderer, "paint");
        var ground = typeof(PixelMapRenderer).GetMethod("GroundAltitude", Members)!;
        var candidates = new List<(Place Label, SKPoint Screen, float Distance)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var occupied = new List<SKRect>();
        foreach (var (tile, transform, _) in visible)
        foreach (var label in tile.Labels)
        {
            if (label.Street && zoom < 15 || label.Priority > 9 && zoom < 9) continue;
            SKPoint screen;
            if (renderer.CurrentTerrain is { } elevation)
            {
                float meters = (float)(camera.WorldSize / pixels / (MapCamera.EarthCircumference *
                    Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)));
                float altitude = (float)ground.Invoke(renderer, [tile, label.Point, elevation, camera, true])!;
                var point = transform.Vertex(label.Point.X, label.Point.Y, altitude * meters);
                if (point.W < transform.Near || point.W > transform.Far) continue;
                screen = point.Point;
                if (buildings.CoversAt(screen.X, screen.Y, altitude + Math.Max(4, .75f / meters))) continue;
            }
            else
            {
                if (transform.Weight(label.Point.X, label.Point.Y) < transform.Near ||
                    transform.Weight(label.Point.X, label.Point.Y) > transform.Far) continue;
                screen = transform.At(label.Point.X, label.Point.Y);
                if (label.Street && buildings.Covers(screen.X, screen.Y)) continue;
            }
            screen.X *= pixels; screen.Y *= pixels;
            if (screen.X < 0 || screen.X >= Width || screen.Y < 0 || screen.Y >= Height) continue;
            float dx = screen.X - Width * .5f, dy = screen.Y - Height * .5f;
            candidates.Add((label, screen, dx * dx + dy * dy));
        }
        candidates.Sort(static (a, b) =>
        {
            int order = a.Label.Priority.CompareTo(b.Label.Priority);
            return order == 0 ? a.Distance.CompareTo(b.Distance) : order;
        });
        foreach (var (label, screen, _) in candidates)
        {
            if (occupied.Count >= 90) break;
            if (names.Contains(label.Text)) continue;
            float size = label.Street ? 12f : 14f;
            float tw = label.TextWidth;
            if (float.IsNaN(tw)) { font.Size = size; label.TextWidth = tw = font.MeasureText(label.Text); }
            if (tw <= 0 || tw > Width * .8f) continue;
            float x = MathF.Round(screen.X - tw * .5f), y = MathF.Round(screen.Y);
            float pad = label.Street ? 22 : 7;
            var bounds = new SKRect(x - pad, y - size - 9, x + tw + pad, y + 9);
            if (bounds.Left < 1 || bounds.Right > Width - 1 || bounds.Top < 1 || bounds.Bottom > Height - 1) continue;
            if (occupied.Any(prior => bounds.IntersectsWith(prior))) continue;
            names.Add(label.Text); occupied.Add(bounds);
            if (overlay is not null)
            {
                overlay.AddLabel(label.Text, size, x, y, colors, font.Typeface);
                continue;
            }
            if (label.Glyphs is null) { font.Size = size; label.Glyphs = SKTextBlob.Create(label.Text, font); }
            if (label.Glyphs is not { } glyphs) continue;
            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 2.8f; paint.Color = new(colors.LabelHalo);
            canvas!.DrawText(glyphs, x, y, paint);
            paint.Style = SKPaintStyle.Fill; paint.Color = new(colors.Label);
            canvas.DrawText(glyphs, x, y, paint);
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Members)!.GetValue(target)!;
    private static void Assert(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
