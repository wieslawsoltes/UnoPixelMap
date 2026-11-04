namespace PixelMap.Rendering;

/// <summary>The geometry and material treatment of the map, independent of its data
/// layer, lighting palette and camera projection.</summary>
public enum MapRenderStyle
{
    Pixel,
    Normal,
    Minecraft,
    Roblox
}

internal static class MapRendering
{
    internal static bool Smooth(MapRenderStyle style) => style is MapRenderStyle.Normal or MapRenderStyle.Roblox;
    internal static bool Blocks(MapRenderStyle style) => style is MapRenderStyle.Minecraft or MapRenderStyle.Roblox;
    internal static int PixelSize(MapRenderStyle style, int requested) => Smooth(style) ? 1 : Math.Clamp(requested, 1, 8);
    internal static float HeightStep(MapRenderStyle style) => style == MapRenderStyle.Minecraft ? 2 : 1;
}
