using SkiaSharp;

namespace PixelMap.Rendering;

public enum PixelStyle { Day, Dusk, Night }

internal readonly record struct Material(uint Roof, uint LightWall, uint DarkWall, uint Rim, uint Trim, uint Glass, uint Lamp);

internal sealed class PixelPalette
{
    public readonly uint Ground, Label, LabelHalo;
    public readonly uint[] Inks;
    public readonly Material[] Materials;
    public readonly PixelStyle Style;
    public static readonly PixelPalette Day = new(PixelStyle.Day);
    public static readonly PixelPalette Dusk = new(PixelStyle.Dusk);
    public static readonly PixelPalette Night = new(PixelStyle.Night);

    private PixelPalette(PixelStyle style)
    {
        Style = style;
        Ground = Adapt(0xe6dabc);
        Label = style == PixelStyle.Night ? 0xffe9e8cd : 0xff293c41;
        LabelHalo = style == PixelStyle.Night ? 0xff253546 : 0xfffbefcc;
        uint[] colors = [0xdfd1b6, 0xc8bdba, 0xf1d79e, 0xe7f2e9, 0xb3c88c, 0x85b486, 0xa7c887,
            0x76bdc4, 0x73b7bf, 0xad9b90, 0xb7b1a9, 0xb29f87, 0xd0b892, 0xece1c7,
            0xf9edcf, 0xf5d799, 0xeab87c, 0x867e79, 0xb0aaa4, 0x3a514b, 0x458d9b];
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

    public void Dispose() { source.Dispose(); bitmap.Dispose(); }
}
