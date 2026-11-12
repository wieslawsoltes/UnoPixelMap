using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace PixelMap.Controls;

public sealed partial class PixelMapControl
{
    /// <summary>Separates logical input/layout coordinates from native map pixels.
    /// Uno 7 records each visual in its local coordinate system, then applies the
    /// display transform during replay. Giving the canvas a density-sized local
    /// viewport requests a native texture; the inverse transform preserves DIPs.</summary>
    private sealed class MapSurfaceViewport : Canvas
    {
        // Bound Uno's presentation texture separately from the renderer's HDR,
        // MSAA and shadow budgets. The texture uses four bytes per pixel.
        private const double MaximumPixels = 8_388_608;
        private const double MaximumDimension = 8192;
        private readonly MapSurface _surface;
        private readonly ScaleTransform _displayScale = new();
        private readonly RectangleGeometry _viewportClip = new();
        private XamlRoot? _root;

        internal MapSurfaceViewport(MapSurface surface)
        {
            _surface = surface;
            surface.RenderTransform = _displayScale;
            Children.Add(surface);
            Clip = _viewportClip;
            // Canvas measures to zero, so the enlarged child cannot increase the
            // containing Grid's desired size or create horizontal overflow.
            SizeChanged += (_, _) => UpdateViewport();
            Loaded += (_, _) =>
            {
                DetachRoot();
                _root = XamlRoot;
                if (_root is not null) _root.Changed += OnRootChanged;
                UpdateViewport();
            };
            Unloaded += (_, _) => DetachRoot();
        }

        private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateViewport();

        private void DetachRoot()
        {
            if (_root is not null) _root.Changed -= OnRootChanged;
            _root = null;
        }

        private void UpdateViewport()
        {
            double width = ActualWidth, height = ActualHeight;
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            {
                _surface.Width = _surface.Height = 0;
                _viewportClip.Rect = new Rect(0, 0, 0, 0);
                return;
            }
            double density = (_root ?? XamlRoot)?.RasterizationScale ?? 1;
            if (!double.IsFinite(density) || density <= 0) density = 1;
            double requestedWidth = Math.Ceiling(width * density);
            double requestedHeight = Math.Ceiling(height * density);
            double scale = Math.Min(1, Math.Min(MaximumDimension / requestedWidth, MaximumDimension / requestedHeight));
            scale = Math.Min(scale, Math.Sqrt(MaximumPixels / requestedWidth / requestedHeight));
            double pixelsX = Math.Max(1, Math.Floor(requestedWidth * scale));
            double pixelsY = Math.Max(1, Math.Floor(requestedHeight * scale));

            _surface.SetViewport(width, height);
            _surface.Width = pixelsX;
            _surface.Height = pixelsY;
            _displayScale.ScaleX = width / pixelsX;
            _displayScale.ScaleY = height / pixelsY;
            _viewportClip.Rect = new Rect(0, 0, width, height);
        }
    }
}
