# Terrain artwork filtering and mip ownership

Smooth Normal and Roblox terrain sample the complete 256²-to-1² artwork mip
chain with explicit perspective UV gradients, trilinear filtering and an 8×
anisotropic sampler. The sampler uses clamp-to-edge addressing, all three linear
filter modes and LOD limits 0–8. The requested anisotropy is bounded at eight;
the WebGPU implementation can clamp the effective value further. The array
layout continues to isolate tiles; it does not invent neighboring tile gutters.

Pixel and Minecraft bind a base-level-only `rgba8unorm` view and the existing
nearest sampler. Their uploaded BGRA bytes, explicit level-zero sampling, height
loads, shade quantization and logical artwork grid remain unchanged.

## Linear light and the public Uno/Silk API

The final artwork array has nine levels, base format `rgba8unorm`, compatible
view format `rgba8unorm-srgb`, and only `TEXTURE_BINDING | COPY_DST` usage.
Its smooth view decodes RGB before hardware filtering. Normal terrain passes
that filtered linear albedo directly into `lightingLinear`; the encoded-color
palette operations for elevation and Roblox convert back to linear once before
lighting. Shader `.bgr` swizzles preserve the original Skia byte channel order.

`WebGpuTerrainMipShaders.Reduce` reads a single-level sRGB array view. Four exact
texel loads provide linear RGB, which are averaged before explicit sRGB encoding.
Alpha is averaged directly. The shader writes an ordinary RGBA8 storage scratch
array; copies then move the encoded bytes into the appropriate final artwork
mip. Every copy precedes the next level's compute pass, so all levels consume the
new data in the same frame encoder, before the scene is sampled.

The scratch array is necessary for the public Silk.NET 2.23 ABI:
`TextureViewDescriptor` has no usage override, while WebGPU rejects an sRGB
view inheriting `STORAGE_BINDING`. No private Uno handle or altered interop ABI
is used. The scratch texture has one 128² level and is reused for all eight
reductions. The source dimensions determine the writable rectangle, including
the sub-workgroup 4², 2² and 1² levels.

## Bounded work and lifetime

A changed base layer increments its upload generation. Pixel-only draws leave
that generation pending. The next smooth draw prepares all pending physical
layers, uploads one bounded layer-index table, and dispatches eight 8×8×1 compute
passes. Dispatch Z is exactly the pending layer count. Each dispatch is followed
by one texture copy per pending layer. There are no CPU image readbacks, extra
queue submissions or waits. Unchanged frames dispatch no mip work.

Encoding alone never acknowledges uploads. Generations are committed only after
the frame's `QueueSubmit` succeeds; interrupted encoding, layer reassignment and
a subsequent Pixel frame retain unsubmitted work. All scratch resources, views,
bind groups and the compute pipeline are owned by the renderer and released on
disposal. The shared Uno device and queue remain borrowed.

For the 96-layer capacity, the artwork chain occupies **33,554,304 bytes**. The
scratch texture is allocated only when smooth dirty work first occurs and adds
**6,291,456 bytes**, plus a **384-byte** layer-index buffer. `ResidentBytes`
reports these allocations exactly, excluding implementation-specific alignment
and object metadata. Mip levels add 8 MiB minus 128 bytes to the previous
24 MiB base artwork array. These are bounded allocation and work-count
requirements; they are not a measured hardware frame-rate claim.

## Validation

`WebGpuTerrainMipChecks` exercises deferred Pixel work, failed submission,
reused layers, later uploads during a prepared batch, empty retained frames and
exact allocation accounting. Browser validation must also compile and execute
the production reducer, check a black/white mean near encoded 188 rather than
128, validate alpha and isolated clean layers, and confirm the submitted
compute/copy order, sRGB views, sampler modes and current mip generations.

The GPU fixtures and all 34 application scenarios passed in both Debug and
Release AOT in [run 18](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43167877049).
They compare 266,256 mip bytes within one encoded byte and preserve 1,048,576
original base bytes exactly. The first smooth Debug capture processes all 96
pending layers in eight dispatches and 768 copies; later retained frames execute
no mip work. Original reports and compressed GPU traces are in the
[verification record](verification/webgpu-filtering-5af429f/README.md).

## API references

- [WebGPU texture view usage and compatible formats](https://www.w3.org/TR/webgpu/#dom-gputextureviewdescriptor-usage)
- [Silk.NET 2.23 public TextureViewDescriptor fields](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/WebGPU/Silk.NET.WebGPU/Structs/TextureViewDescriptor.gen.cs)
- [WebGPU anisotropic sampler requirements](https://www.w3.org/TR/webgpu/#dom-gpusamplerdescriptor-maxanisotropy)
