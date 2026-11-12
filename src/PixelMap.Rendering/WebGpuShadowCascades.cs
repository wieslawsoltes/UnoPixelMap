using System.Numerics;
using PixelMap.Data;

namespace PixelMap.Rendering;

/// <summary>Directional-light fits in the renderer's rebased East/North/Up metre space.
/// Receiver slices select resolution; every loaded caster still contributes to each
/// light-depth interval, including models rejected by the viewer's index selection.</summary>
internal sealed class WebGpuShadowCascades
{
    internal const int MaximumCount = 4;
    internal const float BlendFraction = .10f;
    internal Vector3 Eye { get; }
    internal Vector4 View { get; }
    internal float Near { get; }
    internal WebGpuShadowCascade[] Layers { get; }
    internal WebGpuShadowBounds Bounds { get; }
    internal int Count => Layers.Length;

    private WebGpuShadowCascades(Vector3 eye, Vector4 view, float near,
        WebGpuShadowCascade[] layers, WebGpuShadowBounds bounds)
        => (Eye, View, Near, Layers, Bounds) = (eye, view, near, layers, bounds);

    /// <summary>Pack four affine world-to-shadow transforms into the existing
    /// 512-byte frame ABI. Three dot-product rows replace each four-column matrix;
    /// homogeneous W is exactly one for directional orthographic light fits.</summary>
    internal void WriteUniforms(Span<float> frame)
    {
        if (frame.Length < 128) throw new ArgumentException("A WebGPU frame slot contains 128 floats.", nameof(frame));
        for (int i = 0; i < Count; i++)
        {
            var layer = Layers[i];
            var m = layer.Matrix;
            Row(frame, i * 3, new(m.M11, m.M21, m.M31, m.M41));
            Row(frame, i * 3 + 1, new(m.M12, m.M22, m.M32, m.M42));
            Row(frame, i * 3 + 2, new(m.M13, m.M23, m.M33, m.M43));
            frame[29 * 4 + i] = layer.Far;
            frame[30 * 4 + i] = layer.TexelSize;
            frame[31 * 4 + i] = layer.InverseDepthRange;
        }
        frame[12 * 4 + 3] = Near;
        frame[13 * 4 + 3] = Count;
        frame[20 * 4] = View.X; frame[20 * 4 + 1] = View.Y;
        frame[20 * 4 + 2] = View.Z; frame[20 * 4 + 3] = View.W;

        static void Row(Span<float> target, int row, Vector4 values)
        {
            int offset = (row < 4 ? row + 14 : row + 17) * 4;
            target[offset] = values.X; target[offset + 1] = values.Y;
            target[offset + 2] = values.Z; target[offset + 3] = values.W;
        }
    }

    internal static Vector3 CameraEye(GpuMapFrame frame)
    {
        double pitch = frame.Camera.PitchRadians, bearing = frame.Camera.Bearing * Math.PI / 180;
        double distance = MapCamera.Focal(frame.Height) / (frame.PixelsPerMeter * frame.PixelSize);
        var eye = new Vector3((float)(-Math.Sin(bearing) * Math.Sin(pitch) * distance),
            (float)(-Math.Cos(bearing) * Math.Sin(pitch) * distance),
            (float)(frame.Camera.TargetAltitudeMeters + Math.Cos(pitch) * distance));
        // Preserve the existing orthographic lighting eye. Orthographic receiver
        // rays below use the projection origin, not this synthetic lighting eye.
        return frame.Camera.Perspective ? eye : new(eye.X, eye.Y, Math.Max(eye.Z, 100));
    }

    internal static WebGpuShadowCascades Create(GpuMapFrame frame, Vector3 sun,
        int count, int resolution, float shadowDistance)
    {
        if (count is < 1 or > MaximumCount || resolution < 16 || shadowDistance <= 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Shadow fits require one to four layers and a positive range/resolution.");
        sun = Vector3.Normalize(sun);
        var bounds = FindBounds(frame);
        var eye = CameraEye(frame);
        float pitch = (float)frame.Camera.PitchRadians, bearing = (float)(frame.Camera.Bearing * Math.PI / 180);
        var forward = Vector3.Normalize(new Vector3(MathF.Sin(bearing) * MathF.Sin(pitch),
            MathF.Cos(bearing) * MathF.Sin(pitch), -MathF.Cos(pitch)));
        var right = new Vector3(MathF.Cos(bearing), -MathF.Sin(bearing), 0);
        var up = Vector3.Cross(right, forward);
        bool perspective = frame.Camera.Perspective;
        var origin = perspective ? eye : Vector3.Zero;
        var view = new Vector4(forward, -Vector3.Dot(forward, origin));
        float logicalPpm = frame.PixelsPerMeter * frame.PixelSize;
        float focalDistance = (float)(MapCamera.Focal(frame.Height) / logicalPpm);
        float near = perspective ? (float)frame.Camera.NearLimit(frame.Height) * focalDistance : float.NegativeInfinity;
        float far = perspective ? (float)frame.Camera.FarLimit(frame.Height) * focalDistance : float.PositiveInfinity;
        ProjectRange(bounds, forward, out float minimum, out float maximum);
        minimum += view.W; maximum += view.W;
        near = Math.Max(near, minimum);
        far = Math.Min(far, maximum);
        if (far <= near + .01f)
        {
            // Empty/edge-on scenes still need a finite binding; their shadow pass
            // contains no receiver with a meaningful longitudinal interval.
            far = near + 1;
        }
        // Additional quality layers extend the High interval at its far end.
        // Moving its near end would put the same receiver into a different,
        // potentially coarser first layer when selecting Ultra.
        float priorityDistance = count > 2 ? Math.Min(shadowDistance, WebGpuRenderOptions.For(WebGpuQuality.High).ShadowDistance) : shadowDistance;
        if (far - near > priorityDistance)
        {
            // A free eye prioritizes its nearest receivers. A map lens retains a
            // bounded interval around its focus even when zoomed far above Earth.
            float focus = perspective ? focalDistance : 0;
            float start = frame.Camera.IsFreeLook ? near : Math.Clamp(focus - priorityDistance * .5f, near, far - priorityDistance);
            near = start;
            far = Math.Min(far, start + shadowDistance);
        }

        Span<float> splits = stackalloc float[MaximumCount];
        Split(near, far, Math.Min(far - near, priorityDistance), perspective, count, splits);
        float halfWidth = perspective ? (float)(frame.Width / (2 * MapCamera.Focal(frame.Height))) : frame.Width / (2 * logicalPpm);
        float halfHeight = perspective ? (float)(frame.Height / (2 * MapCamera.Focal(frame.Height))) : frame.Height / (2 * logicalPpm);
        var lightRight = Vector3.Normalize(Vector3.Cross(Math.Abs(sun.Z) < .99f ? Vector3.UnitZ : Vector3.UnitY, sun));
        var lightUp = Vector3.Cross(sun, lightRight);
        double latitudeScale = Math.Cos(Math.Clamp(frame.Camera.Latitude,
            -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180);
        double anchorX = frame.Camera.CenterX * MapCamera.EarthCircumference;
        double anchorY = -frame.Camera.CenterY * MapCamera.EarthCircumference;
        var layers = new WebGpuShadowCascade[count];
        Span<Vector3> corners = stackalloc Vector3[8];
        float previous = near, previousBlend = near;
        for (int i = 0; i < count; i++)
        {
            float end = splits[i];
            float fitNear = i == 0 ? near : previousBlend;
            float midpoint = (fitNear + end) * .5f;
            var center = origin + forward * midpoint;
            float w = halfWidth * (perspective ? end : 1), h = halfHeight * (perspective ? end : 1);
            // A slice bounding sphere keeps its size independent of camera bearing
            // and pitch. Quantization prevents tiny floating point radius changes.
            float requiredRadius = MathF.Sqrt(w * w + h * h + (end - midpoint) * (end - midpoint));
            // Quantize in unscaled Mercator metres. Using physical metres for
            // this grid makes cos(latitude) change the phase of a millions-of-
            // metres global anchor during a centimetre-scale north/south pan.
            double mercatorRadius = Math.Ceiling(Math.Max(.125f, requiredRadius) / latitudeScale * 16) / 16;
            // Leave four texels for snapping, the 3x3 comparison kernel and bias.
            mercatorRadius /= 1 - 8d / resolution;
            double mercatorTexel = mercatorRadius * 2 / resolution;
            float radius = (float)(mercatorRadius * latitudeScale);
            float texel = (float)(mercatorTexel * latitudeScale);
            SliceCorners(origin, forward, right, up, halfWidth, halfHeight, perspective, fitNear, end, corners);
            ProjectRange(bounds, sun, out float low, out float high);
            foreach (var corner in corners)
            {
                float z = Vector3.Dot(corner, sun);
                low = Math.Min(low, z); high = Math.Max(high, z);
            }
            // Include every loaded caster along the light ray. Cropping only X/Y
            // cannot discard a caster whose shadow reaches the receiver rectangle.
            float padding = Math.Max(1, texel * 4);
            low -= padding; high += padding;
            float inverseDepth = 1 / Math.Max(.01f, high - low);
            double globalX = anchorX * lightRight.X + anchorY * lightRight.Y;
            double globalY = anchorX * lightUp.X + anchorY * lightUp.Y;
            // Keep the division and rounding in double Mercator units. Dividing
            // the large anchor by a rounded float physical texel reintroduces
            // the latitude-dependent phase error even with a stable radius.
            double centerX = ((double)center.X * lightRight.X + (double)center.Y * lightRight.Y + (double)center.Z * lightRight.Z) / latitudeScale;
            double centerY = ((double)center.X * lightUp.X + (double)center.Y * lightUp.Y + (double)center.Z * lightUp.Z) / latitudeScale;
            double snappedX = Math.Round((globalX + centerX) / mercatorTexel) * mercatorTexel - globalX;
            double snappedY = Math.Round((globalY + centerY) / mercatorTexel) * mercatorTexel - globalY;
            var matrix = new Matrix4x4(
                lightRight.X / radius, lightUp.X / radius, -sun.X * inverseDepth, 0,
                lightRight.Y / radius, lightUp.Y / radius, -sun.Y * inverseDepth, 0,
                lightRight.Z / radius, lightUp.Z / radius, -sun.Z * inverseDepth, 0,
                (float)(-snappedX / mercatorRadius), (float)(-snappedY / mercatorRadius), high * inverseDepth, 1);
            float blendStart = end - (end - previous) * BlendFraction;
            layers[i] = new(matrix, previous, end, fitNear, blendStart, texel, inverseDepth, center, radius);
            previous = end;
            previousBlend = blendStart;
        }
        return new(eye, view, near, layers, bounds);
    }

    private static void Split(float near, float far, float priorityRange, bool perspective, int count, Span<float> splits)
    {
        float range = far - near;
        if (count >= 2)
            splits[0] = perspective ? near + Math.Min(priorityRange * .35f, Math.Max(24, Math.Max(0, near) * 7)) : near + priorityRange * .5f;
        if (count == 3)
        {
            splits[1] = perspective ? near + Math.Min(range * .65f, Math.Max(96, Math.Max(0, near) * 15)) : near + range * (2f / 3);
        }
        if (count == 4)
        {
            // At street level these end at 24, 64 and 256 metres. The middle
            // layers cover nearby facades without replacing the fine first fit
            // or making the next transition jump directly to the far map.
            splits[1] = perspective ? near + Math.Min(range * .60f, Math.Max(64, Math.Max(0, near) * 15)) :
                splits[0] + (far - splits[0]) / 3;
            splits[2] = perspective ? near + Math.Min(range * .82f, Math.Max(256, Math.Max(0, near) * 31)) :
                splits[0] + (far - splits[0]) * (2f / 3);
        }
        splits[count - 1] = far;
    }

    internal static void SliceCorners(Vector3 origin, Vector3 forward, Vector3 right, Vector3 up,
        float halfWidth, float halfHeight, bool perspective, float near, float far, Span<Vector3> corners)
    {
        for (int plane = 0; plane < 2; plane++)
        {
            float depth = plane == 0 ? near : far, scale = perspective ? depth : 1;
            var center = origin + forward * depth;
            for (int corner = 0; corner < 4; corner++)
                corners[plane * 4 + corner] = center + right * (halfWidth * scale * ((corner & 1) == 0 ? -1 : 1)) +
                    up * (halfHeight * scale * ((corner & 2) == 0 ? -1 : 1));
        }
    }

    private static void ProjectRange(WebGpuShadowBounds bounds, Vector3 direction, out float low, out float high)
    {
        var center = (bounds.Minimum + bounds.Maximum) * .5f;
        var extent = (bounds.Maximum - bounds.Minimum) * .5f;
        float projected = Vector3.Dot(center, direction), radius = Vector3.Dot(extent, Vector3.Abs(direction));
        low = projected - radius; high = projected + radius;
    }

    private static WebGpuShadowBounds FindBounds(GpuMapFrame frame)
    {
        float left = float.PositiveInfinity, south = float.PositiveInfinity;
        float right = float.NegativeInfinity, north = float.NegativeInfinity;
        foreach (var draw in frame.Terrain)
        {
            var world = WorldOrigin(frame, draw.Mesh.Id, draw.Mesh.Copy);
            Include(draw.Mesh.Bounds.Area, world);
        }
        foreach (var draw in frame.Buildings)
        {
            var id = draw.Mesh.Source.Source.Id;
            int copy = (int)Math.Round(frame.Camera.CenterX - (id.X + .5) / (1 << id.Z));
            foreach (var tile in frame.Terrain)
                if (tile.Mesh.Id == id && tile.Transform == draw.Transform) { copy = tile.Mesh.Copy; break; }
            var world = WorldOrigin(frame, id, copy);
            foreach (var range in draw.Mesh.Ranges) Include(range.Model.Bounds, world);
        }
        if (!float.IsFinite(left)) { left = south = -1; right = north = 1; }
        // The frame depth bounds contain ALL submitted ranges, in art pixels.
        // Convert once to the same metre Z used by terrain and building WGSL.
        float low = frame.MinimumAltitude / frame.PixelsPerMeter;
        float high = frame.MaximumAltitude / frame.PixelsPerMeter;
        return new(new(left, south, low), new(right, north, Math.Max(low + .01f, high)));

        void Include(SkiaSharp.SKRect area, Vector3 world)
        {
            left = Math.Min(left, world.X + area.Left * world.Z);
            right = Math.Max(right, world.X + area.Right * world.Z);
            south = Math.Min(south, -(world.Y + area.Bottom * world.Z));
            north = Math.Max(north, -(world.Y + area.Top * world.Z));
        }
    }

    private static Vector3 WorldOrigin(GpuMapFrame frame, TileId id, int copy)
    {
        double meters = MapCamera.EarthCircumference * Math.Cos(Math.Clamp(frame.Camera.Latitude,
            -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180);
        double n = 1 << id.Z;
        return new((float)((id.X / n + copy - frame.Camera.CenterX) * meters),
            (float)((id.Y / n - frame.Camera.CenterY) * meters), (float)(meters / (n * TileGeometry.Extent)));
    }
}

internal readonly record struct WebGpuShadowBounds(Vector3 Minimum, Vector3 Maximum);
internal readonly record struct WebGpuShadowCascade(Matrix4x4 Matrix, float Near, float Far,
    float FitNear, float BlendStart, float TexelSize, float InverseDepthRange, Vector3 Center, float Radius);
