using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace PixelMap.Rendering;

public sealed unsafe partial class WebGpuMapRenderer
{
    // Pipeline objects depend on sample count or target format, never on the
    // current style, shadow texture extent, camera or attachment allocation.
    // Keep the two scene variants and each encountered presentation format for
    // the device lifetime instead of recompiling every Pixel/Normal transition.
    private readonly ScenePipelines?[] scenePipelineCache = new ScenePipelines?[2];
    private readonly Dictionary<TextureFormat, PresentationPipelines> presentationPipelineCache = [];

    private sealed class ScenePipelines
    {
        internal RenderPipeline* Background, Terrain, Buildings, Rim, World, Occluded;
    }

    private sealed class PresentationPipelines
    {
        internal RenderPipeline* Resolve, Labels;
    }

    private void EnsurePipelines(TextureFormat targetFormat, int samples, bool smooth)
    {
        if (pipelineTargetFormat == targetFormat && pipelineSamples == samples && terrainPipeline != null)
        {
            if (smooth) EnsureShadowPipelines(shadowCascadeCount);
            return;
        }
        int index = samples switch { 1 => 0, 4 => 1, _ => throw new ArgumentOutOfRangeException(nameof(samples)) };
        var scene = scenePipelineCache[index] ??= CreateScenePipelines(samples);
        if (!presentationPipelineCache.TryGetValue(targetFormat, out var presentation))
        {
            presentation = CreatePresentationPipelines(targetFormat);
            presentationPipelineCache.Add(targetFormat, presentation);
        }
        if (smooth) EnsureShadowPipelines(shadowCascadeCount);

        // These are aliases only. Cache entries below retain sole ownership.
        backgroundPipeline = scene.Background; terrainPipeline = scene.Terrain;
        buildingPipeline = scene.Buildings; rimPipeline = scene.Rim;
        overlayWorldPipeline = scene.World; overlayOccludedPipeline = scene.Occluded;
        resolvePipeline = presentation.Resolve; overlayScreenPipeline = presentation.Labels;
        pipelineTargetFormat = targetFormat;
        pipelineSamples = samples;
    }

    private static VertexBufferLayout TerrainVertexLayout(VertexAttribute* attributes)
    {
        for (uint i = 0; i < 5; i++) attributes[i] = new() { ShaderLocation = i, Offset = i * 16, Format = VertexFormat.Float32x4 };
        return new() { ArrayStride = 80, StepMode = VertexStepMode.Instance, AttributeCount = 5, Attributes = attributes };
    }

    private static VertexBufferLayout BuildingVertexLayout(VertexAttribute* attributes)
    {
        attributes[0] = new() { ShaderLocation = 0, Offset = 0, Format = VertexFormat.Float32x2 };
        attributes[1] = new() { ShaderLocation = 1, Offset = 8, Format = VertexFormat.Float32x3 };
        attributes[2] = new() { ShaderLocation = 2, Offset = 20, Format = VertexFormat.Float32x2 };
        attributes[3] = new() { ShaderLocation = 3, Offset = 28, Format = VertexFormat.Uint32 };
        return new() { ArrayStride = GpuBuildingVertex.Size, StepMode = VertexStepMode.Vertex, AttributeCount = 4, Attributes = attributes };
    }

    private static VertexBufferLayout OverlayVertexLayout(VertexAttribute* attributes)
    {
        attributes[0] = new() { ShaderLocation = 0, Offset = 0, Format = VertexFormat.Float32x4 };
        attributes[1] = new() { ShaderLocation = 1, Offset = 16, Format = VertexFormat.Float32x4 };
        attributes[2] = new() { ShaderLocation = 2, Offset = 32, Format = VertexFormat.Float32x2 };
        attributes[3] = new() { ShaderLocation = 3, Offset = 40, Format = VertexFormat.Float32 };
        return new() { ArrayStride = 48, StepMode = VertexStepMode.Vertex, AttributeCount = 4, Attributes = attributes };
    }

    private ScenePipelines CreateScenePipelines(int samples)
    {
        var tileAttributes = stackalloc VertexAttribute[5];
        var tile = TerrainVertexLayout(tileAttributes);
        var buildingAttributes = stackalloc VertexAttribute[4];
        var building = BuildingVertexLayout(buildingAttributes);
        var edgeAttributes = stackalloc VertexAttribute[2];
        edgeAttributes[0] = new() { ShaderLocation = 0, Offset = 0, Format = VertexFormat.Float32x4 };
        edgeAttributes[1] = new() { ShaderLocation = 1, Offset = 16, Format = VertexFormat.Float32x4 };
        var edge = new VertexBufferLayout { ArrayStride = 32, StepMode = VertexStepMode.Instance, AttributeCount = 2, Attributes = edgeAttributes };
        var overlayAttributes = stackalloc VertexAttribute[4];
        var overlay = OverlayVertexLayout(overlayAttributes);
        var result = new ScenePipelines();
        try
        {
            result.Background = CreatePipeline("sky", backgroundModule, backgroundLayout, null, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: false, compare: CompareFunction.Always);
            result.Terrain = CreatePipeline("terrain", terrainModule, sceneLayout, &tile, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: true);
            result.Buildings = CreatePipeline("buildings", buildingModule, buildingLayout, &building, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: true, cull: CullMode.Back);
            result.Rim = CreatePipeline("roof edges", rimModule, buildingLayout, &edge, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: false, topology: PrimitiveTopology.TriangleStrip);
            result.World = CreatePipeline("world overlays", overlayModule, overlayPipelineLayout, &overlay, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: false, blend: true);
            result.Occluded = CreatePipeline("occluded route silhouettes", overlayModule, overlayPipelineLayout, &overlay, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: false, blend: true, compare: CompareFunction.Greater);
            return result;
        }
        catch { ReleaseScenePipelines(result); throw; }
    }

    private PresentationPipelines CreatePresentationPipelines(TextureFormat targetFormat)
    {
        var attributes = stackalloc VertexAttribute[4];
        var overlay = OverlayVertexLayout(attributes);
        var result = new PresentationPipelines();
        try
        {
            result.Resolve = CreatePipeline("HDR resolve", resolveModule, resolvePipelineLayout, null, targetFormat, 1);
            result.Labels = CreatePipeline("screen labels", overlayModule, overlayPipelineLayout, &overlay, targetFormat, 1, blend: true);
            return result;
        }
        catch { ReleasePresentationPipelines(result); throw; }
    }

    private void EnsureShadowPipelines(int count)
    {
        if (shadowTerrainPipelines[count - 1] != 0 && shadowBuildingPipelines[count - 1] != 0) return;
        var tileAttributes = stackalloc VertexAttribute[5];
        var tile = TerrainVertexLayout(tileAttributes);
        var buildingAttributes = stackalloc VertexAttribute[4];
        var building = BuildingVertexLayout(buildingAttributes);
        for (int i = 0; i < count; i++)
        {
            if (shadowTerrainPipelines[i] != 0 && shadowBuildingPipelines[i] != 0) continue;
            RenderPipeline* terrain = null;
            RenderPipeline* buildings = null;
            try
            {
                terrain = CreatePipeline($"terrain sun shadows {i}", terrainModule, shadowTerrainLayout, &tile, TextureFormat.Undefined,
                    1, depth: true, depthWrite: true, vertexEntry: $"shadow{i}_vs", shadow: true);
                buildings = CreatePipeline($"building sun shadows {i}", buildingModule, shadowBuildingLayout, &building, TextureFormat.Undefined,
                    1, depth: true, depthWrite: true, vertexEntry: $"shadow{i}_vs", shadow: true);
                shadowTerrainPipelines[i] = (nint)terrain;
                shadowBuildingPipelines[i] = (nint)buildings;
            }
            catch { Release(ref terrain); Release(ref buildings); throw; }
        }
    }

    private RenderPipeline* CreatePipeline(string name, ShaderModule* module, PipelineLayout* layout,
        VertexBufferLayout* vertices, TextureFormat colorFormat, int samples, bool depth = false,
        bool depthWrite = false, CompareFunction compare = CompareFunction.LessEqual,
        CullMode cull = CullMode.None, bool blend = false, PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        string vertexEntry = "vs", bool shadow = false)
    {
        byte* label = (byte*)SilkMarshal.StringToPtr($"PixelMap {name} ({samples}x)");
        byte* vertex = (byte*)SilkMarshal.StringToPtr(vertexEntry);
        byte* fragment = (byte*)SilkMarshal.StringToPtr("fs");
        try
        {
            var blending = new BlendState
            {
                Color = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
                Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add }
            };
            var color = new ColorTargetState { Format = colorFormat, WriteMask = ColorWriteMask.All, Blend = blend ? &blending : null };
            var output = new FragmentState { Module = module, EntryPoint = fragment, TargetCount = 1, Targets = &color };
            var stencil = new StencilFaceState
            {
                Compare = CompareFunction.Always, FailOp = StencilOperation.Keep,
                DepthFailOp = StencilOperation.Keep, PassOp = StencilOperation.Keep
            };
            var depthState = new DepthStencilState
            {
                Format = shadow ? TextureFormat.Depth32float : TextureFormat.Depth24Plus,
                DepthWriteEnabled = depthWrite, DepthCompare = compare,
                StencilFront = stencil, StencilBack = stencil, StencilReadMask = uint.MaxValue, StencilWriteMask = uint.MaxValue,
                DepthBias = shadow ? 1 : 0, DepthBiasSlopeScale = shadow ? 1.5f : 0
            };
            var descriptor = new RenderPipelineDescriptor
            {
                Label = label, Layout = layout,
                Vertex = new VertexState { Module = module, EntryPoint = vertex, BufferCount = vertices == null ? 0u : 1u, Buffers = vertices },
                Fragment = shadow ? null : &output,
                Primitive = new PrimitiveState { Topology = topology, FrontFace = FrontFace.Ccw, CullMode = cull, StripIndexFormat = IndexFormat.Undefined },
                DepthStencil = depth ? &depthState : null,
                Multisample = new MultisampleState { Count = (uint)samples, Mask = uint.MaxValue, AlphaToCoverageEnabled = false }
            };
            var pipeline = api.DeviceCreateRenderPipeline(device, &descriptor);
            Require(pipeline, name + " pipeline");
            return pipeline;
        }
        finally { SilkMarshal.Free((nint)label); SilkMarshal.Free((nint)vertex); SilkMarshal.Free((nint)fragment); }
    }

    private void ReleasePipelines()
    {
        backgroundPipeline = terrainPipeline = buildingPipeline = rimPipeline = null;
        overlayWorldPipeline = overlayOccludedPipeline = overlayScreenPipeline = resolvePipeline = null;
        for (int i = 0; i < scenePipelineCache.Length; i++)
        {
            if (scenePipelineCache[i] is { } scene) ReleaseScenePipelines(scene);
            scenePipelineCache[i] = null;
        }
        foreach (var presentation in presentationPipelineCache.Values) ReleasePresentationPipelines(presentation);
        presentationPipelineCache.Clear();
        for (int i = 0; i < WebGpuShadowCascades.MaximumCount; i++)
        {
            var terrain = (RenderPipeline*)shadowTerrainPipelines[i];
            var building = (RenderPipeline*)shadowBuildingPipelines[i];
            Release(ref terrain); Release(ref building);
            shadowTerrainPipelines[i] = shadowBuildingPipelines[i] = 0;
        }
        pipelineSamples = 0;
    }

    private void ReleaseScenePipelines(ScenePipelines scene)
    {
        Release(ref scene.Background); Release(ref scene.Terrain); Release(ref scene.Buildings);
        Release(ref scene.Rim); Release(ref scene.World); Release(ref scene.Occluded);
    }

    private void ReleasePresentationPipelines(PresentationPipelines presentation)
    { Release(ref presentation.Resolve); Release(ref presentation.Labels); }
}
