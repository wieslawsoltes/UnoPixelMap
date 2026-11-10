using System.Reflection;
using System.Security.Cryptography;
using PixelMap.Rendering;
using SkiaSharp;

/// <summary>Native test hosts may choose a web-only default font that Skia cannot
/// decode. Label checks and preparation benchmarks must render actual glyphs.</summary>
internal static class TestLabelFont
{
    internal static string Apply(PixelMapRenderer renderer)
    {
        string? path = Environment.GetEnvironmentVariable("PIXELMAP_TEST_FONT");
        using var typeface = path is null ? SKTypeface.FromFamilyName("DejaVu Sans") : SKTypeface.FromFile(path);
        var font = (SKFont)typeof(PixelMapRenderer).GetField("font", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        font.Typeface = typeface;
        if (typeface is null || typeface.GlyphCount == 0 || font.MeasureText("Street 1") <= 0)
            throw new InvalidOperationException("Label tests require a readable font. Install DejaVu Sans or set PIXELMAP_TEST_FONT to a TTF/OTF file.");
        return path is null ? $"{typeface.FamilyName}; {typeface.GlyphCount} glyphs" :
            $"{typeface.FamilyName}; {typeface.GlyphCount} glyphs; SHA256 {Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))}";
    }
}
