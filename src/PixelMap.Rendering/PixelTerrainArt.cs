using PixelMap.Data;

namespace PixelMap.Rendering;

/// <summary>One pattern texel spans 2^-25 of the Mercator world. Its geographic
/// phase does not depend on camera zoom, cache-block boundaries or XYZ source LOD.</summary>
internal readonly record struct TerrainPatternPlacement(int Exponent, int PhaseX = 0, int PhaseY = 0)
{
    internal static TerrainPatternPlacement Pixel(TileId tile)
    {
        int exponent = Math.Clamp(tile.Z - 13, -13, 17);
        // For ordinary source tiles the 64-texel repeat divides the 4096-unit
        // tile exactly. Finer sources retain the remaining world phase. Masking
        // before multiplication is exact even for negative/wrapped tile indices.
        int mask = exponent <= 6 ? 0 : (1 << (exponent - 6)) - 1;
        return new(exponent, (tile.X & mask) * 4096, (tile.Y & mask) * 4096);
    }
}

/// <summary>Authored 64×64 repeating pixel motifs. Connected clusters and a fixed
/// geographic grid replace isolated random marks; rasterization allocates no
/// temporary canvas, paint, random generator or per-mark color objects.</summary>
internal static class PixelTerrainArt
{
    internal const int Size = 64;

    internal static void Fill(Span<uint> pixels, int stride, Ink ink, PixelPalette palette)
    {
        if (stride < Size || pixels.Length < (Size - 1) * stride + Size)
            throw new ArgumentException("The pattern requires a complete 64 by 64 pixel surface.", nameof(pixels));
        uint field = palette.Inks[(int)ink];
        var writer = new Writer(pixels, stride);
        for (int y = 0; y < Size; y++) pixels.Slice(y * stride, Size).Fill(field);
        // Every motif reuses a small authored ramp instead of mixing colors in
        // its inner loops. Ink identity, never process/random state, selects it.
        uint shadow = PixelPalette.Mix(field, palette.Inks[(int)Ink.Shore], ink == Ink.Water ? .34f : .26f);
        uint mid = PixelPalette.Mix(field, palette.Inks[(int)Ink.Grass], ink == Ink.Forest ? .18f : .09f);
        uint light = PixelPalette.Mix(field, palette.Inks[(int)Ink.Ice], ink == Ink.Water ? .24f : .15f);
        uint trunk = PixelPalette.Mix(field, palette.Inks[(int)Ink.RoadEdge], .48f);
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
        {
            uint seed = Hash((uint)(column + row * 4), (uint)ink);
            int x = column * 16 + 3 + (int)(seed & 3), y = row * 16 + 3 + (int)((seed >> 3) & 3);
            if (ink == Ink.Water)
            {
                int length = 4 + (int)((seed >> 6) & 3);
                writer.Rect(x + 1, y, length - 2, 1, light);
                writer.Rect(x, y + 1, length, 1, mid);
                writer.Rect(x + 2, y + 2, length - 1, 1, shadow);
            }
            else if (ink == Ink.Forest)
            {
                writer.Tree(x - 2, y - 2, shadow, mid, light, trunk);
                writer.Tree(x + 5, y + 5, shadow, mid, light, trunk);
            }
            else if (ink == Ink.Park && (seed & 3) == 0)
                writer.Tree(x, y - 1, shadow, mid, light, trunk);
            else
            {
                // Small joined grass clusters remain quieter than roads, labels
                // and canopy silhouettes. They contain no single bright pixel.
                writer.Rect(x, y + 1, 3, 1, mid);
                writer.Rect(x + 1, y, 2, 1, light);
                if ((seed & 1) != 0) writer.Rect(x + 7, y + 6, 2, 1, shadow);
            }
        }
    }

    private static uint Hash(uint cell, uint ink)
    {
        uint value = unchecked((cell + 1) * 0x9e3779b9u ^ (ink + 7) * 0x85ebca6bu);
        value = unchecked((value ^ (value >> 16)) * 0x7feb352du);
        return value ^ (value >> 15);
    }

    private readonly ref struct Writer
    {
        private readonly Span<uint> pixels;
        private readonly int stride;

        internal Writer(Span<uint> pixels, int stride) { this.pixels = pixels; this.stride = stride; }

        internal void Rect(int x, int y, int width, int height, uint color)
        {
            for (int row = 0; row < height; row++)
            {
                int offset = ((y + row) & (Size - 1)) * stride;
                for (int column = 0; column < width; column++) pixels[offset + ((x + column) & (Size - 1))] = color;
            }
        }

        internal void Tree(int x, int y, uint shadow, uint mid, uint light, uint trunk)
        {
            Rect(x + 2, y + 6, 4, 2, shadow);
            Rect(x + 3, y + 5, 1, 2, trunk);
            Rect(x + 2, y, 3, 1, mid);
            Rect(x + 1, y + 1, 5, 1, mid);
            Rect(x, y + 2, 7, 3, shadow);
            Rect(x + 1, y + 5, 5, 1, shadow);
            Rect(x + 1, y + 2, 4, 3, mid);
            Rect(x + 1, y + 1, 3, 1, light);
            Rect(x + 1, y + 2, 2, 2, light);
        }
    }
}
