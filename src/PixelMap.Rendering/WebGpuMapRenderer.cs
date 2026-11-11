using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using SkiaSharp;
using GpuBuffer = Silk.NET.WebGPU.Buffer;

namespace PixelMap.Rendering;

/// <summary>
/// Retained, shader-driven WebGPU terrain, buildings, overlays and HDR presentation.
/// The caller lends the API, device, queue and destination view; this renderer owns
/// only the resources it creates. No rendered image is copied to CPU memory.
/// </summary>
public sealed unsafe partial class WebGpuMapRenderer : IDisposable
{
    private const int MaximumTerrainDraws = 64;
    private const int MaximumBuildingDraws = 512;
    private const int TextureLayers = 96;
    private const int HeightLayers = 256;
    private const int UniformStride = 512;
    private const int UniformFloats = UniformStride / sizeof(float);
    private const long CacheBudget = 128L * 1024 * 1024;
    private const long AttachmentBudget = 256L * 1024 * 1024;
    private readonly WebGPU api;
    private readonly Device* device;
    private readonly Queue* queue;
    private readonly Dictionary<object, TerrainResource> terrainCache = [];
    private readonly Dictionary<GpuTerrainTexture, TextureResource> textureCache = [];
    private readonly Dictionary<object, BuildingResource> buildingCache = [];
    private readonly Stack<int> freeHeights = new();
    private readonly Stack<int> freeTextures = new();
    private readonly float[] uniforms = new float[(MaximumBuildingDraws + 1) * UniformFloats];
    private readonly float[] instances = new float[MaximumTerrainDraws * 20];
    private readonly float[] heightsUpload = new float[GpuTerrainMesh.Side * GpuTerrainMesh.Side * 2];
    private readonly List<BuildingDraw> buildingDraws = new(MaximumBuildingDraws);
    private readonly float[] resolveValues = new float[16];
    private PixelPalette? uploadedPalette;
    private GpuBuffer* uniformBuffer, resolveBuffer, instanceBuffer;
    private GpuBuffer* gridIndices, cubeIndices, bevelIndices;
    private Texture* terrainArtwork, terrainHeights, paletteTexture, roomTexture;
    private TextureView* terrainArtworkView, terrainHeightView, paletteView, roomView;
    private Sampler* nearestSampler, linearSampler, roomSampler, comparisonSampler;
    private BindGroupLayout* commonLayout, projectionLayout, terrainLayout, modelLayout, resolveLayout, overlayLayout;
    private BindGroup* commonGroup, projectionGroup, terrainNearestGroup, terrainLinearGroup, resolveGroup;
    private PipelineLayout* backgroundLayout, sceneLayout, buildingLayout, shadowTerrainLayout, shadowBuildingLayout, resolvePipelineLayout, overlayPipelineLayout;
    private ShaderModule* backgroundModule, terrainModule, buildingModule, rimModule, resolveModule, overlayModule;
    private RenderPipeline* backgroundPipeline, terrainPipeline, buildingPipeline, rimPipeline, resolvePipeline;
    private RenderPipeline* shadowTerrainPipeline, shadowBuildingPipeline, overlayWorldPipeline, overlayOccludedPipeline, overlayScreenPipeline;
    private Texture* sceneTexture, multisampleTexture, depthTexture, photographTexture, shadowTexture, bitmapOverlayTexture;
    private TextureView* sceneView, multisampleView, depthView, photographView, shadowView, bitmapOverlayView;
    private int attachmentWidth, attachmentHeight, attachmentSamples, shadowSize, bitmapWidth, bitmapHeight;
    private TextureFormat pipelineTargetFormat;
    private int pipelineSamples;
    private long generation, meshBytes;
    private bool disposed;
    private WebGpuRenderOptions options;
    private GpuMapFrame? retainedShadowFrame;
    private WebGpuQuality retainedShadowQuality;
    private int maximumTextureSize, heightLayerCapacity, textureLayerCapacity;
    private ulong maximumBufferSize, maximumStorageBindingSize;

    /// <summary>Continuous rendering quality. Pixel and Minecraft retain their original logical pixel grid.</summary>
    public WebGpuQuality Quality { get; set; } = WebGpuQuality.High;
    public string Driver => "Uno shared WebGPU device";
    public long LastUploadBytes { get; private set; }
    public int LastDrawCalls { get; private set; }
    public double LastSubmissionMilliseconds { get; private set; }
    /// <summary>Nominal bytes in owned resident GPU buffers and textures. Scene attachments
    /// are reported separately; implementation-specific storage and alignment are excluded.</summary>
    public long ResidentBytes => disposed ? 0 : meshBytes +
        (long)textureLayerCapacity * GpuTerrainTexture.Size * GpuTerrainTexture.Size * 4 +
        (long)heightLayerCapacity * GpuTerrainMesh.Side * GpuTerrainMesh.Side * 8 +
        (long)shadowSize * shadowSize * 4 + StaticResidentBytes + OverlayResidentBytes;
    public long AttachmentBytes { get; private set; }
    public int RenderWidth => attachmentWidth;
    public int RenderHeight => attachmentHeight;

    public WebGpuMapRenderer(WebGPU api, Device* device, Queue* queue)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        if (device == null || queue == null) throw new ArgumentException("A live borrowed WebGPU device and queue are required.");
        this.device = device;
        this.queue = queue;
        try { ReadLimits(); InitializeResources(); }
        catch { Dispose(); throw; }
    }

    private void ReadLimits()
    {
        SupportedLimits supported = default;
        if (!api.DeviceGetLimits(device, &supported))
            throw new NotSupportedException("The shared WebGPU device did not expose its limits.");
        var limits = supported.Limits;
        maximumTextureSize = (int)Math.Min(limits.MaxTextureDimension2D, int.MaxValue);
        maximumBufferSize = limits.MaxBufferSize;
        maximumStorageBindingSize = Math.Min(limits.MaxBufferSize, limits.MaxStorageBufferBindingSize);
        heightLayerCapacity = (int)Math.Min((uint)HeightLayers, limits.MaxTextureArrayLayers);
        textureLayerCapacity = (int)Math.Min((uint)TextureLayers, limits.MaxTextureArrayLayers);
        uint alignment = Math.Max(1u, limits.MinUniformBufferOffsetAlignment);
        if (maximumTextureSize < GpuTerrainTexture.Size || textureLayerCapacity < MaximumTerrainDraws ||
            heightLayerCapacity < MaximumTerrainDraws || limits.MaxBindGroups < 2 || limits.MaxVertexAttributes < 5 ||
            limits.MaxUniformBufferBindingSize < UniformStride || UniformStride % alignment != 0 ||
            maximumBufferSize < (ulong)uniforms.Length * sizeof(float) || maximumStorageBindingSize < 32)
            throw new NotSupportedException("The shared WebGPU device cannot support the map's bounded texture, vertex and uniform layouts.");
        for (int i = heightLayerCapacity - 1; i >= 0; i--) freeHeights.Push(i);
        for (int i = textureLayerCapacity - 1; i >= 0; i--) freeTextures.Push(i);
        // RGBA16Float/Depth24Plus render attachments with 1x and 4x samples are
        // required core WebGPU formats. No optional float32-filterable feature is used.
    }

    /// <summary>Draw into a borrowed Uno target using one GPU queue submission.
    /// Projection and overlays use logical pixels; continuous modes rasterize at a
    /// bounded physical resolution, and pixel modes resolve the logical art grid.</summary>
    public void Draw(GpuMapFrame frame, TextureView* target, TextureFormat targetFormat,
        uint targetWidth, uint targetHeight, SKBitmap? overlay = null, WebGpuSceneOverlay? sceneOverlay = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(frame.IsDisposed, frame);
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (targetWidth == 0 || targetHeight == 0 || frame.Width <= 0 || frame.Height <= 0) return;
        if (frame.Terrain.Length > MaximumTerrainDraws || frame.Buildings.Length > MaximumBuildingDraws)
            throw new NotSupportedException("The visible map exceeds the bounded WebGPU draw budget.");
        if (overlay is not null && (overlay.ColorType != SKColorType.Bgra8888 || overlay.AlphaType != SKAlphaType.Premul))
            throw new ArgumentException("Bitmap overlays must use premultiplied BGRA8888.", nameof(overlay));
        long started = Stopwatch.GetTimestamp();
        LastUploadBytes = 0;
        LastDrawCalls = 0;
        generation++;
        options = WebGpuRenderOptions.For(Quality);
        bool smooth = MapRendering.Smooth(frame.RenderStyle);
        int samples = smooth ? options.SampleCount : 1;
        (int width, int height) = RasterSize(frame, targetWidth, targetHeight, smooth, samples);
        EnsureShadow(smooth ? Math.Min(maximumTextureSize, options.ShadowSize) : 1);
        EnsureAttachments(width, height, samples);
        EnsurePipelines(targetFormat, samples);
        UploadPalette(frame.Palette);
        PrepareFrame(frame, targetWidth, targetHeight, smooth);
        if (overlay is not null) UploadBitmapOverlay(overlay);
        PrepareOverlay(sceneOverlay, smooth);
        UpdateResolve(frame, targetWidth, targetHeight, smooth, overlay is not null);

        CommandEncoder* encoder = null;
        CommandBuffer* commands = null;
        try
        {
            using var encoderLabel = new NativeLabel("PixelMap frame");
            var encoderDescriptor = new CommandEncoderDescriptor { Label = encoderLabel.Pointer };
            encoder = api.DeviceCreateCommandEncoder(device, &encoderDescriptor);
            Require(encoder, "command encoder");
            bool drawShadows = smooth && (!ReferenceEquals(retainedShadowFrame, frame) || retainedShadowQuality != Quality);
            if (drawShadows) DrawShadow(encoder, frame);
            else if (!smooth) ClearShadow(encoder);
            bool photograph = frame.PreservePhotograph && frame.Camera.Pitch < 30;
            var pass = BeginScenePass(encoder, clear: true);
            try
            {
                api.RenderPassEncoderSetPipeline(pass, backgroundPipeline);
                SetFrame(pass, commonGroup, 0);
                api.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
                LastDrawCalls++;
                DrawTerrain(pass, frame, shadow: false);
                if (!photograph)
                {
                    DrawBuildings(pass, shadow: false);
                    DrawWorldOverlay(pass, sceneOverlay);
                }
            }
            finally { EndPass(pass); }
            if (photograph)
            {
                var from = new ImageCopyTexture { Texture = sceneTexture, Aspect = TextureAspect.All };
                var to = new ImageCopyTexture { Texture = photographTexture, Aspect = TextureAspect.All };
                var extent = new Extent3D((uint)width, (uint)height, 1);
                api.CommandEncoderCopyTextureToTexture(encoder, &from, &to, &extent);
                pass = BeginScenePass(encoder, clear: false);
                try { DrawBuildings(pass, shadow: false); DrawWorldOverlay(pass, sceneOverlay); }
                finally { EndPass(pass); }
            }
            pass = BeginTargetPass(encoder, target, clear: true);
            try
            {
                api.RenderPassEncoderSetPipeline(pass, resolvePipeline);
                api.RenderPassEncoderSetBindGroup(pass, 0, resolveGroup, 0, null);
                api.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
                LastDrawCalls++;
                DrawScreenOverlay(pass, sceneOverlay);
            }
            finally { EndPass(pass); }
            commands = api.CommandEncoderFinish(encoder, null);
            Require(commands, "command buffer");
            api.QueueSubmit(queue, 1, &commands);
            if (drawShadows) { retainedShadowFrame = frame; retainedShadowQuality = Quality; }
            TrimCaches();
        }
        finally
        {
            if (commands != null) api.CommandBufferRelease(commands);
            if (encoder != null) api.CommandEncoderRelease(encoder);
            LastSubmissionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    private (int Width, int Height) RasterSize(GpuMapFrame frame, uint targetWidth, uint targetHeight, bool smooth, int samples)
    {
        if (!smooth)
        {
            long bytes = (long)frame.ArtWidth * frame.ArtHeight * 20;
            if (bytes > AttachmentBudget || frame.ArtWidth > maximumTextureSize || frame.ArtHeight > maximumTextureSize)
                throw new NotSupportedException("The logical pixel grid exceeds the map surface budget.");
            return (frame.ArtWidth, frame.ArtHeight);
        }
        long bytesPerPixel = samples > 1 ? samples * 12L + 16 : 20;
        long maximum = Math.Min(options.MaximumScenePixels, AttachmentBudget / bytesPerPixel);
        double scale = Math.Min(1, Math.Sqrt(maximum / ((double)targetWidth * targetHeight)));
        scale = Math.Min(scale, (double)maximumTextureSize / Math.Max(targetWidth, targetHeight));
        return (Math.Max(1, (int)Math.Floor(targetWidth * scale)), Math.Max(1, (int)Math.Floor(targetHeight * scale)));
    }

    private void PrepareFrame(GpuMapFrame frame, uint targetWidth, uint targetHeight, bool smooth)
    {
        foreach (var draw in frame.Terrain)
        {
            if (terrainCache.TryGetValue(draw.Mesh.ResourceKey, out var h)) h.Generation = generation;
            if (textureCache.TryGetValue(draw.Texture, out var t)) t.Generation = generation;
        }
        foreach (var draw in frame.Buildings)
            if (buildingCache.TryGetValue(draw.Mesh.ResourceKey, out var b)) b.Generation = generation;
        uniforms.AsSpan().Clear();
        var transform = frame.Terrain.Length > 0 ? frame.Terrain[0].Transform :
            frame.Buildings.Length > 0 ? frame.Buildings[0].Transform : default;
        FillUniform(uniforms.AsSpan(0, UniformFloats), frame, transform, default, targetWidth, targetHeight, smooth);
        int offset = 0;
        foreach (var draw in frame.Terrain)
        {
            int height = GetHeight(draw.Mesh).Layer;
            int texture = GetTexture(draw.Texture).Layer;
            var t = draw.Transform;
            float z = -t.CosPitch * t.InverseFocal;
            Vector3 world = WorldOrigin(frame, draw.Mesh.Id, draw.Mesh.Copy);
            Write4(instances, ref offset, t.XX, t.XY, t.CX * z, t.X);
            Write4(instances, ref offset, t.YX, t.YY, t.CY * z - t.SinPitch, t.Y);
            Write4(instances, ref offset, t.PX, t.PY, z, t.PW);
            Write4(instances, ref offset, texture, height, 0, 0);
            Write4(instances, ref offset, world.X, world.Y, world.Z, 0);
        }
        Upload(instanceBuffer, instances.AsSpan(0, offset));
        buildingDraws.Clear();
        int slot = 1;
        foreach (var draw in frame.Buildings)
        {
            var source = draw.Mesh;
            if (source.Vertices.Length == 0) continue;
            var resource = GetBuilding(draw);
            // The rebased source transform includes the wrap copy; recover that copy
            // from the source tile's nearest world instance for PBR world coordinates.
            var id = source.Source.Source.Id;
            int copy = FindCopy(frame, id, draw.Transform);
            var world = WorldOrigin(frame, id, copy);
            var tileUniform = uniforms.AsSpan(slot * UniformFloats, UniformFloats);
            uniforms.AsSpan(0, UniformFloats).CopyTo(tileUniform);
            FillProjection(tileUniform, frame, draw.Transform, world);
            buildingDraws.Add(new(resource, (uint)(slot * UniformStride)));
            slot++;
        }
        Upload(uniformBuffer, uniforms.AsSpan(0, slot * UniformFloats));
    }

    private static int FindCopy(GpuMapFrame frame, PixelMap.Data.TileId id, TileTransform transform)
    {
        foreach (var terrain in frame.Terrain)
            if (terrain.Mesh.Id == id && terrain.Transform == transform) return terrain.Mesh.Copy;
        return (int)Math.Round(frame.Camera.CenterX - (id.X + .5) / (1 << id.Z));
    }

    private static Vector3 WorldOrigin(GpuMapFrame frame, PixelMap.Data.TileId id, int copy)
    {
        double meters = MapCamera.EarthCircumference * Math.Cos(Math.Clamp(frame.Camera.Latitude,
            -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180);
        double n = 1 << id.Z;
        return new((float)((id.X / n + copy - frame.Camera.CenterX) * meters),
            (float)((id.Y / n - frame.Camera.CenterY) * meters), (float)(meters / (n * TileGeometry.Extent)));
    }

    private void FillUniform(Span<float> u, GpuMapFrame frame, TileTransform t, Vector3 world,
        uint targetWidth, uint targetHeight, bool smooth)
    {
        FillProjection(u, frame, t, world);
        PixelStyle palette = frame.Palette.Style;
        uint sky = palette == PixelStyle.Night ? 0xff273849 : palette == PixelStyle.Dusk ? 0xffba969a : 0xff9bc1c8;
        uint horizon = palette == PixelStyle.Night ? 0xff405264 : palette == PixelStyle.Dusk ? 0xffd2aaa1 : 0xffc6d6cd;
        float horizonRow = 0;
        if (frame.Camera.Perspective && frame.Camera.Pitch > 0)
            horizonRow = (float)((frame.Height * .5 + (MapCamera.Focal(frame.Height) * Math.Cos(frame.Camera.PitchRadians) *
                (1 / frame.Camera.FarLimit(frame.Height) - 1) + frame.Camera.TargetPixels / frame.Camera.FarLimit(frame.Height)) /
                Math.Sin(frame.Camera.PitchRadians)) / frame.PixelSize);
        SetColor(u, 7, sky, horizonRow);
        SetColor(u, 8, horizon, frame.TerrainOptions.Hypsometric ? 1 : 0);
        SetColor(u, 9, frame.Palette.Ground, 0);
        Set4(u, 10, options.Exposure, .00012f, palette == PixelStyle.Night ? .42f : palette == PixelStyle.Dusk ? 2.5f : 3.7f,
            palette == PixelStyle.Night ? .6f : 1.1f);
        var sun = Vector3.Normalize(new Vector3(-.45f, -.55f, palette == PixelStyle.Dusk ? .35f : .82f));
        Set4(u, 11, sun.X, sun.Y, sun.Z, smooth ? 1 : 0);
        Set4(u, 12, 1.0f, palette == PixelStyle.Dusk ? .68f : .94f, palette == PixelStyle.Dusk ? .4f : .82f, .12f);
        double pitch = frame.Camera.PitchRadians, bearing = frame.Camera.Bearing * Math.PI / 180;
        double distance = MapCamera.Focal(frame.Height) / (frame.PixelsPerMeter * frame.PixelSize);
        var eye = new Vector3((float)(-Math.Sin(bearing) * Math.Sin(pitch) * distance),
            (float)(-Math.Cos(bearing) * Math.Sin(pitch) * distance),
            (float)(frame.Camera.TargetAltitudeMeters + Math.Cos(pitch) * distance));
        if (!frame.Camera.Perspective) eye = new(eye.X, eye.Y, Math.Max(eye.Z, 100));
        Set4(u, 13, eye.X, eye.Y, eye.Z, 0);
        var shadow = ShadowMatrix(frame, sun, eye);
        Set4(u, 14, shadow.M11, shadow.M12, shadow.M13, shadow.M14);
        Set4(u, 15, shadow.M21, shadow.M22, shadow.M23, shadow.M24);
        Set4(u, 16, shadow.M31, shadow.M32, shadow.M33, shadow.M34);
        Set4(u, 17, shadow.M41, shadow.M42, shadow.M43, shadow.M44);
        Set4(u, 18, attachmentWidth, attachmentHeight, frame.Width, frame.Height);
        Set4(u, 19, frame.PixelSize, t.Perspective ? 0 : 1, (float)palette, attachmentSamples == 1 ? 1 : 0);
        Set4(u, 20, targetWidth, targetHeight, 0, 0);
    }

    private static void FillProjection(Span<float> u, GpuMapFrame frame, TileTransform t, Vector3 world)
    {
        float zw = -t.CosPitch * t.InverseFocal;
        Set4(u, 0, t.XX, t.XY, t.CX * zw, t.X);
        Set4(u, 1, t.YX, t.YY, t.CY * zw - t.SinPitch, t.Y);
        Set4(u, 2, t.PX, t.PY, zw, t.PW);
        float depthA, depthB;
        if (t.Perspective)
        {
            float range = Math.Max(1e-12f, t.Far - t.Near);
            depthA = (t.Far + t.Near) / range;
            depthB = -2 * t.Far * t.Near / range;
        }
        else
        {
            float minimum = frame.MinimumAltitude - 1, maximum = frame.MaximumAltitude + 1;
            float range = Math.Max(1, maximum - minimum);
            depthA = -2 / range;
            depthB = (maximum + minimum) / range;
        }
        Set4(u, 3, frame.ArtWidth, frame.ArtHeight, depthA, depthB);
        Set4(u, 4, (float)frame.Camera.GroundAltitudeMeters,
            frame.TerrainOptions.Relief ? (float)frame.TerrainOptions.HeightScale : 0,
            frame.PixelsPerMeter, (float)frame.RenderStyle);
        Set4(u, 5, t.SinPitch, t.Perspective ? t.TargetZ + t.CosPitch / t.InverseFocal : 0, t.Far, t.Near);
        Set4(u, 6, world.X, world.Y, world.Z, t.Perspective ? 1 : 0);
    }

    private Matrix4x4 ShadowMatrix(GpuMapFrame frame, Vector3 sun, Vector3 eye)
    {
        float span = (float)(Math.Max(frame.Width, frame.Height) / (frame.PixelsPerMeter * frame.PixelSize));
        float radius = Math.Clamp(span * .9f, 120, options.ShadowDistance);
        Vector3 center = frame.Camera.IsFreeLook ? new(eye.X, eye.Y, 0) : Vector3.Zero;
        // Stabilize the light-space origin on shadow texels, retaining detail while panning.
        var lightView = Matrix4x4.CreateLookAt(center + sun * (radius * 2.5f), center, Vector3.UnitZ);
        var shadow = lightView * Matrix4x4.CreateOrthographic(radius * 2, radius * 2, 1, radius * 6);
        double meters = MapCamera.EarthCircumference * Math.Cos(Math.Clamp(frame.Camera.Latitude,
            -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180);
        double anchorX = frame.Camera.CenterX * meters, anchorY = -frame.Camera.CenterY * meters;
        double lightX = anchorX * shadow.M11 + anchorY * shadow.M21;
        double lightY = anchorX * shadow.M12 + anchorY * shadow.M22;
        double texel = 2d / Math.Max(1, shadowSize);
        shadow.M41 += (float)(Math.Round(lightX / texel) * texel - lightX);
        shadow.M42 += (float)(Math.Round(lightY / texel) * texel - lightY);
        return shadow;
    }

    private void DrawTerrain(RenderPassEncoder* pass, GpuMapFrame frame, bool shadow)
    {
        if (frame.Terrain.Length == 0) return;
        api.RenderPassEncoderSetPipeline(pass, shadow ? shadowTerrainPipeline : terrainPipeline);
        SetFrame(pass, shadow ? projectionGroup : commonGroup, 0);
        api.RenderPassEncoderSetBindGroup(pass, 1, MapRendering.Smooth(frame.RenderStyle) ? terrainLinearGroup : terrainNearestGroup, 0, null);
        api.RenderPassEncoderSetVertexBuffer(pass, 0, instanceBuffer, 0, (ulong)(frame.Terrain.Length * 80));
        var (indices, count) = frame.RenderStyle switch
        {
            MapRenderStyle.Minecraft => ((nint)cubeIndices, TerrainBlockMesh.CubeIndices.Length),
            MapRenderStyle.Roblox => ((nint)bevelIndices, TerrainBlockMesh.BeveledIndices.Length),
            _ => ((nint)gridIndices, GpuTerrainMesh.Indices.Length)
        };
        api.RenderPassEncoderSetIndexBuffer(pass, (GpuBuffer*)indices, IndexFormat.Uint16, 0, Align4((ulong)count * 2));
        api.RenderPassEncoderDrawIndexed(pass, (uint)count, (uint)frame.Terrain.Length, 0, 0, 0);
        LastDrawCalls++;
    }

    private void DrawBuildings(RenderPassEncoder* pass, bool shadow)
    {
        api.RenderPassEncoderSetPipeline(pass, shadow ? shadowBuildingPipeline : buildingPipeline);
        foreach (var draw in buildingDraws)
        {
            var mesh = draw.Resource;
            int indexCount = shadow && mesh.ShadowIndices != null ? mesh.ShadowIndexCount : mesh.IndexCount;
            if (indexCount == 0) continue;
            SetFrame(pass, shadow ? projectionGroup : commonGroup, draw.UniformOffset);
            api.RenderPassEncoderSetBindGroup(pass, 1, mesh.Group, 0, null);
            api.RenderPassEncoderSetVertexBuffer(pass, 0, mesh.Vertices, 0, (ulong)mesh.VertexBytes);
            api.RenderPassEncoderSetIndexBuffer(pass, shadow && mesh.ShadowIndices != null ? mesh.ShadowIndices : mesh.Indices,
                IndexFormat.Uint32, 0, (ulong)(shadow && mesh.ShadowIndices != null ? mesh.ShadowIndexBytes : mesh.IndexBytes));
            api.RenderPassEncoderDrawIndexed(pass, (uint)indexCount, 1, 0, 0, 0);
            LastDrawCalls++;
        }
        if (shadow) return;
        api.RenderPassEncoderSetPipeline(pass, rimPipeline);
        foreach (var draw in buildingDraws)
        {
            var mesh = draw.Resource;
            if (mesh.EdgeCount == 0) continue;
            SetFrame(pass, commonGroup, draw.UniformOffset);
            api.RenderPassEncoderSetBindGroup(pass, 1, mesh.Group, 0, null);
            api.RenderPassEncoderSetVertexBuffer(pass, 0, mesh.Edges, 0, (ulong)mesh.EdgeBytes);
            api.RenderPassEncoderDraw(pass, 4, (uint)mesh.EdgeCount, 0, 0);
            LastDrawCalls++;
        }
    }

    private void SetFrame(RenderPassEncoder* pass, BindGroup* group, uint offset)
        => api.RenderPassEncoderSetBindGroup(pass, 0, group, 1, &offset);

    private void DrawShadow(CommandEncoder* encoder, GpuMapFrame frame)
    {
        var depth = DepthAttachment(shadowView, clear: true);
        using var label = new NativeLabel("PixelMap sun shadow pass");
        var descriptor = new RenderPassDescriptor { Label = label.Pointer, DepthStencilAttachment = &depth };
        var pass = api.CommandEncoderBeginRenderPass(encoder, &descriptor);
        try { DrawTerrain(pass, frame, shadow: true); DrawBuildings(pass, shadow: true); }
        finally { EndPass(pass); }
    }

    private void ClearShadow(CommandEncoder* encoder)
    {
        var depth = DepthAttachment(shadowView, clear: true);
        using var label = new NativeLabel("PixelMap sun shadow pass");
        var descriptor = new RenderPassDescriptor { Label = label.Pointer, DepthStencilAttachment = &depth };
        EndPass(api.CommandEncoderBeginRenderPass(encoder, &descriptor));
    }

    private RenderPassEncoder* BeginScenePass(CommandEncoder* encoder, bool clear)
    {
        var color = new RenderPassColorAttachment
        {
            View = attachmentSamples > 1 ? multisampleView : sceneView,
            ResolveTarget = attachmentSamples > 1 ? sceneView : null,
            DepthSlice = uint.MaxValue,
            LoadOp = clear ? LoadOp.Clear : LoadOp.Load,
            StoreOp = StoreOp.Store,
            ClearValue = new(0, 0, 0, 0)
        };
        var depth = DepthAttachment(depthView, clear);
        using var label = new NativeLabel("PixelMap scene pass");
        var descriptor = new RenderPassDescriptor { Label = label.Pointer, ColorAttachmentCount = 1, ColorAttachments = &color, DepthStencilAttachment = &depth };
        var pass = api.CommandEncoderBeginRenderPass(encoder, &descriptor);
        Require(pass, "scene render pass");
        return pass;
    }

    private RenderPassEncoder* BeginTargetPass(CommandEncoder* encoder, TextureView* target, bool clear)
    {
        var color = new RenderPassColorAttachment
        {
            View = target, DepthSlice = uint.MaxValue, LoadOp = clear ? LoadOp.Clear : LoadOp.Load,
            StoreOp = StoreOp.Store, ClearValue = new(0, 0, 0, 1)
        };
        using var label = new NativeLabel("PixelMap presentation pass");
        var descriptor = new RenderPassDescriptor { Label = label.Pointer, ColorAttachmentCount = 1, ColorAttachments = &color };
        var pass = api.CommandEncoderBeginRenderPass(encoder, &descriptor);
        Require(pass, "presentation render pass");
        return pass;
    }

    private static RenderPassDepthStencilAttachment DepthAttachment(TextureView* view, bool clear) => new()
    {
        View = view, DepthLoadOp = clear ? LoadOp.Clear : LoadOp.Load,
        DepthStoreOp = StoreOp.Store, DepthClearValue = 1, DepthReadOnly = false,
        StencilLoadOp = LoadOp.Undefined, StencilStoreOp = StoreOp.Undefined, StencilReadOnly = true
    };
    private void EndPass(RenderPassEncoder* pass)
    {
        if (pass == null) return;
        api.RenderPassEncoderEnd(pass);
        api.RenderPassEncoderRelease(pass);
    }
    private static void Require(void* pointer, string resource)
    {
        if (pointer == null) throw new InvalidOperationException($"WebGPU could not create the {resource}.");
    }
    private static ulong Align4(ulong bytes) => (bytes + 3) & ~3UL;
    private static void Set4(Span<float> target, int row, float x, float y, float z, float w)
    {
        int i = row * 4;
        target[i] = x; target[i + 1] = y; target[i + 2] = z; target[i + 3] = w;
    }
    private static void SetColor(Span<float> target, int row, uint color, float fourth)
        => Set4(target, row, ((color >> 16) & 255) / 255f, ((color >> 8) & 255) / 255f, (color & 255) / 255f, fourth);
    private static void Write4(float[] array, ref int i, float a, float b, float c, float d)
    { array[i++] = a; array[i++] = b; array[i++] = c; array[i++] = d; }
    private readonly record struct BuildingDraw(BuildingResource Resource, uint UniformOffset);
    private sealed class TerrainResource(int layer) { internal readonly int Layer = layer; internal long Revision = -1, Generation; }
    private sealed class TextureResource(int layer) { internal readonly int Layer = layer; internal long Generation; }
    private sealed class BuildingResource
    {
        internal GpuBuffer* Vertices, Indices, Models, Edges, ShadowIndices;
        internal BindGroup* Group;
        internal long VertexBytes, IndexBytes, ModelBytes, EdgeBytes, ShadowIndexBytes, Generation, Revision = -1, FoundationRevision = -1;
        internal int IndexCount, EdgeCount, ShadowIndexCount;
        internal object? IndexSource, EdgeSource, ShadowIndexSource;
        internal long Bytes => VertexBytes + IndexBytes + ModelBytes + EdgeBytes + ShadowIndexBytes;
    }
}
