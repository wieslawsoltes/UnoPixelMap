# WebGPU rendering performance

This follow-up to the Uno 7 migration removes CPU preparation stalls, terrain
rebakes, repeated pipeline creation and shader work with no visible contribution.
The production rendering path remains Uno's shared WebGPU device. Quality presets,
physical scene resolution, 4× MSAA in High/Ultra, shadows, HDR bloom, terrain mips,
anisotropic filtering, map styles and native-density labels are preserved.

**Measured application baseline:** `a9a284263da76a201114381467bf45aa4823a2d6`.
**Optimized production source:** `4a3b9c412ab7bb114795a6d4e5281e97508f4d56`.
**Current validated harness revision:** `f159983c68a72bd4fc8d27d592ba3721bf4689c3`,
whose production `src` tree is unchanged.
The final native comparison measures **19.8–53.3% lower preparation/overlay time**
and **22.4–56.0% lower preparation/overlay time including twelve terrain picks**
across the nine moving workloads. All **10,944 identity comparisons pass**.
An independent native runner reproduces **23.6–55.6% lower picking-inclusive CPU
cost**, again passing all 10,944 comparisons. Debug and Release integration each
pass all 34 scenarios. The complete paired Release AOT browser replay now passes
all four cases and 16 image comparisons. Its timings are mixed: Normal pan/orbit
render CPU medians improve 12.6%/13.6%, while Pixel pan CPU median is 1.0% slower
and paused walking-look input latency median is 6.6% slower. Native CPU gains must
not be presented as a uniform browser or physical-GPU FPS improvement.

The earlier `16048b7` optimization checkpoint and its `4dbb962` CI checkout remain
preserved as separate evidence, including their deferred-picking penalty. The final
tables below include the subsequent terrain-only picking and retained-view
ownership fixes. Migration architecture and its existing quality limits remain
in [the migration guide](uno7-webgpu-migration.md).

## Final source: native CPU frame preparation

This table comes from the [successful native comparison job](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43263360230/job/131340439087)
on an AMD EPYC 9V74 runner, using isolated .NET SDK 10.0.403 and runtime 10.0.15.
Its clean candidate checkout is `4a3b9c4`, full tree
`353460159fc607c7d4c3765a08259f5e0f33781f`; the production `src` tree is
`cf568d7832d23566d87b188f23f8afb18bab1d2a`.
One frozen harness was compiled against both revisions;
all compilation finished before measurements. Baseline/candidate/candidate/baseline
(ABBA) order, an explicit hashed font, 32 warm-up poses and 32 measured poses per
workload/run keep the work comparable. Tiered compilation is disabled for both
native hosts. Each table entry pools 64 measured samples per variant. Recomputing
the complete comparison from the original raw samples reproduces the archived
comparison exactly; the five frozen harness files and driver hashes also match.

Times are **CPU elapsed milliseconds for `PrepareGpuFrame` plus activation and
`PrepareWebGpuOverlay`**, not GPU duration or display FPS. Sparse picking is timed
separately in the raw report. Geometry, projections, selected index/edge streams,
terrain texels, overlay streams, font atlas and picking hashes are compared outside
timing. All **10,944 identity comparisons passed**, including repeated runs.

| Moving workload | Baseline median, ms | Optimized median, ms | Time reduction | Baseline → optimized p95, ms |
|---|---:|---:|---:|---:|
| pan-pixel | 13.241 | 9.348 | 29.4% | 13.939 → 10.786 |
| walk-pixel | 4.418 | 2.409 | 45.5% | 5.042 → 2.789 |
| orbit-pixel | 13.832 | 9.349 | 32.4% | 14.517 → 11.365 |
| pan-normal | 14.255 | 10.648 | 25.3% | 14.732 → 11.523 |
| walk-normal | 4.733 | 2.213 | 53.3% | 5.129 → 2.931 |
| orbit-normal | 14.411 | 10.331 | 28.3% | 15.254 → 12.110 |
| pan-roblox | 14.572 | 11.686 | 19.8% | 15.108 → 12.493 |
| walk-roblox | 6.509 | 3.226 | 50.4% | 7.021 → 3.782 |
| orbit-roblox | 14.537 | 11.471 | 21.1% | 15.545 → 13.149 |

The nine cases use bundled Manhattan data, a 1200 × 700 logical viewport and
identical deterministic pan, orbit and first-person walking cameras. Smooth styles
retain their existing physical/art resolution rules. Calling-thread CPU time and
managed allocations are preserved alongside wall time; unavailable thread timing
is null instead of zero on unsupported platforms.

### Picking-inclusive final results

The benchmark performs twelve sparse screen-space terrain picks after each
prepared frame. These totals sum preparation, overlay and picking **within each
raw sample** before calculating the median and linearly interpolated p95. Adding
phase medians would produce a different and invalid combined distribution.

| Moving workload | Combined median, baseline → final ms | Time reduction | Combined p95, baseline → final ms | Twelve picks alone, baseline → final median ms |
|---|---:|---:|---:|---:|
| pan-pixel | 13.697 → 9.461 | 30.9% | 14.479 → 10.920 | 0.472 → 0.108 |
| walk-pixel | 5.262 → 2.780 | 47.2% | 5.520 → 3.037 | 0.795 → 0.315 |
| orbit-pixel | 14.333 → 9.457 | 34.0% | 15.022 → 11.392 | 0.504 → 0.090 |
| pan-normal | 14.950 → 10.772 | 27.9% | 15.514 → 11.690 | 0.719 → 0.127 |
| walk-normal | 5.840 → 2.571 | 56.0% | 6.324 → 3.220 | 1.132 → 0.300 |
| orbit-normal | 15.269 → 10.441 | 31.6% | 16.002 → 12.147 | 0.778 → 0.113 |
| pan-roblox | 15.417 → 11.962 | 22.4% | 16.019 → 12.864 | 0.812 → 0.261 |
| walk-roblox | 7.748 → 3.980 | 48.6% | 8.287 → 4.425 | 1.257 → 0.717 |
| orbit-roblox | 15.504 → 11.711 | 24.5% | 16.364 → 13.378 | 0.848 → 0.207 |

All nine final workloads improve against their paired baseline when the twelve
picks are included. Calling-thread CPU time, managed allocations, individual
phases and all raw samples remain in the
[original comparison](verification/webgpu-performance-4a3b9c4/native-ci-comparison.json)
and [unaltered archive](verification/webgpu-performance-4a3b9c4/native-ci.zip).

### Independent replication at the current harness revision

The [native job at `f159983`](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43405625411/job/131819284919)
repeats the frozen ABBA comparison on an **AMD EPYC 7763** runner, using the same
isolated SDK/runtime versions. This head includes the browser menu/focus harness
corrections; its production `src` tree remains
`cf568d7832d23566d87b188f23f8afb18bab1d2a`, identical to `4a3b9c4`.
The complete comparison recomputes exactly from the original raw samples, and all
**10,944 identity comparisons pass again**. Preparation plus overlay is
**21.8–53.9% lower**; including all twelve terrain picks is **23.6–55.6% lower**.

| Moving workload | Combined median, baseline → optimized ms | Time reduction | Combined p95, baseline → optimized ms |
|---|---:|---:|---:|
| pan-pixel | 15.841 → 10.618 | 33.0% | 16.704 → 12.145 |
| walk-pixel | 6.008 → 3.135 | 47.8% | 6.562 → 3.667 |
| orbit-pixel | 16.530 → 10.853 | 34.3% | 17.298 → 12.937 |
| pan-normal | 16.901 → 11.798 | 30.2% | 17.743 → 12.402 |
| walk-normal | 6.658 → 2.954 | 55.6% | 7.141 → 3.615 |
| orbit-normal | 17.022 → 11.529 | 32.3% | 17.977 → 13.097 |
| pan-roblox | 17.255 → 13.189 | 23.6% | 18.002 → 13.998 |
| walk-roblox | 8.627 → 4.535 | 47.4% | 9.266 → 5.048 |
| orbit-roblox | 17.485 → 12.997 | 25.7% | 18.280 → 14.811 |

The no-rebake source-priority controls are effectively unchanged in this run:
Pixel is 0.202% slower (0.74255 → 0.74405 ms), and Normal is 0.203% faster
(0.76250 → 0.76095 ms). The [independent replication bundle](verification/webgpu-performance-f159983-native/)
preserves those controls, the original archive, frozen driver, complete comparison,
source/toolchain identity and recomputation audit. Its samples remain separate
from the earlier EPYC 9V74 run; neither runner's CPU timings establish browser FPS.

### Historical checkpoint: deferred-picking cost

The [earlier isolated-SDK CI run](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43254794602)
used checkout `4dbb962`, whose production `src` tree matches checkpoint `16048b7`.
It measured 24–56% lower preparation/overlay time; an independent local ABBA run
measured 18–48% lower time. Both passed 10,944 identity comparisons. Those runs
remain separate from the final-source measurements above, with their own source,
font, harness and renderer hashes.

At the `16048b7` / `4dbb962` checkpoint, labels requested less depth
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
preparation from these queries. The final-source picking-inclusive table measures
the resulting behavior against its own paired baseline; this historical table and
the local regression remain preserved in the checkpoint evidence.

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

## Final source: source streaming and artwork

These fixed-camera workloads replace one deterministic vector or imagery source
at a time over nine real bundled city tiles. They deliberately isolate source
invalidation from network requests and decoding outside the renderer. Every
presented terrain texture is hashed and compared against the baseline.

| Source update | Baseline median, ms | Optimized median, ms | Speedup | Baseline → optimized p95, ms | Rebaked visible textures/frame |
|---|---:|---:|---:|---:|---:|
| distant-vector-stream-pixel | 13.180 | 0.683 | 19.29× | 17.771 → 0.736 | 63 → 0 |
| local-vector-stream-pixel | 9.933 | 0.958 | 10.37× | 14.719 → 1.059 | 63 → 1 |
| local-imagery-stream-pixel | 391.099 | 2.495 | 156.74× | 399.512 → 2.601 | 63 → 1 |
| source-priority-control-pixel | 0.641 | 0.660 | 0.97× | 0.673 → 0.709 | 0 → 0 |
| distant-vector-stream-normal | 10.019 | 0.662 | 15.14× | 14.979 → 0.693 | 64 → 0 |
| local-vector-stream-normal | 10.084 | 0.841 | 12.00× | 15.102 → 0.906 | 64 → 1 |
| local-imagery-stream-normal | 261.993 | 1.158 | 226.26× | 267.568 → 1.268 | 64 → 1 |
| source-priority-control-normal | 0.705 | 0.650 | 1.09× | 0.755 → 0.678 | 0 → 0 |

A distant update now retains all visible artwork. A local vector or imagery
replacement rebakes one patch instead of 63–64 in these scenes. Base texture data
falls from 15.75–16 MiB to **256 KiB** per local update; a distant update produces
none. This is the potential base-level texture upload payload, independently
counted from real texture identities. The native harness does not execute a GPU
upload. The actual WebGPU host keeps unchanged texture identities and array layers.

Managed allocations for local imagery updates fell from roughly 16.6–16.8 MB to
281 kB per frame in the same CI run. The source-priority controls rebake no textures
in either implementation. The Pixel control is **3.0% slower** (0.641 → 0.660 ms),
while the Normal control is 7.9% faster (0.705 → 0.650 ms); both results are retained.
The large imagery speedups describe these source-churn cases, not every moving
frame. Streaming uses a 960 × 640 logical viewport, with 32 warm-up and 32 measured
updates per workload/run, pooling 64 measured samples per variant.

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
source matches the merged baseline; the final Debug and Release desktop traces
each reach 25, with no GPU errors or readbacks. These API/work
counts must not be presented as a measured GPU-time or FPS ratio.

### Backlit shader work

The WGSL lighting function computes `N·L` before direct GGX lighting and shadow PCF.
When that term is nonpositive, direct lighting contributes zero, so the shader
skips those operations. Ambient light, emission, fog and positive-grazing/front-lit
calculations remain. The shadow operation uses explicit-level comparison sampling.

The GPU fixture compiles the production function beside the frozen original from
`a9a2842`. **11,232 cases / 33,696 color channels matched exactly** in both final
Debug and Release runs. That fixture reduced comparison-sample calls from **29,970 to 16,092**
(46.3%); this is a shader fixture's executed call count, not hardware instructions,
GPU duration or a full-frame speedup. The
[original final-source fixture reports](verification/webgpu-performance-4a3b9c4-integration/)
retain zero maximum channel error, 5,184 backlit cases and 1,158 omitted-shadow
cases. The recorded production shader blob `4c19169b9522a0a40a19b6053a550d699717a265`
is identical in the measured optimized application and the preserved checkpoint.

## Browser validation and measurement

The existing integration suite validates the actual browser renderer, resource
ownership, all styles and quality settings, imagery/elevation/relief, routes,
walking, resize/high DPI, unsupported adapters and shared-device loss. The expanded
checks also verify copy coverage, pipeline reuse, unused-shadow removal and the
frozen-original lighting fixture.

The [completed integration run](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43263360196)
passes all four jobs for `4a3b9c4`. Its PR checkout is synthetic merge
`d407757af166a61c6dd38d4008c4abfe37870d30`, whose tree
`353460159fc607c7d4c3765a08259f5e0f33781f` equals the measured application head's
tree. This records the actual compiled source, rather than treating the workflow's
head field as proof of its checkout.

| Integration job | Result | Recorded coverage |
|---|---|---|
| WebGPU presentation preflight | Passed | Independent CSS/GPU colors and completed WebGPU queue; no GPU errors or loss |
| Native regression | Passed | Data, rendering, overlays, retained GLES, routing, simulation, input, camera replay and WebGPU observer lifecycle |
| Debug browser / Interpreter | Passed | 34 scenarios; immutable package identity; 89 boot resources and 210 compressed representations |
| Release browser / InterpreterAndAOT | Passed | 34 scenarios; immutable package identity; 73 boot resources and 254 compressed representations |

Both browser jobs exported 558 production-WGSL façade reference cases, 108 GPU
continuity pairs and the actual RG8 room mip chain before executing the integration
suite. The final deliberate device-destruction case verifies the recovery message
and that the managed scene stops submitting work; its expected console diagnostic
does not indicate an unexpected loss in an earlier scenario. The
[API and terminal-log record](verification/webgpu-performance-4a3b9c4/integration-status.json)
preserves source identity, job conclusions, exact package identities and original
artifact digests. These checks establish correctness and resource behavior; they
do not measure a paired browser performance improvement.
The [original integration reports and captures](verification/webgpu-performance-4a3b9c4-integration/)
preserve the final source's numerical shader/resource observations and browser images.

The [current integration run at `f159983`](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43405625433)
also passes all four jobs and all 61 recorded steps, including **34 scenarios in
each of Debug and Release AOT**. Both browser logs identify actual synthetic merge
checkout `6f0bff0a45db4464044a03bfba52abd8605bebc0`; its full tree
`35584c004b091eedc72198bf15afe8a7f5215dfa` equals the published `f159983` tree.
This validates the menu/focus harness corrections alongside the unchanged
production renderer. The [checkout and validation record](verification/webgpu-performance-f159983-browser/integration-validation.json)
retains the exact terminal log lines, job conclusions, package identities and
harness hashes. Its current artifact digests are explicitly identified as
API-reported; the unchanged source's original integration images remain in the
earlier preserved archive.

The new [paired Release AOT protocol](../tests/browser/WEBGPU_PERFORMANCE.md)
builds both applications and replays trusted native inputs with one frozen harness
on one runner. It separates CPU render/command recording, input-to-submit,
queue-completion callback latency and a separate continuous-input submission rate.
Capture/query instrumentation is outside timed windows. Exact camera/projection,
scene extent, quality and geometry gates prevent comparing different work. Original
PNGs and quantified image differences are retained.

### Completed Release AOT comparison

The [successful paired job at `f159983`](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43405625411/job/131824792372)
completes **16 runs in ABBA order**, four cases, **128 trusted measured inputs** and
152 observed map submissions. Both packages use isolated .NET SDK 10.0.403 and
Release `InterpreterAndAOT`; the browser is Chromium 156.0.8078.4 on an AMD EPYC
9V45 runner with four available processors and the SwiftShader Vulkan adapter.
The recorded four harness hashes match the exact `f159983` Git objects.

Each table cell is **reported median / p95, in milliseconds**, pooling sixteen
measured inputs per variant/case. The frozen browser harness reports the upper
middle sample for its median and nearest-rank p95. With sixteen samples, that
p95 is the maximum observed sample. These conventions differ from the native
harness's interpolated quantiles; the original values are preserved.
“Render CPU” is the sum of managed render/command-recording elapsed time for all
frames attributed to one input. It is not OS thread-CPU accounting or GPU duration.
Input-to-first-submit ends at the first attributed submission, so it need not
exceed the summed elapsed time of every frame caused by that input.

| Workload | Input → first submit, baseline | Input → first submit, optimized | Render CPU, baseline | Render CPU, optimized |
|---|---:|---:|---:|---:|
| Pixel pan | 12.0 / 15.7 | 11.6 / 14.1 | 10.4 / 23.0 | 10.5 / 20.3 |
| Normal pan | 14.4 / 17.8 | 13.0 / 19.3 | 12.7 / 26.6 | 11.1 / 22.6 |
| Normal orbit | 14.4 / 21.8 | 12.8 / 17.4 | 12.5 / 17.6 | 10.8 / 15.5 |
| Normal paused walking look | 18.3 / 24.9 | 19.5 / 25.8 | 18.8 / 29.7 | 18.3 / 33.4 |

Normal pan and orbit render CPU medians fall **12.6% and 13.6%**. Pixel pan CPU
median is **0.96% slower**, although its p95 improves. Paused walking-look input
latency median is **6.56% slower**, and its render CPU p95 rises from 29.7 to
33.4 ms. Normal pan input p95 also rises, from 17.8 to 19.3 ms. These negative
results remain part of the successful correctness-qualified comparison; this
short run establishes no universal browser latency improvement.
Submission CPU p95 also rises for Normal pan (3.4 → 5.7 ms) and Normal orbit
(1.5 → 3.8 ms), despite their lower total render medians. These reported p95s are
the observed maxima of sixteen inputs, not estimates from a large tail sample.

| Workload | Input → first queue-completion callback, baseline median / p95 ms | Optimized median / p95 ms |
|---|---:|---:|
| Pixel pan | 1928.2 / 2031.6 | 1871.2 / 1944.8 |
| Normal pan | 4011.3 / 4091.8 | 3982.0 / 4057.1 |
| Normal orbit | 4017.2 / 4116.0 | 3836.4 / 3899.7 |
| Normal paused walking look | 3096.4 / 3315.9 | 2900.4 / 3129.5 |

Queue-completion callback latency includes queued work and callback scheduling;
it is not a GPU timestamp or display time. Every map case has twelve of sixteen
inputs without texture uploads or resource allocations; walking-look has none.
The full distributions therefore include streaming work. The separate six-second
held-key phase records only two to four map submissions per run, with multi-second
queue-prefix and drain latencies. Its raw submission rates and trajectories are
retained, but these small, scheduling-dependent samples do not establish a stable
throughput improvement or physical-GPU FPS.

All endpoint checks for camera/projection, physical simulation, geometry counts,
scene extent, style and High quality pass. All **16 before/after image pairs** pass the
unchanged tolerance: at most 1% of pixels differing by more than 2/255 and mean
RGB error at most 0.25/255. Across these pairs, at most one pixel differs by more
than 2/255, maximum channel error is 3/255, and mean absolute RGB-channel error is at most
0.000076867/255. This is quantified visual parity, not a claim of exact pixel
identity. Every context records one shared GPU device, zero GPU errors, zero
device losses and no live managed failure. Intermediate browser poses are not
individually compared by the endpoint gate; the separate native replay performs
its own all-pose identity checks.

The [permanent browser evidence](verification/webgpu-performance-f159983-browser/)
preserves the original artifact as two byte-exact parts, all sixteen raw runs and
thirty-two original case PNGs inside the reconstructed archive, the original
report, frozen harness, representative captures, source/runner identity,
verification records and file hashes. Concatenating the two parts reproduces the
original artifact SHA-256; splitting only accommodates the publication size limit.

### Earlier harness failures and correction

The earlier comparison job
in [run 43263360230](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43263360230/job/131348103432)
failed after baseline `pixel-pan-1` completed. Candidate repetition 1 timed out in
`sceneProjection` while opening the Rendering quality submenu and waiting for
settled focus. Its failure capture shows that submenu closed. The
[original failed-attempt archive](verification/webgpu-performance-4a3b9c4/browser-comparison-failed.zip)
is preserved; a baseline-only sample cannot supply a paired speedup. Native,
observer-contract and both package-publication jobs succeeded in that run, while
the failed comparison remains unqualified.

Harness follow-up [`cc1cc04`](https://github.com/wieslawsoltes/UnoPixelMap/commit/cc1cc04)
activates menu items with native pointer input after two layout frames, then
checks the public quality/share result. Keyboard focus acquisition is bounded and
reacquires the current native peer until two stable frames are observed. Contract
regressions cover delayed, restored and replaced focus and dismissals that never
execute the command. The timed input sequence and endpoint camera, scene and image
gates remain unchanged. The initial rerun passed observer contracts and presentation,
but the existing camera-replay fixture still asserted the old helper's polling and
single-handle counts. Follow-up [`a9ef50f`](https://github.com/wieslawsoltes/UnoPixelMap/commit/a9ef50f)
reproduces actual animation-frame focus theft, requires the real helper to recover,
and checks one keyboard activation plus balanced handle ownership on success,
failure and timeout. All five local Node contract suites, the complete current
integration run and the full paired Release AOT replay now pass. The earlier
incomplete attempt remains in the evidence.

SwiftShader Vulkan results do not establish physical-GPU FPS, display latency or
mobile thermals.

## Reproduction and permanent evidence

- [Native benchmark source and protocol](../tests/PixelMap.Rendering.Performance/README.md).
- [Independent native replication at `f159983`](verification/webgpu-performance-f159983-native/),
  including its original samples, frozen comparison driver and verified hashes.
- [Current harness integration validation](verification/webgpu-performance-f159983-browser/integration-validation.json).
- [Completed paired browser comparison, original samples and captures](verification/webgpu-performance-f159983-browser/).
- [Final-source native comparison](verification/webgpu-performance-4a3b9c4/native-ci-comparison.json),
  [original raw CI archive](verification/webgpu-performance-4a3b9c4/native-ci.zip)
  and [source/toolchain manifest](verification/webgpu-performance-4a3b9c4/native-ci-manifest.json).
- [Final-source recomputation audit](verification/webgpu-performance-4a3b9c4/native-audit.json)
  and [evidence manifest with hashes](verification/webgpu-performance-4a3b9c4/manifest.json).
- [Final-source integration job and package record](verification/webgpu-performance-4a3b9c4/integration-status.json).
- [Unqualified paired browser attempt](verification/webgpu-performance-4a3b9c4/browser-comparison-failed.zip).
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
