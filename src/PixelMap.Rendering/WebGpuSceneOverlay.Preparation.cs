using PixelMap.Routing;

namespace PixelMap.Rendering;

public sealed partial class PixelMapRenderer
{
    /// <summary>Prepare native GPU triangles for the displayed frame's labels,
    /// navigation and simulation. Static labels retain their atlas assets and geometry;
    /// camera-independent route data and bounded simulation trails are reused. Supply
    /// the physical-to-logical viewport ratio to rasterize smooth labels at display density.</summary>
    public void PrepareWebGpuOverlay(GpuMapFrame frame, WebGpuSceneOverlay overlay,
        RoutePlan? route = null, VehiclePose? vehicle = null, TravelMode mode = TravelMode.Drive,
        RoutePosition? gate = null, float labelRasterScale = 1)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(overlay);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ObjectDisposedException.ThrowIf(frame.IsDisposed, frame);
            ActivateGpuFrameCore(frame);
            if (overlay.Begin(frame, labelRasterScale))
            {
                // A host can display retained frames in any order, independently of
                // the last frame prepared or activated. Rebuild this small visible list
                // from the actual displayed frame; tile geometry remains cached.
                PrepareVisible(frame.Width, frame.Height, frame.PixelSize, frame.Camera, frame.Tiles);
                PrepareLabels(null, frame.Width, frame.Height, frame.Camera.Zoom, frame.PixelSize, frame.Palette, overlay);
            }
            overlay.Complete(route, vehicle, mode, gate);
        }
    }
}
