using System.Runtime.InteropServices;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>A pixel-resolution GPU frame, either immediate or deferred through a host picture recorder.</summary>
internal sealed class GpuFrameSurface : IDisposable
{
    private const int DeferredSizeLimit = 4096;
    private const string ResolveProgram = """
        uniform shader frame;
        uniform float pixelSize;

        half4 main(float2 coordinate)
        {
            // Stabilize interpolated coordinates exactly on a pixel boundary at fractional display scales.
            float2 artPixel = floor(coordinate / pixelSize + float2(0.0001)) + float2(0.5);
            return frame.eval(artPixel);
        }
        """;

    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);
    private GRRecordingContext? context;
    private GRRecordingContext? rejectedContext;
    private SKSurface? surface;
    private SKPictureRecorder? recorder;
    private SKRuntimeEffect? effect;
    private SKRuntimeEffectUniforms? uniforms;
    private SKRuntimeEffectChildren? children;
    private SKVertices? mesh;
    private SKPaint? paint;
    private int width, height, meshPixelSize;
    private int meshWidth, meshHeight;
    private int rejectedWidth, rejectedHeight;
    private bool configured, deferred, recording, rejected, rejectedDeferred, disposed;

    /// <summary>Changes whenever a retained picture could refer to an obsolete GPU surface.</summary>
    internal long Generation { get; private set; }

    internal string? FailureReason { get; private set; }

    /// <summary>Prepares resources without changing a cached frame. Deferred rendering must be
    /// explicitly enabled by a host that will replay its recording on a GPU-backed canvas.</summary>
    internal bool TryPrepare(SKCanvas destination, int artWidth, int artHeight, bool allowDeferred)
    {
        if (disposed || artWidth <= 0 || artHeight <= 0) return false;
        try
        {
            var nextContext = destination.Context;
            bool nextDeferred = nextContext is null && allowDeferred;
            if (!nextDeferred && (nextContext is null || nextContext.Handle == IntPtr.Zero || nextContext.IsAbandoned))
            {
                ReleaseFrame();
                FailureReason = "A GPU-backed destination canvas is unavailable.";
                return false;
            }
            if (rejected && ReferenceEquals(nextContext, rejectedContext) && nextDeferred == rejectedDeferred &&
                artWidth == rejectedWidth && artHeight == rejectedHeight)
                return false;
            if (configured && ReferenceEquals(nextContext, context) && deferred == nextDeferred &&
                width == artWidth && height == artHeight)
                return true;

            ReleaseFrame();
            configured = true;
            context = nextContext;
            deferred = nextDeferred;
            width = artWidth;
            height = artHeight;
            int limit = deferred ? DeferredSizeLimit : context!.MaxRenderTargetSize;
            if (width > limit || height > limit)
                return Fail("The artwork exceeds the GPU render-target size limit.");

            if (effect is null)
            {
                effect = SKRuntimeEffect.CreateShader(ResolveProgram, out string errors);
                if (effect is null) return Fail(errors);
                uniforms = new(effect);
                children = new(effect);
                paint = new() { IsAntialias = false, Color = SKColors.White };
            }
            if (!deferred)
            {
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                surface = SKSurface.Create(context!, true, info, 0, GRSurfaceOrigin.TopLeft);
                if (surface is null) return Fail("The GPU render target could not be allocated.");
            }

            rejected = false;
            rejectedContext = null;
            FailureReason = null;
            Generation++;
            return true;
        }
        catch (Exception error) when (Recoverable(error))
        {
            return Fail(error.Message);
        }
    }

    /// <summary>Starts painting after the caller has ruled out replaying its cached frame.</summary>
    internal SKCanvas? BeginFrame()
    {
        if (disposed || !configured) return null;
        try
        {
            if (deferred)
            {
                recorder ??= new();
                if (recording)
                {
                    recorder.EndRecording()?.Dispose();
                    recording = false;
                }
                var canvas = recorder.BeginRecording(new SKRect(0, 0, width, height));
                if (canvas is null || canvas.Handle == IntPtr.Zero)
                {
                    Fail("The deferred GPU frame could not begin recording.");
                    return null;
                }
                recording = true;
                return canvas;
            }
            if (context!.Handle != IntPtr.Zero && !context.IsAbandoned) return surface!.Canvas;
            Fail("The GPU context was lost.");
        }
        catch (Exception error) when (Recoverable(error))
        {
            Fail(error.Message);
        }
        return null;
    }

    /// <summary>The caller owns the image and continues drawing on the returned canvas.
    /// Lazy picture images generate their textures on the eventual playback context, which also
    /// keeps the ground beneath translucent low-pitch building footprints on that GPU.</summary>
    internal SKImage? TrySnapshot(out SKCanvas? continuation) => Capture(true, out continuation);

    private SKImage? Capture(bool continueRecording, out SKCanvas? continuation)
    {
        continuation = null;
        if (disposed || !configured) return null;
        SKImage? image = null;
        try
        {
            if (deferred)
            {
                if (!recording) return null;
                using var picture = recorder!.EndRecording();
                recording = false;
                if (picture is null || picture.Handle == IntPtr.Zero)
                {
                    Fail("The deferred GPU frame could not finish recording.");
                    return null;
                }
                image = SKImage.FromPicture(picture, new SKSizeI(width, height));
                if (image is null)
                {
                    Fail("The deferred GPU frame could not be retained.");
                    return null;
                }
                if (continueRecording)
                {
                    var canvas = recorder.BeginRecording(new SKRect(0, 0, width, height));
                    if (canvas is null || canvas.Handle == IntPtr.Zero)
                    {
                        Fail("The deferred GPU frame could not continue recording.");
                        return null;
                    }
                    recording = true;
                    canvas.DrawImage(image, 0, 0, Nearest);
                    continuation = canvas;
                }
            }
            else
            {
                if (context!.Handle == IntPtr.Zero || context.IsAbandoned)
                {
                    Fail("The GPU context was lost.");
                    return null;
                }
                image = surface!.Snapshot();
                if (image is null)
                {
                    Fail("The GPU frame could not be retained.");
                    return null;
                }
                continuation = surface.Canvas;
            }
            var result = image;
            image = null;
            return result;
        }
        catch (Exception error) when (Recoverable(error))
        {
            continuation = null;
            Fail(error.Message);
            return null;
        }
        finally
        {
            image?.Dispose();
        }
    }

    /// <summary>Records a pixel-grid resolve into a canvas or picture recorder. Skia's
    /// recorded draw retains the mesh, shader and snapshot after these local wrappers are disposed.</summary>
    internal bool TryResolve(SKCanvas target, int logicalWidth, int logicalHeight, int pixelSize)
    {
        if (disposed || logicalWidth <= 0 || logicalHeight <= 0 || pixelSize <= 0) return false;
        using var image = Capture(false, out _);
        if (image is null) return false;
        try
        {
            if (mesh is null || meshPixelSize != pixelSize || meshWidth != logicalWidth || meshHeight != logicalHeight)
            {
                float right = logicalWidth, bottom = logicalHeight;
                SKPoint[] positions = [new(0, 0), new(right, 0), new(right, bottom), new(0, bottom)];
                var next = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, positions, null,
                    [0, 1, 2, 0, 2, 3]);
                if (next is null) return Fail("The GPU resolve mesh could not be allocated.");
                // Transfer ownership without creating a second native mesh or retaining
                // temporary vertex arrays. SKVertices is immutable after CreateCopy.
                mesh?.Dispose();
                mesh = next;
                meshPixelSize = pixelSize;
                meshWidth = logicalWidth;
                meshHeight = logicalHeight;
                uniforms!["pixelSize"] = (float)pixelSize;
            }

            using var imageShader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Nearest);
            if (imageShader is null) return Fail("The GPU frame could not be sampled.");
            children!["frame"] = imageShader;
            using var shader = effect!.ToShader(uniforms!, children);
            if (shader is null) return Fail("The GPU resolve shader could not be instantiated.");
            paint!.Shader = shader;
            int save = target.Save();
            try
            {
                target.ClipRect(new SKRect(0, 0, logicalWidth, logicalHeight));
                target.DrawVertices(mesh, SKBlendMode.Modulate, paint);
            }
            finally
            {
                target.RestoreToCount(save);
                paint.Shader = null;
                children.Reset();
            }
            return true;
        }
        catch (Exception error) when (Recoverable(error))
        {
            return Fail(error.Message);
        }
    }

    /// <summary>Releases context-bound resources and permits an explicit retry after a failure.</summary>
    internal void Reset()
    {
        ReleaseFrame();
        ReleaseResolve();
        rejected = false;
        rejectedContext = null;
        FailureReason = null;
    }

    private bool Fail(string reason)
    {
        rejectedContext = context;
        rejected = true;
        rejectedDeferred = deferred;
        rejectedWidth = width;
        rejectedHeight = height;
        FailureReason = reason;
        if (effect is null || uniforms is null || children is null || paint is null)
            ReleaseResolve();
        else
        {
            paint.Shader = null;
            children.Reset();
        }
        ReleaseFrame();
        return false;
    }

    private void ReleaseFrame()
    {
        if (configured) Generation++;
        // The host owns the borrowed recording context.
        surface?.Dispose(); surface = null;
        recorder?.Dispose(); recorder = null;
        mesh?.Dispose(); mesh = null;
        context = null;
        configured = deferred = recording = false;
        width = height = meshPixelSize = meshWidth = meshHeight = 0;
    }

    private void ReleaseResolve()
    {
        paint?.Dispose(); paint = null;
        children?.Dispose(); children = null;
        uniforms?.Dispose(); uniforms = null;
        effect?.Dispose(); effect = null;
    }

    private static bool Recoverable(Exception error) => error is InvalidOperationException or ArgumentException or
        NotSupportedException or OutOfMemoryException or TypeLoadException or ExternalException;

    public void Dispose()
    {
        if (disposed) return;
        Reset();
        disposed = true;
    }
}
