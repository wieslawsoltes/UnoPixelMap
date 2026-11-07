using System.Security.Cryptography;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

internal static class PixelWindowFootprintChecks
{
    private readonly record struct PlaneCase(SKPoint Origin, float Inverse, float U, float Z,
        (float X, float Y) Iw, (float X, float Y) Uw, (float X, float Y) Zw,
        int Width, int Height, float Bay, float PixelsPerMeter, float Floor,
        float MinimumWeight = 2, float Near = .01f);

    internal static void Run()
    {
        var flat = new PlaneCase(new(0, 0), 1, 4, 10, (0, 0), (.1f, 0), (0, .2f), 128, 64, 3.2f, 2, 3.2f);
        Assert(Resolved(flat), "The constant resolved footprint did not enable its point-sampling fast path.");
        foreach (var uncertain in new[]
        {
            flat with { Uw = (.2f * flat.Bay, 0) },
            flat with { Iw = (-2f / flat.Width, 0) },
            flat with { MinimumWeight = flat.Near },
            flat with { MinimumWeight = flat.Near * 1.0005f },
            flat with { Origin = new(float.NaN, 0) },
            flat with { Inverse = float.Epsilon },
            flat with { U = float.MaxValue },
            flat with { Origin = new(1e20f, 1e20f), Inverse = 1e23f, U = 0, Uw = (1e20f, -1e20f) },
            flat with { Zw = (0, float.PositiveInfinity) },
            flat with { PixelsPerMeter = 0 },
            flat with { Width = 1 << 24 }
        }) Assert(!Resolved(uncertain), "An uncertain, threshold, near-plane or nonfinite footprint bypassed filtering.");

        var random = new Random(74019);
        int accepted = 0, rejected = 0;
        long pixels = 0;
        for (int i = 0; i < 512; i++)
        {
            int width = i % 31 == 0 ? 4096 : random.Next(8, 96);
            int height = i % 31 == 0 ? 3 : random.Next(4, 48);
            float inverse = MathF.Pow(2, random.Next(-12, 13));
            float along = Signed() * 100, altitude = Signed() * 30;
            float bay = 3.2f + random.Next(4) * .6f, ppm = MathF.Pow(2, random.Next(-3, 5)), floor = 3.2f + random.Next(4) * .22f;
            float target = .01f + (float)random.NextDouble() * .3f;
            (float X, float Y) iw = i % 3 == 0 ? (0, 0) : (Signed() * inverse * .01f / width, Signed() * inverse * .01f / height);
            var value = new PlaneCase(new(Signed() * width, Signed() * height), inverse, along * inverse, altitude * inverse,
                iw, (along * iw.X + inverse * bay * target * .8f, along * iw.Y - inverse * bay * target * .2f),
                (altitude * iw.X - inverse * ppm * floor * target * .3f, altitude * iw.Y + inverse * ppm * floor * target * .7f),
                width, height, bay, ppm, floor);
            if (!Resolved(value)) { rejected++; continue; }
            accepted++;
            // Match the rasterizer's float row initialization and every incremental
            // addition, rather than checking only exact-double corner derivatives.
            for (int y = 0; y < value.Height; y++)
            {
                float dx = .5f - value.Origin.X, dy = y + .5f - value.Origin.Y;
                float inv = value.Inverse + value.Iw.X * dx + value.Iw.Y * dy;
                float u = value.U + value.Uw.X * dx + value.Uw.Y * dy;
                float z = value.Z + value.Zw.X * dx + value.Zw.Y * dy;
                for (int x = 0; x < value.Width; x++, inv += value.Iw.X, u += value.Uw.X, z += value.Zw.X)
                {
                    float alongAt = u / inv, altitudeAt = z / inv;
                    float horizontal = (Math.Abs(value.Uw.X - alongAt * value.Iw.X) + Math.Abs(value.Uw.Y - alongAt * value.Iw.Y)) / (inv * value.Bay);
                    float vertical = (Math.Abs(value.Zw.X - altitudeAt * value.Iw.X) + Math.Abs(value.Zw.Y - altitudeAt * value.Iw.Y)) /
                        (inv * value.PixelsPerMeter * value.Floor);
                    Assert(float.IsFinite(horizontal) && float.IsFinite(vertical) &&
                        horizontal <= PixelWindowFilter.FilterStart && vertical <= PixelWindowFilter.FilterStart,
                        $"The conservative footprint missed a filtered pixel in case {i} at {x},{y}: {horizontal}, {vertical}.");
                    pixels++;
                }
            }
        }
        Assert(accepted >= 64 && rejected >= 64 && pixels > 100000, "The footprint fixtures did not exercise both paths and scanline drift.");
        CheckNearbyImages();
        Console.WriteLine($"Passed {pixels:N0} float-accumulated samples in {accepted} resolved footprint bounds, {rejected} general-path cases, exceptional inputs and 32 exact nearby CPU images.");

        float Signed() => (float)(random.NextDouble() * 2 - 1);
    }

    private static bool Resolved(PlaneCase p) => BuildingRasterizer.HasResolvedWindowFootprint(
        p.Origin, p.Inverse, p.U, p.Z, p.Iw, p.Uw, p.Zw, p.Width, p.Height, p.Bay,
        (double)p.PixelsPerMeter * p.Floor, p.MinimumWeight, p.Near);

    private static void CheckNearbyImages()
    {
        const int width = 768, height = 576, phases = 16;
        var tile = new TileId(14, 8192, 8192);
        var center = new RoutePoint(Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (tile.Y + .5) / (1 << tile.Z)))) * 180 / Math.PI,
            (tile.X + .5) / (1 << tile.Z) * 360 - 180);
        MapTile[] tiles = [new(tile, [new("building", "", "", "", 701, 4096, GeometryType.Polygon,
            [new TilePoint[] { new(600, 2046), new(3496, 2046), new(3496, 2050), new(600, 2050) }],
            1000, 0, false, 0, 0, "")])];
        foreach (int pixelSize in new[] { 4, 1 })
        {
            using var renderer = new PixelMapRenderer();
            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(bitmap);
            for (int phase = 0; phase < phases; phase++)
            {
                var eye = center.Move(90, phase * 3.8 / phases).Move(180, 64);
                var camera = MapCamera.FromEye(eye.Latitude, eye.Longitude, 300, 0, 90, width, height, viewDistanceMeters: 6000);
                renderer.Draw(canvas, width, height, camera, tiles, PixelStyle.Night, pixelSize,
                    null, false, false, null, MapRenderStyle.Pixel);
                canvas.Flush();
                string actual = Convert.ToHexStringLower(SHA256.HashData(bitmap.GetPixelSpan()));
                Assert(actual == (pixelSize == 4 ? Nearby : Native)[phase],
                    $"The resolved fast path changed the original complete nearby image at pixel size {pixelSize}, phase {phase}.");
            }
        }
    }

    // Original ab765d2 CPU controls, also reproduced exactly by fa2cfa7. See
    // docs/verification/pixel-window-cpu-provenance-fa2cfa7.json for full provenance.
    private static readonly string[] Nearby =
    [
        "a69a76d727c03a64c1e412ac5fdec55180104da716b54e0a156252c9a51f4b71",
        "18d3e00ffaf3ff96db7ab2ccb029c5cd6947d0fc72b07cb5afce3fd219a58321",
        "ff89e2982af290645cc00b9d39e435416d9ae2c5119d4e261f5dbc1c3f736acc",
        "d716fcdfaf0132239fa85acc020b65ef40d50524fa17680d60bbb7cfd6343fb7",
        "6dcdeeed3aab7fdaa2f747ddcdafe15f3a33ae2b066c58374609b702682ccf71",
        "5dccc8c05f49eea9eb9ca85ef0fe270e632d706ae186b09a2247ed37cdd48162",
        "8c90bb1dbe6cd06686bf2cb0cf562e0212d1a13c795e9cb61a0a8b69003d2145",
        "a1cc7e1da723db5e031901a513acc303f443bfa28ed540051e6906933ce556c8",
        "5959fa05a510724fba4bdf963848f69fc36070b5193c75790669bf6ba7ddf81e",
        "ad932efbb90c48469f2174bb7d0843515c11fefcb16719a192908592f507ba85",
        "2afacd22430126803111905319ed1c5b63a8c55a451ce432a2641b22fee4c0ac",
        "f7f54ee9999616558d29c2ad5607a05fd0a729d601b8610c90fc4d06ff147bbc",
        "84186b749761abef2d51e506c1dcb558930cd3e0f71bc62aaab334c080874e52",
        "f18f3067eb68fc0459f7c96aff45da6dac5c8f5c0d4c4b5fb0b5af8270f1b2bf",
        "b7b5be98d40c5f13468cee1d8383fe0b165721794c484baff7b58d01d6889079",
        "bafadf99c8af64d43b7eb99a82a8f06fce057788f76c3975cb4ef50acf18c9e6"
    ];

    private static readonly string[] Native =
    [
        "6f8cb59bcc679cb9b8a221486930f364cd6c74b1b8a10d63be87a52e16485f05",
        "c7d0905c5d3eaeb05ea831e3b250ea7247a854d56cb3a63b8d7a90f2a8813d0b",
        "f6b27b766114e8591f4b230da5f158d3e9a9489aef2f735cc78daa888df04691",
        "aaf0851bd87bf269a6948b03e6180b9bcb4f1430f1b244b9e4ed0c84b6f2bc39",
        "bec69d9cfae2ddf86a63af75f2f44be52fde88ceb06c6e699335727e89dc37d2",
        "ae48dd2fa84a28acdad022ef8000cfcfa1b7ad2ee151bdd7b3f714753d39eeb4",
        "6e14cdd6c0018587d8379e3a0f6ceac3144d31a57029f9a27b9d9de30e7ee9ae",
        "ada89a37dadc5bbf3269f352edbe3cd1e9f6ba3f749c68db590a47823a90ebad",
        "630b8950bcaf552a13b302d09835a274f0035f5b3d4edff4cca9ee0ed84075bc",
        "0569489cab9576ab97a792db4ef9575cb459b17ec15d3e68313c985b58b28439",
        "a08ee85f530ea80f21b0112bd5720d1a48a1bddc92761c17923626a5822ccd48",
        "daef78dd108c095f58b6a25a800bb6966a7497f49919402192ab14d3d371492c",
        "4f8c3e42527e945493180e279f4dc114031484cf6e67f8e51a5f8f6503434546",
        "4f6005d28f769245307bf489321d02fddae134674ef042030e88152766bb2174",
        "023da805627976e9002e71a025e3f3cb4224c06ec179d3d33c7773e99c7393fd",
        "c994665017a51e2bbc9229586de1d89bb4a3019e7770af2d751707d366e0b2cb"
    ];

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
