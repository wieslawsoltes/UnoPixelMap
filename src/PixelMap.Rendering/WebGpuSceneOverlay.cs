using System.Numerics;
using System.Runtime.InteropServices;
using PixelMap.Routing;
using SkiaSharp;

namespace PixelMap.Rendering;

/// <summary>A packed overlay vertex consumed directly by the WebGPU pipeline. Positions
/// are homogeneous WebGPU clip coordinates; colors are premultiplied sRGB.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct WebGpuOverlayVertex(Vector4 position, Vector4 color, Vector2 uv = default, float textured = 0)
{
    public readonly Vector4 Position = position;
    public readonly Vector4 Color = color;
    public readonly Vector2 Uv = uv;
    public readonly float Textured = textured;
    public readonly float Reserved = 0;
}

/// <summary>Reusable GPU geometry for labels, routes, simulation models and particles.
/// Skia is used only when a new label asset is added to the bounded font atlas. No map
/// frame, navigation overlay or moving vehicle is rasterized into a CPU bitmap.</summary>
public sealed partial class WebGpuSceneOverlay : IDisposable
{
    private readonly OverlayMesh world = new();
    private readonly OverlayMesh occluded = new();
    private readonly OverlayMesh screen = new();
    private GpuMapFrame? frame;
    private GpuMapFrame? labelFrame;
    private bool disposed;
    private bool labelsChanged;
    private VehiclePose? lastVehicle;
    private TravelMode lastVehicleMode;
    private bool hasNavigation;
    private double worldSize, meters, bearingSin, bearingCos, pitchSin, pitchCos, focal;
    private float near, far;

    public ReadOnlySpan<WebGpuOverlayVertex> WorldVertices => world.VertexSpan;
    public ReadOnlySpan<uint> WorldIndices => world.IndexSpan;
    /// <summary>Navigation silhouettes that are drawn only where scenery is closer.</summary>
    public ReadOnlySpan<WebGpuOverlayVertex> OccludedVertices => occluded.VertexSpan;
    public ReadOnlySpan<uint> OccludedIndices => occluded.IndexSpan;
    public ReadOnlySpan<WebGpuOverlayVertex> ScreenVertices => screen.VertexSpan;
    public ReadOnlySpan<uint> ScreenIndices => screen.IndexSpan;
    public long Revision { get; private set; }
    public int LabelCount { get; private set; }
    public int RouteSegmentCount { get; private set; }
    public int ParticleCount => particleCount;
    public int WorldTriangleCount => world.IndexCount / 3;
    public int ScreenTriangleCount => screen.IndexCount / 3;

    internal bool Begin(GpuMapFrame next)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ObjectDisposedException.ThrowIf(next.IsDisposed, next);
        frame = next;
        world.Clear();
        occluded.Clear();
        RouteSegmentCount = 0;
        worldSize = next.Camera.WorldSize;
        meters = next.PixelsPerMeter * next.PixelSize;
        (bearingSin, bearingCos) = Math.SinCos(next.Camera.Bearing * Math.PI / 180);
        (pitchSin, pitchCos) = Math.SinCos(next.Camera.PitchRadians);
        focal = MapCamera.Focal(next.Height);
        near = (float)next.Camera.NearLimit(next.Height);
        far = (float)next.Camera.FarLimit(next.Height);
        labelsChanged = !ReferenceEquals(labelFrame, next);
        if (labelsChanged) pendingLabels.Clear();
        return labelsChanged;
    }

    internal void Complete(RoutePlan? route, VehiclePose? vehicle, TravelMode mode, RoutePosition? gate)
    {
        if (labelsChanged)
        {
            PrepareLabelAtlas();
            labelFrame = frame;
        }
        if (route is not null) AppendRoute(route, vehicle?.Progress ?? double.NaN);
        else routePlan = null;
        if (vehicle is { } sample)
        {
            mode = route?.Mode ?? mode;
            AdvanceParticles(mode, sample);
            AppendParticles(mode);
            if (route is null) AppendWorldVehicle(mode, sample);
            else AppendSpriteVehicle(mode, sample);
            if (gate is { } checkpoint && route is not null)
                AppendGate(checkpoint, mode, mode == TravelMode.Flight ? sample.AltitudeMeters : 0, sample.AnimationSeconds);
        }
        else ResetParticles();
        lastVehicle = vehicle;
        lastVehicleMode = route?.Mode ?? mode;
        hasNavigation = route is not null || gate is not null;
        Revision++;
    }

    /// <summary>Consume an idle standalone vehicle's timestamp without rebuilding or
    /// submitting an identical picture. Camera/source/quality equality is the host's
    /// responsibility. Route and gate animations are deliberately excluded.</summary>
    public bool TryAdvanceStationaryVehicle(TravelMode mode, VehiclePose vehicle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (hasNavigation || mode != lastVehicleMode || lastVehicle is not { Moving: false } previous ||
            !double.IsFinite(previous.AnimationSeconds) || !double.IsFinite(vehicle.AnimationSeconds) ||
            vehicle.AnimationSeconds < previous.AnimationSeconds ||
            (vehicle with { AnimationSeconds = previous.AnimationSeconds }) != previous) return false;
        AdvanceParticles(mode, vehicle);
        lastVehicle = vehicle;
        return true;
    }

    private H Project(double x, double y, double renderedAltitude)
    {
        var camera = frame!.Camera;
        double east = (x - camera.CenterX) * worldSize;
        double south = (y - camera.CenterY) * worldSize;
        double u = east * bearingCos + south * bearingSin;
        double v = -east * bearingSin + south * bearingCos;
        double local = renderedAltitude - camera.GroundAltitudeMeters;
        double z = local * meters - camera.TargetPixels;
        double w = camera.Perspective ? 1 - (v * pitchSin + z * pitchCos) / focal : 1;
        return new((float)(frame.Width * .5 * w + u), (float)(frame.Height * .5 * w + v * pitchCos - z * pitchSin),
            (float)w, (float)(local * frame.PixelsPerMeter), (float)renderedAltitude);
    }

    private H Project(RoutePoint point, double renderedAltitude)
    {
        var camera = frame!.Camera;
        double x = camera.CenterX + MapCamera.WrapOffset((point.Longitude + 180) / 360 - camera.CenterX);
        return Project(x, MapCamera.MercatorY(point.Latitude), renderedAltitude);
    }

    private double PhysicalSurface(RoutePoint point) =>
        frame!.NavigationTerrain?.Elevation.SampleSurfaceMeters(point.Latitude, point.Longitude) ?? 0;

    private double RenderAltitude(RoutePoint point, double absoluteAltitude)
    {
        if (frame!.NavigationTerrain is not { } terrain) return absoluteAltitude;
        double physical = terrain.Elevation.SampleSurfaceMeters(point.Latitude, point.Longitude);
        double surface = frame.TryHeight((point.Longitude + 180) / 360, MapCamera.MercatorY(point.Latitude), out var sampled)
            ? sampled : physical;
        return frame.Camera.GroundAltitudeMeters + terrain.LocalAltitude(frame.Camera, surface) + absoluteAltitude - physical;
    }

    private H GroundPoint(RoutePoint point, double lift = .6) =>
        Project(point, RenderAltitude(point, PhysicalSurface(point)) + lift);

    private Vector4 Clip(H point, float bias = 0)
    {
        var current = frame!;
        float depth;
        if (current.Camera.Perspective)
        {
            float range = far - near;
            depth = far / range * point.W - far * near / range;
        }
        else
        {
            float minimum = current.MinimumAltitude - 1, maximum = current.MaximumAltitude + 1;
            depth = (maximum - point.Z) / Math.Max(1, maximum - minimum);
            // Flight annotations may be above all scenery in an orthographic map.
            // Keep those in the nearest depth layer instead of clipping the annotation.
            depth = Math.Clamp(depth, 0, 1);
        }
        return new(2 * point.X / (current.ArtWidth * current.PixelSize) - point.W,
            point.W - 2 * point.Y / (current.ArtHeight * current.PixelSize), depth - bias * point.W, point.W);
    }

    private Vector4 ScreenPosition(float x, float y) => new(2 * x / frame!.Width - 1, 1 - 2 * y / frame.Height, 0, 1);
    private static H Offset(H point, float x, float y) => point with { X = point.X + x * point.W, Y = point.Y + y * point.W };
    private static bool Finite(H point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.W) && float.IsFinite(point.Z);
    private bool InFrustum(H point, float margin = 32) => Finite(point) && point.W >= near && point.W <= far &&
        point.X >= -margin * point.W && point.X <= (frame!.Width + margin) * point.W &&
        point.Y >= -margin * point.W && point.Y <= (frame.Height + margin) * point.W;
    private static Vector4 Color(uint color, float opacity = 1)
    {
        float alpha = (color >> 24) / 255f * opacity;
        return new(((color >> 16) & 255) / 255f * alpha, ((color >> 8) & 255) / 255f * alpha, (color & 255) / 255f * alpha, alpha);
    }

    private void Quad(H a, H b, H c, H d, uint color, bool hidden = false, float opacity = 1, float bias = 0)
    {
        if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(d)) return;
        var ink = Color(color, opacity);
        (hidden ? occluded : world).Quad(new(Clip(a, bias), ink), new(Clip(b, bias), ink), new(Clip(c, bias), ink), new(Clip(d, bias), ink));
    }

    private void Block(H anchor, float x, float y, float width, float height, uint color, bool hidden = false, float opacity = 1) =>
        Quad(Offset(anchor, x, y), Offset(anchor, x + width, y), Offset(anchor, x + width, y + height), Offset(anchor, x, y + height), color, hidden, opacity);

    private void Stroke(H a, H b, float width, uint color, bool hidden = false, float opacity = 1, float bias = 0)
    {
        if (!Finite(a) || !Finite(b)) return;
        float first = 0, last = 1;
        for (int plane = 0; plane < 6; plane++)
        {
            float before = TileTransform.Plane(plane, a, frame!.Width, frame.Height, far, near);
            float after = TileTransform.Plane(plane, b, frame.Width, frame.Height, far, near);
            if (before < 0 && after < 0) return;
            if ((before < 0) == (after < 0)) continue;
            float t = before / (before - after);
            if (before < 0) first = Math.Max(first, t); else last = Math.Min(last, t);
        }
        if (last < first) return;
        var left = H.Lerp(a, b, first);
        var right = H.Lerp(a, b, last);
        var delta = new Vector2(right.X / right.W - left.X / left.W, right.Y / right.W - left.Y / left.W);
        if (!float.IsFinite(delta.LengthSquared()) || delta.LengthSquared() < .000001f) return;
        var direction = Vector2.Normalize(delta);
        var normal = new Vector2(-direction.Y, direction.X) * (width * .5f);
        // A half-width cap closes joins without depending on implementation line width.
        var cap = direction * (width * .5f);
        Quad(Offset(left, normal.X - cap.X, normal.Y - cap.Y), Offset(right, normal.X + cap.X, normal.Y + cap.Y),
            Offset(right, -normal.X + cap.X, -normal.Y + cap.Y), Offset(left, -normal.X - cap.X, -normal.Y - cap.Y), color, hidden, opacity, bias);
    }

    private sealed class OverlayMesh
    {
        private WebGpuOverlayVertex[] vertices = new WebGpuOverlayVertex[512];
        private uint[] indices = new uint[768];
        private int vertexCount;
        internal int IndexCount { get; private set; }
        internal ReadOnlySpan<WebGpuOverlayVertex> VertexSpan => vertices.AsSpan(0, vertexCount);
        internal ReadOnlySpan<uint> IndexSpan => indices.AsSpan(0, IndexCount);
        internal void Clear() { vertexCount = IndexCount = 0; }
        internal void Quad(WebGpuOverlayVertex a, WebGpuOverlayVertex b, WebGpuOverlayVertex c, WebGpuOverlayVertex d)
        {
            // A continent-scale route is sampled within a fixed budget; enforce the
            // final mesh bound as well so malformed inputs cannot exhaust GPU memory.
            if (vertexCount > 1_048_572) return;
            if (vertexCount + 4 > vertices.Length) Array.Resize(ref vertices, vertices.Length * 2);
            if (IndexCount + 6 > indices.Length) Array.Resize(ref indices, indices.Length * 2);
            uint start = (uint)vertexCount;
            vertices[vertexCount++] = a; vertices[vertexCount++] = b;
            vertices[vertexCount++] = c; vertices[vertexCount++] = d;
            indices[IndexCount++] = start; indices[IndexCount++] = start + 1; indices[IndexCount++] = start + 2;
            indices[IndexCount++] = start; indices[IndexCount++] = start + 2; indices[IndexCount++] = start + 3;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        atlasCanvas?.Dispose();
        atlas?.Dispose();
        atlasFont?.Dispose();
        atlasPaint?.Dispose();
        atlasEntries.Clear();
        pendingLabels.Clear();
        frame = labelFrame = null;
    }
}
