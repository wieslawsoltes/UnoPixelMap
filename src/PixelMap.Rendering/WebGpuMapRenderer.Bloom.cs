using Silk.NET.WebGPU;

namespace PixelMap.Rendering;

public sealed unsafe partial class WebGpuMapRenderer
{
    private readonly nint[] bloomDownTextures = new nint[WebGpuBloomLayout.MaximumLevels];
    private readonly nint[] bloomDownViews = new nint[WebGpuBloomLayout.MaximumLevels];
    private readonly nint[] bloomDownGroups = new nint[WebGpuBloomLayout.MaximumLevels];
    private readonly nint[] bloomUpTextures = new nint[WebGpuBloomLayout.MaximumLevels - 1];
    private readonly nint[] bloomUpViews = new nint[WebGpuBloomLayout.MaximumLevels - 1];
    private readonly nint[] bloomUpGroups = new nint[WebGpuBloomLayout.MaximumLevels - 1];
    private BindGroupLayout* bloomLayout;
    private PipelineLayout* bloomPipelineLayout;
    private ShaderModule* bloomPrefilterModule, bloomDownModule, bloomUpModule;
    private RenderPipeline* bloomPrefilterPipeline, bloomDownPipeline, bloomUpPipeline;
    private int bloomLevelCount;

    private TextureView* BloomResolveView => bloomLevelCount switch
    {
        0 => sceneView,
        1 => (TextureView*)bloomDownViews[0],
        _ => (TextureView*)bloomUpViews[0]
    };

    private void EnsureBloomResources()
    {
        if (bloomUpPipeline != null) return;
        try
        {
            var entries = stackalloc BindGroupLayoutEntry[4];
            entries[0] = TextureEntry(0, TextureSampleType.Float, TextureViewDimension.Dimension2D, ShaderStage.Fragment);
            entries[1] = TextureEntry(1, TextureSampleType.Float, TextureViewDimension.Dimension2D, ShaderStage.Fragment);
            entries[2] = SamplerEntry(2, SamplerBindingType.Filtering);
            entries[3] = UniformEntry(3, 64, dynamic: false, ShaderStage.Fragment);
            bloomLayout = CreateLayout(entries, 4);
            bloomPipelineLayout = CreatePipelineLayout(bloomLayout);
            bloomPrefilterModule = CreateModule(WebGpuBloomShaders.Prefilter);
            bloomDownModule = CreateModule(WebGpuBloomShaders.Downsample);
            bloomUpModule = CreateModule(WebGpuBloomShaders.Upsample);
            bloomPrefilterPipeline = CreatePipeline("bloom prefilter", bloomPrefilterModule, bloomPipelineLayout, null, TextureFormat.Rgba16float, 1);
            bloomDownPipeline = CreatePipeline("bloom downsample", bloomDownModule, bloomPipelineLayout, null, TextureFormat.Rgba16float, 1);
            bloomUpPipeline = CreatePipeline("bloom upsample", bloomUpModule, bloomPipelineLayout, null, TextureFormat.Rgba16float, 1);
        }
        catch
        {
            // Unlike constructor initialization, a failed quality transition can
            // be retried by the retained renderer. Do not keep a partial cache.
            ReleaseBloomResources();
            throw;
        }
    }

    private void CreateBloomAttachments(int levels)
    {
        bloomLevelCount = levels;
        if (levels == 0) return;
        // Pixel/Minecraft/Balanced-only sessions never compile bloom pipelines.
        // Once created these fixed-format pipelines survive extent/quality changes.
        EnsureBloomResources();
        int width = attachmentWidth, height = attachmentHeight;
        for (int i = 0; i < levels; i++)
        {
            (width, height) = WebGpuBloomLayout.Next(width, height);
            var down = CreateTexture(width, height, TextureFormat.Rgba16float,
                TextureUsage.RenderAttachment | TextureUsage.TextureBinding, name: $"PixelMap bloom down {i}");
            bloomDownTextures[i] = (nint)down;
            bloomDownViews[i] = (nint)CreateView(down, TextureFormat.Rgba16float, name: $"PixelMap bloom down {i}");
            if (i + 1 < levels)
            {
                var up = CreateTexture(width, height, TextureFormat.Rgba16float,
                    TextureUsage.RenderAttachment | TextureUsage.TextureBinding, name: $"PixelMap bloom up {i}");
                bloomUpTextures[i] = (nint)up;
                bloomUpViews[i] = (nint)CreateView(up, TextureFormat.Rgba16float, name: $"PixelMap bloom up {i}");
            }
        }
        for (int i = 0; i < levels; i++)
        {
            var source = i == 0 ? sceneView : (TextureView*)bloomDownViews[i - 1];
            var detail = i == 0 && photographView != null ? photographView : source;
            bloomDownGroups[i] = (nint)CreateBloomGroup(source, detail);
            if (i + 1 < levels)
            {
                source = i + 2 == levels ? (TextureView*)bloomDownViews[i + 1] : (TextureView*)bloomUpViews[i + 1];
                bloomUpGroups[i] = (nint)CreateBloomGroup(source, (TextureView*)bloomDownViews[i]);
            }
        }
    }

    private BindGroup* CreateBloomGroup(TextureView* source, TextureView* detail)
    {
        var entries = stackalloc BindGroupEntry[4];
        entries[0] = new() { Binding = 0, TextureView = source };
        entries[1] = new() { Binding = 1, TextureView = detail };
        entries[2] = new() { Binding = 2, Sampler = linearSampler };
        entries[3] = new() { Binding = 3, Buffer = resolveBuffer, Size = 64 };
        return CreateGroup(bloomLayout, entries, 4);
    }

    private void DrawBloom(CommandEncoder* encoder)
    {
        for (int i = 0; i < bloomLevelCount; i++)
            DrawBloomPass(encoder, (TextureView*)bloomDownViews[i], (BindGroup*)bloomDownGroups[i],
                i == 0 ? bloomPrefilterPipeline : bloomDownPipeline, $"PixelMap bloom down {i}");
        for (int i = bloomLevelCount - 2; i >= 0; i--)
            DrawBloomPass(encoder, (TextureView*)bloomUpViews[i], (BindGroup*)bloomUpGroups[i],
                bloomUpPipeline, $"PixelMap bloom up {i}");
    }

    private void DrawBloomPass(CommandEncoder* encoder, TextureView* target, BindGroup* group,
        RenderPipeline* pipeline, string name)
    {
        var color = new RenderPassColorAttachment
        {
            View = target, DepthSlice = uint.MaxValue, LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store, ClearValue = new(0, 0, 0, 0)
        };
        using var label = new NativeLabel(name);
        var descriptor = new RenderPassDescriptor { Label = label.Pointer, ColorAttachmentCount = 1, ColorAttachments = &color };
        var pass = api.CommandEncoderBeginRenderPass(encoder, &descriptor);
        Require(pass, "bloom render pass");
        try
        {
            api.RenderPassEncoderSetPipeline(pass, pipeline);
            api.RenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
            api.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
            LastDrawCalls++;
        }
        finally { EndPass(pass); }
    }

    private void ReleaseBloomAttachments()
    {
        // Release all binding references before their sampled views and textures.
        for (int i = 0; i < bloomDownGroups.Length; i++)
        { var group = (BindGroup*)bloomDownGroups[i]; Release(ref group); bloomDownGroups[i] = 0; }
        for (int i = 0; i < bloomUpGroups.Length; i++)
        { var group = (BindGroup*)bloomUpGroups[i]; Release(ref group); bloomUpGroups[i] = 0; }
        for (int i = 0; i < bloomDownViews.Length; i++)
        {
            var view = (TextureView*)bloomDownViews[i]; Release(ref view); bloomDownViews[i] = 0;
            var texture = (Texture*)bloomDownTextures[i]; Release(ref texture); bloomDownTextures[i] = 0;
        }
        for (int i = 0; i < bloomUpViews.Length; i++)
        {
            var view = (TextureView*)bloomUpViews[i]; Release(ref view); bloomUpViews[i] = 0;
            var texture = (Texture*)bloomUpTextures[i]; Release(ref texture); bloomUpTextures[i] = 0;
        }
        bloomLevelCount = 0;
    }

    private void ReleaseBloomResources()
    {
        Release(ref bloomPrefilterPipeline); Release(ref bloomDownPipeline); Release(ref bloomUpPipeline);
        Release(ref bloomPrefilterModule); Release(ref bloomDownModule); Release(ref bloomUpModule);
        Release(ref bloomPipelineLayout); Release(ref bloomLayout);
    }
}
