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
            var row = new PixelWindowFilter.SamplingRow(footprint, seed, story, scale);
            uint scanline = PixelWindowFilter.Sample(colors, footprint, row, column);
            if (prepared != expected || direct != expected || constant != expected || scanline != expected)
                throw new InvalidOperationException($"CPU window filter changed scalar color at sample{i}: " +
                    $"{expected:x8}/{prepared:x8}/{direct:x8}/{constant:x8}/{scanline:x8}, rooms {column}/{story}, footprint {x}/{y}.");
        }
        Console.WriteLine("Passed 65,536 exact scalar/SIMD/prepared-row window colors across palettes, signed room boundaries, filter transitions and mip levels.");
        CheckRows();
    }

    private static void CheckRows()
    {
        float[] stories = [-16.25f, -0f, 0, MathF.BitDecrement(.23f), .23f, MathF.BitIncrement(.23f),
            MathF.BitDecrement(.75f), .75f, MathF.BitIncrement(.75f), 15.75f, 16.25f, 65535.25f];
        (float X, float Y)[] extents = [(.2f, .1f), (MathF.BitIncrement(.2f), .1f), (.4f, .4f),
            (.5f, .5f), (1, 1), (MathF.BitIncrement(1), .3f), (1.5f, .7f), (2, 2),
            (7.99f, .3f), (.3f, 16), (16, 16), (256, .35f)];
        uint[] seeds = [0, 255, 0x80000000, uint.MaxValue];
        float[] starts = [-257.75f, -.75f, 255.75f];
        var material = PixelPalette.Night.Materials[0];
        var colors = new PixelWindowFilter.Colors(material.DarkWall, material);
        int compared = 0;
        foreach (float story in stories)
        foreach (var (x, y) in extents)
        foreach (uint seed in seeds)
        foreach (float start in starts)
        {
            var footprint = new PixelWindowFilter.SamplingFootprint(x, y);
            var row = new PixelWindowFilter.SamplingRow(footprint, seed, story, 1);
            float column = start;
            for (int i = 0; i < 129; i++, column += .073f)
            {
                uint expected = ScalarPixelWindowFilterReference.Sample(colors.Wall, material, seed, column, story, x, y, 1);
                uint actual = PixelWindowFilter.Sample(colors, footprint, row, column);
                if (actual != expected)
                    throw new InvalidOperationException($"Prepared scanline changed a scalar window at rooms {column}/{story}, " +
                        $"footprint {x}/{y}, seed {seed}: {expected:x8}/{actual:x8}.");
                compared++;
            }
        }
        Console.WriteLine($"Passed {compared:N0} exact reused-row window samples across signed story/window boundaries and lamp repeats.");
    }
}
