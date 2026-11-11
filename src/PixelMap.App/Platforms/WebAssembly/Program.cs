using System.Runtime.InteropServices.JavaScript;
using Uno.UI.Composition.WebGpu;
using Uno.UI.Hosting;

namespace PixelMap.App;

internal static partial class Program
{
    public static async Task Main()
    {
        try
        {
            // The same WebGPU device draws the Uno UI and WebGpuCanvasElement.
            // Registering only this backend intentionally prevents a silent Skia/GL fallback.
            var host = UnoPlatformHostBuilder.Create()
                .App(() => new App())
                .GraphicsBackend(WebGpuBackend.CreateGraphicsProvider())
                .UseWebAssembly()
                .Build();
            await host.RunAsync();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Pixel Atlas startup failed: {error}");
            ReportStartupFailure("Pixel Atlas could not initialize WebGPU. " + error.Message);
            throw;
        }
    }

    [JSImport("globalThis.PixelAtlasGraphics.fail")]
    private static partial void ReportStartupFailure(string message);
}
