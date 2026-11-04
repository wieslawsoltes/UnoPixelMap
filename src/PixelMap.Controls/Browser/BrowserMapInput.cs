using System.Runtime.InteropServices.JavaScript;

namespace PixelMap.Controls;

internal enum BrowserWheelKind { Consumed = -1, Pan, WheelZoom, PinchZoom, Gesture }

internal readonly record struct BrowserWheelSample(BrowserWheelKind Kind, double X, double Y, bool Shift, bool Alt);

/// <summary>Retains raw browser input while Uno performs native XAML hit testing.</summary>
internal sealed partial class BrowserMapInput : IDisposable
{
    private static int _sequence;
    private int _id;
    private bool _mouseRequested;

    private BrowserMapInput(int id) => _id = id;

    internal static BrowserMapInput? TryCreate(Action suspended)
    {
        if (!OperatingSystem.IsBrowser()) return null;
        try
        {
            int id = Interlocked.Increment(ref _sequence);
            Attach(id, suspended);
            return new(id);
        }
        catch (JSException error)
        {
            Console.WriteLine($"Pixel Atlas browser input adapter could not initialize. {error.Message}");
            return null;
        }
    }

    internal bool TryTakeWheel(out BrowserWheelSample sample)
    {
        sample = default;
        if (!OperatingSystem.IsBrowser() || _id == 0) return false;
        var values = TakeWheel(_id);
        if (values.Length != 5) return false;
        sample = new((BrowserWheelKind)(int)values[0], values[1], values[2], values[3] != 0, values[4] != 0);
        return true;
    }

    internal bool IsRepeatedKey(bool fallback)
    {
        if (!OperatingSystem.IsBrowser() || _id == 0) return fallback;
        int repeat = GetKeyRepeat(_id);
        return repeat < 0 ? fallback : repeat != 0;
    }

    internal void CapturePointer(uint pointerId)
    {
        if (OperatingSystem.IsBrowser() && _id != 0) CaptureNativePointer(_id, pointerId);
    }

    internal void ReleasePointer(uint pointerId)
    {
        if (OperatingSystem.IsBrowser() && _id != 0) ReleaseNativePointer(_id, pointerId);
    }

    internal void ReleasePointers()
    {
        if (OperatingSystem.IsBrowser() && _id != 0) ReleaseNativePointers(_id);
    }

    internal bool CaptureMouse()
    {
        if (!OperatingSystem.IsBrowser() || _id == 0) return false;
        return _mouseRequested = CaptureNativeMouse(_id);
    }

    internal void ReleaseMouse()
    {
        if (!OperatingSystem.IsBrowser() || _id == 0 || !_mouseRequested) return;
        _mouseRequested = false;
        ReleaseNativeMouse(_id);
    }

    internal bool IsMouseCaptured => OperatingSystem.IsBrowser() && _id != 0 && IsNativeMouseCaptured(_id);

    internal bool TryTakeMouseMotion(out double dx, out double dy)
    {
        dx = dy = 0;
        if (!OperatingSystem.IsBrowser() || _id == 0) return false;
        var values = TakeMouseMotion(_id);
        if (values.Length != 2) return false;
        dx = values[0]; dy = values[1];
        return true;
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsBrowser() || _id == 0) return;
        Detach(_id);
        _id = 0;
    }

    [JSImport("globalThis.PixelAtlasInput.attach")]
    private static partial void Attach(int id, [JSMarshalAs<JSType.Function>] Action suspended);

    [JSImport("globalThis.PixelAtlasInput.detach")]
    private static partial void Detach(int id);

    [JSImport("globalThis.PixelAtlasInput.takeWheel")]
    private static partial double[] TakeWheel(int id);

    [JSImport("globalThis.PixelAtlasInput.getKeyRepeat")]
    private static partial int GetKeyRepeat(int id);

    [JSImport("globalThis.PixelAtlasInput.capturePointer")]
    private static partial void CaptureNativePointer(int id, double pointerId);

    [JSImport("globalThis.PixelAtlasInput.releasePointer")]
    private static partial void ReleaseNativePointer(int id, double pointerId);

    [JSImport("globalThis.PixelAtlasInput.releasePointers")]
    private static partial void ReleaseNativePointers(int id);

    [JSImport("globalThis.PixelAtlasInput.captureMouse")]
    private static partial bool CaptureNativeMouse(int id);

    [JSImport("globalThis.PixelAtlasInput.releaseMouse")]
    private static partial void ReleaseNativeMouse(int id);

    [JSImport("globalThis.PixelAtlasInput.isMouseCaptured")]
    private static partial bool IsNativeMouseCaptured(int id);

    [JSImport("globalThis.PixelAtlasInput.takeMouseMotion")]
    private static partial double[] TakeMouseMotion(int id);
}
