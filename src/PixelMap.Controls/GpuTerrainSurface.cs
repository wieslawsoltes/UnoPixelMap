using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using Silk.NET.OpenGL;
using SkiaSharp;
using Uno.WinUI.Graphics3DGL;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace PixelMap.Controls;

public sealed partial class PixelMapControl
{
    private sealed partial class MapSurface
    {
        private static readonly SKSamplingOptions GpuArtworkSampling = new(SKFilterMode.Nearest, SKMipmapMode.None);
        private GpuTerrainCanvas? _terrainGpu;
        private GpuPresentation? _gpuReady, _gpuDisplayed;
        private bool _terrainGpuRequested;
        private bool _terrainGpuPresented;
        private bool _terrainGpuFailed;
        private bool _gpuInitializationQueued;

        private PixelMapRenderer? PresentedRenderer
        {
            get
            {
                if (!_terrainGpuPresented) return _renderer;
                if (_terrainGpu is not { IsGLInitialized: true, Renderer: { } renderer } || _gpuDisplayed is not { } displayed) return null;
                renderer.ActivateGpuFrame(displayed.Frame);
                return renderer;
            }
        }

        private static bool UsesGpuTerrain(Scene scene) => scene.UseGpuRendering &&
            (scene.RenderStyle != MapRenderStyle.Pixel || scene.Camera.IsFreeLook ||
             scene.Terrain is { Elevation.Count: > 0 } terrain && (terrain.Relief || terrain.Hypsometric));

        private void PresentGpuTerrain(Scene scene)
        {
            if (!UsesGpuTerrain(scene))
            {
                ReleaseGpuTerrain();
                Invalidate();
                return;
            }
            if (!_terrainGpuRequested) _terrainGpuFailed = false;
            _terrainGpuRequested = true;
            bool redraw = _terrainGpuFailed || _gpuReady is null;
            if (!_terrainGpuFailed)
            {
                var (width, height) = TerrainViewport;
                int pixelSize = ArtworkPixelSize(scene);
                var reusable = _gpuReady?.Request.Matches(scene, width, height, pixelSize) == true ? _gpuReady
                    : _gpuDisplayed?.Request.Matches(scene, width, height, pixelSize) == true ? _gpuDisplayed : null;
                if (reusable is not null)
                {
                    var previousScene = reusable.Request.Scene;
                    // Simulation time can advance while the map and overlays stay
                    // stationary. Repainting identical artwork on every tick needlessly
                    // submits the entire map to the window compositor.
                    bool samePresentation = ReferenceEquals(_gpuReady, reusable) && ReferenceEquals(_gpuDisplayed, reusable);
                    bool sameOverlays = ReferenceEquals(previousScene.Route, scene.Route) &&
                        previousScene.VehicleMode == scene.VehicleMode && previousScene.Gate == scene.Gate;
                    bool sameVehicle = previousScene.Vehicle == scene.Vehicle;
                    if (!sameVehicle && samePresentation && sameOverlays && previousScene.Vehicle is not null &&
                        scene.Route is null && scene.Gate is null &&
                        scene.Vehicle is { } vehicle && _terrainGpu is { IsGLInitialized: true, Renderer: { } renderer })
                    {
                        // A stopped vehicle has a static picture, but its effect clock
                        // still needs each idle sample before movement resumes.
                        sameVehicle = renderer.TryAdvanceStationaryVehicle(scene.VehicleMode, vehicle);
                    }
                    redraw = !samePresentation || !sameOverlays || !sameVehicle;
                    reusable.Request.Scene = scene;
                    SetReadyGpuPresentation(reusable);
                    _terrainGpu?.CancelRequest();
                }
                else if (_terrainGpu is null)
                {
                    // The first Skia paint establishes the host's graphics context and
                    // final layout. It queues GL initialization after that paint returns.
                    redraw = true;
                }
                else _terrainGpu.RequestFrame(scene, width, height, pixelSize);
            }
            // Camera input coalesces while the current image and overlays are displayed.
            // Publish invalidates the foreground once matching artwork is available.
            if (redraw) Invalidate();
        }

        private void QueueGpuInitialization()
        {
            if (_gpuInitializationQueued || _terrainGpu is not null || !_terrainGpuRequested || _terrainGpuFailed) return;
            _gpuInitializationQueued = true;
            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                lock (_drawing)
                {
                    _gpuInitializationQueued = false;
                    if (_scene is not { } scene || !UsesGpuTerrain(scene) || _terrainGpu is not null || _terrainGpuFailed ||
                        !layers.IsLoaded || layers.ActualWidth <= 0 || layers.ActualHeight <= 0) return;
                    var (width, height) = TerrainViewport;
                    _terrainGpu = new GpuTerrainCanvas(this);
                    _terrainGpu.RequestFrame(scene, width, height, ArtworkPixelSize(scene));
                    _terrainGpu.Measure(new Size(_terrainGpu.Width, _terrainGpu.Height));
                    _terrainGpu.Arrange(new Rect(0, 0, _terrainGpu.Width, _terrainGpu.Height));
                    layers.Children.Insert(0, _terrainGpu);
                }
            })) _gpuInitializationQueued = false;
        }

        private void ReleaseGpuTerrain()
        {
            _terrainGpuRequested = false;
            _terrainGpuPresented = false;
            var ready = _gpuReady;
            var displayed = _gpuDisplayed;
            _gpuReady = _gpuDisplayed = null;
            ready?.Dispose();
            if (!ReferenceEquals(ready, displayed)) displayed?.Dispose();
            if (_terrainGpu is { } surface)
            {
                _terrainGpu = null;
                surface.Retire();
                // OnDestroy releases GL resources while Uno has the context current.
                layers.Children.Remove(surface);
            }
        }

        private void GpuTerrainUnavailable(GpuTerrainCanvas surface)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                lock (_drawing)
                {
                    if (!ReferenceEquals(_terrainGpu, surface)) return;
                    ReleaseGpuTerrain();
                    _terrainGpuRequested = true;
                    _terrainGpuFailed = true;
                    Console.WriteLine("Pixel Atlas GPU terrain fallback. The GL surface could not initialize or present its frame.");
                    ReportBackend(false);
                    Invalidate();
                }
            });
        }

        private void PublishGpuTerrain(GpuTerrainCanvas surface, GpuMapRequest request,
            GpuMapFrame frame, long version, long started)
        {
            // Uno completes its synchronous framebuffer readback after RenderOverride.
            // Publish the immutable artwork and its matching scene together afterward.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () =>
            {
                lock (_drawing)
                {
                    if (!ReferenceEquals(_terrainGpu, surface) || !_terrainGpuRequested ||
                        !surface.CanPublish(request, frame, version)) return;
                    try
                    {
                        var image = surface.ReadArtwork(request);
                        surface.CompletePublication(version);
                        PublishGpuArtwork(surface, request, frame, image,
                            Stopwatch.GetElapsedTime(started).TotalMilliseconds, bottomUp: false);
                    }
                    catch (Exception error)
                    {
                        Console.WriteLine($"Pixel Atlas GPU terrain readback failed. {error}");
                        GpuTerrainUnavailable(surface);
                    }
                }
            });
        }

        private void PublishGpuArtwork(GpuTerrainCanvas surface, GpuMapRequest request,
            GpuMapFrame frame, SKImage image, double mapMilliseconds, bool bottomUp)
        {
            if (!ReferenceEquals(_terrainGpu, surface) || !_terrainGpuRequested || surface.IsGLInitialized != true)
            {
                image.Dispose();
                frame.Dispose();
                return;
            }
            SetReadyGpuPresentation(new GpuPresentation(request, frame, image, mapMilliseconds, bottomUp));
            Invalidate();
        }

        private void SetReadyGpuPresentation(GpuPresentation presentation)
        {
            var previous = _gpuReady;
            _gpuReady = presentation;
            if (!ReferenceEquals(previous, presentation) && !ReferenceEquals(previous, _gpuDisplayed)) previous?.Dispose();
        }

        private bool DrawGpuPresentation(SKCanvas canvas, Size area)
        {
            if (_gpuReady is not { } presentation ||
                _terrainGpu is not { IsGLInitialized: true, Renderer: { } renderer }) return false;
            long started = Stopwatch.GetTimestamp();
            var frame = presentation.Frame;
            var scene = presentation.Request.Scene;
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)area.Width, (float)area.Height));
            if ((frame.Width < area.Width || frame.Height < area.Height) && layers.Background is SolidColorBrush backdrop)
            {
                var color = backdrop.Color;
                canvas.DrawColor(new SKColor(color.R, color.G, color.B, color.A));
            }
            canvas.ClipRect(new SKRect(0, 0, frame.Width, frame.Height));
            canvas.Save();
            if (presentation.BottomUp)
            {
                canvas.Translate(0, presentation.Image.Height * frame.PixelSize);
                canvas.Scale(1, -1);
            }
            canvas.DrawImage(presentation.Image,
                new SKRect(0, 0, presentation.Image.Width * frame.PixelSize, presentation.Image.Height * frame.PixelSize),
                GpuArtworkSampling);
            canvas.Restore();
            renderer.DrawGpuOverlay(canvas, frame);
            if (scene.Gate is { } gate && scene.Route is { } trip && scene.Vehicle is { } driver)
                renderer.DrawGate(canvas, scene.Camera, frame.Width, frame.Height, gate, trip.Mode,
                    trip.Mode == TravelMode.Flight ? driver.AltitudeMeters : 0, driver.AnimationSeconds);
            if (scene.Route is { } route)
                renderer.DrawNavigation(canvas, frame.Width, frame.Height, scene.Camera, route, scene.Vehicle, frame.PixelSize);
            else if (scene.Vehicle is { } vehicle)
                renderer.DrawVehicle(canvas, frame.Width, frame.Height, scene.Camera, scene.VehicleMode, vehicle, frame.PixelSize);
            canvas.Restore();

            var previous = _gpuDisplayed;
            _gpuDisplayed = presentation;
            _terrainGpuPresented = true;
            if (!ReferenceEquals(previous, presentation)) previous?.Dispose();
            _renderer?.Dispose();
            _renderer = null;
            ReportBackend(true);
            TrackFrameCost(scene, presentation.MapMilliseconds + Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (!ReferenceEquals(_presentedScene, scene))
            {
                _presentedScene = scene;
                framePresented();
            }
            return true;
        }

        private sealed class GpuMapRequest(Scene scene, int width, int height, int pixelSize)
        {
            internal Scene Scene = scene;
            internal readonly int Width = width, Height = height, PixelSize = pixelSize;
            internal readonly int ArtWidth = (width + pixelSize - 1) / pixelSize;
            internal readonly int ArtHeight = (height + pixelSize - 1) / pixelSize;
            internal readonly MapTile[] Tiles = scene.Tiles.ToArray();
            internal readonly ImageryTile[]? Imagery = scene.Imagery?.ToArray();

            internal bool Matches(Scene candidate, int candidateWidth, int candidateHeight, int candidatePixelSize) =>
                Width == candidateWidth && Height == candidateHeight && PixelSize == candidatePixelSize &&
                Scene.Camera == candidate.Camera && Scene.Palette == candidate.Palette && Scene.Terrain == candidate.Terrain &&
                Scene.RenderStyle == candidate.RenderStyle &&
                SameSources(Tiles, candidate.Tiles) && SameSources(Imagery, candidate.Imagery);

            private static bool SameSources<T>(IReadOnlyList<T>? first, IReadOnlyList<T>? second) where T : class
            {
                int count = first?.Count ?? 0;
                if (count != (second?.Count ?? 0)) return false;
                for (int i = 0; i < count; i++)
                    if (!ReferenceEquals(first![i], second![i])) return false;
                return true;
            }
        }

        private sealed class GpuPresentation(GpuMapRequest request, GpuMapFrame frame, SKImage image,
            double mapMilliseconds, bool bottomUp) : IDisposable
        {
            internal readonly GpuMapRequest Request = request;
            internal readonly GpuMapFrame Frame = frame;
            internal readonly SKImage Image = image;
            internal readonly double MapMilliseconds = mapMilliseconds;
            internal readonly bool BottomUp = bottomUp;

            public void Dispose()
            {
                Image.Dispose();
                Frame.Dispose();
            }
        }

        private sealed class GpuTerrainCanvas : GLCanvasElement
        {
            private readonly MapSurface _owner;
            private readonly DispatcherTimer _readbackTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
            private readonly Queue<GpuSubmission> _submissions = new(2);
            private GlMapRenderer? _engine;
            private BrowserGpuReadback? _browserReadback;
            private GpuMapRequest? _request;
            private GpuMapRequest? _pendingRequest;
            private GpuMapFrame? _pendingFrame;
            private byte[] _readback = [];
            private long _requestVersion, _renderedVersion = -1;
            private bool _retired;

            internal GpuTerrainCanvas(MapSurface owner) : base(null)
            {
                _owner = owner;
                IsHitTestVisible = false;
                Opacity = 0;
                HorizontalAlignment = HorizontalAlignment.Left;
                VerticalAlignment = VerticalAlignment.Top;
                if (Background is ImageBrush readbackBrush) readbackBrush.Opacity = 0;
                _readbackTimer.Tick += (_, _) => PollReadback();
                RegisterPropertyChangedCallback(IsGLInitializedProperty, (_, _) =>
                {
                    if (IsGLInitialized == false)
                    {
                        Console.WriteLine($"Pixel Atlas GPU surface unavailable at {RenderSize.Width}×{RenderSize.Height}, loaded={IsLoaded}, renderer={Renderer is not null}.");
                        _owner.GpuTerrainUnavailable(this);
                    }
                });
            }

            internal PixelMapRenderer? Renderer { get; private set; }

            internal void RequestFrame(Scene scene, int width, int height, int pixelSize)
            {
                if (_request?.Matches(scene, width, height, pixelSize) == true)
                {
                    _request.Scene = scene;
                    return;
                }
                var request = new GpuMapRequest(scene, width, height, pixelSize);
                if (_browserReadback is null && _pendingRequest is { } pending &&
                    (pending.ArtWidth != request.ArtWidth || pending.ArtHeight != request.ArtHeight))
                {
                    _pendingFrame?.Dispose();
                    _pendingFrame = null;
                    _pendingRequest = null;
                }
                _request = request;
                _requestVersion++;
                Width = _request.ArtWidth;
                Height = _request.ArtHeight;
                ScheduleGeometry();
            }

            internal void CancelRequest()
            {
                _requestVersion++;
                _request = null;
                _pendingFrame?.Dispose();
                _pendingFrame = null;
                _pendingRequest = null;
                foreach (var submission in _submissions) submission.Dispose();
                Opacity = 0;
            }

            private void ScheduleGeometry()
            {
                if (_retired || _request is null || _renderedVersion == _requestVersion) return;
                if (_browserReadback is not null && _submissions.Count >= 2)
                {
                    Opacity = 0;
                    return;
                }
                // A visible GL visual already has a paint scheduled. That callback
                // reads the latest coalesced request, including viewport changes.
                if (Opacity == 1) return;
                Opacity = 1;
                Invalidate();
            }

            private void PollReadback()
            {
                lock (_owner._drawing)
                {
                    if (_retired || _browserReadback is not { } readback || IsGLInitialized != true)
                    {
                        _readbackTimer.Stop();
                        return;
                    }
                    try
                    {
                        if (readback.TryGetReadyForCopy(out var ready))
                        {
                            GpuSubmission? submission = null;
                            foreach (var candidate in _submissions)
                                if (candidate.Version == ready.Version) submission = candidate;
                            bool retired;
                            if (submission is not { Request: { } request, Frame: { } frame })
                            {
                                retired = readback.TryDiscardReady(ready);
                            }
                            else
                            {
                                if (ready.Width != request.ArtWidth || ready.Height != request.ArtHeight)
                                    throw new InvalidOperationException("The selected GPU transfer does not match its retained viewport.");
                                var image = readback.ReadCompleted(ready);
                                submission.Frame = null;
                                if (_requestVersion == ready.Version) _request = null;
                                _owner.PublishGpuArtwork(this, request, frame, image,
                                    Stopwatch.GetElapsedTime(submission.Started).TotalMilliseconds, bottomUp: true);
                                retired = true;
                            }
                            // Keep managed occupancy aligned with the native slots. A
                            // canceled unsignaled transfer still owns its queue entry.
                            if (retired)
                                while (_submissions.TryPeek(out var older) && older.Version <= ready.Version)
                                    _submissions.Dequeue().Dispose();
                        }
                        if (_submissions.Count == 0) _readbackTimer.Stop();
                        ScheduleGeometry();
                    }
                    catch (Exception error)
                    {
                        _readbackTimer.Stop();
                        Opacity = 0;
                        Console.WriteLine($"Pixel Atlas asynchronous GPU terrain readback failed. {error}");
                        _owner.GpuTerrainUnavailable(this);
                    }
                }
            }

            internal bool CanPublish(GpuMapRequest request, GpuMapFrame frame, long version) =>
                !_retired && IsGLInitialized == true && _renderedVersion == version &&
                ReferenceEquals(_pendingRequest, request) && ReferenceEquals(_pendingFrame, frame);

            internal SKImage ReadArtwork(GpuMapRequest request)
            {
                if (Background is not ImageBrush { ImageSource: WriteableBitmap bitmap } ||
                    bitmap.PixelWidth != request.ArtWidth || bitmap.PixelHeight != request.ArtHeight)
                    throw new InvalidOperationException("The GPU artwork does not match its retained viewport.");
                int rowBytes = checked(request.ArtWidth * 4);
                int byteCount = checked(rowBytes * request.ArtHeight);
                if (bitmap.PixelBuffer.Length < byteCount)
                    throw new InvalidOperationException("The GPU artwork readback is incomplete.");
                if (_readback.Length != byteCount) _readback = new byte[byteCount];
                using (var reader = DataReader.FromBuffer(bitmap.PixelBuffer)) reader.ReadBytes(_readback);
                using var artwork = new SKBitmap(request.ArtWidth, request.ArtHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
                var destination = artwork.GetPixelSpan();
                for (int y = 0; y < request.ArtHeight; y++)
                    _readback.AsSpan((request.ArtHeight - 1 - y) * rowBytes, rowBytes)
                        .CopyTo(destination.Slice(y * artwork.RowBytes, rowBytes));
                artwork.SetImmutable();
                return SKImage.FromBitmap(artwork);
            }

            internal void CompletePublication(long version)
            {
                // The presentation now owns the frame. Hiding the GL visual prevents its
                // paint hook from scheduling another readback during foreground-only updates.
                _pendingFrame = null;
                _pendingRequest = null;
                if (_requestVersion == version)
                {
                    _request = null;
                    Opacity = 0;
                }
                else Invalidate();
            }

            protected override void Init(GL gl)
            {
                try
                {
                    _engine = new GlMapRenderer(gl);
                    Renderer = new PixelMapRenderer();
                    _browserReadback = BrowserGpuReadback.TryCreate();
                    // Uno retains its private backbuffer for the readback callback. The
                    // asynchronous host does not observe or present that bitmap.
                    if (_browserReadback is not null && Background is ImageBrush brush) brush.ImageSource = null;
                }
                catch (Exception error)
                {
                    Console.WriteLine($"Pixel Atlas GPU terrain initialization failed. {error}");
                    throw;
                }
            }

            protected override void RenderOverride(GL gl)
            {
                try
                {
                    lock (_owner._drawing)
                    {
                        // Uno reads its backbuffer even when no geometry is pending. A
                        // scoped selection also suppresses those already queued paints.
                        _browserReadback?.SelectSurface();
                        // Framebuffer resizing attaches a new bitmap to the brush.
                        if (_browserReadback is not null && Background is ImageBrush { ImageSource: not null } brush)
                            brush.ImageSource = null;
                        if (_retired || _request is not { } request || _renderedVersion == _requestVersion ||
                            Renderer is null || _engine is null)
                        {
                            if (_browserReadback is not null) Opacity = 0;
                            return;
                        }
                        if ((int)RenderSize.Width != request.ArtWidth || (int)RenderSize.Height != request.ArtHeight) return;
                        if (_browserReadback is { CanSubmit: false })
                        {
                            Opacity = 0;
                            return;
                        }
                        long started = Stopwatch.GetTimestamp();
                        long version = _requestVersion;
                        var frame = Renderer.PrepareGpuFrame(request.Width, request.Height, request.Scene.Camera, request.Tiles,
                            request.Scene.Palette, request.PixelSize, request.Imagery, request.Scene.Terrain, request.Scene.RenderStyle);
                        try
                        {
                            _engine.DrawArtwork(frame);
                            if (_browserReadback is { } readback)
                            {
                                if (!readback.TryBeginFrame(version, request.ArtWidth, request.ArtHeight))
                                    throw new InvalidOperationException("The browser GPU transfer slot could not be reserved.");
                                _submissions.Enqueue(new GpuSubmission(request, frame, version, started));
                                _renderedVersion = version;
                                // The following Uno readback submits a PBO transfer. Poll
                                // its fence independently without another GLCanvas paint.
                                Opacity = 0;
                                if (!_readbackTimer.IsEnabled) _readbackTimer.Start();
                                return;
                            }
                        }
                        catch
                        {
                            frame.Dispose();
                            throw;
                        }
                        _pendingFrame?.Dispose();
                        _pendingFrame = frame;
                        _pendingRequest = request;
                        _renderedVersion = version;
                        _owner.PublishGpuTerrain(this, request, frame, version, started);
                    }
                }
                catch (Exception error)
                {
                    _browserReadback?.CancelFrame();
                    Console.WriteLine($"Pixel Atlas GPU terrain drawing failed. {error}");
                    throw;
                }
            }

            internal void Retire()
            {
                _retired = true;
                ReleaseManagedResources();
            }

            protected override void OnDestroy(GL gl)
            {
                lock (_owner._drawing)
                {
                    try { _engine?.Dispose(); }
                    finally
                    {
                        _engine = null;
                        ReleaseManagedResources();
                    }
                }
            }

            private void ReleaseManagedResources()
            {
                _readbackTimer.Stop();
                _browserReadback?.Dispose();
                _browserReadback = null;
                while (_submissions.TryDequeue(out var submission)) submission.Dispose();
                _requestVersion++;
                _request = null;
                _pendingFrame?.Dispose();
                _pendingFrame = null;
                _pendingRequest = null;
                Renderer?.Dispose();
                Renderer = null;
                _readback = [];
            }

            private sealed class GpuSubmission(GpuMapRequest request, GpuMapFrame frame, long version, long started) : IDisposable
            {
                internal GpuMapRequest? Request = request;
                internal GpuMapFrame? Frame = frame;
                internal readonly long Version = version, Started = started;

                public void Dispose()
                {
                    Frame?.Dispose();
                    Frame = null;
                    Request = null;
                }
            }
        }

        private (int Width, int Height) TerrainViewport =>
            (Math.Max(1, (int)Math.Ceiling(layers.ActualWidth)), Math.Max(1, (int)Math.Ceiling(layers.ActualHeight)));
    }
}
