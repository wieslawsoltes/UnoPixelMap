using SkiaSharp;
using System.Runtime.InteropServices;

namespace PixelMap.Rendering;

public enum PixelStyle { Day, Dusk, Night }

internal readonly record struct Material(uint Roof, uint LightWall, uint DarkWall, uint Rim, uint Trim, uint Glass, uint Lamp);

internal sealed class PixelPalette
{
    public readonly uint Ground, Label, LabelHalo;
    public readonly uint[] Inks;
    public readonly Material[] Materials;
    public readonly PixelStyle Style;
    public readonly bool IsPixelArt;
    public static readonly PixelPalette Day = new(PixelStyle.Day);
    public static readonly PixelPalette Dusk = new(PixelStyle.Dusk);
    public static readonly PixelPalette Night = new(PixelStyle.Night);
    public static readonly PixelPalette PixelDay = new(PixelStyle.Day, true);
    public static readonly PixelPalette PixelDusk = new(PixelStyle.Dusk, true);
    public static readonly PixelPalette PixelNight = new(PixelStyle.Night, true);

    internal static PixelPalette For(PixelStyle lighting, MapRenderStyle renderStyle) => (lighting, renderStyle) switch
    {
        (PixelStyle.Dusk, MapRenderStyle.Pixel) => PixelDusk,
        (PixelStyle.Night, MapRenderStyle.Pixel) => PixelNight,
        (_, MapRenderStyle.Pixel) => PixelDay,
        (PixelStyle.Dusk, _) => Dusk,
        (PixelStyle.Night, _) => Night,
        _ => Day
    };

    private PixelPalette(PixelStyle style, bool pixelArt = false)
    {
        Style = style;
        IsPixelArt = pixelArt;
        // Label colors are a separate accessibility/overlay contract. Changing the
        // authored map materials must not change label preparation or its hashes.
        Label = style == PixelStyle.Night ? 0xffe9e8cd : 0xff293c41;
        LabelHalo = style == PixelStyle.Night ? 0xff253546 : 0xfffbefcc;
        if (pixelArt)
        {
            Ground = AdaptPixel(0xd8cfac);
            uint[] inks = [0xcec5ac, 0xacafa5, 0xe8c483, 0xddece0, 0xa0b478, 0x547a60, 0x7e9b61,
                0x4c8c94, 0x497f8a, 0x9c9a82, 0x8a918d, 0x716f65, 0xc5b18a, 0xddd4bb,
                0xefe1bc, 0xe6c58b, 0xd7a46b, 0x68756f, 0xbcc3b1, 0x364c57, 0xb6d2c3, 0x356f78];
            Inks = Array.ConvertAll(inks, AdaptPixel);
            Inks[(int)Ink.Water] = style switch
            { PixelStyle.Dusk => 0xff527a8b, PixelStyle.Night => 0xff274e64, _ => Inks[(int)Ink.Water] };
            Inks[(int)Ink.Stream] = Mix(Inks[(int)Ink.Water], AdaptPixel(0x244d60), .12f);
            Inks[(int)Ink.Shore] = Mix(Inks[(int)Ink.Water], AdaptPixel(0x213e4d), .38f);
            Inks[(int)Ink.Shadow] = (Inks[(int)Ink.Shadow] & 0xffffff) | 0x4a000000;
            Inks[(int)Ink.Ferry] = (Inks[(int)Ink.Ferry] & 0xffffff) | 0x90000000;
            Inks[(int)Ink.Tunnel] = (Inks[(int)Ink.Tunnel] & 0xffffff) | 0x79000000;
            // Twelve material slots preserve model identity and the shared seven-
            // color ABI. Warm masonry/roofs turn into cool, legible shaded planes;
            // windows use connected glass/lamp clusters rather than white flecks.
            uint[] pixelRoofs = [0xc79e6a, 0xb66f57, 0x9ca98a, 0xd2b18a, 0xa08c88, 0x6e9b91,
                0xcebb88, 0x7d8f9c, 0x6f949f, 0xb18b76, 0x93a27c, 0xb77f68];
            Materials = new Material[pixelRoofs.Length];
            uint sun = AdaptPixel(0xe4d5b1), shade = AdaptPixel(0x344f62), trim = AdaptPixel(0x243f49);
            uint glazing = AdaptPixel(0x345c65), reflection = AdaptPixel(0xc7d5ba), rim = AdaptPixel(0xf0ddb3);
            for (int i = 0; i < pixelRoofs.Length; i++)
            {
                uint roof = AdaptPixel(pixelRoofs[i]), lightWall = Mix(roof, sun, .42f), darkWall = Mix(roof, shade, .60f);
                uint glass = Mix(glazing, darkWall, .18f);
                Materials[i] = new(roof, lightWall, darkWall, Mix(roof, rim, .50f), Mix(darkWall, trim, .40f),
                    glass, style == PixelStyle.Day ? Mix(glass, reflection, .42f) : 0xffffd58b);
            }
            return;
        }

        // Normal, Minecraft and Roblox retain their established colors exactly.
        Ground = Adapt(0xe6dabc);
        uint[] colors = [0xdfd1b6, 0xc8bdba, 0xf1d79e, 0xe7f2e9, 0xb3c88c, 0x85b486, 0xa7c887,
            0x76bdc4, 0x73b7bf, 0xad9b90, 0xb7b1a9, 0xb29f87, 0xd0b892, 0xece1c7,
            0xf9edcf, 0xf5d799, 0xeab87c, 0x867e79, 0xb0aaa4, 0x3a514b, 0x458d9b, 0x5e9aa4];
        Inks = colors.Select(Adapt).ToArray();
        Inks[(int)Ink.Shadow] = (Inks[(int)Ink.Shadow] & 0xffffff) | 0x42000000;
        Inks[(int)Ink.Ferry] = (Inks[(int)Ink.Ferry] & 0xffffff) | 0x72000000;
        Inks[(int)Ink.Tunnel] = (Inks[(int)Ink.Tunnel] & 0xffffff) | 0x79000000;
        uint[] roofs = [0xddb17e,0xc28168,0xb8c2af,0xe1b598,0xc6aa9e,0x8fbab0,0xe5caa0,0xa5afbc,0x96b6c2,0xd6b6a4,0xa3bd9f,0xce998a];
        Materials = roofs.Select(rgb =>
        {
            uint roof = Adapt(rgb), lightWall = Mix(roof, Adapt(0xb2a189), .24f);
            uint darkWall = Mix(roof, Adapt(0x354f61), .58f);
            uint glass = Mix(Adapt(0x547c87), darkWall, .22f);
            return new Material(roof, lightWall, darkWall, Mix(roof, Adapt(0xffedd2), .52f),
                Mix(darkWall, Adapt(0x47525c), .38f), glass,
                style == PixelStyle.Day ? Mix(glass, Adapt(0xc0d0c9), .52f) : 0xffffd58b);
        }).ToArray();
    }

    private uint AdaptPixel(uint rgb) => Style switch
    {
        PixelStyle.Dusk => Mix(rgb | 0xff000000, 0xff6b5370, .24f),
        PixelStyle.Night => Mix(rgb | 0xff000000, 0xff1d2c46, .66f),
        _ => rgb | 0xff000000
    };

    private uint Adapt(uint rgb)
    {
        uint color = rgb | 0xff000000;
        if (Style == PixelStyle.Day) return color;
        if (Style == PixelStyle.Dusk)
        {
            if (rgb == 0x76bdc4 || rgb == 0x73b7bf) return 0xff749aaf;
            return Mix(color, 0xffa16879, .36f);
        }
        if (rgb == 0x76bdc4 || rgb == 0x73b7bf) return 0xff345e73;
        return Mix(color, 0xff15243c, .76f);
    }

    internal static uint Mix(uint a, uint b, float t)
    {
        uint r = (uint)(((a >> 16) & 255) * (1 - t) + ((b >> 16) & 255) * t);
        uint g = (uint)(((a >> 8) & 255) * (1 - t) + ((b >> 8) & 255) * t);
        uint blue = (uint)((a & 255) * (1 - t) + (b & 255) * t);
        return 0xff000000 | (r << 16) | (g << 8) | blue;
    }
}

internal sealed class TerrainPattern : IDisposable
{
    private readonly SKBitmap bitmap;
    private readonly SKShader source;

    public TerrainPattern(Ink ink, PixelPalette palette)
    {
        const int size = 64;
        uint field = palette.Inks[(int)ink];
        bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Opaque);
        if (palette.IsPixelArt)
        {
            PixelTerrainArt.Fill(MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan()), bitmap.RowBytes / sizeof(uint), ink, palette);
            bitmap.NotifyPixelsChanged();
            bitmap.SetImmutable();
            source = SKShader.CreateBitmap(bitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
            return;
        }
        bitmap.Erase(new SKColor(field));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { IsAntialias = false };
        var random = new Random(ink == Ink.Water ? 29 : 41);
        for (int i = 0; i < (ink == Ink.Water ? 13 : 21); i++)
        {
            int x = random.Next(2, size - 8), y = random.Next(5, size - 3);
            if (ink == Ink.Water)
            {
                paint.Color = new(PixelPalette.Mix(field, palette.Inks[(int)Ink.Ice], .28f));
                canvas.DrawRect(x, y, random.Next(2, 6), 1, paint);
                paint.Color = new(PixelPalette.Mix(field, palette.Label, .10f));
                canvas.DrawRect(x + 2, y + 1, 3, 1, paint);
            }
            else if (ink == Ink.Forest || (ink == Ink.Park && i % 4 == 0))
            {
                paint.Color = new(PixelPalette.Mix(field, palette.Label, .20f));
                canvas.DrawRect(x, y, 5, 2, paint);
                canvas.DrawRect(x + 1, y + 2, 1, 1, paint);
                paint.Color = new(PixelPalette.Mix(field, palette.Label, .32f));
                canvas.DrawRect(x - 1, y - 2, 5, 3, paint);
                canvas.DrawRect(x, y - 4, 3, 2, paint);
                paint.Color = new(PixelPalette.Mix(field, palette.Inks[(int)Ink.Grass], .55f));
                canvas.DrawRect(x, y - 3, 2, 2, paint);
            }
            else
            {
                paint.Color = new(PixelPalette.Mix(field, palette.Label, .16f));
                canvas.DrawRect(x, y, 2, 1, paint);
            }
        }
        bitmap.SetImmutable();
        source = SKShader.CreateBitmap(bitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
    }

    public SKShader AtScale(float tileUnitsPerPixel) =>
        source.WithLocalMatrix(SKMatrix.CreateScale(tileUnitsPerPixel, tileUnitsPerPixel));

    internal SKShader At(TerrainPatternPlacement placement) => source.WithLocalMatrix(
        SKMatrix.CreateScaleTranslation(MathF.ScaleB(1, placement.Exponent), MathF.ScaleB(1, placement.Exponent),
            -placement.PhaseX, -placement.PhaseY));

    public void Dispose() { source.Dispose(); bitmap.Dispose(); }
}
