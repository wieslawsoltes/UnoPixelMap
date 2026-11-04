using System.Runtime.InteropServices;
using SkiaSharp;

namespace PixelMap.Controls;

/// <summary>A bounded, context-owned browser pixel-transfer queue.</summary>
internal sealed class BrowserGpuReadback : IDisposable
{
    private int _handle;

    private BrowserGpuReadback(int handle) => _handle = handle;

    /// <summary>Create while the GLCanvas context is current. Hosts without the native
    /// adapter retain the synchronous presentation path.</summary>
    internal static BrowserGpuReadback? TryCreate()
    {
        if (!OperatingSystem.IsBrowser()) return null;
        try
        {
            int handle = Native.Create();
            return handle == 0 ? null : new BrowserGpuReadback(handle);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Call at every RenderOverride entry, including callbacks that will skip
    /// drawing. Uno's subsequent bitmap readback will leave its hidden bitmap untouched.</summary>
    internal void SelectSurface() => Check(Native.Select(Handle));

    internal bool CanSubmit => Check(Native.Capacity(Handle)) > 0;

    /// <summary>Reserve one of two slots for Uno's readback after the drawing callback.</summary>
    internal bool TryBeginFrame(long version, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        return Check(Native.Begin(Handle, (uint)version, (uint)((ulong)version >> 32), width, height)) > 0;
    }

    /// <summary>Release a reservation whose drawing callback did not complete.</summary>
    internal void CancelFrame()
    {
        if (_handle != 0) Native.Cancel(_handle);
    }

    /// <summary>Poll with zero timeout. This can run from a timer without making the
    /// GLCanvas context current or triggering another render.</summary>
    internal bool TryGetCompleted(out BrowserGpuReadbackFrame frame)
    {
        int status = Check(Native.Poll(Handle, out var result));
        frame = status == 0 ? default : new BrowserGpuReadbackFrame(
            (long)(((ulong)result.High << 32) | result.Low), result.Width, result.Height);
        return status != 0;
    }

    /// <summary>Copy the selected completed transfer into immutable RGBA Skia storage.
    /// The image retains GL's bottom-up row order and must be flipped when presented.</summary>
    internal SKImage ReadCompleted(BrowserGpuReadbackFrame frame)
    {
        using var bitmap = new SKBitmap(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        if (bitmap.GetPixels() == IntPtr.Zero || bitmap.RowBytes != checked(frame.Width * sizeof(uint)))
            throw new InvalidOperationException("The completed GPU image could not allocate tightly packed Skia storage.");
        Check(Native.Copy(Handle, (uint)frame.Version, (uint)((ulong)frame.Version >> 32),
            bitmap.GetPixels(), checked(bitmap.RowBytes * frame.Height)));
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>Release a canceled or superseded frame without transferring its pixels.</summary>
    internal void DiscardCompleted(BrowserGpuReadbackFrame frame) =>
        Check(Native.Copy(Handle, (uint)frame.Version, (uint)((ulong)frame.Version >> 32), IntPtr.Zero, 0));

    private int Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    private static int Check(int status)
    {
        if (status < 0) throw new InvalidOperationException("The browser GPU pixel-transfer pipeline failed.");
        return status;
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        Native.Dispose(_handle);
        _handle = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFrame
    {
        internal uint Low, High;
        internal int Width, Height;
    }

    private static class Native
    {
        private const string Library = "pixel_map_gl_readback";

        [DllImport(Library, EntryPoint = "pixel_map_readback_create")]
        internal static extern int Create();

        [DllImport(Library, EntryPoint = "pixel_map_readback_select")]
        internal static extern int Select(int handle);

        [DllImport(Library, EntryPoint = "pixel_map_readback_capacity")]
        internal static extern int Capacity(int handle);

        [DllImport(Library, EntryPoint = "pixel_map_readback_begin")]
        internal static extern int Begin(int handle, uint low, uint high, int width, int height);

        [DllImport(Library, EntryPoint = "pixel_map_readback_cancel")]
        internal static extern void Cancel(int handle);

        [DllImport(Library, EntryPoint = "pixel_map_readback_poll")]
        internal static extern int Poll(int handle, out NativeFrame frame);

        [DllImport(Library, EntryPoint = "pixel_map_readback_copy")]
        internal static extern int Copy(int handle, uint low, uint high, IntPtr destination, int capacity);

        [DllImport(Library, EntryPoint = "pixel_map_readback_dispose")]
        internal static extern void Dispose(int handle);
    }
}

internal readonly record struct BrowserGpuReadbackFrame(long Version, int Width, int Height);
