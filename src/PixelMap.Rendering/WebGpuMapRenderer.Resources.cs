using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using SkiaSharp;
using GpuBuffer = Silk.NET.WebGPU.Buffer;

namespace PixelMap.Rendering;

public sealed unsafe partial class WebGpuMapRenderer
{
    private long StaticResidentBytes
    {
        get
        {
            long bytes = (long)(uniforms.Length + instances.Length + resolveValues.Length) * sizeof(float) +
                (long)(GpuTerrainMesh.Indices.Length + TerrainBlockMesh.CubeIndices.Length + TerrainBlockMesh.BeveledIndices.Length) * sizeof(ushort) +
                7L * PixelPalette.Day.Materials.Length * 4 + (long)bitmapWidth * bitmapHeight * 4;
            foreach (var level in PixelWindowFilter.Levels) bytes += (long)level.Width * level.Height * 2;
            return bytes;
        }
    }

    private void InitializeResources()
    {
        uniformBuffer = CreateBuffer((ulong)uniforms.Length * sizeof(float), BufferUsage.Uniform | BufferUsage.CopyDst);
        resolveBuffer = CreateBuffer(64, BufferUsage.Uniform | BufferUsage.CopyDst);
        instanceBuffer = CreateBuffer((ulong)instances.Length * sizeof(float), BufferUsage.Vertex | BufferUsage.CopyDst);
        gridIndices = CreateIndexBuffer(GpuTerrainMesh.Indices);
        cubeIndices = CreateIndexBuffer(TerrainBlockMesh.CubeIndices);
        bevelIndices = CreateIndexBuffer(TerrainBlockMesh.BeveledIndices);
        nearestSampler = CreateSampler(FilterMode.Nearest);
        linearSampler = CreateSampler(FilterMode.Linear);
        roomSampler = CreateSampler(FilterMode.Linear, repeat: true, mipmapped: true);
        using var shadowSamplerLabel = new NativeLabel("PixelMap shadow sampler");
        var comparison = new SamplerDescriptor
        {
            Label = shadowSamplerLabel.Pointer,
            AddressModeU = AddressMode.ClampToEdge, AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge, MinFilter = FilterMode.Linear,
            MagFilter = FilterMode.Linear, MipmapFilter = MipmapFilterMode.Nearest,
            Compare = CompareFunction.LessEqual, LodMaxClamp = 0, MaxAnisotropy = 1
        };
        comparisonSampler = api.DeviceCreateSampler(device, &comparison);
        Require(comparisonSampler, "shadow comparison sampler");
        terrainArtwork = CreateTexture(GpuTerrainTexture.Size, GpuTerrainTexture.Size, TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst, layers: textureLayerCapacity);
        terrainArtworkView = CreateView(terrainArtwork, TextureFormat.Rgba8Unorm, TextureViewDimension.Dimension2DArray, layers: textureLayerCapacity);
        terrainHeights = CreateTexture(GpuTerrainMesh.Side, GpuTerrainMesh.Side, TextureFormat.RG32float,
            TextureUsage.TextureBinding | TextureUsage.CopyDst, layers: heightLayerCapacity);
        terrainHeightView = CreateView(terrainHeights, TextureFormat.RG32float, TextureViewDimension.Dimension2DArray, layers: heightLayerCapacity);
        paletteTexture = CreateTexture(7, PixelPalette.Day.Materials.Length, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst);
        paletteView = CreateView(paletteTexture, TextureFormat.Rgba8Unorm);
        roomTexture = CreateTexture(256, 16, TextureFormat.RG8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst,
            mipLevels: PixelWindowFilter.Levels.Length);
        roomView = CreateView(roomTexture, TextureFormat.RG8Unorm, mipLevels: PixelWindowFilter.Levels.Length);
        for (int i = 0; i < PixelWindowFilter.Levels.Length; i++)
        {
            var level = PixelWindowFilter.Levels[i];
            fixed (byte* p = level.Pixels)
                WriteTexture(roomTexture, p, (nuint)level.Pixels.Length, level.Width, level.Height, level.Width * 2, mip: i);
        }
        bitmapOverlayTexture = CreateTexture(1, 1, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst);
        bitmapOverlayView = CreateView(bitmapOverlayTexture, TextureFormat.Rgba8Unorm);
        bitmapWidth = bitmapHeight = 1;
        uint empty = 0;
        WriteTexture(bitmapOverlayTexture, &empty, 4, 1, 1, 4);
        CreateLayouts();
        backgroundModule = CreateModule(WebGpuMapShaders.Background);
        terrainModule = CreateModule(WebGpuMapShaders.Terrain);
        buildingModule = CreateModule(WebGpuMapShaders.Buildings);
        rimModule = CreateModule(WebGpuMapShaders.Rim);
        resolveModule = CreateModule(WebGpuMapShaders.Resolve);
        overlayModule = CreateModule(WebGpuMapShaders.Overlay);
        InitializeOverlayResources();
    }

    private void CreateLayouts()
    {
        var common = stackalloc BindGroupLayoutEntry[6];
        common[0] = UniformEntry(0, UniformStride, dynamic: true, ShaderStage.Vertex | ShaderStage.Fragment);
        common[1] = TextureEntry(1, TextureSampleType.Float, TextureViewDimension.Dimension2D, ShaderStage.Vertex | ShaderStage.Fragment);
        common[2] = TextureEntry(2, TextureSampleType.Float, TextureViewDimension.Dimension2D, ShaderStage.Fragment);
        common[3] = SamplerEntry(3, SamplerBindingType.Filtering);
        common[4] = TextureEntry(4, TextureSampleType.Depth, TextureViewDimension.Dimension2D, ShaderStage.Fragment);
        common[5] = SamplerEntry(5, SamplerBindingType.Comparison);
        commonLayout = CreateLayout(common, 6);
        projectionLayout = CreateLayout(common, 1);
        var terrain = stackalloc BindGroupLayoutEntry[3];
        terrain[0] = TextureEntry(0, TextureSampleType.Float, TextureViewDimension.Dimension2DArray, ShaderStage.Fragment);
        terrain[1] = TextureEntry(1, TextureSampleType.UnfilterableFloat, TextureViewDimension.Dimension2DArray, ShaderStage.Vertex);
        terrain[2] = SamplerEntry(2, SamplerBindingType.Filtering);
        terrainLayout = CreateLayout(terrain, 3);
        var model = new BindGroupLayoutEntry
        {
            Binding = 0, Visibility = ShaderStage.Vertex,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage, MinBindingSize = 32 }
        };
        modelLayout = CreateLayout(&model, 1);
        var resolve = stackalloc BindGroupLayoutEntry[5];
        resolve[0] = UniformEntry(0, 64, dynamic: false, ShaderStage.Fragment);
        for (uint i = 1; i <= 3; i++) resolve[i] = TextureEntry(i, TextureSampleType.Float, TextureViewDimension.Dimension2D, ShaderStage.Fragment);
        resolve[4] = SamplerEntry(4, SamplerBindingType.Filtering);
        resolveLayout = CreateLayout(resolve, 5);
        var over = stackalloc BindGroupLayoutEntry[3];
        over[0] = TextureEntry(0, TextureSampleType.Float, TextureViewDimension.Dimension2D, ShaderStage.Fragment);
        over[1] = SamplerEntry(1, SamplerBindingType.Filtering);
        over[2] = UniformEntry(2, 16, dynamic: false, ShaderStage.Fragment);
        overlayLayout = CreateLayout(over, 3);
        backgroundLayout = CreatePipelineLayout(commonLayout);
        sceneLayout = CreatePipelineLayout(commonLayout, terrainLayout);
        buildingLayout = CreatePipelineLayout(commonLayout, modelLayout);
        shadowTerrainLayout = CreatePipelineLayout(projectionLayout, terrainLayout);
        shadowBuildingLayout = CreatePipelineLayout(projectionLayout, modelLayout);
        resolvePipelineLayout = CreatePipelineLayout(resolveLayout);
        overlayPipelineLayout = CreatePipelineLayout(overlayLayout);
        var projection = new BindGroupEntry { Binding = 0, Buffer = uniformBuffer, Size = UniformStride };
        projectionGroup = CreateGroup(projectionLayout, &projection, 1);
        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new() { Binding = 0, TextureView = terrainArtworkView };
        entries[1] = new() { Binding = 1, TextureView = terrainHeightView };
        entries[2] = new() { Binding = 2, Sampler = nearestSampler };
        terrainNearestGroup = CreateGroup(terrainLayout, entries, 3);
        entries[2].Sampler = linearSampler;
        terrainLinearGroup = CreateGroup(terrainLayout, entries, 3);
    }

    private static BindGroupLayoutEntry UniformEntry(uint binding, ulong size, bool dynamic, ShaderStage visibility) => new()
    {
        Binding = binding, Visibility = visibility,
        Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = dynamic, MinBindingSize = size }
    };
    private static BindGroupLayoutEntry TextureEntry(uint binding, TextureSampleType type, TextureViewDimension dimension, ShaderStage visibility) => new()
    {
        Binding = binding, Visibility = visibility,
        Texture = new TextureBindingLayout { SampleType = type, ViewDimension = dimension, Multisampled = false }
    };
    private static BindGroupLayoutEntry SamplerEntry(uint binding, SamplerBindingType type) => new()
    { Binding = binding, Visibility = ShaderStage.Fragment, Sampler = new SamplerBindingLayout { Type = type } };

    private BindGroupLayout* CreateLayout(BindGroupLayoutEntry* entries, nuint count)
    {
        using var label = new NativeLabel("PixelMap bind group layout");
        var descriptor = new BindGroupLayoutDescriptor { Label = label.Pointer, EntryCount = count, Entries = entries };
        var value = api.DeviceCreateBindGroupLayout(device, &descriptor);
        Require(value, "bind group layout");
        return value;
    }
    private BindGroup* CreateGroup(BindGroupLayout* layout, BindGroupEntry* entries, nuint count)
    {
        using var label = new NativeLabel("PixelMap bind group");
        var descriptor = new BindGroupDescriptor { Label = label.Pointer, Layout = layout, EntryCount = count, Entries = entries };
        var value = api.DeviceCreateBindGroup(device, &descriptor);
        Require(value, "bind group");
        return value;
    }
    private PipelineLayout* CreatePipelineLayout(BindGroupLayout* first, BindGroupLayout* second = null)
    {
        var groups = stackalloc BindGroupLayout*[2];
        groups[0] = first;
        groups[1] = second;
        using var label = new NativeLabel("PixelMap pipeline layout");
        var descriptor = new PipelineLayoutDescriptor { Label = label.Pointer, BindGroupLayoutCount = second == null ? 1u : 2u, BindGroupLayouts = groups };
        var value = api.DeviceCreatePipelineLayout(device, &descriptor);
        Require(value, "pipeline layout");
        return value;
    }
    private ShaderModule* CreateModule(string source)
    {
        using var label = new NativeLabel("PixelMap WGSL module");
        var code = (byte*)SilkMarshal.StringToPtr(source);
        try
        {
            var wgsl = new ShaderModuleWGSLDescriptor { Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor }, Code = code };
            var descriptor = new ShaderModuleDescriptor { Label = label.Pointer, NextInChain = &wgsl.Chain };
            var module = api.DeviceCreateShaderModule(device, &descriptor);
            Require(module, "WGSL module");
            return module;
        }
        finally { SilkMarshal.Free((nint)code); }
    }

    private void EnsureShadow(int size)
    {
        if (shadowSize == size) return;
        Release(ref commonGroup);
        Release(ref shadowView);
        Release(ref shadowTexture);
        shadowSize = 0;
        retainedShadowFrame = null;
        shadowTexture = CreateTexture(size, size, TextureFormat.Depth32float, TextureUsage.RenderAttachment | TextureUsage.TextureBinding);
        shadowView = CreateView(shadowTexture, TextureFormat.Depth32float);
        var entries = stackalloc BindGroupEntry[6];
        entries[0] = new() { Binding = 0, Buffer = uniformBuffer, Size = UniformStride };
        entries[1] = new() { Binding = 1, TextureView = paletteView };
        entries[2] = new() { Binding = 2, TextureView = roomView };
        entries[3] = new() { Binding = 3, Sampler = roomSampler };
        entries[4] = new() { Binding = 4, TextureView = shadowView };
        entries[5] = new() { Binding = 5, Sampler = comparisonSampler };
        commonGroup = CreateGroup(commonLayout, entries, 6);
        shadowSize = size;
    }

    private void EnsureAttachments(int width, int height, int samples)
    {
        if (attachmentWidth == width && attachmentHeight == height && attachmentSamples == samples) return;
        ReleaseAttachments();
        sceneTexture = CreateTexture(width, height, TextureFormat.Rgba16float,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc);
        sceneView = CreateView(sceneTexture, TextureFormat.Rgba16float);
        photographTexture = CreateTexture(width, height, TextureFormat.Rgba16float,
            TextureUsage.TextureBinding | TextureUsage.CopyDst);
        photographView = CreateView(photographTexture, TextureFormat.Rgba16float);
        depthTexture = CreateTexture(width, height, TextureFormat.Depth24Plus, TextureUsage.RenderAttachment, samples: samples);
        depthView = CreateView(depthTexture, TextureFormat.Depth24Plus);
        if (samples > 1)
        {
            multisampleTexture = CreateTexture(width, height, TextureFormat.Rgba16float, TextureUsage.RenderAttachment, samples: samples);
            multisampleView = CreateView(multisampleTexture, TextureFormat.Rgba16float);
        }
        attachmentWidth = width;
        attachmentHeight = height;
        attachmentSamples = samples;
        AttachmentBytes = (long)width * height * (samples > 1 ? samples * 12L + 16 : 20);
        RebuildResolveGroup();
    }
    private void ReleaseAttachments()
    {
        Release(ref resolveGroup);
        Release(ref multisampleView); Release(ref multisampleTexture);
        Release(ref sceneView); Release(ref sceneTexture);
        Release(ref depthView); Release(ref depthTexture);
        Release(ref photographView); Release(ref photographTexture);
        attachmentWidth = attachmentHeight = attachmentSamples = 0;
        AttachmentBytes = 0;
    }
    private void RebuildResolveGroup()
    {
        Release(ref resolveGroup);
        if (sceneView == null) return;
        var entries = stackalloc BindGroupEntry[5];
        entries[0] = new() { Binding = 0, Buffer = resolveBuffer, Size = 64 };
        entries[1] = new() { Binding = 1, TextureView = sceneView };
        entries[2] = new() { Binding = 2, TextureView = photographView };
        entries[3] = new() { Binding = 3, TextureView = bitmapOverlayView };
        entries[4] = new() { Binding = 4, Sampler = linearSampler };
        resolveGroup = CreateGroup(resolveLayout, entries, 5);
    }
    private bool TargetIsSrgb => pipelineTargetFormat is TextureFormat.Rgba8UnormSrgb or TextureFormat.Bgra8UnormSrgb;

    private void UpdateResolve(GpuMapFrame frame, uint targetWidth, uint targetHeight, bool smooth, bool hasBitmapOverlay)
    {
        float photographOpacity = frame.PreservePhotograph ? (float)(209d / 255 * Math.Max(0, 1 - frame.Camera.Pitch / 30)) : 0;
        Set4(resolveValues, 0, attachmentWidth, attachmentHeight, 0, 0);
        Set4(resolveValues, 1, targetWidth, targetHeight, frame.Width, frame.Height);
        Set4(resolveValues, 2, smooth ? 1 : 0, frame.PixelSize, photographOpacity, hasBitmapOverlay ? 1 : 0);
        Set4(resolveValues, 3, smooth ? options.Bloom : 0, attachmentSamples == 1 ? 1 : 0, options.Exposure, TargetIsSrgb ? 1 : 0);
        Upload(resolveBuffer, resolveValues.AsSpan());
    }

    private TerrainResource GetHeight(GpuTerrainMesh source)
    {
        if (!terrainCache.TryGetValue(source.ResourceKey, out var resource))
        {
            if (freeHeights.Count == 0)
            {
                var victim = terrainCache.Where(p => p.Value.Generation != scenePreparation.Generation).MinBy(p => p.Value.Generation);
                if (victim.Key is null) throw new NotSupportedException("The frame exceeds the retained terrain layer budget.");
                freeHeights.Push(victim.Value.Layer);
                terrainCache.Remove(victim.Key);
            }
            resource = new(freeHeights.Pop());
            terrainCache.Add(source.ResourceKey, resource);
        }
        resource.Generation = scenePreparation.Generation;
        if (resource.Revision != source.Revision)
        {
            heightsUpload.AsSpan().Clear();
            if (source.HeightLight is { } blocks) blocks.CopyTo(heightsUpload, 0);
            else for (int i = 0; i < GpuTerrainMesh.Side * GpuTerrainMesh.Side; i++)
            {
                heightsUpload[i * 2] = source.Vertices[i * GpuTerrainMesh.VertexStride + 2];
                heightsUpload[i * 2 + 1] = source.Vertices[i * GpuTerrainMesh.VertexStride + 3];
            }
            fixed (float* p = heightsUpload)
                WriteTexture(terrainHeights, p, (nuint)heightsUpload.Length * sizeof(float),
                    GpuTerrainMesh.Side, GpuTerrainMesh.Side, GpuTerrainMesh.Side * 8, layer: resource.Layer);
            resource.Revision = source.Revision;
        }
        return resource;
    }
    private TextureResource GetTexture(GpuTerrainTexture source)
    {
        if (!textureCache.TryGetValue(source, out var resource))
        {
            if (freeTextures.Count == 0)
            {
                var victim = textureCache.Where(p => p.Value.Generation != scenePreparation.Generation).MinBy(p => p.Value.Generation);
                if (victim.Key is null) throw new NotSupportedException("The frame exceeds the retained artwork layer budget.");
                freeTextures.Push(victim.Value.Layer);
                textureCache.Remove(victim.Key);
            }
            resource = new(freeTextures.Pop());
            textureCache.Add(source, resource);
            fixed (uint* p = source.Texels)
                WriteTexture(terrainArtwork, p, (nuint)source.Texels.Length * sizeof(uint), GpuTerrainTexture.Size,
                    GpuTerrainTexture.Size, GpuTerrainTexture.Size * 4, layer: resource.Layer);
        }
        resource.Generation = scenePreparation.Generation;
        return resource;
    }
    private BuildingResource GetBuilding(GpuBuildingDraw draw)
    {
        var source = draw.Mesh;
        if (!buildingCache.TryGetValue(source.ResourceKey, out var resource))
        {
            resource = new();
            buildingCache.Add(source.ResourceKey, resource);
        }
        resource.Generation = scenePreparation.Generation;
        if (resource.Revision != source.Revision)
        {
            UploadMesh(resource, ref resource.Vertices, ref resource.VertexBytes, source.Vertices.AsSpan(), BufferUsage.Vertex);
            resource.Revision = source.Revision;
        }
        var indices = draw.Selection?.Indices ?? source.Indices;
        if (!ReferenceEquals(resource.IndexSource, indices))
        {
            UploadMesh(resource, ref resource.Indices, ref resource.IndexBytes, indices.AsSpan(), BufferUsage.Index);
            resource.IndexSource = indices;
            resource.IndexCount = indices.Length;
        }
        // Viewer frustum/cap rejection must not remove offscreen shadow casters.
        // Keep a complete index stream only while the main stream is culled.
        if ((!ReferenceEquals(indices, source.Indices) || resource.ShadowIndices != null) &&
            !ReferenceEquals(resource.ShadowIndexSource, source.Indices))
        {
            UploadMesh(resource, ref resource.ShadowIndices, ref resource.ShadowIndexBytes,
                source.Indices.AsSpan(), BufferUsage.Index);
            resource.ShadowIndexSource = source.Indices;
            resource.ShadowIndexCount = source.Indices.Length;
        }
        var edges = draw.Selection?.RoofEdges ?? source.RoofEdges;
        if (!ReferenceEquals(resource.EdgeSource, edges))
        {
            UploadMesh(resource, ref resource.Edges, ref resource.EdgeBytes, edges.AsSpan(), BufferUsage.Vertex);
            resource.EdgeSource = edges;
            resource.EdgeCount = edges.Length / (GpuBuildingMesh.EdgeStride * 2);
        }
        if (resource.FoundationRevision != source.FoundationRevision || resource.Models == null)
        {
            bool resized = UploadMesh(resource, ref resource.Models, ref resource.ModelBytes, source.Models.AsSpan(), BufferUsage.Storage);
            resource.FoundationRevision = source.FoundationRevision;
            if (resized || resource.Group == null)
            {
                Release(ref resource.Group);
                var entry = new BindGroupEntry { Binding = 0, Buffer = resource.Models, Size = (ulong)resource.ModelBytes };
                resource.Group = CreateGroup(modelLayout, &entry, 1);
            }
        }
        return resource;
    }
    private bool UploadMesh<T>(BuildingResource owner, ref GpuBuffer* buffer, ref long capacity, ReadOnlySpan<T> values,
        BufferUsage usage) where T : unmanaged
    {
        if (values.IsEmpty) return false;
        long bytes = (long)values.Length * sizeof(T);
        bool resized = capacity < bytes;
        if (resized)
        {
            EnsureMeshBudget(owner, bytes - capacity);
            var next = CreateBuffer((ulong)bytes, usage | BufferUsage.CopyDst);
            Release(ref buffer);
            buffer = next;
            meshBytes += bytes - capacity;
            capacity = bytes;
        }
        Upload(buffer, values);
        return resized;
    }
    private void EnsureMeshBudget(BuildingResource owner, long additional)
    {
        while (meshBytes + additional > CacheBudget)
        {
            var victim = buildingCache.Where(p => !ReferenceEquals(p.Value, owner) && p.Value.Generation != scenePreparation.Generation)
                .MinBy(p => p.Value.Generation);
            if (victim.Key is null) throw new NotSupportedException("The visible building working set exceeds the 128 MiB GPU mesh budget.");
            DestroyBuilding(victim.Value);
            buildingCache.Remove(victim.Key);
        }
    }
    private void TrimCaches()
    {
        // Pin every current draw. Only stale resources can be evicted; commands in
        // the submitted frame retain their native references until GPU completion.
        foreach (var key in buildingCache.Where(p => scenePreparation.IsExpired(p.Value.Generation)).Select(p => p.Key).ToArray())
        { DestroyBuilding(buildingCache[key]); buildingCache.Remove(key); }
    }
    private void DestroyBuilding(BuildingResource resource)
    {
        meshBytes -= resource.Bytes;
        Release(ref resource.Group);
        Release(ref resource.Vertices); Release(ref resource.Indices);
        Release(ref resource.Models); Release(ref resource.Edges); Release(ref resource.ShadowIndices);
    }
    private void UploadPalette(PixelPalette palette)
    {
        if (ReferenceEquals(uploadedPalette, palette)) return;
        Span<uint> colors = stackalloc uint[palette.Materials.Length * 7];
        int i = 0;
        foreach (var m in palette.Materials)
        { colors[i++] = m.Roof; colors[i++] = m.LightWall; colors[i++] = m.DarkWall; colors[i++] = m.Rim; colors[i++] = m.Trim; colors[i++] = m.Glass; colors[i++] = m.Lamp; }
        fixed (uint* p = colors)
            WriteTexture(paletteTexture, p, (nuint)colors.Length * sizeof(uint), 7, palette.Materials.Length, 7 * 4);
        uploadedPalette = palette;
    }
    private void UploadBitmapOverlay(SKBitmap bitmap)
    {
        if (bitmap.Width > maximumTextureSize || bitmap.Height > maximumTextureSize || bitmap.Width <= 0 || bitmap.Height <= 0)
            throw new NotSupportedException("The bitmap overlay exceeds the supported texture size.");
        if (bitmap.Width != bitmapWidth || bitmap.Height != bitmapHeight)
        {
            Release(ref resolveGroup);
            Release(ref bitmapOverlayView); Release(ref bitmapOverlayTexture);
            bitmapOverlayTexture = CreateTexture(bitmap.Width, bitmap.Height, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst);
            bitmapOverlayView = CreateView(bitmapOverlayTexture, TextureFormat.Rgba8Unorm);
            bitmapWidth = bitmap.Width; bitmapHeight = bitmap.Height;
            RebuildResolveGroup();
        }
        WriteTexture(bitmapOverlayTexture, (void*)bitmap.GetPixels(), (nuint)bitmap.ByteCount, bitmap.Width, bitmap.Height, bitmap.RowBytes);
    }

    private GpuBuffer* CreateBuffer(ulong size, BufferUsage usage)
    {
        using var label = new NativeLabel($"PixelMap buffer {usage}");
        ulong aligned = Math.Max(4, Align4(size));
        if (aligned > maximumBufferSize || (usage & BufferUsage.Storage) != 0 && aligned > maximumStorageBindingSize)
            throw new NotSupportedException("The map buffer exceeds the shared WebGPU device's binding or allocation limit.");
        var descriptor = new BufferDescriptor { Label = label.Pointer, Size = aligned, Usage = usage, MappedAtCreation = false };
        var buffer = api.DeviceCreateBuffer(device, &descriptor);
        Require(buffer, "GPU buffer");
        return buffer;
    }
    private GpuBuffer* CreateIndexBuffer(ReadOnlySpan<ushort> indices)
    {
        var buffer = CreateBuffer((ulong)indices.Length * sizeof(ushort), BufferUsage.Index | BufferUsage.CopyDst);
        try { Upload(buffer, indices); return buffer; }
        catch { Release(ref buffer); throw; }
    }
    private void Upload<T>(GpuBuffer* buffer, ReadOnlySpan<T> values) where T : unmanaged
    {
        if (values.IsEmpty) return;
        nuint bytes = checked((nuint)values.Length * (nuint)sizeof(T));
        if ((bytes & 3) != 0) throw new ArgumentException("GPU writes must have a four-byte-aligned size.");
        fixed (T* p = values) api.QueueWriteBuffer(queue, buffer, 0, p, bytes);
        LastUploadBytes += (long)bytes;
    }
    private Texture* CreateTexture(int width, int height, TextureFormat format, TextureUsage usage,
        int layers = 1, int samples = 1, int mipLevels = 1)
    {
        if (width <= 0 || height <= 0 || width > maximumTextureSize || height > maximumTextureSize)
            throw new NotSupportedException("The map texture exceeds the shared WebGPU device's dimensions.");
        using var label = new NativeLabel($"PixelMap texture {format} {width}x{height} ({samples}x)");
        var descriptor = new TextureDescriptor
        {
            Label = label.Pointer,
            Dimension = TextureDimension.Dimension2D, Size = new Extent3D((uint)width, (uint)height, (uint)layers),
            Format = format, Usage = usage, SampleCount = (uint)samples, MipLevelCount = (uint)mipLevels
        };
        var texture = api.DeviceCreateTexture(device, &descriptor);
        Require(texture, "GPU texture");
        return texture;
    }
    private TextureView* CreateView(Texture* texture, TextureFormat format,
        TextureViewDimension dimension = TextureViewDimension.Dimension2D, int layers = 1, int mipLevels = 1)
    {
        using var label = new NativeLabel($"PixelMap view {format}");
        var descriptor = new TextureViewDescriptor
        {
            Label = label.Pointer,
            Format = format, Dimension = dimension, Aspect = TextureAspect.All,
            BaseMipLevel = 0, MipLevelCount = (uint)mipLevels, BaseArrayLayer = 0, ArrayLayerCount = (uint)layers
        };
        var view = api.TextureCreateView(texture, &descriptor);
        Require(view, "GPU texture view");
        return view;
    }
    private Sampler* CreateSampler(FilterMode filter, bool repeat = false, bool mipmapped = false)
    {
        using var label = new NativeLabel($"PixelMap sampler {filter}");
        var descriptor = new SamplerDescriptor
        {
            Label = label.Pointer,
            AddressModeU = repeat ? AddressMode.Repeat : AddressMode.ClampToEdge,
            AddressModeV = repeat ? AddressMode.Repeat : AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge, MinFilter = filter, MagFilter = filter,
            MipmapFilter = mipmapped ? MipmapFilterMode.Linear : MipmapFilterMode.Nearest,
            LodMaxClamp = mipmapped ? 32 : 0, MaxAnisotropy = 1
        };
        var sampler = api.DeviceCreateSampler(device, &descriptor);
        Require(sampler, "GPU sampler");
        return sampler;
    }
    private void WriteTexture(Texture* texture, void* pixels, nuint bytes, int width, int height, int rowBytes,
        int layer = 0, int mip = 0, int x = 0, int y = 0)
    {
        var destination = new ImageCopyTexture
        { Texture = texture, MipLevel = (uint)mip, Origin = new Origin3D((uint)x, (uint)y, (uint)layer), Aspect = TextureAspect.All };
        var layout = new TextureDataLayout { BytesPerRow = (uint)rowBytes, RowsPerImage = (uint)height };
        var extent = new Extent3D((uint)width, (uint)height, 1);
        api.QueueWriteTexture(queue, &destination, pixels, bytes, &layout, &extent);
        LastUploadBytes += (long)bytes;
    }

    private readonly ref struct NativeLabel(string value)
    {
        internal readonly byte* Pointer = (byte*)SilkMarshal.StringToPtr(value);
        public void Dispose() => SilkMarshal.Free((nint)Pointer);
    }

    // Current-frame resources stay pinned until submission. Destroy retires owned
    // allocations deterministically in browsers; previously submitted work remains valid.
    private void Release(ref GpuBuffer* value)
    {
        if (value == null) return;
        var owned = value;
        value = null;
        try { api.BufferDestroy(owned); }
        finally { api.BufferRelease(owned); }
    }
    private void Release(ref Texture* value)
    {
        if (value == null) return;
        var owned = value;
        value = null;
        try { api.TextureDestroy(owned); }
        finally { api.TextureRelease(owned); }
    }
    private void Release(ref TextureView* value) { if (value == null) return; api.TextureViewRelease(value); value = null; }
    private void Release(ref Sampler* value) { if (value == null) return; api.SamplerRelease(value); value = null; }
    private void Release(ref BindGroup* value) { if (value == null) return; api.BindGroupRelease(value); value = null; }
    private void Release(ref BindGroupLayout* value) { if (value == null) return; api.BindGroupLayoutRelease(value); value = null; }
    private void Release(ref PipelineLayout* value) { if (value == null) return; api.PipelineLayoutRelease(value); value = null; }
    private void Release(ref ShaderModule* value) { if (value == null) return; api.ShaderModuleRelease(value); value = null; }
    private void Release(ref RenderPipeline* value) { if (value == null) return; api.RenderPipelineRelease(value); value = null; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        scenePreparation.Invalidate();
        buildingDraws.Clear();
        retainedShadowFrame = null;
        ReleasePipelines(); ReleaseAttachments(); ReleaseOverlayResources();
        foreach (var resource in buildingCache.Values) DestroyBuilding(resource);
        buildingCache.Clear(); terrainCache.Clear(); textureCache.Clear();
        Release(ref commonGroup); Release(ref projectionGroup); Release(ref terrainNearestGroup); Release(ref terrainLinearGroup);
        Release(ref backgroundLayout); Release(ref sceneLayout); Release(ref buildingLayout); Release(ref shadowTerrainLayout); Release(ref shadowBuildingLayout);
        Release(ref resolvePipelineLayout); Release(ref overlayPipelineLayout);
        Release(ref commonLayout); Release(ref projectionLayout); Release(ref terrainLayout); Release(ref modelLayout);
        Release(ref resolveLayout); Release(ref overlayLayout);
        Release(ref backgroundModule); Release(ref terrainModule); Release(ref buildingModule);
        Release(ref rimModule); Release(ref resolveModule); Release(ref overlayModule);
        Release(ref terrainArtworkView); Release(ref terrainHeightView); Release(ref paletteView); Release(ref roomView);
        Release(ref shadowView); Release(ref bitmapOverlayView);
        Release(ref terrainArtwork); Release(ref terrainHeights); Release(ref paletteTexture); Release(ref roomTexture);
        Release(ref shadowTexture); Release(ref bitmapOverlayTexture);
        Release(ref nearestSampler); Release(ref linearSampler); Release(ref roomSampler); Release(ref comparisonSampler);
        Release(ref uniformBuffer); Release(ref resolveBuffer); Release(ref instanceBuffer);
        Release(ref gridIndices); Release(ref cubeIndices); Release(ref bevelIndices);
        // Never release the borrowed device, queue, API, or caller's target view.
    }
}
