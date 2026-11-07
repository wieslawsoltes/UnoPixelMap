using PixelMap.Rendering;

internal static class PixelWindowColorChecks
{
    internal static void Run()
    {
        var random = new Random(17059);
        PixelPalette[] palettes = [PixelPalette.Day, PixelPalette.Dusk, PixelPalette.Night];
        float[] boundaries = [.0001f, MathF.BitDecrement(.2f), .2f, MathF.BitIncrement(.2f),
            MathF.BitDecrement(.5f), .5f, MathF.BitIncrement(.5f), MathF.BitDecrement(1), 1,
            MathF.BitIncrement(1), MathF.BitDecrement(2), 2, MathF.BitIncrement(2), 8, 128, 256];
        for (int i = 0; i < 65536; i++)
        {
            var palette = palettes[i % palettes.Length];
            var material = palette.Materials[(i / palettes.Length) % palette.Materials.Length];
            uint wall = i % 3 == 0 ? 0xff000000u | (uint)random.Next(1 << 24) : material.DarkWall;
            uint seed = (uint)random.NextInt64(1L << 32);
            float column = (float)(random.NextDouble() * 514 - 257);
            float story = (float)(random.NextDouble() * 34 - 17);
            if (i % 7 == 0) column = MathF.Floor(column) + (i % 2 == 0 ? .17f : .76f);
            if (i % 11 == 0) story = MathF.Floor(story) + (i % 2 == 0 ? .23f : .75f);
            if (i % 13 == 0)
            {
                float edge = (i % 5) switch { 0 => MathF.BitDecrement(2), 1 => 2, 2 => MathF.BitIncrement(2), 3 => 32768, _ => 1048576 };
                column = (i % 2 == 0 ? 1 : -1) * edge;
                story = -column;
            }
            float x = i % 2 == 0 ? boundaries[(i / 2) % boundaries.Length] : (float)Math.Pow(2, random.NextDouble() * 12 - 8);
            float y = i % 3 == 0 ? boundaries[(i / 3) % boundaries.Length] : (float)Math.Pow(2, random.NextDouble() * 12 - 8);
            float scale = i % 4 == 0 ? .4f : i % 4 == 1 ? MathF.BitIncrement(.4f) : (float)random.NextDouble() * 4;
            uint expected = ScalarPixelWindowFilterReference.Sample(wall, material, seed, column, story, x, y, scale);
            var colors = new PixelWindowFilter.Colors(wall, material);
            uint prepared = PixelWindowFilter.Sample(colors, seed, column, story, x, y, scale);
            uint direct = PixelWindowFilter.Sample(wall, material, seed, column, story, x, y, scale);
            var footprint = new PixelWindowFilter.SamplingFootprint(x, y);
            uint constant = PixelWindowFilter.Sample(colors, footprint, seed, column, story, scale);
            if (prepared != expected || direct != expected || constant != expected)
                throw new InvalidOperationException($"CPU window filter changed scalar color at sample{i}: " +
                    $"{expected:x8}/{prepared:x8}/{direct:x8}/{constant:x8}, rooms {column}/{story}, footprint {x}/{y}.");
        }
        Console.WriteLine("Passed 65,536 exact scalar/SIMD window colors across palettes, signed room boundaries, filter transitions and mip levels.");
    }
}
