using Uno.UI.Hosting;

namespace PixelMap.App;

internal static class Program
{
    public static Task Main() => UnoPlatformHostBuilder.Create()
        .App(() => new App()).UseWebAssembly().Build().RunAsync();
}
