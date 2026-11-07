using System.Runtime.CompilerServices;
using System.Numerics;

namespace PixelMap.Rendering;

/// <summary>Art-pixel filtering for the existing periodic façade windows. Resolved
/// windows keep their exact palette texels; minified windows retain their average
/// glass and lamp contribution instead of aliasing or disappearing at a distance.</summary>
internal static class PixelWindowFilter
{
    internal const int Columns = 256, Stories = 16;
    internal const float StartX = .17f, EndX = .76f, StartY = .23f, EndY = .75f;
    internal const float FilterStart = .2f, FilterEnd = .5f;
    internal static readonly byte[] Rooms = CreateRooms();
    internal static readonly Level[] Levels = CreateLevels();

    internal readonly record struct Level(int Width, int Height, byte[] Pixels);

    /// <summary>Palette expansion is constant across a wall triangle. Retain the
    /// three color vectors so the filtered pixel loop only blends their channels.</summary>
    internal readonly struct Colors
    {
        internal readonly uint Wall, Glass, Lamp;
        internal readonly Vector4 Opaque, Glazing, LampDifference;

        internal Colors(uint wall, in Material material)
        {
            Wall = wall; Glass = material.Glass; Lamp = material.Lamp;
            Opaque = Expand(wall);
            Glazing = Expand(Glass);
            LampDifference = Expand(Lamp) - Glazing;
        }
    }

    /// <summary>A front-facing wall with constant reciprocal W has the same
    /// footprint at every sample. Its mip level and transition need one evaluation
    /// per triangle, while varying-perspective walls keep the general path.</summary>
    internal readonly struct SamplingFootprint
    {
        internal readonly float X, Y, Maximum, Lod, Blend;

        internal SamplingFootprint(float x, float y)
        {
            X = x; Y = y; Maximum = Math.Max(x, y);
            Lod = LevelOfDetail(Maximum);
            Blend = FilterBlend(Maximum);
        }
    }

    private static byte[] CreateRooms()
    {
        var rooms = new byte[Columns];
        var random = new Random(859);
        for (int i = 0; i < rooms.Length; i += 2)
        {
            rooms[i] = (byte)random.Next(4);
            rooms[i + 1] = random.Next(3) == 0 ? (byte)random.Next(4) : rooms[i];
        }
        return rooms;
    }

    private static Level[] CreateLevels()
    {
        var levels = new Level[9];
        for (int level = 0; level < levels.Length; level++)
        {
            int width = Math.Max(1, Columns >> level), height = Math.Max(1, Stories >> level);
            int across = Columns / width, down = Stories / height, samples = across * down;
            var pixels = new byte[width * height * 2];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int sum = 0, lamps = 0;
                for (int row = 0; row < down; row++)
                for (int column = 0; column < across; column++)
                {
                    int room = Rooms[(x * across + column + (y * down + row) * Stories) & 255];
                    sum += room;
                    if (room == 0) lamps++;
                }
                int offset = (y * width + x) * 2;
                // R preserves the original nearest lookup (including every non-Pixel
                // shader). G is the lamp coverage. Derive each mip from the original
                // samples so repeated byte rounding cannot brighten distant façades.
                pixels[offset] = (byte)((sum + samples / 2) / samples);
                pixels[offset + 1] = (byte)((lamps * 255 + samples / 2) / samples);
            }
            levels[level] = new(width, height, pixels);
        }
        return levels;
    }

    internal static uint Sample(uint wall, in Material material, uint seed, float column, float story,
        float footprintX, float footprintY, float pixelScale)
        => Sample(new Colors(wall, material), seed, column, story, footprintX, footprintY, pixelScale);

    internal static uint Sample(in Colors colors, uint seed, float column, float story,
        float footprintX, float footprintY, float pixelScale)
    {
        float footprint = Math.Max(footprintX, footprintY);
        if (footprint <= FilterStart) return Point(colors, seed, column, story, pixelScale);
        float coverage = Coverage(column, footprintX, StartX, EndX) * Coverage(story, footprintY, StartY, EndY);
        if (coverage <= 0) return colors.Wall;

        return Filtered(colors, seed, column, story, footprintX, footprintY, pixelScale,
            coverage, LevelOfDetail(footprint), FilterBlend(footprint));
    }

    internal static uint Sample(in Colors colors, in SamplingFootprint footprint, uint seed,
        float column, float story, float pixelScale)
    {
        if (footprint.Maximum <= FilterStart) return Point(colors, seed, column, story, pixelScale);
        float coverage = Coverage(column, footprint.X, StartX, EndX) * Coverage(story, footprint.Y, StartY, EndY);
        if (coverage <= 0) return colors.Wall;

        return Filtered(colors, seed, column, story, footprint.X, footprint.Y, pixelScale,
            coverage, footprint.Lod, footprint.Blend);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Filtered(in Colors colors, uint seed, float column, float story,
        float footprintX, float footprintY, float pixelScale, float coverage, float lod, float blend)
    {
        float x = Lookup(column, footprintX, StartX, EndX) + (seed & 255);
        float y = Lookup(story, footprintY, StartY, EndY);
        float lamps = Lamp(x, y, lod);
        var point = blend < 1 ? Expand(Point(colors, seed, column, story, pixelScale)) : colors.Opaque;
        // Keep the scalar operation order, including the identity blend at one.
        // SIMD combines the independent RGB channels without changing rounding.
        var filtered = colors.Opaque + (colors.Glazing + colors.LampDifference * lamps - colors.Opaque) * coverage;
        var result = Vector4.Clamp(point + (filtered - point) * blend + new Vector4(.5f), Vector4.Zero, new Vector4(255));
        return 0xff000000 | (uint)result.X << 16 | (uint)result.Y << 8 | (uint)result.Z;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LevelOfDetail(float footprint) => footprint <= 1 ? 0 : Math.Min(8, MathF.Log2(footprint));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FilterBlend(float footprint)
    {
        float blend = Math.Clamp((footprint - FilterStart) / (FilterEnd - FilterStart), 0, 1);
        return blend * (blend * (3 - 2 * blend));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector4 Expand(uint color) => new((color >> 16) & 255, (color >> 8) & 255, color & 255, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Point(in Colors colors, uint seed, float column, float story, float pixelScale)
    {
        if (pixelScale <= .4f) return colors.Wall;
        int x = (int)MathF.Floor(column), y = (int)MathF.Floor(story);
        float u = column - x, v = story - y;
        return u > StartX && u < EndX && v > StartY && v < EndY
            ? Rooms[((int)seed + x + y * Stories) & 255] == 0 ? colors.Lamp : colors.Glass
            : colors.Wall;
    }

    /// <summary>Exact box coverage of a periodic interval. Splitting whole periods
    /// from the two end fragments avoids cancellation at large façade coordinates.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Coverage(float position, float footprint, float start, float end)
    {
        float width = Math.Max(footprint, .0001f), half = width * .5f;
        float a = position - half, b = position + half;
        float first = MathF.Floor(a), last = MathF.Floor(b), duty = end - start;
        return Math.Clamp(((last - first) * duty + Math.Clamp(b - last - start, 0, duty) -
            Math.Clamp(a - first - start, 0, duty)) / width, 0, 1);
    }

    /// <summary>Place a bilinear lamp lookup at the covered-window centroid. Up to
    /// one room per pixel, its four weights equal the exact window intersections,
    /// including across room, story, seed and repeat boundaries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float WeightedCell(float position, float footprint, float start, float end)
    {
        float width = Math.Clamp(footprint, .0001f, 1), a = position - width * .5f, b = a + width;
        float cell = MathF.Floor(a);
        float first = Math.Max(0, Math.Min(b, cell + end) - Math.Max(a, cell + start));
        float second = Math.Max(0, Math.Min(b, cell + 1 + end) - Math.Max(a, cell + 1 + start));
        return cell + .5f + (first + second > .000001f ? second / (first + second) : 0);
    }

    private static float Lookup(float position, float footprint, float start, float end)
    {
        // Beyond two rooms the blend is exactly one. For |position| >= 2,
        // position and the centroid have the same sign and differ by at most a
        // factor of two. Sterbenz's lemma makes their subtraction exact, so the
        // original centroid + (position - centroid) rounds to position itself.
        // Keep the original arithmetic near zero and for exceptional coordinates.
        if (footprint >= 2 && float.IsFinite(position) && Math.Abs(position) >= 2) return position;
        float centroid = WeightedCell(position, footprint, start, end);
        float blend = Math.Clamp(footprint - 1, 0, 1);
        blend *= blend * (3 - 2 * blend);
        return centroid + (position - centroid) * blend;
    }

    internal static float Lamp(float column, float story, float lod)
    {
        int first = (int)lod, second = Math.Min(8, first + 1);
        float a = Bilinear(Levels[first], column, story);
        return first == second || lod == first ? a : a + (Bilinear(Levels[second], column, story) - a) * (lod - first);
    }

    private static float Bilinear(Level level, float column, float story)
    {
        float x = column * (level.Width / (float)Columns) - .5f;
        float y = story * (level.Height / (float)Stories) - .5f;
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float dx = x - ix, dy = y - iy;
        int left = ix & (level.Width - 1), right = (ix + 1) & (level.Width - 1);
        int top = iy & (level.Height - 1), bottom = (iy + 1) & (level.Height - 1);
        var pixels = level.Pixels;
        float a = pixels[(top * level.Width + left) * 2 + 1], b = pixels[(top * level.Width + right) * 2 + 1];
        float c = pixels[(bottom * level.Width + left) * 2 + 1], d = pixels[(bottom * level.Width + right) * 2 + 1];
        float upper = a + (b - a) * dx, lower = c + (d - c) * dx;
        return (upper + (lower - upper) * dy) / 255;
    }
}
