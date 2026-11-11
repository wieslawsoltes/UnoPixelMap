using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace PixelMap.Rendering;

public sealed unsafe partial class WebGpuMapRenderer
{
    private void EnsurePipelines(TextureFormat targetFormat, int samples)
    {
        if (pipelineTargetFormat == targetFormat && pipelineSamples == samples && terrainPipeline != null) return;
        ReleasePipelines();
        var tileAttributes = stackalloc VertexAttribute[5];
        for (uint i = 0; i < 5; i++) tileAttributes[i] = new() { ShaderLocation = i, Offset = i * 16, Format = VertexFormat.Float32x4 };
        var tile = new VertexBufferLayout { ArrayStride = 80, StepMode = VertexStepMode.Instance, AttributeCount = 5, Attributes = tileAttributes };
        var buildingAttributes = stackalloc VertexAttribute[4];
        buildingAttributes[0] = new() { ShaderLocation = 0, Offset = 0, Format = VertexFormat.Float32x2 };
        buildingAttributes[1] = new() { ShaderLocation = 1, Offset = 8, Format = VertexFormat.Float32x3 };
        buildingAttributes[2] = new() { ShaderLocation = 2, Offset = 20, Format = VertexFormat.Float32x2 };
        buildingAttributes[3] = new() { ShaderLocation = 3, Offset = 28, Format = VertexFormat.Uint32 };
        var building = new VertexBufferLayout { ArrayStride = GpuBuildingVertex.Size, StepMode = VertexStepMode.Vertex, AttributeCount = 4, Attributes = buildingAttributes };
        var edgeAttributes = stackalloc VertexAttribute[2];
        edgeAttributes[0] = new() { ShaderLocation = 0, Offset = 0, Format = VertexFormat.Float32x4 };
        edgeAttributes[1] = new() { ShaderLocation = 1, Offset = 16, Format = VertexFormat.Float32x4 };
        var edge = new VertexBufferLayout { ArrayStride = 32, StepMode = VertexStepMode.Instance, AttributeCount = 2, Attributes = edgeAttributes };
        var overlayAttributes = stackalloc VertexAttribute[4];
        overlayAttributes[0] = new() { ShaderLocation = 0, Offset = 0, Format = VertexFormat.Float32x4 };
        overlayAttributes[1] = new() { ShaderLocation = 1, Offset = 16, Format = VertexFormat.Float32x4 };
        overlayAttributes[2] = new() { ShaderLocation = 2, Offset = 32, Format = VertexFormat.Float32x2 };
        overlayAttributes[3] = new() { ShaderLocation = 3, Offset = 40, Format = VertexFormat.Float32 };
        var overlay = new VertexBufferLayout { ArrayStride = 48, StepMode = VertexStepMode.Vertex, AttributeCount = 4, Attributes = overlayAttributes };
        try
        {
            backgroundPipeline = CreatePipeline("sky", backgroundModule, backgroundLayout, null, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: false, compare: CompareFunction.Always);
            terrainPipeline = CreatePipeline("terrain", terrainModule, sceneLayout, &tile, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: true);
            buildingPipeline = CreatePipeline("buildings", buildingModule, buildingLayout, &building, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: true, cull: CullMode.Back);
            rimPipeline = CreatePipeline("roof edges", rimModule, buildingLayout, &edge, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: false, topology: PrimitiveTopology.TriangleStrip);
            shadowTerrainPipeline = CreatePipeline("terrain sun shadows", terrainModule, shadowTerrainLayout, &tile, TextureFormat.Undefined,
                1, depth: true, depthWrite: true, vertexEntry: "shadow_vs", shadow: true);
            shadowBuildingPipeline = CreatePipeline("building sun shadows", buildingModule, shadowBuildingLayout, &building, TextureFormat.Undefined,
                1, depth: true, depthWrite: true, vertexEntry: "shadow_vs", shadow: true);
            resolvePipeline = CreatePipeline("HDR resolve", resolveModule, resolvePipelineLayout, null, targetFormat, 1);
            overlayWorldPipeline = CreatePipeline("world overlays", overlayModule, overlayPipelineLayout, &overlay, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: false, blend: true);
            overlayOccludedPipeline = CreatePipeline("occluded route silhouettes", overlayModule, overlayPipelineLayout, &overlay, TextureFormat.Rgba16float,
                samples, depth: true, depthWrite: false, blend: true, compare: CompareFunction.Greater);
            overlayScreenPipeline = CreatePipeline("screen labels", overlayModule, overlayPipelineLayout, &overlay, targetFormat, 1, blend: true);
            pipelineTargetFormat = targetFormat;
            pipelineSamples = samples;
        }
        catch { ReleasePipelines(); throw; }
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
        Release(ref backgroundPipeline); Release(ref terrainPipeline); Release(ref buildingPipeline); Release(ref rimPipeline);
        Release(ref shadowTerrainPipeline); Release(ref shadowBuildingPipeline); Release(ref resolvePipeline);
        Release(ref overlayWorldPipeline); Release(ref overlayOccludedPipeline); Release(ref overlayScreenPipeline);
        pipelineSamples = 0;
    }
}
