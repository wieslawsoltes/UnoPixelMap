using Silk.NET.WebGPU;

namespace PixelMap.Rendering;

/// <summary>Pixel's authored, encoded-color path. It is not HDR radiance: storing
/// these palette bytes in an unorm target preserves dark inks exactly and halves
/// color attachment storage without changing the art grid or native-size labels.</summary>
internal static class WebGpuPixelArt
{
    internal const int WallRampStart = 7;
    internal const int AccentRampStart = 11;
    internal const int RampSteps = 4;
    internal const int RoofPanel = 15;
    internal const int RoofPanelLight = 16;
    internal const int PaletteWidth = 17;

    internal static TextureFormat SceneFormat(MapRenderStyle style) => style == MapRenderStyle.Pixel
        ? TextureFormat.Rgba8Unorm : TextureFormat.Rgba16float;

    internal static WebGpuAttachmentPlan Attachments(int width, int height, bool photograph)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        // Color = 4, depth = nominal 4, optional encoded photograph = 4 bytes.
        // No MSAA/bloom belongs to the nearest-neighbour art grid.
        return new(width, height, 1, photograph, 0,
            checked((long)width * height * (photograph ? 12 : 8)), 0);
    }

    internal static int PipelineIndex(TextureFormat format, int samples) => (format, samples) switch
    {
        (TextureFormat.Rgba8Unorm, 1) => 0,
        (TextureFormat.Rgba16float, 1) => 1,
        (TextureFormat.Rgba16float, 4) => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(format), "Unexpected scene color/sample combination.")
    };

    internal static void WritePalette(ReadOnlySpan<Material> materials, Span<uint> destination)
    {
        if (destination.Length != checked(materials.Length * PaletteWidth))
            throw new ArgumentException("The complete GPU material table is required.", nameof(destination));
        for (int index = 0; index < materials.Length; index++)
        {
            var m = materials[index];
            var row = destination.Slice(index * PaletteWidth, PaletteWidth);
            // Preserve every original material column for the other styles.
            row[0] = m.Roof; row[1] = m.LightWall; row[2] = m.DarkWall;
            row[3] = m.Rim; row[4] = m.Trim; row[5] = m.Glass; row[6] = m.Lamp;
            for (int step = 0; step < RampSteps; step++)
            {
                uint wall = PixelPalette.Mix(m.DarkWall, m.LightWall, step / 3f);
                row[WallRampStart + step] = wall;
                row[AccentRampStart + step] = PixelPalette.Mix(wall, m.Rim, .22f);
            }
            row[RoofPanel] = PixelPalette.Mix(m.Roof, m.Trim, .16f);
            row[RoofPanelLight] = PixelPalette.Mix(m.Roof, m.Rim, .24f);
        }
    }
}
