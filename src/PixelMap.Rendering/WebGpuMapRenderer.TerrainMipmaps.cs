using Silk.NET.WebGPU;
using GpuBuffer = Silk.NET.WebGPU.Buffer;

namespace PixelMap.Rendering;

public sealed unsafe partial class WebGpuMapRenderer
{
    private TextureView* terrainFilteredView;
    private Sampler* terrainAnisotropicSampler;
    private WebGpuTerrainMipmaps? terrainMipmaps;
    private Texture* terrainMipScratch;
    private TextureView* terrainMipScratchView;
    private GpuBuffer* terrainMipLayers;
    private BindGroupLayout* terrainMipLayout;
    private PipelineLayout* terrainMipPipelineLayout;
    private ShaderModule* terrainMipModule;
    private ComputePipeline* terrainMipPipeline;
    private readonly nint[] terrainMipViews = new nint[WebGpuTerrainMipmaps.LevelCount - 1];
    private readonly nint[] terrainMipGroups = new nint[WebGpuTerrainMipmaps.LevelCount - 1];

    private long TerrainArtworkResidentBytes => WebGpuTerrainMipmaps.ArtworkBytes(textureLayerCapacity) +
        (terrainMipScratch == null ? 0 : WebGpuTerrainMipmaps.ScratchBytes(textureLayerCapacity)) +
        (terrainMipLayers == null ? 0 : (long)textureLayerCapacity * sizeof(uint));

    private void InitializeTerrainArtwork()
    {
        terrainMipmaps = new(textureLayerCapacity);
        using var label = new NativeLabel("PixelMap terrain artwork");
        var srgb = TextureFormat.Rgba8UnormSrgb;
        var descriptor = new TextureDescriptor
        {
            Label = label.Pointer, Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D(GpuTerrainTexture.Size, GpuTerrainTexture.Size, (uint)textureLayerCapacity),
            Format = TextureFormat.Rgba8Unorm, Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            SampleCount = 1, MipLevelCount = (uint)WebGpuTerrainMipmaps.LevelCount,
            ViewFormatCount = 1, ViewFormats = &srgb
        };
        terrainArtwork = api.DeviceCreateTexture(device, &descriptor);
        Require(terrainArtwork, "terrain artwork array");
        // The art styles retain the original encoded mip-zero bytes and nearest
        // sampler. Only smooth styles bind the sRGB view and its complete chain.
        terrainArtworkView = CreateTerrainMipView(terrainArtwork, TextureFormat.Rgba8Unorm, 0, 1,
            "PixelMap terrain pixel mip zero");
        terrainFilteredView = CreateTerrainMipView(terrainArtwork, srgb, 0, WebGpuTerrainMipmaps.LevelCount,
            "PixelMap terrain filtered sRGB chain");
        using var samplerLabel = new NativeLabel("PixelMap terrain anisotropic sampler");
        var sampler = new SamplerDescriptor
        {
            Label = samplerLabel.Pointer,
            AddressModeU = AddressMode.ClampToEdge, AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
            MinFilter = FilterMode.Linear, MagFilter = FilterMode.Linear, MipmapFilter = MipmapFilterMode.Linear,
            LodMinClamp = 0, LodMaxClamp = WebGpuTerrainMipmaps.LevelCount - 1,
            MaxAnisotropy = WebGpuTerrainMipmaps.Anisotropy
        };
        terrainAnisotropicSampler = api.DeviceCreateSampler(device, &sampler);
        Require(terrainAnisotropicSampler, "terrain anisotropic sampler");
    }

    private TextureView* CreateTerrainMipView(Texture* texture, TextureFormat format, int first, int levels, string name)
    {
        using var label = new NativeLabel(name);
        var descriptor = new TextureViewDescriptor
        {
            Label = label.Pointer, Format = format, Dimension = TextureViewDimension.Dimension2DArray,
            Aspect = TextureAspect.All, BaseMipLevel = (uint)first, MipLevelCount = (uint)levels,
            BaseArrayLayer = 0, ArrayLayerCount = (uint)textureLayerCapacity
        };
        var view = api.TextureCreateView(texture, &descriptor);
        Require(view, "terrain mip array view");
        return view;
    }

    private void EnsureTerrainMipResources()
    {
        if (terrainMipPipeline != null) return;
        try
        {
            // Silk.NET 2.23's public TextureViewDescriptor has no view-usage
            // override. Giving the sRGB artwork StorageBinding would make its
            // sampling view invalid. A six-MiB scratch array keeps both usages
            // legal and is reused for every level, only when smooth work starts.
            terrainMipScratch = CreateTexture(WebGpuTerrainMipmaps.ScratchSize, WebGpuTerrainMipmaps.ScratchSize,
                TextureFormat.Rgba8Unorm, TextureUsage.StorageBinding | TextureUsage.CopySrc,
                layers: textureLayerCapacity, name: "PixelMap terrain mip scratch");
            terrainMipScratchView = CreateTerrainMipView(terrainMipScratch, TextureFormat.Rgba8Unorm, 0, 1,
                "PixelMap terrain mip scratch storage");
            terrainMipLayers = CreateBuffer((ulong)textureLayerCapacity * sizeof(uint), BufferUsage.Storage | BufferUsage.CopyDst);
            var layout = stackalloc BindGroupLayoutEntry[3];
            layout[0] = TextureEntry(0, TextureSampleType.Float, TextureViewDimension.Dimension2DArray, ShaderStage.Compute);
            layout[1] = new()
            {
                Binding = 1, Visibility = ShaderStage.Compute,
                StorageTexture = new StorageTextureBindingLayout
                {
                    Access = StorageTextureAccess.WriteOnly, Format = TextureFormat.Rgba8Unorm,
                    ViewDimension = TextureViewDimension.Dimension2DArray
                }
            };
            layout[2] = new()
            {
                Binding = 2, Visibility = ShaderStage.Compute,
                Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage, MinBindingSize = sizeof(uint) }
            };
            terrainMipLayout = CreateLayout(layout, 3);
            terrainMipPipelineLayout = CreatePipelineLayout(terrainMipLayout);
            terrainMipModule = CreateModule(WebGpuTerrainMipShaders.Reduce);
            var entries = stackalloc BindGroupEntry[3];
            entries[1] = new() { Binding = 1, TextureView = terrainMipScratchView };
            entries[2] = new() { Binding = 2, Buffer = terrainMipLayers, Size = (ulong)textureLayerCapacity * sizeof(uint) };
            for (int level = 0; level < terrainMipViews.Length; level++)
            {
                var view = CreateTerrainMipView(terrainArtwork, TextureFormat.Rgba8UnormSrgb, level, 1,
                    $"PixelMap terrain mip source {level}");
                terrainMipViews[level] = (nint)view;
                entries[0] = new() { Binding = 0, TextureView = view };
                terrainMipGroups[level] = (nint)CreateGroup(terrainMipLayout, entries, 3);
            }
            using var label = new NativeLabel("PixelMap terrain linear mip reduction");
            using var entry = new NativeLabel("reduce");
            var pipeline = new ComputePipelineDescriptor
            {
                Label = label.Pointer, Layout = terrainMipPipelineLayout,
                Compute = new ProgrammableStageDescriptor { Module = terrainMipModule, EntryPoint = entry.Pointer }
            };
            terrainMipPipeline = api.DeviceCreateComputePipeline(device, &pipeline);
            Require(terrainMipPipeline, "terrain mip compute pipeline");
        }
        catch { ReleaseTerrainMipResources(); throw; }
    }

    private void GenerateTerrainMipmaps(CommandEncoder* encoder, bool smooth)
    {
        terrainMipmaps!.Prepare(smooth);
        var layers = terrainMipmaps.PreparedLayers;
        if (layers.IsEmpty) return;
        EnsureTerrainMipResources();
        Upload(terrainMipLayers, layers);
        for (int level = 1; level < WebGpuTerrainMipmaps.LevelCount; level++)
        {
            int size = GpuTerrainTexture.Size >> level;
            using var label = new NativeLabel($"PixelMap terrain mip {level}");
            var descriptor = new ComputePassDescriptor { Label = label.Pointer };
            var pass = api.CommandEncoderBeginComputePass(encoder, &descriptor);
            Require(pass, "terrain mip compute pass");
            try
            {
                api.ComputePassEncoderSetPipeline(pass, terrainMipPipeline);
                api.ComputePassEncoderSetBindGroup(pass, 0, (BindGroup*)terrainMipGroups[level - 1], 0, null);
                uint groups = (uint)((size + WebGpuTerrainMipmaps.WorkgroupSize - 1) / WebGpuTerrainMipmaps.WorkgroupSize);
                // Z dispatches exactly the prepared count, never the buffer's
                // retained capacity. Shader guards cover the rounded XY groups.
                api.ComputePassEncoderDispatchWorkgroups(pass, groups, groups, (uint)layers.Length);
            }
            finally
            {
                try { api.ComputePassEncoderEnd(pass); }
                finally { api.ComputePassEncoderRelease(pass); }
            }
            foreach (uint layer in layers)
            {
                var source = new ImageCopyTexture
                { Texture = terrainMipScratch, Aspect = TextureAspect.All, Origin = new Origin3D(0, 0, layer) };
                var destination = new ImageCopyTexture
                { Texture = terrainArtwork, MipLevel = (uint)level, Aspect = TextureAspect.All, Origin = new Origin3D(0, 0, layer) };
                var extent = new Extent3D((uint)size, (uint)size, 1);
                api.CommandEncoderCopyTextureToTexture(encoder, &source, &destination, &extent);
            }
        }
    }

    private void ReleaseTerrainMipResources()
    {
        for (int i = 0; i < terrainMipGroups.Length; i++)
        {
            var group = (BindGroup*)terrainMipGroups[i]; Release(ref group); terrainMipGroups[i] = 0;
            var view = (TextureView*)terrainMipViews[i]; Release(ref view); terrainMipViews[i] = 0;
        }
        if (terrainMipPipeline != null) { api.ComputePipelineRelease(terrainMipPipeline); terrainMipPipeline = null; }
        Release(ref terrainMipPipelineLayout); Release(ref terrainMipLayout); Release(ref terrainMipModule);
        Release(ref terrainMipLayers); Release(ref terrainMipScratchView); Release(ref terrainMipScratch);
    }

    private void ReleaseTerrainArtworkResources()
    {
        ReleaseTerrainMipResources();
        Release(ref terrainFilteredView); Release(ref terrainAnisotropicSampler);
        terrainMipmaps = null;
    }
}
