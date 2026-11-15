using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

internal static class PixelArtworkChecks
{
    private static readonly Ink[] PatternInks = [Ink.Water, Ink.Grass, Ink.Forest, Ink.Park];
    // Authored motif fixtures: Day/Dusk/Night, each Water/Grass/Forest/Park.
    // Update deliberately after inspecting --pixel-artwork-output captures.
    private static readonly string[] PatternHashes = [
        "5c45ba1752cd767adaf0c56c58e0175263171b5af3e4acadef9172274a8b0861",
        "6d4a0ce1d364cbda0c4c34db10a8b5daae18788632b52fc0003327ba3a29b08a",
        "edc70b52e18b30d2ad493f6d7b24ccd1563fa25dc1b5bff2fc50fd23a6540cab",
        "a437028ba3d9dffbd368bc7546e950f9561523b6324c390e13737b9a5148a61c",
        "05008549c52b959a6e85651d94c7e8a4f9a49adf62a445d131df434a30dbb037",
        "5773525f934b6f38ac8c6a8410d2ef88ed1223d443de0f99d0a4ddc7f5364530",
        "953bacd39f6731b18541bde8bea9da8b2f1f3aab4f14b15e81012d6938c70d1a",
        "9f851016edd1bd226005127887735d1a640c042a8db6524949ad75d757760fb1",
        "34beb9bf0208d0ecaef6c4f97fff8637cf10c64f19c4da181ac6abe545b6bd80",
        "6ea67f74041699e0cc5d6295a256567bc76fc9d3ba353f0b0266aef99bf41b1f",
        "4d691ae65c1177f25a9322c5c75352f3315205a054f8ea89b13226e160f553eb",
        "fab1f2a133a998457bcd306774a145b4624cca64abf352167bf0dbaf8d8f8067"];

    internal static void Run()
    {
        CheckPalettes();
        CheckPatternPixels();
        CheckWorldPhase();
        CheckShaderSeams();
        var features = Features();
        CheckLegacyArtwork(features);
        CheckPixelOperations();
        CheckClassificationAllocation(features);
        Console.WriteLine("Passed Pixel artwork: isolated palettes, authored cluster fixtures, signed/world/LOD phase, shared immutable paths, legacy paint parity and allocation-free classification.");
    }

    private static void CheckPalettes()
    {
        PixelPalette[] legacy = [PixelPalette.Day, PixelPalette.Dusk, PixelPalette.Night];
        for (int lighting = 0; lighting < 3; lighting++)
        {
            var pixel = PixelPalette.For((PixelStyle)lighting, MapRenderStyle.Pixel);
            Assert(pixel.IsPixelArt && !ReferenceEquals(pixel, legacy[lighting]), "Pixel requires its own authored palette.");
            Assert(pixel.Label == legacy[lighting].Label && pixel.LabelHalo == legacy[lighting].LabelHalo,
                "Pixel material changes must preserve native label/overlay colors.");
            Assert(pixel.Materials.Length == 12, "Packed building material identity must retain twelve slots.");
            foreach (var material in pixel.Materials)
            {
                Assert(Luminance(material.LightWall) > Luminance(material.DarkWall) + 10,
                    "A material must separate lit and shaded building planes.");
                Assert(Luminance(material.Rim) > Luminance(material.Roof), "Roof rims must remain readable.");
                foreach (uint color in new[] { material.Roof, material.LightWall, material.DarkWall, material.Rim,
                             material.Trim, material.Glass, material.Lamp }) Assert((color >> 24) == 255, "Material opacity changed.");
            }
            foreach (var style in new[] { MapRenderStyle.Normal, MapRenderStyle.Minecraft, MapRenderStyle.Roblox })
                Assert(ReferenceEquals(PixelPalette.For((PixelStyle)lighting, style), legacy[lighting]),
                    $"{style} must retain its original palette instance.");
        }
        Assert(PixelPalette.Day.Ground == 0xffe6dabc && PixelPalette.Day.Materials[0].Roof == 0xffddb17e,
            "The original material source changed.");
        Assert(PixelPalette.PixelDay.Ground == 0xffd8cfac && PixelPalette.PixelDay.Materials[0].Roof == 0xffc79e6a &&
            PixelPalette.PixelDay.Inks[(int)Ink.Water] == 0xff4c8c94, "The authored Pixel color fixture changed.");
    }

    private static int Luminance(uint rgb) => (int)((54 * ((rgb >> 16) & 255) + 183 * ((rgb >> 8) & 255) + 19 * (rgb & 255)) >> 8);

    private static void CheckPatternPixels()
    {
        var pixels = new uint[64 * 64];
        var padded = new uint[72 * 64];
        int fixture = 0;
        foreach (var lighting in Enum.GetValues<PixelStyle>())
        foreach (var ink in PatternInks)
        {
            var palette = PixelPalette.For(lighting, MapRenderStyle.Pixel);
            PixelTerrainArt.Fill(pixels, 64, ink, palette);
            string hash = Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan())));
            Assert(hash == PatternHashes[fixture++], $"Authored {lighting}/{ink} motif changed: {hash}");
            Array.Fill(padded, 0x12345678u);
            PixelTerrainArt.Fill(padded, 72, ink, palette);
            for (int row = 0; row < 64; row++)
            {
                Assert(pixels.AsSpan(row * 64, 64).SequenceEqual(padded.AsSpan(row * 72, 64)), "Row padding changes pattern pixels.");
                foreach (uint value in padded.AsSpan(row * 72 + 64, 8)) Assert(value == 0x12345678u, "Pattern touched padding.");
            }
            Assert(pixels.All(value => value >> 24 == 255), "Terrain motifs must remain opaque.");
            Assert(pixels.Distinct().Count() is >= 3 and <= 5, "Terrain motifs require a restrained color ramp.");
            uint light = PixelPalette.Mix(palette.Inks[(int)ink], palette.Inks[(int)Ink.Ice], ink == Ink.Water ? .24f : .15f);
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                if (pixels[y * 64 + x] == light)
                    Assert(pixels[y * 64 + ((x - 1) & 63)] == light || pixels[y * 64 + ((x + 1) & 63)] == light ||
                        pixels[((y - 1) & 63) * 64 + x] == light || pixels[((y + 1) & 63) * 64 + x] == light,
                        "Terrain highlights must be connected clusters, not isolated bright specks.");
        }
        PixelTerrainArt.Fill(pixels, 64, Ink.Forest, PixelPalette.PixelDay);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int repeat = 0; repeat < 64; repeat++) PixelTerrainArt.Fill(pixels, 64, Ink.Forest, PixelPalette.PixelDay);
        Assert(GC.GetAllocatedBytesForCurrentThread() == before, "Motif rasterization allocated managed scratch objects.");
    }

    private static void CheckWorldPhase()
    {
        foreach (int zoom in new[] { 0, 7, 13, 14, 19, 20, 22, 28 })
        foreach (int index in new[] { -1000001, -17, -1, 0, 1, 17, 1000001 })
        foreach (double local in new[] { -1.25, 0, .5, 97.25, 2048.5, 4096.0 })
        {
            var tile = new TileId(zoom, index, -index);
            var placement = TerrainPatternPlacement.Pixel(tile);
            double actualX = Wrap(Math.ScaleB(local + placement.PhaseX, -placement.Exponent));
            double actualY = Wrap(Math.ScaleB(local + placement.PhaseY, -placement.Exponent));
            Assert(actualX == Wrap(Math.ScaleB(index * 4096d + local, 13 - zoom)) &&
                actualY == Wrap(Math.ScaleB(-index * 4096d + local, 13 - zoom)), "Signed world phase differs from the geographic grid.");
            if (zoom < 28)
            {
                var child = TerrainPatternPlacement.Pixel(new(zoom + 1, index * 2 + 1, -index * 2));
                Assert(actualX == Wrap(Math.ScaleB(local * 2 - 4096 + child.PhaseX, -child.Exponent)) &&
                    actualY == Wrap(Math.ScaleB(local * 2 + child.PhaseY, -child.Exponent)),
                    "Changing XYZ source LOD moved the geographic motif phase.");
            }
        }
        static double Wrap(double value) => value - Math.Floor(value / 64) * 64;
    }

    private static void CheckShaderSeams()
    {
        var expected = new uint[4096];
        PixelTerrainArt.Fill(expected, 64, Ink.Forest, PixelPalette.PixelDay);
        using var pattern = new TerrainPattern(Ink.Forest, PixelPalette.PixelDay);
        using var bitmap = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { IsAntialias = false };
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
        {
            using var shader = pattern.At(TerrainPatternPlacement.Pixel(new(20, x - 1, y - 1)));
            paint.Shader = shader;
            canvas.Save(); canvas.Translate(x * 32, y * 32); canvas.Scale(1f / 128);
            canvas.DrawRect(0, 0, 4096, 4096, paint); canvas.Restore();
            paint.Shader = null;
        }
        var actual = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
            Assert(actual[y * 64 + x] == expected[((y + 32) & 63) * 64 + ((x + 32) & 63)],
                "Actual Skia sampling introduced a seam or wrong phase across signed XYZ neighbors.");
    }

    private static MapFeature[] Features()
    {
        var features = new List<MapFeature>();
        string[] classes = ["forest", "orchard", "sand", "ice", "grass", "wetland", "park", "garden", "industrial",
            "unknown", "river", "motorway", "primary", "tertiary", "service", "path", "rail", "ferry", "aerialway"];
        foreach (string layer in new[] { "landcover", "landuse", "park", "water", "waterway", "boundary", "aeroway", "transportation", "unknown" })
        foreach (string kind in classes)
        foreach (var type in Enum.GetValues<GeometryType>())
        foreach (int level in new[] { -8, 0, 8 })
        foreach (string brunnel in new[] { "", "bridge", "tunnel" })
        {
            int index = features.Count, x = (index % 5) * 700, y = (index % 3) * 1000;
            features.Add(new(layer, kind, "", "", (ulong)index + 1, 4096, type,
                [[new(x, y), new(x + 500, y + 20), new(x + 550, y + 600), new(x + 20, y + 620)]],
                null, null, false, 0, level, brunnel));
        }
        return features.ToArray();
    }

    private static void CheckLegacyArtwork(MapFeature[] features)
    {
        foreach (var style in new[] { MapRenderStyle.Normal, MapRenderStyle.Minecraft, MapRenderStyle.Roblox })
        {
            var reference = new Dictionary<BatchKey, SKPath>();
            try
            {
                foreach (var feature in features)
                {
                    var original = LegacyGroundArtwork.PaintOperations(feature).ToArray();
                    var actual = TileGeometry.PaintOperations(feature, style);
                    Assert(actual.Count == original.Length && (original.Length == 0 || actual.First == original[0]) &&
                        (original.Length < 2 || actual.Second == original[1]), $"{style} changed a legacy paint operation.");
                    var bounds = LegacyGroundArtwork.Bounds(feature, 1);
                    int bucket = Math.Clamp((int)(bounds.MidX / 512), 0, 7) + Math.Clamp((int)(bounds.MidY / 512), 0, 7) * 8;
                    foreach (var operation in original)
                    {
                        var key = operation with { Bucket = bucket };
                        if (!reference.TryGetValue(key, out var path)) reference.Add(key, path = new());
                        LegacyGroundArtwork.Append(path, feature, 1);
                    }
                }
                using var geometry = new TileGeometry(new(new(14, 8192, 8192), features), style);
                var ordered = reference.OrderBy(item => item.Key.Order).ThenBy(item => item.Key.Width).ToArray();
                Assert(geometry.Batches.Length == ordered.Length, $"{style} lost or split a legacy paint cohort.");
                for (int i = 0; i < ordered.Length; i++)
                    Assert(geometry.Batches[i].Key == ordered[i].Key && geometry.Batches[i].Path.ToSvgPathData() == ordered[i].Value.ToSvgPathData(),
                        $"{style} changed contour bytes or stable paint order at batch {i}.");
                var unique = geometry.Batches.Select(batch => batch.Path).Distinct().ToArray();
                Assert(unique.Length < reference.Count, "Road casings still retain duplicate native paths.");
                Console.WriteLine($"{style} cartography: {ordered.Length} identical paints, {reference.Count} → {unique.Length} native paths; " +
                    $"{reference.Values.Sum(path => path.PointCount)} → {unique.Sum(path => path.PointCount)} retained contour points.");
            }
            finally { foreach (var path in reference.Values) path.Dispose(); }
        }
    }

    private static void CheckPixelOperations()
    {
        var river = new MapFeature("water", "river", "", "", 1, 4096, GeometryType.Polygon,
            [[new(0, 0), new(4096, 0), new(4096, 4096), new(0, 4096)]], null, null, false, 0, 0, "");
        var paint = TileGeometry.PaintOperations(river, MapRenderStyle.Pixel);
        Assert(paint.Count == 2 && paint.First == new BatchKey(19, Ink.Shore, 1.5f) && paint.Second == new BatchKey(20, Ink.Water, 0),
            "Pixel shoreline must underpaint the unchanged water fill.");
        using var geometry = new TileGeometry(new(new(14, 8192, 8192), [river]));
        Assert(geometry.Batches.Length == 2 && ReferenceEquals(geometry.Batches[0].Path, geometry.Batches[1].Path),
            "Shoreline and water fill must share immutable source contours.");
        Assert(ReferenceEquals(river.Paths, geometry.Source.Features[0].Paths), "Artwork styling changed physical feature geometry.");
        var road = river with { Layer = "transportation", Class = "primary", Type = GeometryType.LineString };
        paint = TileGeometry.PaintOperations(road, MapRenderStyle.Pixel);
        Assert(paint.Count == 2 && paint.First.Ink == Ink.RoadEdge && paint.First.Width == 6.3f &&
            paint.Second.Ink == Ink.Arterial && paint.Second.Width == 4.5f, "Pixel roads lost the authored casing/fill hierarchy.");
    }

    private static void CheckClassificationAllocation(MapFeature[] features)
    {
        long checksum = 0;
        foreach (var feature in features) checksum += TileGeometry.PaintOperations(feature, MapRenderStyle.Normal).Count;
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 0; pass < 16; pass++)
            foreach (var feature in features) checksum += TileGeometry.PaintOperations(feature, MapRenderStyle.Normal).Count;
        long actual = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 0; pass < 16; pass++)
            foreach (var feature in features)
                foreach (var _ in LegacyGroundArtwork.PaintOperations(feature)) checksum++;
        long legacy = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert(checksum > 0 && actual == 0 && legacy > features.Length * 16L * 24,
            $"Value classification must remove the original per-feature iterator allocation (legacy={legacy}, actual={actual}, checksum={checksum}).");
        Console.WriteLine($"Cartographic classification: {features.Length * 16} features, {legacy:N0} → {actual:N0} managed bytes; no paint operations removed.");
    }

    internal static void WriteFixtures(string directory)
    {
        Directory.CreateDirectory(directory);
        using var atlas = new SKBitmap(512, 384, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(atlas);
        using var paint = new SKPaint { IsAntialias = false };
        var pixels = new uint[4096];
        for (int lighting = 0; lighting < 3; lighting++)
        for (int column = 0; column < PatternInks.Length; column++)
        {
            var pixel = PixelPalette.For((PixelStyle)lighting, MapRenderStyle.Pixel);
            PixelTerrainArt.Fill(pixels, 64, PatternInks[column], pixel);
            Console.WriteLine($"{(PixelStyle)lighting}/{PatternInks[column]} {Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan())))}");
            for (int variant = 0; variant < 2; variant++)
            {
                var palette = PixelPalette.For((PixelStyle)lighting, variant == 0 ? MapRenderStyle.Minecraft : MapRenderStyle.Pixel);
                using var pattern = new TerrainPattern(PatternInks[column], palette);
                using var shader = pattern.AtScale(2);
                paint.Shader = shader;
                canvas.Save(); canvas.Translate(column * 128, lighting * 128 + variant * 64);
                canvas.DrawRect(0, 0, 128, 64, paint); canvas.Restore(); paint.Shader = null;
            }
        }
        using var image = SKImage.FromBitmap(atlas);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(Path.Combine(directory, "pixel-terrain-motifs.png"));
        data.SaveTo(output);
        PixelWindowFootprintChecks.WriteFixtures(directory);
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
