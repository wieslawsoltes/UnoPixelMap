using System.Runtime.InteropServices.JavaScript;

namespace PixelMap.App;

/// <summary>Event-driven browser safe-area and visual-viewport information for native XAML chrome.</summary>
internal sealed partial class BrowserViewport : IDisposable
{
    private static int _sequence;
    private int _id;

    private BrowserViewport(int id) => _id = id;

    internal static BrowserViewport? TryCreate(Action changed)
    {
        if (!OperatingSystem.IsBrowser()) return null;
        try
        {
            int id = Interlocked.Increment(ref _sequence);
            Attach(id, changed);
            return new(id);
        }
        catch (JSException error)
        {
            Console.WriteLine($"Browser viewport information is unavailable: {error.Message}");
            return null;
        }
    }

    internal double[] Read() => _id == 0 ? [] : ReadNative();

    internal void Locate(Action<double, double> found, Action<string> failed)
    {
        if (_id == 0) { failed("Location is unavailable in this window."); return; }
        try { LocateNative(_id, found, failed); }
        catch (JSException) { failed("Your browser could not request a location. You can search for a place instead."); }
    }

    public void Dispose()
    {
        if (_id == 0) return;
        Detach(_id);
        _id = 0;
    }

    [JSImport("globalThis.PixelAtlasViewport.attach")]
    private static partial void Attach(int id, [JSMarshalAs<JSType.Function>] Action changed);

    [JSImport("globalThis.PixelAtlasViewport.detach")]
    private static partial void Detach(int id);

    [JSImport("globalThis.PixelAtlasViewport.read")]
    private static partial double[] ReadNative();

    [JSImport("globalThis.PixelAtlasViewport.locate")]
    private static partial void LocateNative(
        int id,
        [JSMarshalAs<JSType.Function<JSType.Number, JSType.Number>>] Action<double, double> found,
        [JSMarshalAs<JSType.Function<JSType.String>>] Action<string> failed);
}
