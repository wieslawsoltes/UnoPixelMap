# WebGPU rendering performance

This follow-up to the Uno 7 migration removes CPU preparation stalls, terrain
rebakes, repeated pipeline creation and shader work with no visible contribution.
The production rendering path remains Uno's shared WebGPU device. Quality presets,
physical scene resolution, 4× MSAA in High/Ultra, shadows, HDR bloom, terrain mips,
anisotropic filtering, map styles and native-density labels are preserved.

**Measured application baseline:** `a9a284263da76a201114381467bf45aa4823a2d6`.
**Measured optimization checkpoint:** `16048b7981ba2b08f57f437dc833457a886df56d`.
The timing tables below describe these committed source trees. The later CI
checkout `4dbb962` has the same production `src` tree as `16048b7`. Terrain-only
picking and retained GPU interaction were improved after that checkpoint; their
implementation and exact regression results are documented below. Updated native
and browser measurements for the final source are pending. Migration architecture
and its existing quality limits remain in [the migration guide](uno7-webgpu-migration.md).

## Checkpoint results: native CPU frame preparation

This preserved table comes from the [paired CI run](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43254794602)
on an AMD EPYC 9V74 runner, using isolated .NET SDK 10.0.403 and runtime 10.0.15.
The candidate checkout is `4dbb962`, whose production `src` tree is identical to
`16048b7`; its later changes improve the benchmark and toolchain provenance.
One frozen harness was compiled against both revisions;
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
| pan-pixel | 13.436 | 8.977 | 33.2% | 14.081 → 10.376 |
| walk-pixel | 4.446 | 2.276 | 48.8% | 5.141 → 2.688 |
| orbit-pixel | 13.980 | 9.144 | 34.6% | 14.668 → 11.038 |
| pan-normal | 14.372 | 10.194 | 29.1% | 14.801 → 10.769 |
| walk-normal | 5.020 | 2.422 | 51.7% | 5.699 → 3.097 |
| orbit-normal | 14.539 | 9.915 | 31.8% | 16.619 → 11.354 |
| pan-roblox | 14.768 | 11.238 | 23.9% | 15.942 → 12.022 |
| walk-roblox | 6.598 | 2.901 | 56.0% | 7.464 → 3.543 |
| orbit-roblox | 15.173 | 11.087 | 26.9% | 16.156 → 12.824 |

The nine cases use bundled Manhattan data, a 1200 × 700 logical viewport and
identical deterministic pan, orbit and first-person walking cameras. Smooth styles
retain their existing physical/art resolution rules. Calling-thread CPU time and
managed allocations are preserved alongside wall time; unavailable thread timing
is null instead of zero on unsupported platforms.

An independent local ABBA run also passed all 10,944 identity comparisons and
measured 18–48% lower preparation/overlay time. The isolated-SDK checkpoint CI run measured
24–56% lower time. These are separate machines/runs, not a combined sample pool.
The source, font, harness and renderer hashes are retained for each.

### Checkpoint picking cost and the subsequent fix

The benchmark also performs twelve sparse screen-space terrain picks after each
prepared frame. At the `16048b7` / `4dbb962` checkpoint, labels requested less depth
geometry, but a later terrain pick still demanded the combined terrain/model
sample. Picking consequently became the first consumer of some building geometry.
These checkpoint walking results expose that cost shift:

| Walking style | Picking alone, baseline → checkpoint ms | Preparation + overlay + 12 picks, baseline → checkpoint ms | Combined reduction |
|---|---:|---:|---:|
| Pixel | 0.765 → 2.633 | 5.165 → 5.094 | 1.4% |
| Normal | 1.153 → 3.139 | 6.094 → 5.591 | 8.2% |
| Roblox | 1.222 → 3.480 | 7.739 → 6.470 | 16.4% |

Combined distributions sum the phases **within each sample** before calculating
medians; they are not sums of the phase medians. In the independent local run,
Pixel walking with all twelve picks regressed 12.6% (5.687 → 6.405 ms), despite its
faster preparation/overlay phase. The other local walking totals and all checkpoint
CI totals improved. Frequent-picking applications therefore must use the combined
results rather than applying the preparation-only percentage to their whole frame.
The subsequent terrain-only demand change removes the redundant building
preparation from these queries. The table above remains evidence of the earlier
checkpoint; final-source measurements will establish the resulting timing change.

The application does not issue twelve screen-space picks on every walking frame.
Its movement contacts and terrain-following camera use geographic height sampling.
Map interaction and the visible desktop scale bar request screen-space locations;
camera-driven scale updates are throttled to roughly 100 ms and query two points.
The scale bar is hidden below 790 logical pixels of page width.

The baseline and measured checkpoint had a separate readiness defect: the WebGPU
host prepares frames without legacy overlays, while `TryPickTerrain` and
`TryProjectTerrain` required a nonnull legacy `SKPicture`. Those GPU-only frames
were rejected even when the presented terrain mesh was valid. Relief taps and
scale readouts therefore fell back to camera/DEM unprojection, and pins used the
geographic elevation projection fallback. Feature picking also failed to recognize
the matching retained mesh. The later retained-view fix accepts the renderer-owned
GPU view while preserving camera, viewport and ownership guards. Relief scale readouts,
taps and pins can now use the presented mesh; flat/hidden-relief location queries
retain camera unprojection. Geographic movement-height sampling is unchanged.
These UI callbacks are outside the renderer callback's stopwatch; the paired
browser protocol includes input handling and main-thread work beyond that scope.

## Checkpoint results: source streaming and artwork

These fixed-camera workloads replace one deterministic vector or imagery source
at a time over nine real bundled city tiles. They deliberately isolate source
invalidation from network requests and decoding outside the renderer. Every
presented terrain texture is hashed and compared against the baseline.

| Source update | Baseline median, ms | Optimized median, ms | Speedup | Rebaked visible textures/frame |
|---|---:|---:|---:|---:|
| distant-vector-stream-pixel | 11.814 | 0.650 | 18.17× | 63 → 0 |
| local-vector-stream-pixel | 10.132 | 0.907 | 11.18× | 63 → 1 |
| local-imagery-stream-pixel | 395.295 | 2.479 | 159.45× | 63 → 1 |
| source-priority-control-pixel | 0.655 | 0.625 | 1.05× | 0 → 0 |
| distant-vector-stream-normal | 10.048 | 0.664 | 15.12× | 64 → 0 |
| local-vector-stream-normal | 10.529 | 0.823 | 12.79× | 64 → 1 |
| local-imagery-stream-normal | 263.391 | 1.150 | 229.07× | 64 → 1 |
| source-priority-control-normal | 0.658 | 0.651 | 1.01× | 0 → 0 |

A distant update now retains all visible artwork. A local vector or imagery
replacement rebakes one patch instead of 63–64 in these scenes. Base texture data
falls from 15.75–16 MiB to **256 KiB** per local update; a distant update produces
none. This is the potential base-level texture upload payload, independently
counted from real texture identities. The native harness does not execute a GPU
upload. The actual WebGPU host keeps unchanged texture identities and array layers.

Managed allocations for local imagery updates fell from roughly 16.6–16.8 MB to
281 kB per frame in the same CI run. The source-priority controls rebake no textures
in either implementation; their medians remain within roughly 1–5%. The large
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

### Terrain-only picking

`GpuSceneDepth.TryTerrain` returns the terrain triangle selected independently of
building and roof-rim depth. It now reuses an existing complete cached hit, or
prepares only terrain groups contributing to the queried pixel. Sampling retains
the existing finite-depth checks, interpolation and source-order tie rules across
local cells, large proxies and exact/coarse index-budget fallback. A building over
the queried pixel does not change the underlying terrain intersection.

Terrain-only answers never enter the complete occlusion cache, mark a cell
complete, or consume its model-occlusion query budget. A later `CoversAt` query
still demands the models needed for its altitude and subpixel position. No new
persistent cache or pooled state is introduced. `TerrainSurface.TryPick` obtains
rendered altitude, inverse depth and physical height from one GPU query instead
of querying the same pixel twice; the CPU sampling path is unchanged.

The isolation fixture performs 32 terrain queries in one cell containing 48
overlapping buildings and prepares none of those models. Its first occlusion
query prepares one model; a later complete query prepares all 48. Mixed picking
and visibility tests compare the results bit-for-bit with the frozen eager oracle.

### Retained GPU interaction

The shared `HasRetainedView` predicate recognizes the native geometry generation
through the renderer's retained `activeGpuDepth` reference matching
`activeGpuScene.Depth`; CPU/legacy generations continue to require their retained
picture. The caller's frame and the renderer independently own depth references.
Disposing the caller's frame releases that ownership while terrain picking,
projection and feature inspection remain valid through the renderer's retained
view. Passing a disposed frame to `ActivateGpuFrame` is still rejected.

`TryPickTerrain`, `TryProjectTerrain` and the aligned feature-pick path use this
predicate with their existing camera, viewport, terrain and tile-source checks.
Releasing or replacing the renderer's view ends its eligibility, even if an old
picture survives or another frame reuses the pooled depth storage. A stale GPU
reference cannot authorize a later CPU view without a picture.

GPU-only frames therefore expose the same terrain coordinates, physical heights,
mesh projections and feature picks as equivalent legacy-overlay frames. Tests
cover all four styles, perspective and orthographic views, wrapped worlds,
A→B→A activation with changed terrain/style/viewport, independent caller/renderer
reference transitions with both overlay modes, cached frame wrappers, actual
pooled storage reuse and GPU→CPU→GPU transitions. Geographic height sampling for
movement retains its existing independent contract.

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
- [Isolated-SDK checkpoint CI comparison](verification/webgpu-performance-16048b7/native-ci-sdk-comparison.json)
  and [original raw CI archive](verification/webgpu-performance-16048b7/native-ci-sdk.zip).
- [Picking-inclusive totals derived from raw samples](verification/webgpu-performance-16048b7/native-picking-inclusive.json).
- [Earlier independent CI comparison](verification/webgpu-performance-16048b7/native-ci-comparison.json)
  and [raw earlier CI archive](verification/webgpu-performance-16048b7/native-ci.zip).
- [Independent local comparison](verification/webgpu-performance-16048b7/native-local-comparison.json)
  and [raw local archive](verification/webgpu-performance-16048b7/native-local.zip).
- [GPU shader fixture results](verification/webgpu-performance-16048b7/shader-fixtures-debug.json).
- [Evidence manifest and hashes](verification/webgpu-performance-16048b7/manifest.json).

Native validation now includes:

| Check | Exact comparisons | Coverage |
|---|---:|---|
| Terrain artwork invalidation | 74 | Source replacement/removal, ordering, coverage and retained texels |
| Terrain-only picking | 193,384 | 44 frames; mixed visibility queries, geographic picks, model/cache isolation, ties and fallback |
| Retained GPU interaction | 7,691 | 41 views; exact pre/post caller-disposal answers, independent renderer ownership, GPU-only/legacy equality and lifetime guards |
| Depth index pressure | 15,037 | Actual exact/coarse budget exhaustion, cached answers before the transition and uncached recomputation afterward |
| Large-building depth chunks | 595,630 | Conservative rejection, projection reuse and exact eager results |
| Sparse/eager depth | 11,935,261 | 56 frames; projection, terrain, model, query-order and retained-frame parity |
| Independent depth raster | 9,063,600 | 562 frames; raster coverage and numerical boundary cases |

Label images, overlay buffers and atlas bytes also match exactly. Large fixtures
prove that offscreen work is actually rejected; they include all styles,
concave/disconnected roofs and holes, clipping boundaries, signed foundations,
nonfinite input, retained frames and scratch reuse. Existing terrain/navigation,
feature-picking and CPU fallback checks remain in the complete native CI suite.
The ownership correction also passes the unchanged native GLES reference on
surfaceless Mesa llvmpipe: 408 CPU/GPU mesh intersections, retained-frame picking,
48 rendering cases and complete EGL context destruction/recreation. These checks
exercise queries after the rendering helper disposes its caller-owned frame;
the renderer's independent ownership keeps the displayed view valid. Broader
application regressions remain in CI.
