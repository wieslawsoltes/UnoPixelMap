using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using PixelMap.Rendering;
using Silk.NET.OpenGL;
using SkiaSharp;

namespace PixelMap.Controls;

/// <summary>Retains map textures on the WebGL context already owned by Uno's Skia canvas.</summary>
internal sealed class BrowserGpuSurface : IDisposable
{
    private readonly GRContext _context;
    private readonly int _contextHandle;
    private readonly GL _gl;
    private readonly GlMapRenderer _engine;
    private readonly Action _invalidate;
    private readonly DispatcherTimer _completionTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Queue<(IntPtr Handle, long Started)> _fences = new(2);
    private Exception? _failure;
    private bool _pendingRender;
    private bool _disposed;

    private BrowserGpuSurface(GRContext context, int contextHandle, Action invalidate)
    {
        _context = context;
        _contextHandle = contextHandle;
        _invalidate = invalidate;
        _completionTimer.Tick += (_, _) => PollCompletion();
        context.Flush(submit: false);
        _gl = GL.GetApi(Native.Resolve);
        try
        {
            _engine = new GlMapRenderer(_gl);
            Renderer = new PixelMapRenderer();
        }
        catch
        {
            _engine?.Dispose();
            _gl.Dispose();
            throw;
        }
        finally
        {
            context.ResetContext();
        }
        Console.WriteLine($"Pixel Atlas GPU texture presentation uses the Skia WebGL context. {_engine.Driver}");
    }

    internal PixelMapRenderer Renderer { get; }
    internal double CompletedMilliseconds { get; private set; }

    internal bool IsAvailable => !_disposed && _context.Handle != IntPtr.Zero &&
        !_context.IsAbandoned && Native.IsLive(_contextHandle) != 0;

    internal bool Matches(SKCanvas canvas)
    {
        if (!IsAvailable) return false;
        var context = canvas.Context;
        if (context is null && BrowserGpuContext.TryGet(out var ownerContext, out _)) context = ownerContext;
        return context?.Handle == _context.Handle;
    }

    internal static BrowserGpuSurface? TryCreate(SKCanvas canvas, Action invalidate)
    {
        if (!OperatingSystem.IsBrowser()) return null;
        try
        {
            var context = canvas.Context as GRContext;
            object? owner = null;
            if (context is null && !BrowserGpuContext.TryGet(out context, out owner)) return null;
            if (context is null || context.Handle == IntPtr.Zero || context.IsAbandoned ||
                context.Backend != GRBackend.OpenGL) return null;
            int previous = Native.CurrentContext();
            try
            {
                if (owner is not null) BrowserGpuContext.MakeCurrent(owner);
                int handle = Native.CurrentContext();
                return handle != 0 && Native.IsLive(handle) != 0 ? new BrowserGpuSurface(context, handle, invalidate) : null;
            }
            finally
            {
                if (Native.CurrentContext() != previous) Native.MakeCurrent(previous);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    internal bool CanDraw
    {
        get
        {
            if (_failure is { } failure) throw new InvalidOperationException("GPU texture completion failed.", failure);
            if (!IsAvailable) throw new InvalidOperationException("The Skia WebGL context is unavailable.");
            using var current = new ContextScope(_contextHandle);
            if (!current.IsCurrent) throw new InvalidOperationException("The Skia WebGL context could not be selected.");
            while (_fences.TryPeek(out var fence))
            {
                var status = (GLEnum)Native.PollFence(fence.Handle);
                if (status == GLEnum.TimeoutExpired) break;
                if (status != GLEnum.AlreadySignaled && status != GLEnum.ConditionSatisfied)
                    throw new InvalidOperationException("The GPU texture completion fence failed.");
                _fences.Dequeue();
                Native.DeleteFence(fence.Handle);
                CompletedMilliseconds = Stopwatch.GetElapsedTime(fence.Started).TotalMilliseconds;
            }
            return _fences.Count < 2;
        }
    }

    internal void RequestRender()
    {
        if (_fences.Count < 2) _invalidate();
        else
        {
            _pendingRender = true;
            if (!_completionTimer.IsEnabled) _completionTimer.Start();
        }
    }

    internal void CancelRequest() => _pendingRender = false;

    private void PollCompletion()
    {
        try
        {
            bool canDraw = CanDraw;
            if (_fences.Count == 0) _completionTimer.Stop();
            if (_pendingRender && canDraw)
            {
                _pendingRender = false;
                _invalidate();
            }
        }
        catch (Exception error)
        {
            _completionTimer.Stop();
            _failure = error;
            _invalidate();
        }
    }

    internal SKImage Draw(GpuMapFrame frame, long started)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable) throw new InvalidOperationException("The map texture's Skia context is unavailable.");
        using var current = new ContextScope(_contextHandle);
        if (!current.IsCurrent) throw new InvalidOperationException("The map texture's Skia context could not be selected.");
        var image = _engine.DrawArtworkImage(frame, _context);
        try
        {
            var fence = Native.CreateFence();
            if (fence == IntPtr.Zero) throw new InvalidOperationException("The GPU could not create a texture completion fence.");
            _fences.Enqueue((fence, started));
            Native.Flush();
            _pendingRender = false;
            if (!_completionTimer.IsEnabled) _completionTimer.Start();
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    internal void ReleaseImage(SKImage image)
    {
        using var current = new ContextScope(_contextHandle);
        if (!current.IsCurrent) AbandonRemovedContext();
        image.Dispose();
    }

    private void AbandonRemovedContext()
    {
        // Emscripten can no longer select a context after its owner destroys it.
        // Abandoning that dead backend releases Skia references without issuing GL
        // deletions against a replacement context's unrelated texture identifiers.
        if (_context.Handle != IntPtr.Zero && !_context.IsAbandoned) _context.AbandonContext();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _completionTimer.Stop();
        Renderer.Dispose();
        using var current = new ContextScope(_contextHandle);
        if (current.IsCurrent)
        {
            while (_fences.TryDequeue(out var fence)) Native.DeleteFence(fence.Handle);
            _engine.Dispose();
            if (_context.Handle != IntPtr.Zero && !_context.IsAbandoned) _context.ResetContext();
        }
        else AbandonRemovedContext();
        _gl.Dispose();
        // Uno owns the GRContext. Its lifetime is independent of this map control.
    }

    private readonly struct ContextScope : IDisposable
    {
        private readonly int _previous;
        internal readonly bool IsCurrent;

        internal ContextScope(int handle)
        {
            _previous = Native.CurrentContext();
            IsCurrent = _previous == handle || Native.MakeCurrent(handle) != 0;
        }

        public void Dispose()
        {
            if (Native.CurrentContext() != _previous) Native.MakeCurrent(_previous);
        }
    }

    private static class Native
    {
        private const string Library = "pixel_map_gl_readback";

        [DllImport(Library, EntryPoint = "pixel_map_gl_resolve", CharSet = CharSet.Ansi)]
        internal static extern IntPtr Resolve(string name);

        [DllImport(Library, EntryPoint = "pixel_map_gl_current_context")]
        internal static extern int CurrentContext();

        [DllImport(Library, EntryPoint = "pixel_map_gl_make_current")]
        internal static extern int MakeCurrent(int handle);

        [DllImport(Library, EntryPoint = "pixel_map_gl_context_live")]
        internal static extern int IsLive(int handle);

        [DllImport(Library, EntryPoint = "pixel_map_gl_fence_create")]
        internal static extern IntPtr CreateFence();

        [DllImport(Library, EntryPoint = "pixel_map_gl_fence_poll")]
        internal static extern int PollFence(IntPtr fence);

        [DllImport(Library, EntryPoint = "pixel_map_gl_fence_delete")]
        internal static extern void DeleteFence(IntPtr fence);

        [DllImport(Library, EntryPoint = "pixel_map_gl_flush")]
        internal static extern void Flush();
    }
}
