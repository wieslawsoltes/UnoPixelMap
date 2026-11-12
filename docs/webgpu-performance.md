# WebGPU rendering performance

This follow-up to the Uno 7 migration removes CPU preparation stalls, terrain
rebakes, repeated pipeline creation and shader work with no visible contribution.
The production rendering path remains Uno's shared WebGPU device. Quality presets,
physical scene resolution, 4× MSAA in High/Ultra, shadows, HDR bloom, terrain mips,
anisotropic filtering, map styles and native-density labels are preserved.

**Measured application baseline:** `a9a284263da76a201114381467bf45aa4823a2d6`.
**Measured optimized application:** `16048b7981ba2b08f57f437dc833457a886df56d`.
Both are committed source trees. Later documentation or benchmark-only changes do
not change that production implementation. Migration architecture and its existing
quality limits remain in [the migration guide](uno7-webgpu-migration.md).

## Results: native CPU frame preparation

The primary table comes from the [paired CI run](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43248840497)
on an AMD EPYC 7763 runner. One frozen harness was compiled against both revisions;
all compilation finished before measurements. Baseline/candidate/candidate/baseline
(ABBA) order, an explicit hashed font, 32 warm-up poses and 32 measured poses per
workload/run keep the work comparable. Tiered compilation is disabled for both
native hosts. Each table entry pools 64 measured samples per variant.

Times are **CPU elapsed milliseconds for `PrepareGpuFrame` plus activation and
`PrepareWebGpuOverlay`**, not GPU duration or display FPS. Sparse picking is timed
separately in the raw report. Geometry, projections, selected index/edge streams,
terrain texels, overlay streams, font atlas and picking hashes are compared outside
timing. All **10,944 identity comparisons passed**, including repeated runs.

| Moving workload | Baseline median, ms | Optimized median, ms | Time reduction | Baseline → optimized p95, ms |
|---|---:|---:|---:|---:|
| pan-pixel | 15.776 | 10.917 | 30.8% | 17.032 → 12.465 |
| walk-pixel | 5.598 | 2.996 | 46.5% | 6.353 → 3.621 |
| orbit-pixel | 16.420 | 11.293 | 31.2% | 17.667 → 13.297 |
| pan-normal | 16.678 | 12.483 | 25.2% | 17.875 → 13.124 |
| walk-normal | 6.022 | 3.162 | 47.5% | 6.523 → 3.947 |
| orbit-normal | 16.884 | 12.224 | 27.6% | 18.111 → 13.932 |
| pan-roblox | 17.002 | 13.855 | 18.5% | 18.123 → 15.034 |
| walk-roblox | 7.956 | 3.833 | 51.8% | 8.612 → 4.643 |
| orbit-roblox | 17.298 | 13.515 | 21.9% | 18.600 → 15.634 |

The nine cases use bundled Manhattan data, a 1200 × 700 logical viewport and
identical deterministic pan, orbit and first-person walking cameras. Smooth styles
retain their existing physical/art resolution rules. Calling-thread CPU time and
managed allocations are preserved alongside wall time; unavailable thread timing
is null instead of zero on unsupported platforms.

An independent local ABBA run also passed all 10,944 identity comparisons and
measured 18–48% lower preparation/overlay time. The primary CI run measures
18–52% lower time. These are separate machines/runs, not a combined sample pool.
The source, font, harness and renderer hashes are retained for each.

## Results: source streaming and artwork

These fixed-camera workloads replace one deterministic vector or imagery source
at a time over nine real bundled city tiles. They deliberately isolate source
invalidation from network requests and decoding outside the renderer. Every
presented terrain texture is hashed and compared against the baseline.

| Source update | Baseline median, ms | Optimized median, ms | Speedup | Rebaked visible textures/frame |
|---|---:|---:|---:|---:|
| distant-vector-stream-pixel | 15.400 | 0.845 | 18.22× | 63 → 0 |
| local-vector-stream-pixel | 12.130 | 1.174 | 10.33× | 63 → 1 |
| local-imagery-stream-pixel | 491.957 | 3.142 | 156.55× | 63 → 1 |
| source-priority-control-pixel | 0.809 | 0.797 | 1.01× | 0 → 0 |
| distant-vector-stream-normal | 13.372 | 0.851 | 15.72× | 64 → 0 |
| local-vector-stream-normal | 12.546 | 1.098 | 11.43× | 64 → 1 |
| local-imagery-stream-normal | 328.605 | 1.574 | 208.81× | 64 → 1 |
| source-priority-control-normal | 0.860 | 0.832 | 1.03× | 0 → 0 |

A distant update now retains all visible artwork. A local vector or imagery
replacement rebakes one patch instead of 63–64 in these scenes. Base texture data
falls from 15.75–16 MiB to **256 KiB** per local update; a distant update produces
none. This is the potential base-level texture upload payload, independently
counted from real texture identities. The native harness does not execute a GPU
upload. The actual WebGPU host keeps unchanged texture identities and array layers.

Managed allocations for local imagery updates fell from roughly 16.6–16.8 MB to
281 kB per frame in the same CI run. The source-priority controls rebake no textures
in either implementation; their medians remain within roughly 1–3%. The large
imagery speedups describe these source-churn cases, not every moving frame.

## Implementation

### Localized terrain invalidation

`PixelMapRenderer.GpuScene.cs` compares vector/imagery sources by canonical tile ID
and object identity. `TerrainSurface` removes only cached patches whose dyadic
geographic rectangles overlap a changed source. It invalidates cached offscreen
patches too. Additions, replacements, removals, mixed zooms and world copies retain
the correct coverage. Duplicate IDs use conservative complete invalidation because
painter order can be significant. Baked neighboring building-shadow changes also
invalidate their dependent patch region. Palette/style/pixel/shadow-mode changes
still trigger the required complete rebake. Old retained frame texels stay immutable.

### Label selection before depth work

`PixelMapRenderer.Labels.cs` rejects candidates by name, bounds and collisions
before requesting expensive occlusion. It handles equal-priority/equal-distance
groups by their actual visible placement. Buffered copies whose text, font class,
rounded origin and collision rectangle match have equivalent output. Hidden or
already blocked ties do not force a global eager visibility pass.

Genuinely different visible placements still restart using the original source
order, visibility compaction and unstable `List.Sort`, preserving its exact winner.
Glyph emission starts after selection, so a restart cannot duplicate output.
Per-candidate visibility is reused within that preparation and never across frames.

### Large-building depth chunks

Some demanded building features have tens of thousands of vertices while only a
small part reaches the viewport. `GpuBuildingDepthChunks` lazily records immutable
bounds over consecutive 64-triangle and 64-roof-segment ranges. Foundation-only
snapshots share that metadata. `GpuSceneDepth.Buildings.cs` rejects wholly offscreen
chunks and projects vertices only when a surviving primitive references them.
Small buildings and large buildings wholly inside the viewport retain the prior
path, avoiding unnecessary chunk checks.

The exact face tests, transform arithmetic, primitive order, clipper, interpolation
and picking rules remain. Near-plane/nonfinite bounds take the conservative full
viewport path. Per-invocation generations keep projected scratch vertices and clip
codes separate across models, transformed copies, retained frames, pooled storage
and generation rollover. No GPU geometry is simplified or removed.

In a diagnostic production orbit view, the largest model projected **1,337 of
25,128 vertices**, visiting 17/241 triangle chunks and 4/67 roof-edge chunks.
The full replay tables above include the cost of chunk checks and metadata reuse.
Near-first model sorting and another repeated-pixel cache were measured and
rejected because their benefit did not justify their overhead or complexity.

### WebGPU commands, pipelines and staging

| Work | Baseline | Optimized |
|---|---:|---:|
| Copy commands for all 96 terrain layers across eight generated mips | 768 | 8 |
| Render pipelines created over the established desktop integration sequence | 163 | 25 |
| Initial Pixel render-pipeline creations | 16 | 8 |
| Explicit shadow passes for Pixel/Minecraft | One unused 1 × 1 clear | None |
| CPU uniform staging cleared per preparation | 262,656 bytes | 512 bytes |

Consecutive dirty physical layers become one texture-array copy per mip. Holes
remain separate runs; copied texels and mip dependencies are unchanged. Pipeline
families are retained by sample count and output format; shadow variants are
created lazily. Cache families own the handles, while active pipeline pointers are
aliases, preserving single release and partial-creation cleanup. The smaller
uniform clear initializes the complete scene template; every used building slot
is still overwritten before upload.

Render-pipeline counts refer to a fixed integration sequence, not all workloads. The
163 baseline creations came from the preserved migration trace whose application
source matches the merged baseline; the new Debug trace reaches 25. These API/work
counts must not be presented as a measured GPU-time or FPS ratio.

### Backlit shader work

The WGSL lighting function computes `N·L` before direct GGX lighting and shadow PCF.
When that term is nonpositive, direct lighting contributes zero, so the shader
skips those operations. Ambient light, emission, fog and positive-grazing/front-lit
calculations remain. The shadow operation uses explicit-level comparison sampling.

The GPU fixture compiles the production function beside the frozen original from
`a9a2842`. **11,232 cases / 33,696 color channels matched exactly** in the preserved
Debug run. That fixture reduced comparison-sample calls from **29,970 to 16,092**
(46.3%); this is a shader fixture's executed call count, not hardware instructions,
GPU duration or a full-frame speedup. The recorded shader blob is identical in the
measured optimized application.

## Browser validation and measurement

The existing integration suite validates the actual browser renderer, resource
ownership, all styles and quality settings, imagery/elevation/relief, routes,
walking, resize/high DPI, unsupported adapters and shared-device loss. The expanded
checks also verify copy coverage, pipeline reuse, unused-shadow removal and the
frozen-original lighting fixture.

The new [paired Release AOT protocol](../tests/browser/WEBGPU_PERFORMANCE.md)
builds both applications and replays trusted native inputs with one frozen harness
on one runner. It separates CPU render/command recording, input-to-submit,
queue-completion callback latency and a separate continuous-input submission rate.
Capture/query instrumentation is outside timed windows. Exact camera/projection,
scene extent, quality and geometry gates prevent comparing different work. Original
PNGs and quantified image differences are retained.

**Release AOT browser comparison: validation in progress.** This section will be
updated with the completed comparison and any measured limitations. SwiftShader
Vulkan results do not establish physical-GPU FPS, display latency or mobile thermals.

## Reproduction and permanent evidence

- [Native benchmark source and protocol](../tests/PixelMap.Rendering.Performance/README.md).
- [CI comparison JSON](verification/webgpu-performance-16048b7/native-ci-comparison.json)
  and [raw CI archive](verification/webgpu-performance-16048b7/native-ci.zip).
- [Independent local comparison](verification/webgpu-performance-16048b7/native-local-comparison.json)
  and [raw local archive](verification/webgpu-performance-16048b7/native-local.zip).
- [GPU shader fixture results](verification/webgpu-performance-16048b7/shader-fixtures-debug.json).
- [Evidence manifest and hashes](verification/webgpu-performance-16048b7/manifest.json).

Focused native validation includes 74 terrain artwork comparisons, exact label
images/overlay buffers/atlas bytes, 595,630 new large-building depth comparisons,
11,935,261 sparse/eager depth comparisons and 9,063,600 raster comparisons. Large
fixtures prove that offscreen work is actually rejected; they include all styles,
concave/disconnected roofs and holes, clipping boundaries, signed foundations,
nonfinite input, retained frames and scratch reuse. Existing broader application
regressions remain in CI.
