namespace PixelMap.Rendering;

/// <summary>Bounded map-rendering work budgets. Continuous styles select shadow and
/// antialiasing quality; moving pixel styles may adjust their logical grid density
/// while preserving nearest-neighbour sampling.</summary>
public enum WebGpuQuality { Balanced, High, Ultra }

internal readonly record struct WebGpuRenderOptions(int ShadowSize, int ShadowCascades, int SampleCount,
    long MaximumScenePixels, float ShadowDistance, float Exposure, float Bloom, int BloomLevels)
{
    internal static WebGpuRenderOptions For(WebGpuQuality quality) => quality switch
    {
        WebGpuQuality.Balanced => new(1024, 1, 1, 2_073_600, 1200, 1.05f, 0, 0),
        WebGpuQuality.Ultra => new(2048, 4, 4, 8_294_400, 2400, 1.05f, .055f, 6),
        _ => new(1536, 2, 4, 3_686_400, 1800, 1.05f, .035f, 5)
    };
}
