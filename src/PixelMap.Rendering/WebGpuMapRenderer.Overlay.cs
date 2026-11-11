using Silk.NET.WebGPU;
using SkiaSharp;
using GpuBuffer = Silk.NET.WebGPU.Buffer;

namespace PixelMap.Rendering;

public sealed unsafe partial class WebGpuMapRenderer
{
    private GpuBuffer* overlayLinearSettings, overlaySrgbSettings;
    private Texture* labelAtlas;
    private TextureView* labelAtlasView;
    private BindGroup* overlayLinearGroup, overlaySrgbGroup;
    private int atlasWidth = 1, atlasHeight = 1;
    private long atlasRevision = -1;
    private WebGpuSceneOverlay? atlasOwner;
    private readonly OverlayBatch worldOverlay = new(), occludedOverlay = new(), screenOverlay = new();

    private void InitializeOverlayResources()
    {
        overlayLinearSettings = CreateBuffer(16, BufferUsage.Uniform | BufferUsage.CopyDst);
        overlaySrgbSettings = CreateBuffer(16, BufferUsage.Uniform | BufferUsage.CopyDst);
        Upload<float>(overlayLinearSettings, [1, 0, 0, 0]);
        Upload<float>(overlaySrgbSettings, [0, 0, 0, 0]);
        labelAtlas = CreateTexture(1, 1, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst);
        labelAtlasView = CreateView(labelAtlas, TextureFormat.Rgba8Unorm);
        uint white = uint.MaxValue;
        WriteTexture(labelAtlas, &white, 4, 1, 1, 4);
        RebuildOverlayGroups();
    }
    private void RebuildOverlayGroups()
    {
        Release(ref overlayLinearGroup); Release(ref overlaySrgbGroup);
        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new() { Binding = 0, TextureView = labelAtlasView };
        entries[1] = new() { Binding = 1, Sampler = linearSampler };
        entries[2] = new() { Binding = 2, Buffer = overlayLinearSettings, Size = 16 };
        overlayLinearGroup = CreateGroup(overlayLayout, entries, 3);
        entries[2].Buffer = overlaySrgbSettings;
        overlaySrgbGroup = CreateGroup(overlayLayout, entries, 3);
    }
    private void PrepareOverlay(WebGpuSceneOverlay? overlay, bool smooth)
    {
        worldOverlay.Count = occludedOverlay.Count = screenOverlay.Count = 0;
        if (overlay is null) return;
        if (overlay.Atlas is { } atlas && (overlay.AtlasRevision != atlasRevision || !ReferenceEquals(atlasOwner, overlay)))
        {
            bool full = atlas.Width != atlasWidth || atlas.Height != atlasHeight || !ReferenceEquals(atlasOwner, overlay);
            if (atlas.Width != atlasWidth || atlas.Height != atlasHeight)
            {
                if (atlas.Width > 4096 || atlas.Height > 4096) throw new NotSupportedException("The label atlas exceeds the GPU glyph budget.");
                Release(ref overlayLinearGroup); Release(ref overlaySrgbGroup);
                Release(ref labelAtlasView); Release(ref labelAtlas);
                labelAtlas = CreateTexture(atlas.Width, atlas.Height, TextureFormat.Rgba8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst);
                labelAtlasView = CreateView(labelAtlas, TextureFormat.Rgba8Unorm);
                atlasWidth = atlas.Width; atlasHeight = atlas.Height;
                RebuildOverlayGroups();
            }
            var dirty = full ? new SKRectI(0, 0, atlas.Width, atlas.Height) : overlay.AtlasDirtyBounds;
            if (dirty.Width > 0 && dirty.Height > 0)
            {
                byte* pixels = (byte*)atlas.GetPixels() + dirty.Top * atlas.RowBytes + dirty.Left * 4;
                nuint bytes = (nuint)((dirty.Height - 1) * atlas.RowBytes + dirty.Width * 4);
                WriteTexture(labelAtlas, pixels, bytes, dirty.Width, dirty.Height, atlas.RowBytes, x: dirty.Left, y: dirty.Top);
            }
            atlasRevision = overlay.AtlasRevision;
            atlasOwner = overlay;
            overlay.AcknowledgeAtlasUpload(atlasRevision);
        }
        UploadOverlayBatch(worldOverlay, overlay.WorldVertices, overlay.WorldIndices, overlay, overlay.Revision);
        UploadOverlayBatch(occludedOverlay, overlay.OccludedVertices, overlay.OccludedIndices, overlay, overlay.Revision);
        UploadOverlayBatch(screenOverlay, overlay.ScreenVertices, overlay.ScreenIndices, overlay, overlay.ScreenRevision);
        worldOverlay.Linear = occludedOverlay.Linear = smooth;
        screenOverlay.Linear = TargetIsSrgb;
    }
    private void UploadOverlayBatch(OverlayBatch batch, ReadOnlySpan<WebGpuOverlayVertex> vertices, ReadOnlySpan<uint> indices,
        WebGpuSceneOverlay owner, long revision)
    {
        batch.Count = indices.Length;
        if (ReferenceEquals(batch.Owner, owner) && batch.Revision == revision) return;
        if (indices.IsEmpty)
        {
            batch.Owner = owner;
            batch.Revision = revision;
            return;
        }
        long vertexBytes = (long)vertices.Length * sizeof(WebGpuOverlayVertex), indexBytes = (long)indices.Length * sizeof(uint);
        if (vertexBytes + indexBytes > 32 * 1024 * 1024) throw new NotSupportedException("The overlay exceeds the GPU geometry budget.");
        if (batch.VertexCapacity < vertexBytes)
        {
            var next = CreateBuffer((ulong)vertexBytes, BufferUsage.Vertex | BufferUsage.CopyDst);
            Release(ref batch.Vertices); batch.Vertices = next; batch.VertexCapacity = vertexBytes;
        }
        if (batch.IndexCapacity < indexBytes)
        {
            var next = CreateBuffer((ulong)indexBytes, BufferUsage.Index | BufferUsage.CopyDst);
            Release(ref batch.Indices); batch.Indices = next; batch.IndexCapacity = indexBytes;
        }
        Upload(batch.Vertices, vertices); Upload(batch.Indices, indices);
        // Publish the upload identity only after both streams have been written.
        // Counts and bind groups still advance on every draw, including empty batches.
        batch.Owner = owner;
        batch.Revision = revision;
    }
    private void DrawWorldOverlay(RenderPassEncoder* pass, WebGpuSceneOverlay? overlay)
    {
        if (overlay is null) return;
        DrawOverlayBatch(pass, occludedOverlay, overlayOccludedPipeline);
        DrawOverlayBatch(pass, worldOverlay, overlayWorldPipeline);
    }
    private void DrawScreenOverlay(RenderPassEncoder* pass, WebGpuSceneOverlay? overlay)
    {
        if (overlay is not null) DrawOverlayBatch(pass, screenOverlay, overlayScreenPipeline);
    }
    private void DrawOverlayBatch(RenderPassEncoder* pass, OverlayBatch batch, RenderPipeline* pipeline)
    {
        if (batch.Count == 0) return;
        api.RenderPassEncoderSetPipeline(pass, pipeline);
        api.RenderPassEncoderSetBindGroup(pass, 0, batch.Linear ? overlayLinearGroup : overlaySrgbGroup, 0, null);
        api.RenderPassEncoderSetVertexBuffer(pass, 0, batch.Vertices, 0, (ulong)batch.VertexCapacity);
        api.RenderPassEncoderSetIndexBuffer(pass, batch.Indices, IndexFormat.Uint32, 0, (ulong)batch.IndexCapacity);
        api.RenderPassEncoderDrawIndexed(pass, (uint)batch.Count, 1, 0, 0, 0);
        LastDrawCalls++;
    }
    private void ReleaseOverlayResources()
    {
        Release(ref overlayLinearGroup); Release(ref overlaySrgbGroup);
        Release(ref labelAtlasView); Release(ref labelAtlas);
        Release(ref overlayLinearSettings); Release(ref overlaySrgbSettings);
        foreach (var batch in new[] { worldOverlay, occludedOverlay, screenOverlay })
        { Release(ref batch.Vertices); Release(ref batch.Indices); batch.Owner = null; batch.Revision = -1; batch.Count = 0; }
        atlasOwner = null;
    }
    private sealed class OverlayBatch
    {
        internal GpuBuffer* Vertices, Indices;
        internal long VertexCapacity, IndexCapacity;
        internal int Count;
        internal bool Linear;
        internal WebGpuSceneOverlay? Owner;
        internal long Revision = -1;
    }
}
