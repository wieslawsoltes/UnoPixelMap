using PixelMap.Routing;
using SkiaSharp;

namespace PixelMap.Rendering;

public sealed partial class PixelMapRenderer
{
    /// <summary>Georeferenced game gate. Its two flagpoles straddle the next station on the course.</summary>
    public void DrawGate(SKCanvas canvas, MapCamera camera, double width, double height,
        RoutePosition gate, TravelMode mode, double altitude, double clock)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (width <= 0 || height <= 0) return;
        double halfWidth = mode switch { TravelMode.Flight => 46, TravelMode.Boat => 9, TravelMode.Train => 3, _ => 4 };
        var left = gate.Point.Move(gate.Bearing - 90, halfWidth);
        var right = gate.Point.Move(gate.Bearing + 90, halfWidth);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var ink = new SKPaint { IsAntialias = false };
            void Flag(RoutePoint foot, int direction)
            {
                var terrain = CurrentTerrain;
                var mesh = lastView.Camera == camera && lastView.Width == (int)Math.Ceiling(width) &&
                    lastView.Height == (int)Math.Ceiling(height) ? reliefSurface : null;
                double absolute = terrain is not null && mode != TravelMode.Flight
                    ? terrain.Elevation.SampleSurfaceMeters(foot.Latitude, foot.Longitude) : altitude;
                double renderedAltitude = terrain?.RenderAltitude(camera, foot.Latitude, foot.Longitude, absolute, mesh) ?? absolute;
                if (!camera.TryProject(foot.Latitude, foot.Longitude, renderedAltitude, width, height, out var screen)) return;
                if (terrain is { Relief: true } && lastView.Camera == camera && lastView.Width == (int)Math.Ceiling(width) &&
                    lastView.Height == (int)Math.Ceiling(height) && buildings.CoversAt((float)screen.X / Math.Max(1, lastView.PixelSize),
                        (float)screen.Y / Math.Max(1, lastView.PixelSize), (float)(renderedAltitude - camera.GroundAltitudeMeters + 2))) return;
                var (x, y) = screen;
                if (!double.IsFinite(x) || !double.IsFinite(y) || x < -30 || x > width + 30 || y < -30 || y > height + 30) return;
                float u = (float)Math.Round(x / 2) * 2, v = (float)Math.Round(y / 2) * 2;
                ink.Color = new SKColor(25, 48, 57);
                canvas.DrawRect(u - 2, v - 18, 4, 18, ink);
                canvas.DrawRect(direction < 0 ? u - 10 : u, v - 18, 10, 8, ink);
                ink.Color = new SKColor(253, 218, 116); canvas.DrawRect(direction < 0 ? u - 8 : u + 2, v - 16, 6, 4, ink);
                ink.Color = ((int)(clock * 2) & 1) == 0 ? new SKColor(113, 252, 192) : new SKColor(44, 181, 151);
                canvas.DrawRect(u - 2, v - 22, 4, 4, ink);
            }
            Flag(left, -1); Flag(right, 1);
        }
    }
}
