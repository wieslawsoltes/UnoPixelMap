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
            scene.Terrain is { Elevation.Count: > 0 } terrain && (terrain.Relief || terrain.Hypsometric);

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
            if (!_terrainGpuFailed)
            {
                var (width, height) = TerrainViewport;
                int pixelSize = scene.Moving ? _interactivePixelSize : 2;
                var reusable = _gpuReady?.Request.Matches(scene, width, height, pixelSize) == true ? _gpuReady
                    : _gpuDisplayed?.Request.Matches(scene, width, height, pixelSize) == true ? _gpuDisplayed : null;
                if (reusable is not null)
                {
                    reusable.Request.Scene = scene;
                    SetReadyGpuPresentation(reusable);
                    _terrainGpu?.CancelRequest();
                }
                else if (_terrainGpu is null)
                {
                    _terrainGpu = new GpuTerrainCanvas(this);
                    _terrainGpu.RequestFrame(scene, width, height, pixelSize);
                    // GLCanvasElement initializes its framebuffer during Loaded. Establish
                    // positive art dimensions before insertion into an already loaded grid.
                    _terrainGpu.Measure(new Size(_terrainGpu.Width, _terrainGpu.Height));
                    _terrainGpu.Arrange(new Rect(0, 0, _terrainGpu.Width, _terrainGpu.Height));
                    layers.Children.Insert(0, _terrainGpu);
                }
                else _terrainGpu.RequestFrame(scene, width, height, pixelSize);
            }
            Invalidate();
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
                        var presentation = new GpuPresentation(request, frame, image, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        surface.CompletePublication(version);
                        SetReadyGpuPresentation(presentation);
                        Invalidate();
                    }
                    catch (Exception error)
                    {
                        Console.WriteLine($"Pixel Atlas GPU terrain readback failed. {error}");
                        GpuTerrainUnavailable(surface);
                    }
                }
            });
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
            if (layers.Background is SolidColorBrush backdrop)
            {
                var color = backdrop.Color;
                canvas.DrawColor(new SKColor(color.R, color.G, color.B, color.A));
            }
            canvas.ClipRect(new SKRect(0, 0, frame.Width, frame.Height));
            canvas.DrawImage(presentation.Image,
                new SKRect(0, 0, presentation.Image.Width * frame.PixelSize, presentation.Image.Height * frame.PixelSize),
                GpuArtworkSampling);
            renderer.DrawGpuOverlay(canvas, frame);
            if (scene.Gate is { } gate && scene.Route is { } trip && scene.Vehicle is { } driver)
                renderer.DrawGate(canvas, scene.Camera, frame.Width, frame.Height, gate, trip.Mode,
                    trip.Mode == TravelMode.Flight ? driver.AltitudeMeters : 0, driver.AnimationSeconds);
            if (scene.Route is { } route)
                renderer.DrawNavigation(canvas, frame.Width, frame.Height, scene.Camera, route, scene.Vehicle, frame.PixelSize);
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

        private sealed class GpuPresentation(GpuMapRequest request, GpuMapFrame frame, SKImage image, double mapMilliseconds) : IDisposable
        {
            internal readonly GpuMapRequest Request = request;
            internal readonly GpuMapFrame Frame = frame;
            internal readonly SKImage Image = image;
            internal readonly double MapMilliseconds = mapMilliseconds;

            public void Dispose()
            {
                Image.Dispose();
                Frame.Dispose();
            }
        }

        private sealed class GpuTerrainCanvas : GLCanvasElement
        {
            private readonly MapSurface _owner;
            private GlMapRenderer? _engine;
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
                HorizontalAlignment = HorizontalAlignment.Left;
                VerticalAlignment = VerticalAlignment.Top;
                RegisterPropertyChangedCallback(IsGLInitializedProperty, (_, _) =>
                {
                    if (IsGLInitialized == false) _owner.GpuTerrainUnavailable(this);
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
                if (_pendingRequest is { } pending &&
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
                Opacity = 1;
                Invalidate();
            }

            internal void CancelRequest()
            {
                _requestVersion++;
                _request = null;
                _pendingFrame?.Dispose();
                _pendingFrame = null;
                _pendingRequest = null;
                Opacity = 0;
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
                        if (_retired || _request is not { } request || _renderedVersion == _requestVersion ||
                            Renderer is null || _engine is null) return;
                        if ((int)RenderSize.Width != request.ArtWidth || (int)RenderSize.Height != request.ArtHeight) return;
                        long started = Stopwatch.GetTimestamp();
                        long version = _requestVersion;
                        var frame = Renderer.PrepareGpuFrame(request.Width, request.Height, request.Scene.Camera, request.Tiles,
                            request.Scene.Palette, request.PixelSize, request.Imagery, request.Scene.Terrain);
                        try { _engine.DrawArtwork(frame); }
                        catch { frame.Dispose(); throw; }
                        _pendingFrame?.Dispose();
                        _pendingFrame = frame;
                        _pendingRequest = request;
                        _renderedVersion = version;
                        _owner.PublishGpuTerrain(this, request, frame, version, started);
                    }
                }
                catch (Exception error)
                {
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
                    _engine?.Dispose();
                    _engine = null;
                    ReleaseManagedResources();
                }
            }

            private void ReleaseManagedResources()
            {
                _requestVersion++;
                _request = null;
                _pendingFrame?.Dispose();
                _pendingFrame = null;
                _pendingRequest = null;
                Renderer?.Dispose();
                Renderer = null;
                _readback = [];
            }
        }

        private (int Width, int Height) TerrainViewport =>
            (Math.Max(1, (int)Math.Ceiling(layers.ActualWidth)), Math.Max(1, (int)Math.Ceiling(layers.ActualHeight)));
    }
}
