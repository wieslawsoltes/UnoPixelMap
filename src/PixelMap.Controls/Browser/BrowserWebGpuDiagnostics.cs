using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Uno.UI.Composition.WebGpu;

namespace PixelMap.Controls;

/// <summary>Small, bounded frame counters for graphics diagnostics. No pixels or
/// user data are read back. Reporting failure works even when the UI device is lost.</summary>
internal static partial class BrowserWebGpuDiagnostics
{
    /// <summary>Observe the exact device borrowed from Uno's canvas context. Its
    /// native handle is the imported pointer accepted by Uno's public JS interop;
    /// disposing this subscription never destroys or releases the borrowed device.</summary>
    internal static IDisposable? ObserveDevice(nint device, Action<string> failed)
    {
        if (!OperatingSystem.IsBrowser()) return null;
        ArgumentNullException.ThrowIfNull(failed);
        if (device == 0) throw new ArgumentException("A live shared WebGPU device is required.", nameof(device));
        // Uno's browser importer returns this same wasm32 pointer as an int. Keep
        // its unsigned bit pattern; GetJsObject performs the JS handle-table lookup.
        // JS interop canonicalizes proxies: this lookup can return the JSObject
        // Uno already holds for presentation and resize. It is borrowed too; only
        // our newly created subscription token is owned and disposed below.
        var browserDevice = WebGpuJsInterop.GetJsObject(unchecked((int)device));
        return new DeviceObservation(ObserveSharedDevice(browserDevice, failed));
    }

    internal static void Report(long frames, long preparations, int width, int height,
        int physicalWidth, int physicalHeight, int pixelSize, int patches, int triangles, int draws,
        long uploadBytes, long residentBytes, double frameMilliseconds, double submissionMilliseconds,
        string style, string quality, int renderWidth, int renderHeight, long attachmentBytes,
        int imageryCount, bool relief, bool elevation, double zoom,
        double preparationMilliseconds, double overlayMilliseconds)
    {
        if (!OperatingSystem.IsBrowser()) return;
        ReportFrame(frames, preparations, width, height, physicalWidth, physicalHeight, pixelSize,
            patches, triangles, draws, uploadBytes, residentBytes, frameMilliseconds, submissionMilliseconds, style, quality,
            renderWidth, renderHeight, attachmentBytes, imageryCount, relief, elevation, zoom,
            preparationMilliseconds, overlayMilliseconds);
    }

    internal static void Fail(string message)
    {
        if (OperatingSystem.IsBrowser()) ReportFailure(message);
    }

    [JSImport("globalThis.PixelAtlasInput.reportWebGpu")]
    private static partial void ReportFrame(double frames, double preparations, int width, int height,
        int physicalWidth, int physicalHeight, int pixelSize, int patches, int triangles, int draws,
        double uploadBytes, double residentBytes, double frameMilliseconds, double submissionMilliseconds,
        string style, string quality, int renderWidth, int renderHeight, double attachmentBytes,
        int imageryCount, bool relief, bool elevation, double zoom,
        double preparationMilliseconds, double overlayMilliseconds);

    [JSImport("globalThis.PixelAtlasInput.failWebGpu")]
    private static partial void ReportFailure(string message);

    [JSImport("globalThis.PixelAtlasInput.observeWebGpuDevice")]
    private static partial JSObject ObserveSharedDevice(JSObject device,
        [JSMarshalAs<JSType.Function<JSType.String>>] Action<string> failed);

    [JSImport("globalThis.PixelAtlasInput.stopObservingWebGpuDevice")]
    private static partial void StopObserving(JSObject subscription);

    [SupportedOSPlatform("browser")]
    private sealed class DeviceObservation(JSObject subscription) : IDisposable
    {
        private JSObject? current = subscription;

        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref current, null);
            if (previous is null) return;
            try { StopObserving(previous); }
            finally { previous.Dispose(); }
        }
    }
}
