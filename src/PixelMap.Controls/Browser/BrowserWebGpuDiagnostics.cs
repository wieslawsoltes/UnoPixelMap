using System.Runtime.InteropServices.JavaScript;

namespace PixelMap.Controls;

/// <summary>Small, bounded frame counters for graphics diagnostics. No pixels or
/// user data are read back. Reporting failure works even when the UI device is lost.</summary>
internal static partial class BrowserWebGpuDiagnostics
{
    internal static void Report(long frames, long preparations, int width, int height,
        int physicalWidth, int physicalHeight, int pixelSize, int patches, int triangles, int draws,
        long uploadBytes, long residentBytes, double frameMilliseconds, double submissionMilliseconds,
        string style, string quality, int renderWidth, int renderHeight, long attachmentBytes,
        int imageryCount, bool relief, bool elevation, double zoom)
    {
        if (!OperatingSystem.IsBrowser()) return;
        ReportFrame(frames, preparations, width, height, physicalWidth, physicalHeight, pixelSize,
            patches, triangles, draws, uploadBytes, residentBytes, frameMilliseconds, submissionMilliseconds, style, quality,
            renderWidth, renderHeight, attachmentBytes, imageryCount, relief, elevation, zoom);
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
        int imageryCount, bool relief, bool elevation, double zoom);

    [JSImport("globalThis.PixelAtlasInput.failWebGpu")]
    private static partial void ReportFailure(string message);
}
