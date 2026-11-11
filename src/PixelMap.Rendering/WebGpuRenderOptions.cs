namespace PixelMap.Rendering;

/// <summary>Bounded work budgets for the native WebGPU renderer. Pixel geometry is
/// never blurred; these presets affect continuous Normal/Roblox shading only.</summary>
public enum WebGpuQuality { Balanced, High, Ultra }

internal readonly record struct WebGpuRenderOptions(int ShadowSize, int SampleCount,
    long MaximumScenePixels, float ShadowDistance, float Exposure, float Bloom)
{
    internal static WebGpuRenderOptions For(WebGpuQuality quality) => quality switch
    {
        WebGpuQuality.Balanced => new(1024, 1, 2_073_600, 1200, 1.05f, 0),
        WebGpuQuality.Ultra => new(2048, 4, 8_294_400, 2500, 1.05f, .055f),
        _ => new(1536, 4, 3_686_400, 1800, 1.05f, .035f)
    };
}
