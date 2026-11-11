# Rendering performance

> Historical renderer measurements: the CPU, GL and Skia performance results in this document precede the Uno 7 WebGPU migration. See [the migration record](uno7-webgpu-migration.md) for the current pipeline and its separate verification. These measurements do not establish a WebGPU speedup.

The original CPU optimization measurements compare the renderer with commit `31efd232a2e6e273fd0a56f23e8d50319d29f433`. The retained GPU terrain implementation builds on elevation commit `c238352dcb9df7fd2cfb8ba5e86fd20f38169597`. GPU rendering and Pixel art are enabled by default. The View menu can switch to the CPU backend.

## Early label occlusion and aligned terrain textures · 10 November 2026

Sparse Boolean visibility now stops preparing model geometry once an opaque model
or roof rim permanently proves occlusion. All contributing terrain is resolved
first, preserving continuous subpixel terrain sampling and the original depth
tolerances. An early result is never cached as a complete pixel sample; later
picking and height queries still demand omitted geometry. Existing complete-cell
and memory-pressure fallbacks remain active.

The isolated four-process ABBA comparison changes only `GpuSceneDepth.cs` from
`ce82b2c`. The same compiled harness and dependencies measure fixed camera poses,
including label preparation and the navigation/overlay pass:

| Native preparation + navigation | Baseline median | Candidate median | Change |
| --- | ---: | ---: | ---: |
| Manhattan walking | 5.9254 ms | 4.6848 ms | −20.94% |
| Manhattan chase | 10.1424 ms | 7.5728 ms | −25.33% |
| Manhattan free flight | 7.3768 ms | 6.4162 ms | −13.02% |
| Manhattan orbit | 13.4515 ms | 11.4957 ms | −14.54% |
| Manhattan Normal flight | 7.9400 ms | 7.0940 ms | −10.66% |

The same final sequence records higher hill medians: +8.58% Pixel, +16.00%
Minecraft and +13.71% Roblox. Process spread is substantial, including changed
timings in unchanged constructor work. Separate instrumentation shows that these
hill fixtures never enter the new Boolean traversal, but that does not rule out
effects from ordinary sampling refactoring or JIT layout. All regressions,
exploratory sequences and per-process values remain in the
[complete visibility evidence](verification/occlusion-20261110/README.md).

For the city views, total prepared triangles after labels/navigation decrease
1.56–12.46% across the exact measured camera sequences. The final auditor verifies
3,168 query/overlay hash comparisons and 7,920 frame/geometry/range comparisons.
This is native CPU preparation evidence, excluding GL and browser presentation.

### Exact terrain texture copying

New opaque vector-terrain patches can copy or replicate pixels from the existing
world-aligned block directly into their destination bitmap. Power-of-two grid
alignment reduces nearest-neighbor sampling to integer shifts and span copies or
fills. Partial coverage, distinct duplicate sources, photographs and transparent
overlays retain ordered Skia compositing. There are no additional persistent
buffers, and the existing block/surface cache budgets remain unchanged.

An independent ABBA changes only the two terrain production files. Isolated
16-texture bake batches improve 60.54% for identity copies, 17.30% for 2×
magnification, 86.01% for 16×, and 87.17% for 256×. Warm source blocks and output
bitmap allocation outside the timer define this narrow scope. Managed allocation
decreases by 104 bytes per eligible texture.

The public `PrepareGpuFrame` comparison uses fixed, advancing camera positions
and records actual texture misses, rather than revisiting only a warm working set:

| Native frame preparation | Baseline median | Candidate median | Change | Eligible misses / all misses per 64 frames |
| --- | ---: | ---: | ---: | ---: |
| Walking first person | 4.7428 ms | 4.4901 ms | −5.33% | 164 / 183 |
| Driving first person | 9.7549 ms | 9.3808 ms | −3.83% | 587 / 615 |
| Driving chase | 11.0367 ms | 11.0308 ms | −0.05% | 586 / 621 |
| Fully retained control | 6.1395 ms | 6.1879 ms | +0.79% | 0 / 0 |

Walking process medians overlap. Both first-person driving candidate process
medians are below both baselines. Chase is effectively flat; the retained control
is slightly slower and executes no texture-copy work. All 1,024 frame controls
and 16,384 isolated texture controls match exactly. The
[terrain evidence](verification/terrain-copy-20261110/README.md) retains raw
samples, allocations, cache diagnostics, hashes and reproduction tools.
Percentages from the two isolated optimizations cannot simply be added.

### Verification and controlled browser comparison

The integrated native suites pass 11,935,261 sparse/eager depth comparisons,
9,063,600 projected-raster comparisons and 768 exact texture comparisons against
Skia, plus 42 asymmetric interior-pixel boundary checks at extreme zooms. The
complete GLES report is byte-identical to the baseline, including all
192 standard image hashes. Held frames, source changes, picking and index-budget
exhaustion remain covered. [Integrated native checks](verification/rendering-20261110/validation.json)

The [camera replay](camera-replay.md) adds a controlled browser workload alongside
the existing held-key play benchmark. It retains the ordered native inputs and
every submitted projection value and frame sequence, including asynchronous
tile refreshes. Cross-artifact timing comparisons require exact sequence identity.
It measures controlled input/render latency; it does not replace simulation
throughput measurements or establish physical display FPS.

The [same-artifact preflight](verification/camera-replay-preflight-20261110/README.md)
reproduces all 96 trusted inputs and 102 ordered, individually fenced submissions
in each of two independent captures. Its complete raw records and offline verifier
establish repeatability of the observer before the cross-artifact comparison.

### Current release browser measurements

Source [`dbde48e`](https://github.com/wieslawsoltes/UnoPixelMap/commit/dbde48e9fd5318ef309bf31ed4b9b7e1596ec7d6)
passes all four jobs in [run 42907891884](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42907891884)
and is deployed to Pages. The live page loads the checked
`package_801cbc2830341569438a9ce8c44febc16626f1501278ed456482a888efe98347`
bootstrap. Functional validation passes all camera rigs, styles, terrain,
CPU/GPU journeys, native gestures, pointer lock, touch takeover and nine DPR2
close-camera checkpoints. All six direct/detached presentation comparisons match:
1,782,000 map pixels and 5,616,000 complete-canvas pixels have zero differences.

The comparison remeasures the exact `555990d` Pages artifact from run `42696985047`
on the same runner as the candidate. Chromium 156.0.8078.4, SwiftShader, AMD EPYC
9V45, affinity 0–2, viewport 1200 × 780, DPR 1 and the harness are shared. SDK
10.0.403 and .NET/WASM 10.0.14 match the baseline; this does not include a runtime
upgrade. Primary summaries are medians across independent process values. The
older weighted rates/occupancy and pooled within-process distributions remain
available as secondary measures.

#### Exact submitted-camera workloads

Three scenarios each run four fresh ABBA processes. All 384 trusted native inputs
and 408 individually fenced map submissions pass the complete ordered projection
contract, including every tile-refresh frame. All nine comparisons against the
first baseline and the independent raw audit pass. No tolerance, frame deletion,
sorting or deduplication is applied to workload identity.

| Controlled workload | Task CPU/input, baseline → candidate | CPU change | First-submission median, baseline → candidate | Latency change |
| --- | ---: | ---: | ---: | ---: |
| Map pan over relief | 74.916 → 58.652 ms | −21.71% | 24.95 → 21.30 ms | −14.63% |
| Map orbit and tilt | 101.214 → 67.089 ms | −33.72% | 31.40 → 28.40 ms | −9.55% |
| Paused first-person look | 111.701 → 69.496 ms | −37.78% | 72.85 → 39.85 ms | −45.30% |

For latency, each process contributes its own 32-input median; there are two
processes per variant. The corresponding process-p95 first-submission values are
151.15 → 62.35 ms for pan, 235.05 → 75.85 ms for orbit, and 268.05 → 117.25 ms for
paused look. Some individual inputs remain slower, and all matched-step changes
are retained. CPU includes observation/automation overhead. These barrier-paced
cases measure controlled CPU/latency, not maximum throughput or locomotion FPS.

Fence fields record when the application observes a signal. They include polling
cadence and software-GPU work; they do not directly measure actual GPU completion
or physical display latency. The roughly one-second map fence observations do
not establish one second of visible lag. Exact projections establish workload
identity, with pixel correctness supplied by the separate rendering checks.

#### Moving play workloads

The unchanged 30-process held-key protocol runs stationary AB and moving ABBA
comparisons with an 8,000 ms requested timing window. Task CPU is the main-thread
`TaskDuration` delta; CPU occupancy uses the longer CDP metric interval, including
probe-result transfer. Submission rates use actual probe duration. Each moving
entry below summarizes two processes per variant.

| Moving workload | Task CPU, baseline → candidate | CPU change | Submitted maps/s, baseline → candidate | Rate change |
| --- | ---: | ---: | ---: | ---: |
| Walking, first person | 2,554.26 → 2,443.66 ms | −4.33% | 8.985 → 8.809 | −1.95% |
| Walking, chase | 3,381.87 → 3,160.36 ms | −6.55% | 13.452 → 14.042 | +4.38% |
| Driving, chase | 4,165.60 → 3,378.86 ms | −18.89% | 9.866 → 9.666 | −2.03% |
| Driving, first person | 3,560.23 → 2,837.00 ms | −20.31% | 7.196 → 7.684 | +6.78% |
| Free flight | 2,791.83 → 2,620.19 ms | −6.15% | 10.308 → 10.682 | +3.63% |

Every candidate moving Task CPU observation is below both baseline observations
for its scenario. Map submission rates remain mixed; this is not a general FPS
improvement. Chase driving's median submission interval rises 89.95 → 96.60 ms.
Flight's second candidate repetition has a lower map rate than both baselines,
despite its higher two-process summary. First-person driving's rAF p95 decreases
33.3 → 16.8 ms, but rAF measures callback scheduling rather than map rendering or
physical presentation.

Held-key paths can differ. Chase-driving distances are 175/174 m versus 179/177 m;
first-person driving is 161/156 m versus 154/155 m. Thus these end-to-end results
do not isolate a patch or prove identical submitted trajectories. The fixed-camera
replay provides the separate controlled comparison.

Stationary windows have zero map submissions. Their single-process Task CPU
observations include first-person walking 349.14 → 381.39 ms (+9.24%), default-map
idle +2.82%, and stationary flight +0.15%; relief idle and stationary chase driving
decrease 6.74% and 3.92%. Observed held-key fence p95 also rises 4.31% for walking
first person and 16.49% for flight. Those fence samples are capture-censored;
carry-in signals and unobserved tail submissions are counted explicitly. All
regressions and repetition values remain in the evidence. These small sample
counts support descriptive comparisons, not confidence intervals.

All 1,614 measured maps use the compositor, with the same 300 × 166 artwork and
pixel size 4: 799 baseline and 815 candidate. There are zero map readbacks or PBO
copies, at most two pending direct frames and no release before a fence signal.
Hardware GPUs and Safari/iOS are outside this performance measurement.

All 14 separately captured CPU profiles are retained and symbolicated from each
exact published package. Their 92,416 samples and time deltas are unchanged;
only resolvable method names are replaced. Profiles identify remaining work but
are not costs from the preceding timing window and cannot causally explain a
particular throughput result.

[Complete browser evidence](verification/browser-rendering-dbde48e/README.md) ·
[All process values and distributions](verification/browser-rendering-dbde48e/analysis.json) ·
[Independent replay audit](verification/browser-rendering-dbde48e/replay-independent/README.md) ·
[Pixel oracles and functional validation](verification/browser-rendering-dbde48e/functional/README.md) ·
[Exact-package profiles](verification/browser-rendering-dbde48e/profiles/README.md)

## Camera clearance and exact Earth preprocessing · 9 November 2026

Third-person follow now checks terrain clearance at the actual interpolated eye.
Previously, both spring endpoints could be above the surface while their chord
crossed below an intervening ridge. The final eye keeps the existing 35 cm floor,
rendered-mesh elevation and vertical exaggeration, with unchanged horizontal
position, orientation and lens. First-person and free-flight controls retain their
existing behavior. Twenty-four regression poses start with safe endpoints and
verify an unsafe midpoint before correction, including signed terrain and viewport
changes. Reusing DEM samples reduces explicit surface queries in the follow method
from two to one for relief first-person, five to three for smoothed relief chase,
and three to one for flat chase. These counts exclude subsequent camera
normalization and rendered-mesh queries.

Earth image preprocessing specializes the common 256-to-128 reduction as an exact
integer 2×2 sum in linear light. Smooth output clusters share palette unpacking and
their four Bayer decisions. Irregular image dimensions retain the generic box
integral; nearby detail, palette choices and world-aligned dither remain exact.
The frozen-raster comparison covers 74 art/natural textures containing 3,989,462
pixels and all 131,072 palette-key/even-phase clusters, including codec guards,
replacement and cache reuse.

| Native imagery batch | Previous median | New median | Change |
| --- | ---: | ---: | ---: |
| World overview | 1.6676 ms | 1.3472 ms | −19.21% |
| Nine Garda tiles | 14.9205 ms | 11.8351 ms | −20.68% |
| Nine Manhattan tiles | 20.0956 ms | 17.5198 ms | −12.82% |
| All 19 bundled tiles | 36.6291 ms | 30.9744 ms | −15.44% |

The isolated four-process ABBA run uses the same harness and dependencies, changing
only the renderer assembly. It includes decode, resampling, palette lookup,
dithering and cache insertion. The bundled-batch p95 changes from 51.4324 to
42.9544 ms; measured managed allocation remains 12,848 bytes per batch. All 33
benchmark control images match. One synthetic 512-to-128 generic-path median is
1.08% slower, and the first cold world-image call is slower in both candidate
processes; cold calls include JIT/static initialization. These are native
preprocessing measurements, not frame-rate gains.
[Raw measurements and provenance](verification/earth-raster-20261109.json) ·
[Reproduction harness](verification/earth-raster-harness/README.md)

Retained GLES/WebGL artwork images now allocate immutable RGBA storage without a
pixel source. Cached detached frames avoid all seven unpack-state queries and
their corresponding state changes. Cached images adopted by Skia preserve only
the two row lengths that Skia resets; actual pixel uploads still capture and
restore the complete unpack state. The desktop OpenGL 3.3 allocation path keeps
its original API requirements. The host-state regression additionally covers
nondefault pack row lengths, failed uploads, resize, retained textures and delayed
Skia playback. All 192 standard GPU/CPU/artwork image hashes remain unchanged,
alongside navigation, source-order and context-recreation checks.

GPU building-cache pruning now runs when the source geometry generation changes.
Camera-only frames avoid a copied key array and redundant scan. Replacement,
33-tile eviction, style reset and held-frame replay are covered explicitly. An
eight-frame control removes exactly 72 managed bytes per frame for six retained
keys, with identical overlay fingerprints. This control issues no independent
depth queries, and no timing gain is claimed.
[Allocation evidence](verification/gpu-building-cache-20261109/summary.json)

### Browser measurements for source 555990d

Source release [`555990d`](https://github.com/wieslawsoltes/UnoPixelMap/commit/555990d0e086ffb9908d4d98ec30a73a245a6c6e)
passed all four jobs in [run 42696985047](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42696985047):
native/build/package checks, browser validation, the paired benchmark and Pages
deployment. At that release's validation, the live site served the same checked package. Browser validation
includes CPU/GPU journeys and terrain, all five camera rigs, four styles, pointer
lock, touch takeover and DPR2 close-camera recovery. All six compositor-versus-
detached presentation cases have zero differing map pixels and exact canvas
output, covering 1,782,000 compared map pixels.

The workflow remeasures the exact `53e9c68` Pages artifact from run `42415166135`
alongside `555990d`; it does not reuse timings from the earlier runner. Thirty
fresh browser processes cover five stationary AB controls and five moving ABBA
comparisons. Moving scenarios have two repetitions per variant, with an 8,000 ms
requested timed window and a separate profile capture. Both variants use Chromium
156.0.8078.4, SwiftShader, an AMD EPYC 7763 runner, affinity `0,1,2`, a 1200 × 780
viewport and DPR 1. CPU occupancy is total task CPU time divided by the total
`Performance.getMetrics` interval; submission rates use total submissions divided
by the total captured interval. The metric interval includes probe-result transfer.

| Moving workload | Main-thread CPU occupancy, baseline → candidate | Submitted map images/s, baseline → candidate | Submission-rate change |
| --- | ---: | ---: | ---: |
| Walking, first person | 26.29% → 26.80% | 5.623 → 5.622 | −0.025% |
| Walking, chase | 32.08% → 32.85% | 9.209 → 8.911 | −3.24% |
| Driving, chase | 46.09% → 43.44% | 6.123 → 6.055 | −1.10% |
| Driving, first person | 43.07% → 42.02% | 4.206 → 3.932 | −6.51% |
| Free flight | 35.34% → 35.72% | 4.866 → 4.747 | −2.46% |

These measurements do **not** show a general play-mode throughput improvement.
Driving chase reduces mean main-thread task CPU time from 4,448.01 to 4,224.99 ms
(−5.01%), while its map submission rate is slightly lower. Walking chase has
2.41% more task CPU work and a 3.24% lower submission rate; both repetitions have
longer map-submission p95 intervals. First-person driving has a 6.51% lower submission
rate, with both candidate repetitions below both baseline repetitions. Its
pooled median submission interval rises from 237.1 to 260.7 ms, despite a lower
submission p95. These regressions remain part of the release evidence.

The first-person driving trajectories differ: baseline distances are 153/152 m,
candidate distances 170/158 m. Walking-chase distances match at 14/13 m. These
held-key workloads preserve the observed end-to-end differences but do not isolate
one optimization's contribution. Two repetitions provide observed spread, not
statistical confidence. The moving scenarios use the Map layer and therefore do
not measure the separate Earth preprocessing gains above.

All 475 candidate submissions use the compositor context at 300 × 166 art pixels
(pixel size 4), matching the baseline's artwork resolution and pixel size. There are zero map
readbacks, zero PBO copies, at most two outstanding direct frames and no premature
frame releases. Stationary windows submit no map images. rAF p95 is about 16.8 ms
for the other moving workloads and 50 ms for first-person driving in both variants;
rAF counts browser scheduling, not map rendering or physical display FPS. These
measurements use a software GPU and do not establish physical-GPU or Safari/iOS
performance.

[Complete browser evidence and reproduction](verification/browser-rendering-555990d/README.md) ·
[Thirty original reports and raw probes](verification/browser-rendering-555990d/manifest.json) ·
[Audited per-run values and pooled distributions](verification/browser-rendering-555990d/analysis.json) ·
[Functional validation and live capture](verification/browser-rendering-555990d/validation.json)

## Selective depth preparation and reusable pixel rows · 7 November 2026

Source release [`53e9c68`](https://github.com/wieslawsoltes/UnoPixelMap/commit/53e9c684a45ff1224c91ebeeefbb4d321c1c8c8b)
continues from the accepted compact-mesh release `c937ddd`. It removes
noncontributing subpixel triangles from the CPU visibility index, reuses exact
Pixel window-filter state across a wall row, and rejects irrelevant collision
polygons before expensive coverage tests. OpenGL still draws the same geometry.
The new depth rule changes CPU query preparation, not the camera projection,
artwork resolution, visual detail or GL shader.

### Choosing the work from the shipped WASM profiles

The new [symbolication script](../scripts/symbolicate-browser-profile.py) resolves
ordinary Mono AOT methods through the **exact published** Webcil metadata,
per-assembly method-pointer tables and WASM function table. It validates native
module identity and byte locations, retains aliases, and leaves unresolved
native/runtime/generic extras anonymous. An independent metadata reader matches
115,694 MethodDef rows across 66 assemblies. All seven recovered candidate
profiles preserve their 46,866 samples and every field except function names.

The previous release's first-person walking profile attributes 1,951.217 ms of
8,129.817 ms sampled weight to depth visibility; 1,900.424 ms of that is under
label drawing. Labels also contain 98.0% of sampled depth cost in free flight
and 98.1% in first-person driving. This identifies depth preparation as useful
work to reduce. These profiles were captured separately from timed windows;
sample weights do not measure invocation counts or establish the cause of a
particular throughput result. GL-state capture and terrain baking remain other
observed costs. [Exact package, validation, recovered stacks and limitations](verification/browser-rendering-c937ddd-symbolicated.json)

### Exact removal of raster-empty depth geometry

An artwork pixel is queried at `(x + 0.5, y + 0.5)`. For a triangle's original
half-open horizontal bound `[L, R)`, eligible integer columns are
`[ceil(L - 0.5), ceil(R - 0.5))`, intersected with the viewport. The vertical
interval follows the same rule. An empty interval cannot contribute to any
query. A box containing only one pixel center can also be rejected when the
original triangle coverage test rejects that center.

The implementation computes those intervals only when at least one projected
extent is at most one art pixel. Its non-inlined helper keeps the range
calculations out of the ordinary-face path. Subtraction and ceiling use double
precision to preserve the float bounds around half-pixel ties, and clamping
precedes integer conversion. Viewports beyond the exact float half-integer
range keep the original conservative path. Surviving triangles retain their
coverage/interpolation arithmetic and source order; roof-rim sampling is
unchanged. [Derivation and compatibility checks](sparse-visibility.md)

The base grid remains 16 × 16 art pixels. Power-of-two cell lookup uses shifts,
and an already-prepared cell traverses its contiguous candidate span. The active
16 MiB membership-buffer limit, 8 MiB pool limit, 16,384-cell bound and exact
pressure fallback remain unchanged. `Vertex` remains 28 bytes and `Triangle`
84 bytes. Smaller-grid and cached-projection prototypes were measured and
rejected because they regressed other workloads. Their data are separate from
the selected implementation's evidence.

### Paired native preparation measurements

Four fresh processes run baseline → candidate → candidate → baseline, with CPU
affinity 0, two managed processors, tiered compilation disabled and .NET 10.0.14.
Both variants use the same harness and dependencies; only `PixelMap.Rendering.dll`
differs. Ordinary workloads use 16 warmups and 64 measurements per process;
the two controls use four warmups and eight measurements. The complete export
contains 2,112 measured frames. Medians use the lower middle sample and p95 uses
nearest rank over the two corresponding process blocks.

The measured total is `PrepareGpuFrame`, including labels, plus the route,
vehicle, gate and picking overlay pass. It excludes GL submission, presentation
and free-roam simulation. These are CPU preparation times, not browser frame
rates.

| Workload | Median, baseline → selected | Median change | p95, baseline → selected |
| --- | ---: | ---: | ---: |
| Manhattan walk | 8.9250 → 6.9148 ms | −22.52% | 10.3123 → 9.9563 ms |
| Manhattan chase | 12.1925 → 9.8855 ms | −18.92% | 14.1184 → 12.3519 ms |
| Manhattan Pixel free flight | 9.2437 → 9.3068 ms | +0.68% | 10.9395 → 12.6397 ms |
| Manhattan orbit | 15.9121 → 16.1028 ms | +1.20% | 18.0794 → 22.2703 ms |
| Manhattan Normal free flight | 10.1261 → 10.4410 ms | +3.11% | 12.1448 → 14.8408 ms |
| Hills, Pixel | 1.0033 → 1.0219 ms | +1.85% | 1.3113 → 1.2139 ms |
| Hills, Minecraft | 1.3639 → 1.2074 ms | −11.47% | 2.0282 → 2.2060 ms |
| Hills, Roblox | 1.4968 → 1.5203 ms | +1.57% | 2.2980 → 2.1526 ms |
| Zero independent-query control | 10.0374 → 11.2505 ms | +12.09% | 28.1218 → 30.5191 ms |
| Full independent-query control | 10.6648 → 9.8703 ms | −7.45% | 29.0350 → 30.1114 ms |

Walking and chase improve in both candidate processes. Flight, orbit and Normal
have higher wall-time **and thread-CPU p95**, and Normal's median regression is
present in both candidate processes. The zero-query control's slower total is
mostly frame preparation, which still includes label queries; only the separate
diagnostic index receives zero queries. Its 16 samples per variant are not a
large latency study. The full-query control independently visits 79,639 pixels,
exceeding the 65,536-entry answer cache, so its repeated query pass is not a
pure cache-hit measurement. Two processes per variant do not provide confidence
intervals or establish uniform latency improvement.

Median retained CPU triangles after frame and overlay work fall from 34,714 to
10,255 for walking and 39,208 to 12,071 for chase. Their maximum recorded
membership-buffer capacities fall from 520,192 to 260,576 bytes and from
978,064 to 492,560 bytes, respectively. These counts describe the CPU visibility
index, not GL mesh reduction or total application memory. The whole-frame
allocation measurements are essentially unchanged after warmup; pooled capacity
includes earlier workloads. Every recorded exact/coarse index remains valid,
and membership-buffer capacities stay below the 8 MiB pool threshold. These
counters do not measure all other workspace pool-admission limits.

[Complete selected native evidence](verification/native-depth-selective-20261107/)
contains raw reports, all samples, exact source/binary identities, phase results
and an independent audit. The auditor reconstructs the CSV byte for byte,
recomputes 1,120 per-block summary fields, and matches 11,088 corresponding
query/overlay/depth-range fingerprint fields across the four processes.

### Reusing Pixel window-filter rows

For perspective walls with constant reciprocal depth and no horizontal
altitude-over-depth gradient, the filter prepares story coverage, lamp-map row
offsets, interpolation fractions and seed once per raster row. The inner loop
retains its original horizontal increments, division order, bilinear arithmetic,
color association and depth test. Exceptional and ineligible rows use the
general path. The specialized loop lives in a non-inlined helper: an earlier
inline prototype improved filtered cases but regressed the near-native fixture
by 11.06%, and was rejected.

The final four-process native CPU `Draw` + `Flush` comparison contains 5,120
samples. Median changes are **−3.86% near, −16.31% distant, −2.57% grazing,
−0.55% far and +1.56% near-native**. The remaining near-native difference lies
within the observed baseline block spread; it is retained as a measured
regression, not turned into a universal speedup claim. Whole draws still
allocate 1,552–1,632 measured managed bytes in this fixture. All retained
quality metrics, reference-image hashes and nearby-frame outputs match the
previous filter. [Algorithm, exact row oracle, timings and scope](pixel-window-filtering.md)

### Collision candidate reduction

`VectorCollisionWorld` retains inclusive polygon bounds and rejects disjoint
points or swept building bounds before polygon/interior tests. Aircraft and
free flight avoid irrelevant water coverage. Established collision facts
short-circuit further work while preserving bridges, tunnel overrides and
escape from newly streamed geometry. The frozen previous implementation agrees
on **69,552 differential sweeps**, and the full simulation suite passes.

Synthetic paired batches reduce dense short-walk median time by **21.60%**,
dense clear-body time by **26.56%**, and complex-water flight time by **92.73%**.
The ground cases allocate zero measured managed bytes after warmup; flight
allocates 83,072 bytes per 9,152-query batch in both variants. Ground-case p95
increases in this short sequence. This is a same-process ABBA comparison without
explicit affinity or tiering controls, separate from the native rendering
protocol. Collision occupies a small part of the sampled browser workload;
these percentages are not application FPS gains. [Raw batches, oracle provenance
and independent statistics](verification/collision-broad-phase-20261107.json)

### Regression and release evidence

The complete native rendering suite passes, including the existing 6,612,305
sparse/eager comparisons, 8,202 memory-pressure comparisons and 845,516 mesh
vertex comparisons. The new projected-raster suite adds **9,063,600 exact
comparisons across 562 frames** for half-pixel/cell boundaries, winding, one-row
and one-column coverage, source-order ties and the large-coordinate fallback.
The pixel-filter oracle adds 222,912 reused-row samples to the 65,536 exact
scalar/SIMD/prepared-row comparisons. Existing camera coverage includes 12,424
already-aligned aim poses, 486 inset-aim cases, 84 physical-camera poses and 432
steep-terrain follow anchors.

The final native GLES report is byte-for-byte identical to `c937ddd`, including
all **192 standard image hashes**, three duplicate-imagery hashes, 24 navigation
configurations, 48 source-order permutations, and draw/upload/residency controls.
Context destruction/recreation and retained-frame replay also pass.
[Frozen final GLES audit](verification/gles-final-20261107-audit.json)

Browser release validation and the paired benchmark use
[run 42415166135](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42415166135),
with the exact `c937ddd` Pages artifact pinned by baseline run `42377138938`.
The run completed successfully, including browser verification, deployment and
the paired benchmark. The recovered evidence now retains all 30 original
reports, package provenance, browser/runner identity and independently computed
aggregates in [browser-rendering-53e9c68](verification/browser-rendering-53e9c68/summary.json).

| Moving workload | Main-thread CPU occupancy, baseline → candidate | Relative change | Map submissions/s, baseline → candidate |
| --- | ---: | ---: | ---: |
| Walking, first person | 34.852% → 26.455% | −24.09% | 5.356 → 5.607 |
| Walking, chase | 34.023% → 33.642% | −1.12% | 8.833 → 8.715 |
| Driving, chase | 43.827% → 44.784% | +2.18% | 6.302 → 6.246 |
| Driving, first person | 47.631% → 40.550% | −14.87% | 4.137 → 4.215 |
| Free flight | 35.444% → 32.513% | −8.27% | 4.801 → 5.183 |

Moving cases use two fresh processes per variant in ABBA order. Walking
first-person rAF p95 changes from 33.35 to 16.80 ms; driving first-person changes
from 66.65 to 49.95 ms. Chase throughput remains slightly lower, and the driving
chase CPU result regresses. Stationary controls have one run per variant and
remain available in the complete comparison; their small CPU occupancy is not
a reliable basis for a general speedup claim. All 486 candidate map submissions
use the compositor context, with zero readbacks and at most two images pending.

These are Chromium/SwiftShader scheduling and submission measurements, not
physical-GPU display rates. External providers are blocked, adaptive art
resolution remains active, and the reports preserve pixel-size distributions
and before/after vehicle positions. Native Safari/iOS and physical-GPU timing
remain unmeasured. This evidence closes the previous release's pending benchmark
record; it is separate from subsequent source changes.

## Previous release: moving-frame preparation and pixel-art filtering · c937ddd

The current pass targets work performed while the camera moves. Conservative screen-space bounds defer exact depth geometry until picking or overlays query it. Visibility then reduces finite terrain, face and rim depths without sorting the cell contents; source-order keys preserve equal-depth terrain picks and the original rim tolerance. The independent eager reference matches **6,612,305 queries across 55 frames**. Label layout rejects off-screen, duplicate and colliding candidates before occlusion where ordering permits, with an exact compatibility path for ambiguous sort ties. Forty synthetic and real-city images preserve the original visible glyphs. [Depth algorithm, proof, bounds and measurement protocol](sparse-visibility.md)

The moving chase fixture traverses 118 terrain artwork tiles, exceeding the previous 96-entry cache and repeatedly rebaking 60–64 tiles per subsequent 16-view cycle. A GPU-only texture now releases its duplicate CPU bitmap; an opaque bitmap can be reconstructed exactly from the retained RGB data when CPU fallback needs it. The cache uses the former worst-case **48 MiB** allowance as a byte budget, fitting up to 192 GPU-only entries while retaining the 96-entry CPU limit. The corrected fixture creates **118 / 0 / 0** textures over its first three cycles. Nonopaque originals remain intact, and GPU arrays held by pending frames remain immutable. This budget covers the renderer's texture cache, excluding caller-held frames, transient baking and the separate GL resource cache. [Cache regressions](../tests/PixelMap.Rendering.Tests/TerrainTextureCacheChecks.cs)

Building selections retain face indices and roof-rim vertices independently when only the other stream's visibility changes. This preserves immutable-array upload identities while avoiding unnecessary copying and uploads. Hardware back-face culling also removes hidden building fragments before the pixel shader; outward cap winding and the caller's cull mode are preserved. The isolated culling change retains all 192 original standard GLES image hashes, but its standalone software-GPU timing was mixed, so no isolated percentage speedup is attributed to it. [Selection checks](../tests/PixelMap.Rendering.Tests/GpuBuildingSelectionChecks.cs) · [Culling checks](../tests/PixelMap.Rendering.Tests/GpuCullingChecks.cs)

Perspective Pixel façades now integrate periodic window coverage over the art-pixel footprint and filter the deterministic lamp pattern. The distant fixture's RGB error falls by **77.5–78.1%** against a finite 4×4 spatial reference, while all 64 nearby CPU/GPU frame comparisons remain exact. This improves quality at a measured shader cost: the stressed pixel-size-4 wall fixtures add **0.011–0.495 ms** on llvmpipe. [Pixel-art algorithm, visual evidence, raw samples and limitations](pixel-window-filtering.md)

The current four-process native comparison, at `c937ddd`, measures complete GPU-frame preparation plus a navigation-overlay stress pass against the original `ab765d2` release. All 2,112 samples preserve corresponding query and visible-overlay hashes. Hills preparation falls by **58.1% in Pixel, 65.8% in Minecraft and 76.3% in Roblox**. Manhattan walk, chase, free-flight, orbit and Normal free-flight medians fall by **5.6%, 9.5%, 4.3%, 3.9% and 3.8%**, respectively. Chase allocation falls from **2,081,536 to 724,915 bytes per frame**, a **65.2%** reduction. Tail improvements are not universal: free-flight p95 is slightly higher, and the eight-sample full-query control has a worse p95. These phases exclude GL submission and presentation, so the results describe CPU work rather than displayed frame rate. The [current comparison](sparse-visibility.md#current-native-results-compact-building-meshes) includes per-run medians, tails, capacities and limitations. The [earlier bounded-index comparison](sparse-visibility.md#earlier-native-results-bounded-index) retains its mixed city results and exact `6bc7761` provenance.

### North-up orientation at nadir

Browser verification exposed a North-up pinch defect in `TryAimAt`: a target already at the requested screen position could rotate a nadir camera from 0° to ±90°. Mercator round-trip error left a tiny nonzero horizontal target offset, making the yaw solver choose a new direction even though the existing pose was valid. A native reproduction changed yaw at 2,800 of 10,000 nearby Manhattan positions on the unchanged `fa2cfa7` camera implementation.

The fix checks the current pose after `ForViewport` using the existing acceptance test: successful near/far projection and summed screen error below 0.0001 pixels. Already aligned targets retain the viewport-adjusted camera exactly; other targets use the unchanged analytic solver. The regression suite passes 12,424 aligned poses, including the original reproduction, repeated calls, different bearings and terrain datums, wrapped coordinates, resized viewports and oblique inset targets. The existing 486 reachable inset-aim cases and 84 physical-camera poses also pass, including clipped and unreachable targets. [Camera implementation](../src/PixelMap.Rendering/MapCamera.cs) · [Focused regression suite](../tests/PixelMap.Rendering.Tests/FreeCameraChecks.cs)

### Depth-index storage at very close camera poses

The intermediate `e086f47` release passed the existing CI suite, then a larger live viewport exposed an additional failure. At 1800 × 924 logical pixels and DPR 2, a Night Manhattan walker changed from North Up to a ground-clamped Orbit view. During this transition, `GpuSceneDepth.PrepareCell` threw `OutOfMemoryException`; the WebAssembly dispatcher subsequently aborted. The first exception was logged at normal console severity, so checking only console errors could miss the initial fault.

The exact index had no active capacity limit: a clipped primitive could add a 64-bit reference to every overlapping cell. The existing 8 MiB pool limit applied only when releasing a frame. The original eager index was also unbounded. DPR itself does not multiply this depth grid: the observed 1800 × 807 logical map corresponds to 900 × 404 art pixels at pixel size two, whereas the compositor canvas was 3600 × 1848. The art-pixel size at the failing frame was not captured. A nine-tile native reconstruction traverses the same camera region without exhausting memory; its smaller data set is diagnostic evidence of index growth, not a complete reproduction of the live process's heap state. [Observed release, viewport, input and exception evidence](verification/live-depth-memory-failure-e086f47.json)

The index now stores 32-bit primitive handles and checks a combined **16 MiB capacity budget before growing its active membership buffers**. Source-order metadata is stored once per triangle. On capacity pressure, queries scan the exact contiguous primitive ranges of conservatively matching groups; there is no second growing overflow list. Coarse-index pressure uses a conservative group scan, and extreme artwork dimensions keep the grid within 16,384 cells. This preserves geometry and query semantics while exchanging indexed lookup speed for bounded storage in exceptional views. The 8 MiB pool admission limit remains separate. Neither figure is a bound on total application heap or GPU memory. [Memory policy and fallback invariants](sparse-visibility.md#active-memory-pressure-and-exact-fallback)

The browser regression explicitly selects Night, enables game terrain relief, starts a North Up walker, applies continuous Pan input, and traverses the close Orbit and horizon poses at the observed viewport and DPR. It verifies fresh depth rendering and responsive pause, resume and exit controls, and rejects managed and runtime failures at every console severity. A continuous Pan intentionally changes North Up to Orbit; a Ctrl/Meta pinch retains North Up. The original live event's delivered modifier classification was not captured, so this memory failure is not used as evidence that pinch routing was incorrect. [Browser regression](../tests/browser/explore.cjs)

### Separate native raster failure with live map data

The `9171e95` artifact passed the full Chromium suite and deployed, but its subsequent live-provider replay failed at 2026-11-07 12:36:14.277 UTC. The exact deployed WASM stack resolves to `SkScan::FillPath` → `sk_fill_path` → `SkEdgeBuilder::buildEdges` → `SkEdgeBuilder::buildPoly` → `SkArenaAlloc::ensureSpace` → `sk_malloc_flags` → `abort`. Skia received a null native allocation while expanding the edge-pointer arena for CPU path rasterization. The available stack does not identify the application's draw caller or the allocation's requested size. This is a separate failure from the earlier managed depth-index exception. [Exact artifact, input sequence, native stack and failed live evidence](verification/live-native-raster-failure-9171e95.json)

The artifact configures one wasm32 linear memory with a 2 GiB maximum; that ceiling alone does not establish the memory size reached at the abort. Skia and Mono allocate from that shared address space. The passing CI scene used nine bundled Manhattan vector tiles and a coarse world elevation tile, whereas the live scene requested a larger vector set and detailed elevation. CI supplied explicitly continuous Pan events; the live browser's delivered wheel fields were not recorded. Later static screenshots and unchanged controls from the aborted runtime are retained as failure evidence and do not establish responsiveness. The isolated frozen-artifact diagnostic records the live response corpus, trusted wheel events, memory-growth requests and acknowledged public control transitions without rebuilding the application.

The completed frozen-artifact capture subsequently reached **2,115,305,472 bytes of allocated Wasm memory**, 98.50% of the configured maximum, while its full native-input protocol remained responsive. It recorded 165 unique response bodies: 68 vectors, 96 DEMs and one catalog. This diagnostic did not reproduce the original abort; it establishes how little additional growth headroom the unchanged release retained. [Complete capture and limitations](verification/live-native-capture-83980ac.json)

### Compact building meshes and exact allocation

The captured vector union retains 425 MB in building vertex/index/rim streams. Their original growing lists and final copies allocate 1.61 GB cumulatively, before counting tessellation. `c937ddd` stores seven unchanged float attributes plus an integer model/endpoint word in **32 bytes per vertex instead of 48**. Original top/bottom heights and zero/full along-edge distances are selected from existing values. The builder tessellates each cap once, retains compact cap staging, computes checked output counts, and fills exact final arrays in the original order. GPU construction also leaves the CPU rasterizer's edge table lazy.

On that captured union, constructor allocation including tessellation falls by **53.6%**, and directly attributed retained mesh/edge payload falls by **146.3 MB**. This is a native allocation/retention measurement, separate from the browser's linear-memory growth. The independent legacy-builder oracle matches 845,516 logical vertices bit for bit; the full native depth suite still matches, and all 192 standard GLES case image hashes remain unchanged. [Layout, construction, ownership and measurement scope](building-mesh-memory.md)

### Journey submission during a pending text update

Release verification also exposed a journey-planner race: entering destination coordinates and immediately pressing Calculate could submit the preceding destination. The editor already contained the new value, but the cached stop draft still depended on a deferred `TextChanged` notification. `OnPlan` now copies the active editor's trimmed text into its stop draft before starting the existing planning path. This makes submission consume the value visible to the user at the time of the action.

The browser regression enters an intermediate coordinate and a new destination, then presses Calculate without Enter, Find or an added delay. A read-only capture at pointer-down records both the native editor value and the still-old stop label. CPU and GPU runs both resolve the new destination as Dropped pin and retain the intermediate stop. [Submission implementation](../src/PixelMap.App/JourneyPanel.xaml.cs) · [Journey regression](../tests/browser/journeys.cjs)

### Paired browser measurements for c937ddd

The completed [run 42377138938](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42377138938) compares the exact compact-mesh `c937ddd` Pages artifact with the original cache-identity release `1829855`, pinned to run `42300149918`. Native/GLES tests, strict packaging verification, the complete Chromium suite, the paired benchmark and Pages deployment all passed. The subsequent [independent live-provider camera replay](verification/live-rendering-c937ddd.json) also completed its original native gestures and acknowledged pause/resume/exit controls without a captured managed exception or native abort. [Separate CI and artifact provenance](verification/browser-validation-c937ddd.json)

Both performance variants ran on the same AMD EPYC 9V74 runner with affinity 0–2, Chromium 156.0.8078.4 and in-process SwiftShader, at 1200 × 780 CSS pixels and DPR 1. Five moving scenarios each use fresh processes in baseline → candidate → candidate → baseline order, with two seconds of warmup and an eight-second requested sample. Five stationary scenarios use one baseline/candidate pair. Fourteen CPU profiles are collected separately after timing. External providers are blocked; bundled data supplies the scene. All moving submissions use pixel size four. These conditions differ from the DPR-2 live-provider memory replay, and this host differs from the preceding Intel `668bf4b` run: absolute rates across those runs are not compared as renderer speedups.

The table averages the two per-run moving measurements. Submission-interval p95s are averaged per run, not pooled. The harness uses sorted index `floor(n × 0.95)`, capped to the last sample. Each moving run contains 44–97 submissions, so the tails have few observations.

| Moving scenario | Map submissions/s, baseline → candidate | Rate change | Mean p95 submission interval, baseline → candidate | Main-thread task CPU, baseline → candidate |
| --- | ---: | ---: | ---: | ---: |
| Walk, first person | 6.994 → 7.236 | +3.47% | 201.50 → 201.60 ms | 34.24% → 34.54% |
| Walk, chase | 11.682 → 11.893 | +1.81% | 150.95 → 147.70 ms | 32.96% → 32.13% |
| Car, chase | 8.307 → 8.428 | +1.46% | 263.10 → 254.15 ms | 44.05% → 42.18% |
| Car, first person | 5.580 → 5.659 | +1.40% | 281.00 → 278.95 ms | 44.33% → 45.71% |
| Free flight | 7.371 → 7.052 | −4.32% | 260.45 → 271.35 ms | 36.92% → 35.84% |

Browser timing remains mixed. Free-flight submission rate and its interval p95 regress. First-person walking's mean rAF p95 increases from **16.80 to 33.30 ms**, and mean total long-task duration per measured window rises from **566.0 to 941.0 ms**. First-person driving uses more main-thread task CPU; the stationary car's single pair rises from **4.07% to 5.66%**. These results do not establish uniformly smoother interaction. Two moving repetitions and one stationary pair do not establish confidence intervals, and identical held-key input can produce different final physical poses when simulation progress differs.

Across all 30 timed runs, **1,291 of 1,291 map submissions use the direct compositor path**. There are zero framebuffer readbacks, pixel-pack copies, map-sized artwork uploads, releases before GPU signal, or positive-timeout GPU polls. Every stationary run submits zero new map frames and performs zero foreground draws; its before/after map crop is bitwise stable. The browser still performs other UI and scheduling work, so retained image reuse does not mean zero process activity.

Map submissions count newly resolved map images, not measured display presentations. rAF counts scheduling callbacks. Main-thread task CPU uses the `Performance.getMetrics` timestamp interval, including probe-result transfer, and excludes other threads. These SwiftShader results are not physical-GPU timings. They compare the complete rendering patch, including filtered windows, rather than isolating the compact vertex representation. The four Perspective Pixel stationary baseline/candidate crops contain expected window-filter changes; only the default-map crop is identical across variants.

The [30-run export](verification/browser-rendering-c937ddd-runs.csv), [all ten scenario comparisons](verification/browser-rendering-c937ddd-comparison.csv) and [full provenance](verification/browser-rendering-c937ddd-provenance.json) preserve source/runtime/artifact identities, order, poses, ranges, tails, images and all profile summaries. An [independent audit](verification/browser-rendering-c937ddd-independent-audit.json) recomputes the raw timings, profile aggregates, exported comparisons and image checks with **26,040 assertions**. The raw performance artifact is `13576479398`, ZIP SHA-256 `4bf247e49cab16f85836b3ebf6bb6a555564b4ad8de176c6839018599f37f501`.

The separate fixed-corpus AOT replay reduces peak allocated Wasm length from **2,077,556,736 to 1,908,146,176 bytes**, while both runs complete 27 public checkpoints and three additional camera cycles. This is a **169,410,560-byte** reduction in observed allocated length, with possible configured growth increasing from 66.6875 to 228.25 MiB. It is not a measurement of allocator free space. Both runs retain bounded sampling timeouts, and their request/frame scheduling differs. [Complete browser-memory comparison and limitations](building-mesh-memory.md#frozen-corpus-aot-replay)

### Browser measurements before compact building meshes

The paired benchmark in [run 42366657287](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42366657287) measures the exact `668bf4b880d0004b8a6a1801f1d0b0abb065bd9f` application artifact against the original cache-identity release, `1829855ea07d1fd43b66d4478210398a605bb3c9`, from pinned run `42300149918`. The later `9171e95` changes only the camera regression test. Both commits have the same complete `src` tree, `be18099689edbe6a951b9e604691adb28af13365`; their published binaries are not assumed identical. These measurements predate `c937ddd` and retain their exact `668bf4b` artifact/runtime identities. Current application measurements and final acceptance are recorded separately.

Both variants run on the same Intel Xeon Platinum 8573C runner with affinity 0–2, Chromium 156.0.8078.4 and in-process SwiftShader, at 1200 × 780 logical pixels and DPR 1. Every case uses a fresh browser process, two seconds of warmup and one requested eight-second sample. Each moving scenario runs baseline → candidate → candidate → baseline. Stationary scenarios run one baseline/candidate pair. Fourteen CPU profiles are collected separately after timing. External map providers are blocked and bundled data supplies the scene. This is a different host from the earlier EPYC runs; absolute rates are not compared across those runs.

The table averages the two per-run moving measurements for each variant. The p95 column averages per-run interval p95s; it is not a pooled percentile. Percentiles use the harness's sorted zero-based index `floor(n × 0.95)`, capped at the last element. All moving map submissions use pixel size four.

| Moving scenario | Map submissions/s, baseline → candidate | Rate change | Mean p95 submission interval, baseline → candidate | Main-thread task CPU, baseline → candidate |
| --- | ---: | ---: | ---: | ---: |
| Walk, first person | 5.122 → 5.230 | +2.10% | 289.30 → 252.20 ms | 31.30% → 33.00% |
| Walk, chase | 7.965 → 7.926 | -0.49% | 227.15 → 219.00 ms | 31.28% → 31.68% |
| Car, chase | 5.310 → 5.434 | +2.34% | 392.75 → 388.65 ms | 42.13% → 42.28% |
| Car, first person | 3.844 → 4.080 | +6.13% | 404.10 → 373.55 ms | 41.62% → 41.86% |
| Free flight | 4.363 → 4.497 | +3.06% | 463.30 → 453.85 ms | 32.62% → 32.91% |

The first-person car rate improves in both orderings; the chase-walking mean is slightly lower. All five mean p95 submission intervals decrease, but individual reverse-order comparisons include worse tails. Main-thread task CPU increases in every moving aggregate, by 0.14–1.70 percentage points. Browser scheduling also has regressions: mean rAF p95 rises from 25.05 to 33.30 ms for first-person walking and from 50.00 to 66.60 ms for first-person driving. Mean total long-task duration per measured window rises from 970.5 to 1,153.5 ms and from 2,379.5 to 2,628.0 ms, respectively. Lower map-submission tails therefore do not establish uniformly smoother interaction.

Each moving run contains only 30–65 map submissions, so the p95s have few tail observations. Two repetitions do not establish confidence intervals or a universal speedup. Identical held-key input can also produce different final physical poses when simulation progress differs; the raw export retains those poses and distances.

Across all 30 timed runs, **866 of 866 map submissions use the direct compositor path**. There are zero framebuffer readbacks, pixel-pack copies, map-sized artwork uploads, releases before GPU signal, or positive-timeout polls. The five stationary cases, across both variants, produce zero new map submissions and zero foreground draws. Their retained frames remain reusable while the browser continues scheduling UI callbacks.

Map submissions are newly resolved map images, not measured display presentations. rAF counts scheduling callbacks, and main-thread task CPU excludes work on other threads. The CPU percentage uses the `Performance.getMetrics` timestamp interval, including probe-result transfer, rather than the requested sample duration. These SwiftShader results do not measure a physical GPU. They compare the complete rendering patch, including the new window filter; they do not attribute gains to individual components.

The [per-run CSV](verification/browser-rendering-668bf4b-runs.csv) retains all 30 runs, phase counts, CPU/rAF statistics, pixel sizes, poses and artifact identities. The [comparison CSV](verification/browser-rendering-668bf4b-comparison.csv) includes every stationary and moving case, both per-variant ranges and the slower values. [Provenance](verification/browser-rendering-668bf4b-provenance.json) records the pinned baseline, exact runtime resources, environment, execution order and CI evidence. An [independent audit](verification/browser-rendering-668bf4b-independent-audit.json) recomputes headline statistics and all exported aggregate fields from the 30 raw captures, passing 1,413 assertions. The raw performance artifact is `13573165547`, ZIP SHA-256 `a92b726bad712673e11abbb3d1f0d9ed982cf497204fce899f1777c6900cd676`, with retention through 7 December 2026.

Earlier browser measurements remain revision-specific: [`fa2cfa7`](verification/browser-rendering-fa2cfa7-provenance.json), [`e086f47`](verification/browser-rendering-e086f47-provenance.json) and [`6bc7761`](verification/browser-rendering-6bc7761-provenance.json). Their native/camera findings and failed or incomplete acceptance steps are retained rather than treated as the final release.


## Camera correctness and dense GPU scenes

The camera changes in [`ca0fe45`](https://github.com/wieslawsoltes/UnoPixelMap/commit/ca0fe45ab630aa345856e5d836ff8ffe5f578ada) address paused route following, gesture ownership and dense route fitting:

- Paused route zoom applies the full wheel or browser trackpad pinch delta immediately and retains follow ownership. It resets the tracking spring so the paused journey clock does not have to advance to finish the zoom. Repeated trackpad pinches and their inverse are covered by browser regressions.
- Zero-delta wheel events and Safari gesture-start events do not cancel an existing flight or take manual ownership. A pure browser trackpad pinch preserves route follow and the free-roam North Up camera. Explicit Safari rotation while following a route detaches follow and rotates the map.
- Terrain route fitting samples every route vertex's elevation once into a pooled buffer, reusing those heights during the zoom search. The previous evenly spaced limit of 513 vertices could omit an isolated mountain on a dense route. The new regression first reproduced that failure, then passed in orthographic and perspective views. Failed zoom trials exit at the first out-of-bounds vertex; successful trials still validate every vertex against the viewport insets.

The native camera suite also passes all 54 existing inset route fits and 432 steep-terrain zoom anchors. These changes preserve the existing physical-eye, terrain-picking and follow-camera conventions documented below.

### Retained GPU cache pressure

In a live 1800-by-900 Manhattan play session on the preceding deployment, walking caused `GlMapRenderer` to throw `The visible map exceeds the bounded GPU geometry cache.` The application then switched to CPU fallback. The failure occurred because all visible building resources were pinned to the current frame, even when their combined working set exceeded the retained cache budget.

[`9c86002`](https://github.com/wieslawsoltes/UnoPixelMap/commit/9c86002a1efa15be08b4aea29e53a63717e064cf) streams those building resources through the existing 128 MiB retained cache. It evicts unused older resources first, then other resources needed by the current frame when necessary, while protecting the mesh currently being uploaded. Empty selections no longer pin unused tiles. OpenGL retains resources referenced by queued commands until those commands complete.

The renderer still draws all building faces before all roof rims. The rim pass reconstructs evicted edge/model buffers without uploading unused face vertices, preserving depth and occlusion order. A single tile that cannot fit remains an explicit unsupported case; the regression verifies that a subsequent smaller scene recovers correctly.

The 128 MiB figure bounds **retained geometry and texture cache storage**. It excludes framebuffer attachments and resources that the driver must retain until queued commands complete, so it is not a bound on instantaneous total GPU memory. The existing presentation queue still limits outstanding map submissions to two.

The native GLES regression compares 32 frames with a cache large enough for approximately one tile against the ordinary cache. It covers all four styles, moving and physical cameras, retained-frame replay after eviction, exact face/rim draw counts, unchanged warm uploads when the working set fits, the configured retained-memory limit, and oversized-tile recovery. Every constrained frame has exactly the same pixels as its ordinary-cache counterpart. The original 48-case GLES report remains byte-identical across all 192 image-hash fields.

## Terrain mesh preparation and retained LOD layouts

The terrain preparation pass reduces repeated elevation sampling and retains stitched height data while the camera moves within the same level-of-detail layout. It preserves the source resolution, patch count, lighting formulas, seam topology and rendering styles.

- Each 17-by-17 patch reuses a one-sample border for its five-point slope stencil. Elevation queries fall from **1,445 to 357 per new patch**, a 75.3% reduction. The four unused border corners are not sampled. Latitude and ground-distance calculations run once per row, reducing those calculations from 289 to 17. Tile origins and sample steps are binary fractions, so the reused coordinates and subsequent arithmetic preserve the original floating-point results exactly.
- The ordered patch IDs, repeated-world copies and immutable elevation snapshot identify the stitched height layout. Unchanged layouts retain their shared edges and skirt floors. The geographic height lookup still updates for the current camera, including the nearest repeated-world copy.
- A GPU mesh skips its repeated height/light comparison only when its render style matches and it was used in the preceding preparation frame. A CPU-only interlude, a changed elevation snapshot or a changed patch layout forces validation. The four-child subdivision list is reused between frames.

### Paired native phase measurements

The comparison uses the renderer at `e35485601d9cb0afdf426ba300e7f2fbe0fb7f3e` and the terrain changes above. Both variants use the same diagnostic harness. Four fresh processes run in baseline, candidate, candidate, baseline order, with CPU affinity 0, two managed processors and disabled tiered compilation. The environment is .NET 10.0.14, SDK 10.0.403, Debian 13 and an AMD EPYC 9V74. Each workload performs 16 warmup iterations and 64 measured iterations at a 1200-by-680 logical viewport with requested pixel size 4.

The fixture uses nine synthetic z14 elevation tiles with 64-by-64 samples, a signed 32-by-32 z0 parent and a mapped ocean polygon. The replacement snapshot includes missing samples that exercise parent fallback. The moving-LOD workload changes bearing and zoom through a deterministic 16-frame cycle. The table reports the median of the two process medians for each variant.

| Preparation workload | Baseline median | Updated median | Speedup |
| --- | ---: | ---: | ---: |
| Cold nine-patch source meshes | 0.8431 ms | 0.2626 ms | 3.21× |
| Orbit within a retained layout | 0.1919 ms | 0.0670 ms | 2.87× |
| Camera motion across changing LOD | 1.0302 ms | 0.8693 ms | 1.19× |
| Replacement elevation snapshot | 6.9210 ms | 2.0079 ms | 3.45× |
| Minecraft orbit within a retained layout | 0.3132 ms | 0.0701 ms | 4.47× |

These timings cover terrain source-mesh or terrain LOD/stitch preparation, depending on the workload. They exclude scene-depth construction, buildings, labels, GL submission and presentation, so they are **not application frame-rate measurements**. Artwork in this diagnostic is a blank deterministic tile. Output hashing runs outside the measured region.

All five phase medians improve in both pairs. Tail latency is mixed for changing LOD: the first baseline-to-candidate pair has p95 **2.6058 → 3.7332 ms**, while the reverse-order pair has **2.6779 → 2.3261 ms**. The raw CSV retains all 1,280 timing/allocation rows, output hashes, runtime information and assembly identities rather than suppressing the slower tail. Ordinary retained-orbit allocation falls from 5,256 to 4,848 bytes per preparation. Other workloads can include cache growth and runtime bookkeeping. [Complete terrain preparation samples](terrain-preparation-benchmarks.csv)

The measured baseline renderer SHA-256 is `944887fd2b326c113f65c9416a20c68b5169c7e905d9fa7009a91a4370517cba`; the candidate is `ca2362514b403faa8ae44d8af8e795438fdc0d9eedccfa51c6f0956a4f73d4c2`. The common harness SHA-256 is `41f0906baf421d33082bc560460b9756a49651be5b9dc80917b565c8e2d8769e`. These phase measurements precede the separate GPU cache-pressure changes.

### Exact output checks

The independent original-stencil reference matches **35,547 height/lighting pairs bit-for-bit** across zooms 0–22, signed elevation, missing samples, ocean masking, longitude wrap and polar borders. Another 28 retained-versus-fresh scene comparisons cover LOD changes, all four styles, changed elevation, CPU/GPU handoffs, resizing and repeated worlds, including terrain picking and physical heights. All 320 measured output hashes match across the four timing processes, accounting for 1,280 recorded hash entries.

The 48-case native GLES report is byte-identical to the preceding renderer, including all 192 CPU, GPU, art-pixel and RGBA-image hashes. The complete native rendering suite also passes. The bounded GPU-cache regression is documented separately from these terrain phase measurements.

To reproduce the phase report, use the same compiled test harness with each renderer and run the processes sequentially:

```sh
DOTNET_TieredCompilation=0 DOTNET_PROCESSOR_COUNT=2 taskset -c 0 \
  dotnet tests/PixelMap.Rendering.Tests/bin/Release/net10.0/PixelMap.Rendering.Tests.dll \
  --terrain-preparation --terrain-output=artifacts/terrain-preparation
```

## Release browser verification and paired play measurements

The subsequent bootstrap cache-identity fix and its artifact-verification contract are described in [Browser release identity](publishing.md). The performance measurements in this section retain their exact `9c86002` provenance.

The Release `InterpreterAndAOT` build at `9c86002a1efa15be08b4aea29e53a63717e064cf` passed the complete native and Chromium suites, the paired benchmark and Pages deployment in [run 42291311815](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42291311815). Deployment completed at 2026-11-06 21:17:02 UTC. Chromium verification covers paused route zoom and ownership, zero-delta Safari events, explicit route-camera twist, North Up pinch, Pointer Lock, all five driving camera rigs and touch reattachment. The Safari-event checks run through the browser adapter in Chromium; they are not a native Safari validation.

The direct GPU path passes delayed-frame picking and backend recreation with zero framebuffer readbacks or map-sized CPU uploads. The explicitly forced detached path passes the same retained-frame checks using asynchronous pixel-pack transfers. Phone and DPR-2 resizing also pass. All five final stationary map crops in the paired performance evidence match exactly; whole-canvas equality is not claimed because walking/driving footer text differs. Eight of the ten initial map crops match exactly; two differ.

### Paired browser results for this deployment

This run compares the previously deployed `01315510709597d91f1e41397e601b1aa3b41118` with `9c86002a1efa15be08b4aea29e53a63717e064cf`, including the direct compositor change and this camera, terrain and cache pass. Both artifacts run on one AMD EPYC 7763 runner with affinity 0–2, Chromium 156.0.8078.4 and in-process SwiftShader, a 1200-by-780 viewport and DPR 1. Every scenario uses fresh browser processes, two seconds of warmup and one requested eight-second sample, in baseline-then-candidate order. External providers are blocked and bundled data supplies the scene. Held native keys advance play mode, so final moving poses can differ between versions.

| Moving scenario | Map submissions/s, baseline → updated | Throughput change | Task CPU, baseline → updated | rAF callbacks/s, baseline → updated |
| --- | ---: | ---: | ---: | ---: |
| Walking, first person | 4.247 → 4.998 | +17.68% | 32.60% → 32.98% | 54.84 → 54.10 |
| Walking, chase | 6.713 → 8.696 | +29.55% | 33.10% → 35.17% | 53.22 → 53.26 |
| Driving, chase | 4.873 → 6.244 | +28.15% | 42.05% → 46.82% | 47.48 → 44.84 |
| Driving, first person | 3.440 → 3.769 | +9.58% | 46.82% → 44.39% | 43.73 → 44.38 |
| Free flight | 3.748 → 5.120 | +36.58% | 34.68% → 35.85% | 52.98 → 54.32 |

Map submission throughput increases in every moving pair. Main-thread CPU and scheduling remain mixed. Driving chase task CPU rises from 4,117.362 to 4,559.313 ms and cumulative long-task duration rises from 1,884 to 2,295 ms. Driving first person reduces task CPU from 4,137.378 to 3,944.377 ms, but its long-task duration rises from 2,571 to 2,901 ms. Walking first person has slightly less total task CPU, 2,813.782 → 2,778.379 ms, but a higher percentage because the CDP measurement windows differ; its long-task duration rises from 805 to 1,119 ms. Walking-chase and free-flight long-task duration improve from 686 to 679 ms and 1,153 to 1,052 ms. Free-flight submit-to-signaled-completion p95 increases from 748.9 to 858.5 ms, and its maximum rAF gap increases from 150.0 to 250.1 ms.

The five moving candidate samples contain 234 compositor-context map submissions, with zero framebuffer readbacks, pixel-pack copies or map-sized CPU uploads. The baseline contains 187 detached-context submissions and 178 completed pixel-pack copies. Every baseline copy follows a correlated signaled, zero-timeout fence poll. Both paths retain at most two outstanding maps, with zero unsafe overwrites, abandoned transfers, premature releases or positive-timeout polls. Every timed moving submission uses pixel size 4 at 300-by-166 art pixels.

All ten stationary samples across the pair record zero foreground GL draws, transfers, map submissions and long tasks, with approximately 60 rAF callbacks/s. Default-map task CPU is 0.83% → 0.80%; relief-map CPU is 1.41% → 1.51%. Stationary walking, driving and free-flight CPU is 5.03% → 5.05%, 4.82% → 4.78% and 3.86% → 4.22%, respectively.

Actual probe durations span 8,002.6–8,342.4 ms. CPU percentages divide main-thread `threadTicks` by the surrounding CDP metric window, spanning 8,019.346–9,848.814 ms and including raw-result collection. Software-GPU worker CPU is excluded. Map submissions, fence completion, transfer copies and rAF callbacks describe different pipeline stages; final display presentation is outside the probe. These individual short pairs establish transfer elimination and higher measured map throughput, not universal CPU or displayed-frame-rate gains. Physical-GPU, native Safari/iOS and repeated-run timing remain unmeasured. The earlier `e354856` comparison used an Intel Xeon Platinum 8573C, so differences between that separate run and this one cannot isolate the terrain/cache changes.

### Evidence and provenance

All 20 report summaries recompute exactly from the raw probes and CDP metrics, and all ten separate CPU-profile summaries recompute from their `.cpuprofile` files. The profiles include driving in first person. [Complete deployment comparison CSV](camera-terrain-browser-benchmarks.csv)

The [performance evidence artifact](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42291311815/artifacts/13542255328) is 4,090,543 bytes with ZIP SHA-256 `4d50bbc99eeb333986821ebfcee8976a1b33fe6032e29da6ce1ae9528b98f6f1`. Candidate Pages artifact `13539884401` is 88,316,267 bytes with ZIP SHA-256 `f9e8c7d34466085d99a91c6494ad876e271b5ef1d73f887a3cd955e930af221f`; its native WASM SHA-256 is `fdbafedc4238a762f2eae23cff91b57a9402f7ff006a80e0d317a9d9678afa6a`. All 72 candidate boot resources were validated. Baseline Pages artifact `13495501541` and its hashes are also retained in the CSV and the preceding comparison below.

The browser SHA-256 is `4fa61f0ba0b4f89b7bec242c32ca16d3bccf69dbfef0651924a9ba8fede5f9d6`. The common play harness SHA-256 is `fe2fa5570db1aa8fa471730c8082d2fb201d1c59ef051c47f059d87062febd44`, the probe harness is `0853a3be2ae6c804cacb5e8f50a93a23173d030f3c672a382ba47694f3ab7183`, and the exploration harness is `1048200cb8b4845df96b574a65d9e9d0028f7c2207417fc462083eae18ee255e`.

## Direct compositor GPU presentation

Commit `e35485601d9cb0afdf426ba300e7f2fbe0fb7f3e` presents retained map textures on Uno's Skia WebGL context. This removes framebuffer readback and the subsequent CPU image upload from that path. A two-fence queue bounds outstanding map submissions; texture drawing follows GL command order without waiting for CPU-visible completion. The detached GLCanvas path remains available when the compositor context cannot support the terrain renderer, using the existing asynchronous pixel-pack transfer queue.

### Paired browser measurements

The recovered comparison uses the exact Release `InterpreterAndAOT` Pages artifacts for baseline `01315510709597d91f1e41397e601b1aa3b41118` and candidate `e35485601d9cb0afdf426ba300e7f2fbe0fb7f3e`. Run `42203874612` executes each baseline/candidate pair on one Intel Xeon Platinum 8573C runner, with affinity 0–2, Chromium 156.0.8078.4 and in-process SwiftShader. Each scenario uses fresh browser processes, a 1200×780 viewport, DPR 1, two seconds of warmup and one requested eight-second sample. External providers are blocked, and bundled data supplies the scene. Held native keys advance play mode; final moving poses can differ between versions.

| Moving scenario | Map submissions/s, baseline → direct | Task CPU, baseline → direct | rAF callbacks/s, baseline → direct | rAF interval p95, baseline → direct |
| --- | ---: | ---: | ---: | ---: |
| Walking, first person | 4.247 → 4.998 | 30.93% → 29.51% | 55.71 → 55.85 | 33.3 → 33.3 ms |
| Walking, chase | 6.086 → 7.977 | 32.11% → 33.19% | 54.53 → 54.59 | 16.8 → 16.8 ms |
| Driving, chase | 4.241 → 5.367 | 43.79% → 43.26% | 46.15 → 45.68 | 16.8 → 16.8 ms |
| Driving, first person | 3.249 → 3.828 | 39.79% → 43.08% | 48.23 → 46.43 | 49.9 → 66.5 ms |
| Free flight | 3.374 → 4.496 | 32.92% → 32.77% | 52.86 → 55.57 | 33.3 → 16.8 ms |

Map submission throughput increases by 17.7–33.2% in these five samples. Every timed moving map uses pixel size 4 at 300×166. The candidate records 214 compositor-context map submissions, zero framebuffer readbacks, zero pixel-pack copies and zero map-sized CPU texture uploads. The baseline records 170 detached-context submissions and 161 completed pixel-pack copies. Every baseline copy follows a correlated signaled, zero-timeout fence poll. Both paths retain at most two outstanding maps, with zero unsafe overwrites, abandoned transfers, premature releases or positive-timeout polls. All twenty report summaries recompute exactly from their raw probes and CDP metrics.

Main-thread CPU and scheduling results are mixed. Driving in first person increases task CPU by 3.29 percentage points and long-task duration from 2,224 to 2,520 ms. First-person walking reduces task CPU but increases long-task duration from 609 to 1,091 ms. Free-flight long-task duration falls from 980 to 930 ms. Map submit-to-signaled-completion p95 improves in all five pairs, but still ranges from 292.7 to 802.5 ms on the candidate's software GPU. The direct-path fences measure completion of submitted GL work; final display presentation remains outside the probe.

All ten idle/stationary samples across the two builds record zero foreground GL draws, transfers, map submissions and long tasks, while rAF stays near 60 callbacks/s. Default-map task CPU is 0.86% → 0.84%, relief-map CPU is 1.51% → 1.48%, and stationary walk/car/free-flight CPU is 4.24% → 4.69%, 4.94% → 4.98% and 3.91% → 4.56%, respectively.

Rates use each actual probe interval, spanning 8,001.8–8,098.6 ms across the pair. Task CPU uses main-thread `threadTicks` divided by the surrounding `Performance.getMetrics` timestamp interval, spanning 8,020.2–9,559.8 ms and including raw-result collection. This denominator differs from the historical probe-duration percentage retained in `play-browser-benchmarks.csv`. Software-GPU worker CPU is excluded. Map submissions, completed transfers and rAF callbacks measure different pipeline stages; none is a direct displayed-frame counter. These single short pairs establish transfer elimination and measured map-throughput gains, with mixed main-thread results. Physical-GPU, Safari/iOS and repeated-run timing remain unmeasured for this change. [All twenty direct-presentation comparison rows](direct-gpu-browser-benchmarks.csv)

### Artifact provenance and verification status

The baseline Pages artifact is `13495501541` from run `42201447082`, with ZIP SHA-256 `8ea612ea149bff3cd445a2898fcf84a39118236138ec6b81c6808d0d8a70b574`. The candidate Pages artifact is `13496024690`, with ZIP SHA-256 `dc5bcad33e587bc8aa7cf8782dbc96eba27f3487a4afe0961e52fb37d8c4e6e5`. Their native WASM SHA-256 values are `e8f074a13099c8616897b1015892cb1c596c1eae551e8c9eab0ff58c72c7793d` and `4b34251a7faa4a3cac6eaf4d876f6eccf108bf27d8597de4c4388b4b73a9f598`, respectively. The shared play harness SHA-256 is `fe2fa5570db1aa8fa471730c8082d2fb201d1c59ef051c47f059d87062febd44`.

Performance evidence artifact `13497356985`, `atlas-play-performance`, has ZIP SHA-256 `286574cdbfacada1ffc767405dee11d1c21b81eb71e5c475ab1fbbc9a75fcb51`. It retains raw reports, screenshots, selected separate CPU profiles and both builds' resource provenance. The CSV includes all three harness hashes, browser hash, source and artifact identities, metric/probe intervals, pixel sizes, queue accounting and recorded HUD poses. [Paired benchmark and artifacts](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42203874612)

That run's build and paired benchmark succeeded. Browser verification stopped in `terrain.cjs` because its old assertion required a positive readback count from the default GPU path; the direct compositor path correctly recorded none. Deployment was skipped. Terrain verification now distinguishes direct compositor rendering from an explicitly forced asynchronous GLCanvas fallback, and exercises displayed-frame picking while each presentation queue is held. The original run remains failed and is not evidence of deployment or completion of the updated regression suite.

## Camera input and terrain interaction

Commit `088017aadb3f3b333c2850e5c09f8133fe03cf51` fixes raw browser input ownership and physical camera mathematics. Its Release browser build passed the published Chromium suites and deployed successfully. [Camera build and deployment](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42074751416)

- Raw wheel, pointer and keyboard metadata follows the DOM event's dispatch lifetime. Trusted events can run a microtask checkpoint between listeners, so the adapter now uses `eventPhase` and restores outer metadata after nested dispatch. Native fractional wheel axes, pointer capture and key-repeat flags reach the Uno handlers. The adapter removes one queued closure and microtask per retained event.
- Free-camera interpolation reconstructs the focus from the interpolated physical eye. A stationary-eye regression previously drifted 3.375166 m through a 180-degree yaw transition and 0.699802 m vertically through a 0-to-90-degree pitch transition. The updated regression records zero drift to nine decimal places.
- `MapCamera.TryAimAt` solves the two yaw branches and pitch for a requested screen ray while retaining the physical eye. It selects the nearest feasible no-roll orientation and validates direct reprojection within 0.0001 px. The 486-case fixture reaches a worst residual of 0.000099 px.
- The virtual focus can extend beyond the Mercator data extent. The 80 boundary poses cover both latitude limits and antimeridian sides through resizing, interpolation and v5 shared-link restoration. A north-facing boundary eye previously shifted 0.291165 m. Free-focus latitude now uses round-trip serialization.
- Follow and inertial damping integrate the full elapsed time. Paused movement keys retain game ownership. Escape pauses, and P toggles pause/resume. Immediate navigation and completed flights publish their final motion state with the new camera, reducing redundant presentation requests.
- Manual navigation detaches the game camera, and subsequent map gestures retain that ownership. Explicit game-look, mouse capture and the C camera shortcut restore follow. New flights and follow reattachment cancel stale pointer baselines. The first-person reticle follows the same state transition.
- Terrain picking handles upward intersections through signed terrain. Camera scale, Mercator coordinates, altitude and lens constants are evaluated once per ray query, outside the sampling and refinement loops.

### Native camera-query measurements

The comparison uses the application source at `e1e1fa03252fbcd0ef501227a865a41301e42593` and the camera changes at `088017aadb3f3b333c2850e5c09f8133fe03cf51`. It runs synchronous `MapCamera` queries on two seeded 5-by-5 grids of 256-by-256 z14 elevation tiles, each with a 2-by-2 z0 parent. Heights span 943.964–1456.337 m and −640.820 to −359.015 m. Cases include 3× relief exaggeration, grazing rays and anchored pan/orbit operations. The environment is .NET 10.0.14 x64, SDK 10.0.403, Debian 13, an Intel Xeon Platinum 8573C, CPU affinity 0 and disabled tiered compilation.

The table reports median thread-CPU microseconds per operation across three separate processes per variant. The execution order is baseline, candidate, candidate, baseline, baseline, candidate. A baseline-derived plan fixes the same repeat counts in both variants. Each process performs 168,498 timed operations. All 621 unique ray results and 54 full anchor-camera results have identical IEEE 754 output bits across both six-process passes.

| Workload | Baseline µs | Updated µs | Speedup |
| --- | ---: | ---: | ---: |
| Positive relief, orbital ray | 28.948 | 11.755 | 2.46× |
| Positive relief, free-eye ray | 13.224 | 5.966 | 2.22× |
| Positive relief, grazing street ray | 29.994 | 13.129 | 2.28× |
| Positive relief, hilltop horizon miss | 663.911 | 229.954 | 2.89× |
| Positive relief, anchored pan/orbit | 44.297 | 31.659 | 1.40× |
| Negative relief, orbital ray | 48.201 | 22.608 | 2.13× |
| Negative relief, free-eye ray | 10.483 | 6.056 | 1.73× |
| Negative relief, grazing street ray | 55.637 | 25.628 | 2.17× |
| Negative relief, hilltop horizon miss | 556.070 | 282.235 | 1.97× |
| Negative relief, anchored pan/orbit | 52.957 | 27.277 | 1.94× |
| Exaggerated relief, orbital ray | 19.902 | 11.187 | 1.78× |
| Exaggerated relief, anchored pan/orbit | 40.633 | 26.811 | 1.52× |
| Orthographic relief | 22.195 | 9.991 | 2.22× |
| Flat hypsometric control | 0.754 | 0.823 | 0.92× |

The flat control increases by 0.070 µs per query, approximately 9.25%. Hosted-runtime outliers remain visible even in thread-CPU time. The positive pan/orbit baseline samples are 37.226, 44.297 and 438.515 µs. All samples are retained, and the table uses medians. The wall-only pass records zero allocations per workload. The CPU-clock confirmation retains the 32-byte first P/Invoke setup allocation, followed by zero-byte workload rows. The raw confirmation rows include wall time, thread-CPU time, allocation counters, runtime and source/binary hashes. [Camera-query measurements](camera-query-benchmarks.csv)

## Deferred depth interpolation and roof rims

Commit `59a6a65255d4f3fe3748a4264820c8c412db48ed` contains this rendering pass and the follow-ownership refinements.

Depth triangles retain their existing 80-byte storage and prepare inverse-W, altitude and physical-height coefficients on first use. Each channel caches its original arithmetic result in the existing gradient fields. Containment checks require no interpolation coefficients, and retained references preserve the cache across queries. Terrain picking can subsequently prepare the additional height channels without overwriting the cached depth plane.

Building preparation skips cap vertices already rejected by the existing eye-facing predicate. Direct homogeneous outcodes retain the exact subtraction and multiplication expressions for the six clipping planes. GPU roof rims use a four-vertex triangle strip that preserves the original two triangles and their winding, reducing vertex shader invocations for this draw by one third.

Deterministic counts after ordinary label preparation use the bundled nine-tile scene and the same 1200×680 logical viewport, requested pixel size 4 and 64 terrain patches in each case. Count instrumentation runs separately from timings.

| Scene | Required planes before deferral | Planes prepared | Planes deferred | Cap vertices skipped / previously projected |
| --- | ---: | ---: | ---: | ---: |
| Walking with relief | 95,270 | 3,685 | 96.1% | 27,712 / 146,622 |
| Flight with relief | 42,015 | 763 | 98.2% | 24,726 / 153,668 |

The final native diagnostic pair runs the candidate before the baseline, with eight warmup and 24 moving frames per scene. Its baseline is `e1e1fa03252fbcd0ef501227a865a41301e42593`, and its candidate includes both the camera changes in `088017a` and the deferred-depth patch. The table uses the upper-middle wall-time statistic for complete `PrepareGpuFrame` calls, including label visibility. It also reports an independently reconstructed depth object from the same immutable frame. Both versions use .NET 10.0.14, disabled tiered compilation, two managed processors and CPU affinity 0–1.

| Scene | Complete preparation, baseline → updated | Change | Independent depth construction, baseline → updated |
| --- | ---: | ---: | ---: |
| Walking with relief | 28.511 → 21.162 ms | −25.8% | 15.114 → 11.868 ms |
| Flight with relief | 39.013 → 42.680 ms | +9.4% | 18.176 → 14.869 ms |

The flight result regresses in this diagnostic pair, and the runtime has substantial outliers. Its updated complete-preparation samples range from 27.514 to 1,271.362 ms. An earlier timing pair was excluded from performance conclusions after similarly severe runtime variability. Two broader model-index prototypes were also removed after complete preparation regressed. The retained patch reduces deterministic interpolation and shader work while end-to-end throughput is checked separately in the paired browser benchmark. The 96 warm timing rows and two separately marked count rows retain source/binary identities, wall and thread-CPU times, allocations and GC counters. [Depth-preparation measurements](depth-preparation-benchmarks.csv)

The GLES comparison preserves all 48 cases and 192 hash fields. Its complete `checks.json` is byte-identical to the preceding renderer. The CPU comparison preserves 232 cases, 3,712 moving-frame hashes, 232 cold-frame hashes and 232 sequence hashes with identical offline inputs, OpenAI Sans and a 1100×650 viewport. Eight additional moving scenes preserve all 128 dense-query payload hashes and 128 overlay pixel hashes, including terrain altitude, inverse depth, physical height and subpixel coverage. The native regression suite also passes 128 exact interpolation/cache/layout comparisons and 936 clipping-code boundary comparisons, including NaN and infinity inputs.

### Two paired browser runs

Both runs compare exact Release `InterpreterAndAOT` artifacts against camera commit `088017aadb3f3b333c2850e5c09f8133fe03cf51`. Run 1 measures `59a6a65` on an Intel Xeon Platinum 8573C. Run 2 measures `650feb5` on an AMD EPYC 9V74. The two candidates have identical application source. The latter commit corrects the touchscreen regression fixture to use a visible desktop-width camera readout and then restore the portrait viewport.

Each run pairs baseline then candidate on its own runner in fresh processes, using Chromium 156.0.8078.4, in-process SwiftShader, CPU affinity 0–2, a 1200×780 viewport and DPR 1. Each case warms for two seconds and requests eight seconds of measurement. All timed moving readbacks use pixel size 4 at 300×166. External providers are blocked, and the same bundled inputs and held native keys exercise play mode. Moving endpoints can differ as simulation advances against wall time. The table reports completed map-image readbacks per second using each actual probe duration.

| Moving scenario | Run 1 baseline → updated | Run 1 change | Run 2 baseline → updated | Run 2 change |
| --- | ---: | ---: | ---: | ---: |
| Walking, first person | 3.747 → 3.721 | −0.69% | 4.488 → 4.497 | +0.18% |
| Walking, chase | 5.163 → 5.186 | +0.43% | 6.428 → 6.679 | +3.91% |
| Driving, chase | 3.374 → 3.611 | +7.04% | 4.478 → 4.458 | −0.46% |
| Driving, first person | 2.709 → 2.749 | +1.49% | 3.335 → 3.448 | +3.39% |
| Free flight | 2.873 → 2.991 | +4.10% | 3.854 → 3.498 | −9.24% |

End-to-end throughput remains mixed across these software-GPU runs. The five moving cases complete 145 → 148 copies in run 1 and 184 → 183 in run 2. Walking first-person long-task duration falls from 600 → 549 ms and 1,146 → 902 ms. Flight long-task duration falls from 959 → 849 ms in run 1 and rises from 1,141 → 1,335 ms in run 2. Run 1 also increases walking-chase long-task duration from 643 → 664 ms and driving-first-person duration from 2,158 → 2,411 ms. The same baseline's throughput differs by 19.8–34.1% between the two runner environments. Physical-GPU and Safari/iOS timing remain unmeasured.

All 40 raw report summaries and 16 separate CPU-profile summaries recompute exactly. Every one of the 660 completed pixel-pack copies follows a correlated signaled zero-timeout poll. The 697 submissions retain the two-slot bound with zero overwrites, abandoned transfers, releases without copying or positive-timeout waits. All 20 idle/stationary measurements record zero foreground graphics work and long tasks. A run-1 car interval ends with 27 completed copies and 26 art uploads, so upload and readback counts remain separate measurements.

The CSV retains its original `TaskDuration / probe duration` percentage. `Performance.getMetrics` spans the surrounding CDP start/finish and raw-data transfer, so its collection window is longer than the rendering probe. In run 1, moving metric intervals span 8.567–9.610 seconds while rendering probes span 8.003–8.328 seconds. Run 2 spans 8.539–9.836 seconds and 8.006–8.401 seconds respectively. CPU percentages therefore include instrumentation work and use the narrower probe interval as their denominator. The audit separately computes task shares against the actual metric timestamps. The historical browser tables below use the same original calculation. [All browser measurements and both paired runs](play-browser-benchmarks.csv)

Run 1 preserves nine of ten initial screenshot pairs and all five final idle/stationary pairs exactly. Its initial moving-car baseline uses adaptive pixel size 3 while the candidate uses size 4, with size 4 used for all timed readbacks. Run 2 preserves eight of ten initial pairs and all five final idle/stationary pairs. Its two startup differences are the same car raster states and different relief-map capture states at equal pixel size 2. Both final relief captures match exactly. Moving screenshots reflect independently advanced poses, and their capture follows the recorded HUD sample. The deterministic native comparisons above provide the fixed-pose visual regression coverage.

The first browser verification attempt stopped because the new touchscreen helper queried a desktop status field hidden by the compact phone layout. Commit `650feb58cbdf7050c195735cf0e4790e53fd05d3` fixes that fixture. Its complete Chromium suite passes the held-touch Home, C reattachment and pinch-cancellation sequence, followed by restored portrait controls, native landscape scrolling and restart/exit. All native checks, AOT publication, browser verification, paired benchmark and Pages deployment pass in run `42086131355`. [Verified build and deployment](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42086131355)

The deployed Pages artifact is `13444315889`, with ZIP SHA-256 `bf43f9005349e811ea10714ff0de9fc39f619afcfe9953b95c5673eefc0f23c6` and native WASM SHA-256 `9e54519b406895b8ab3b3af4ce55b7ec725e20f896b9c5909df26f907202fc46`. All 72 boot resources validate against the manifest, and all 142 non-hidden uncompressed live files match the artifact byte-for-byte. The run-2 performance evidence artifact is `13443897611`, with ZIP SHA-256 `7573f01d19b56313f9ddf5a7e56e8be8616c7a6fc7812f044817cd3fd7efe0c7`. Run 1's successful benchmark job produced artifact `13443555033`, with ZIP SHA-256 `5df526066609d3f993ab82a98b793e7bb1bb2d8e1e216303870f64d182c215c2`. [First paired benchmark](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42081873673)

## GPU submission and depth queries

The next optimization pass is implemented in `43940e051a5b6bcd5c8eda9c39d5775ae482036f`. The workflow correction in `0b47e25af6e47dc66c1d9fc9483bab54b1fbd348` moves a runner-specific environment value into step scope. Both commits contain the same application source. This pass compares against the previously deployed application at `3777867da454f21ef264c72adb1a65b99ddca95a`.

- Each renderer-owned shader program retains its uniform locations and last submitted values. Typed raw-bit comparisons preserve signed zero and distinct NaN payloads. Unchanged uniforms and inactive locations skip native calls. A failed frame invalidates the caches before retry.
- Frame drawing and shared-context restoration use one final GL error checkpoint. Depth clearing uses `ClearBuffer`, preserving the host's clear-depth value directly. Read-framebuffer capture is scoped to photograph copies, and renderbuffer capture is scoped to framebuffer allocation. Terrain texture filters update when the sampling mode changes. Uno's shared context still restores all modified state, including front-face direction used by building shading.
- Building depth triangles prepare the inverse-W plane for perspective views and the altitude plane for orthographic views. Terrain retains its full interpolation data for picking, physical heights and overlays. Depth-only queries evaluate the required channel with the original floating-point operation order. Query loops access retained triangle storage by reference.

The native call probe uses an identical single-tile, five-draw fixture in both versions. Its moving step changes the camera while keeping the source data and output dimensions fixed.

| Calls or bytes per frame | Previous implementation | Optimized implementation |
| --- | ---: | ---: |
| Uniform uploads, identical warm frame | 54 | 0 |
| Uniform uploads, moving camera | 54 | 6 |
| GL error queries | 2 | 1 |
| Integer, Boolean and float state queries | 29 | 26 |
| Draw calls | 5 | 5 |
| Uploaded bytes | 728 | 728 |

Capability checks run in both versions. The moving-camera uniform reduction is 88.9% in this fixture. These counts quantify submission work independently of end-to-end browser latency.

The full 48-case native GLES suite matches the preceding implementation in all 192 CPU, GPU, artwork and RGBA hash fields. Sixteen new shared-context cases verify exact restored state and pixels across all four render styles, photograph blending, both presentation paths and forced framebuffer resizing. An injected uniform-upload failure restores host state and produces exact pixels after retry. The depth regression adds 128 exact interpolation comparisons. Another eight deterministic moving scenes preserve all 128 dense-query hashes and 128 overlay hashes across perspective, orthographic, flat/relief terrain and block rendering styles.

The full CPU visual comparison also preserves all 232 cases, including 3,712 moving-frame hashes, 232 cold-frame hashes and 232 aggregate sequence hashes. Both runs use identical offline inputs, OpenAI Sans, a 1100×650 viewport and .NET 10.0.14 with tiered compilation disabled. Assembly hashes recorded before and after execution match.

### Paired browser measurements

The paired comparison uses the exact Release `InterpreterAndAOT` Pages artifacts for `3777867` and `0b47e25` on one GitHub Actions runner. Chromium 156.0.8078.4 runs in-process SwiftShader on an AMD EPYC 7763 with affinity 0–2, a 1200×780 viewport and DPR 1. Each of ten scenarios runs the previous artifact followed by the optimized artifact, in fresh Node and Chromium processes. Both warm for two seconds and request an eight-second measurement. Separate passes collect CPU profiles. External providers are blocked and bundled data supplies the scene.

All twenty cases complete successfully. Actual moving windows span 8,002.0–8,151.4 ms previously and 8,003.2–8,157.5 ms after optimization. Rates use each actual interval. Every timed moving readback uses pixel size 4 at 300×166. Held native keys advance the simulation, and final moving poses can differ between versions. Completed artwork copies measure pipeline completion, rAF measures browser scheduling, and final display presentation is outside the probe. Task CPU uses main-thread `threadTicks` and excludes software-GPU worker CPU.

| Moving scenario | Task CPU, previous → optimized | Completed art copies/s, previous → optimized | rAF callbacks/s, previous → optimized |
| --- | ---: | ---: | ---: |
| Walking, first person | 37.00% → 32.70% | 3.965 → 3.998 | 51.17 → 54.85 |
| Walking, chase | 40.05% → 41.27% | 6.258 → 6.497 | 53.62 → 53.94 |
| Driving, chase | 55.15% → 55.20% | 4.371 → 4.122 | 46.33 → 46.85 |
| Driving, first person | 51.62% → 52.34% | 3.067 → 3.081 | 41.22 → 44.24 |
| Free flight | 41.19% → 41.56% | 3.624 → 3.623 | 52.74 → 52.47 |

First-person walking task CPU falls from 2,986.3 to 2,617.2 ms, a 12.36% reduction in the measured windows and an 11.61% reduction after duration normalization. Its rAF interval p95 falls from 50.0 to 33.3 ms. Long-task count falls from 32 to 14, with cumulative duration falling from 2,093 to 851 ms. The other four moving task CPU totals rise by 0.05–3.15%. Each build completes 172 timed artwork copies across the five moving cases. These individual paired samples show mixed end-to-end throughput.

All 344 copies across both builds follow a signaled zero-timeout fence poll. Transfer queues peak at two, with zero overwrites, abandoned transfers or positive-timeout polls. All ten stationary samples across the pair record zero foreground GL draws, transfers, artwork copies and long tasks. Idle relief runs near 60.1 rAF callbacks/s with 1.20% and 1.39% task CPU. The earlier local attempts had two baseline browser crashes and an idle-relief CPU spike. Those attempts are excluded from this paired comparison, and the spike's cause remains unassigned.

Eight of ten initial screenshots match directly, and all five final stationary screenshots match. The car-chase startup difference exactly cross-matches the retained stationary-car adaptive variants in both builds. Relief startup images differ and settle to identical final images. The CSV distinguishes capped startup pixel-size history from actual timed readback sizes and records the input-window poses.

The separate moving profiles contain anonymous AOT/WebAssembly work and shared-context GL queries. Sampled `getParameter` and `getError` self time totals about 205–216 ms per roughly 8.2-second profile. Uniform self time totals 7.8–14.4 ms. Stripped production WASM names limit attribution to individual managed methods. Optimized timed copy-call p95 ranges from 1.9 to 4.5 ms, while submit-to-copy p95 ranges from 341 to 850 ms. Free flight includes one 42.5 ms copy outlier and a maximum rAF gap that rises from 166.7 to 283.4 ms. Physical-GPU and Safari/iOS timing remain unmeasured. [Complete browser measurements, including all twenty paired CI rows](play-browser-benchmarks.csv)

The opt-in `play-benchmark` job runs when a push message contains `[play-benchmark]` or the manual workflow's `play_benchmark` input is enabled. `baseline_run_id` can select a previous successful main run with an unexpired Pages artifact. The job verifies artifact source metadata and boot-resource hashes, runs both builds serially, and uploads raw reports, screenshots, profiles and provenance. The common play harness SHA-256 is `dc6a1801bca4600978e2f3b544c774ab59c9b77abb95d59f34649ce6597dc239`.

Workflow `42030771844` passes the native checks, AOT publish, browser verification, paired benchmark and Pages deployment. Pages artifact `13417530493` has ZIP SHA-256 `f450086fa2259da7f333ac8003ddae44419becaec67059a15bcead5f41817362`. Its native WASM SHA-256 is `9010404fea52d0b0edc042e380b7b7b5c70473ce7537e53474a7697e8c171b8f`. After deployment, all 142 non-hidden uncompressed live files match that artifact byte-for-byte. Evidence artifact `13417148446`, `atlas-play-performance`, has ZIP SHA-256 `e78be10ed9d8ec3f546f27050ac3c812f489e3c0fea7379b126d4f8f0ac008e4`. [Verified build, benchmark evidence and Pages deployment](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42030771844)

### Diagnostic managed preparation measurements

The native probe uses .NET 10.0.14 with tiered compilation disabled, two managed processors, affinity 0–1 and a 1200×680 logical viewport requesting pixel size 4. Each case warms eight frames and measures 24 moving frames. The first pair runs the previous renderer before the optimized renderer. The repeat reverses that order. The table reports the upper middle order statistic for complete `PrepareGpuFrame` calls.

| Scene | Previous, first pair | Optimized, first pair | Previous, reverse pair | Optimized, reverse pair |
| --- | ---: | ---: | ---: | ---: |
| Walking with relief | 35.87 ms | 22.42 ms | 44.36 ms | 28.88 ms |
| Flight with relief | 53.99 ms | 35.61 ms | 45.81 ms | 41.41 ms |
| Walking on flat terrain | 33.23 ms | 15.85 ms | 19.77 ms | 15.96 ms |
| Flight on flat terrain | 42.14 ms | 31.81 ms | 44.74 ms | 38.20 ms |
| Walking in Minecraft style | 50.29 ms | 36.66 ms | 54.75 ms | 54.59 ms |
| Flight in Roblox style | 47.26 ms | 44.19 ms | 48.84 ms | 44.47 ms |
| Orbital camera | 22.85 ms | 14.17 ms | 15.05 ms | 22.89 ms |
| Orthographic camera | 16.08 ms | 11.85 ms | 11.25 ms | 16.03 ms |

The six physical-camera cases have lower preparation medians in both orders. Orbital and orthographic results reverse between runs, and individual samples have substantial scheduling variation. These local latency magnitudes remain diagnostic. Complete-preparation and independent depth-reconstruction medians describe separate samples. Geometry, bin memberships and label-query counts match frame by frame.

The CSV retains all 768 timing records, allocation counts, exact renderer hashes, both execution orders and separately collected dense-query and overlay hashes. The first harness executable was overwritten during a metadata and trace-instrumentation revision, so its binary hash is explicitly unavailable. The repeat records its exact harness hash. First-run resolved pixel size is inferred from the repeat with the same renderer hash. [All managed preparation and depth samples](play-depth-benchmarks.csv)

## Play-mode rendering

The play-mode changes landed in `6757c313a5645e610e7ed7c9d1f8b04dc7788a03` and `3777867da454f21ef264c72adb1a65b99ddca95a`. The comparison below uses the original application at `11188d0c265c9688ca8a169200e2f1573ec9fbeb` and the final implementation at `3777867`.

- Physical cameras select building prisms against conservative homogeneous frustum planes. Visible wall/cap indices and roof-edge instances compact in their original primitive order. Source vertices stay resident, and unchanged model visibility reuses the compact arrays and GPU uploads. The scene-depth index shares whole-model rejection. Vertex shaders exit immediately for invisible caps and roof edges.
- Browser pixel transfers copy only after a fence signals. The two-slot queue retains bounded backpressure, canceled transfers drain after completion, and pending camera changes coalesce into the latest request. The previous elapsed-time copy policy could block `getBufferSubData` for seconds in the software-GPU play fixture.
- A stationary camera reuses identical foreground artwork. Stopped standalone vehicles advance their effects clocks through `TryAdvanceStationaryVehicle` while retaining their picture. First drawing, changed poses, route/gate overlays, mode transitions, clock discontinuities and resumed movement use the ordinary draw path. This preserves the effects timeline when movement resumes after a long stop.
- Roof endpoints contain four floats each, reducing each edge instance from 96 to 32 bytes. CPU scene-depth construction caches clipping codes after face rejection and reuses them for indexed triangles. Projected vertices and model visibility reuse the existing two-workspace pool. The additional retained scratch is bounded at 131,072 projected vertices and 16,384 visibility entries per accepted workspace.
- HUD text and accessible names update on changed values, structural controls update on session transitions, and hidden map readouts refresh when returning from Play. Visible telemetry, scale information and status changes retain their live update paths.

The native regression compares selected streams with complete meshes on 108 identical physical-camera frames across Pixel, Normal, Minecraft and Roblox styles and Map, Earth and Elevation layers. Exact RGBA, terrain intersections, overlay depth, held-stream restoration and unchanged-visibility upload reuse pass. The existing 48-case GLES suite and HiDPI/context-recreation checks also pass. Another 48 cases compare stationary vehicle pixels and complete effects state across all six travel modes, four styles and flat/relief terrain, including long stops and resumed movement. The final renderer also preserves all 232 pinned Pixel cases against `c238352`, including 3,712 moving-frame hashes and 232 cold-frame hashes.

### Browser play measurements

The browser comparison uses the exact GitHub Actions Release `InterpreterAndAOT` artifacts for `11188d0` and `3777867`, Chromium 156.0.8078.4, SwiftShader with an in-process GPU, CPU affinity 0–2, a 1200×780 viewport and DPR 1. External providers are blocked, and the bundled map data supplies both scenes. Each scenario starts in a new browser context, warms for two seconds and requests an eight-second measurement. The actual moving windows span 8,044.6–9,133.4 ms originally and 8,001.9–8,244.9 ms after optimization. Rates use each measured elapsed interval. CPU profiles run in separate passes.

All moving submissions use actual 300×166 art buffers at pixel size 4 in both builds. Completed pixel-pack copies count completed GPU artwork. `requestAnimationFrame` counts browser scheduling callbacks. Final display presentation is outside these probes. Held native keys exercise the same input workload, while the faster simulation travels farther and reaches different final poses. The native comparison below uses identical camera sequences to verify the rendered inputs separately.

| Moving scenario | Original completed art copies/s | Optimized completed art copies/s | Throughput gain | Original rAF callbacks/s | Optimized rAF callbacks/s |
| --- | ---: | ---: | ---: | ---: | ---: |
| Walk, first person | 0.691 | 4.584 | 6.64× | 4.03 | 46.22 |
| Walk, chase | 0.564 | 7.520 | 13.34× | 3.72 | 53.97 |
| Car, chase | 0.657 | 4.444 | 6.76× | 4.27 | 43.70 |
| Car, first person | 0.557 | 3.999 | 7.17× | 2.56 | 41.86 |
| Free flight | 1.243 | 4.249 | 3.42× | 8.70 | 53.86 |

| Moving scenario | Original copy-call p95 | Optimized copy-call p95 | Original rAF interval p95 | Optimized rAF interval p95 |
| --- | ---: | ---: | ---: | ---: |
| Walk, first person | 2,142.4 ms | 5.7 ms | 1,350.0 ms | 66.6 ms |
| Walk, chase | 2,560.6 ms | 3.4 ms | 1,699.9 ms | 16.8 ms |
| Car, chase | 2,538.5 ms | 3.4 ms | 1,016.7 ms | 16.8 ms |
| Car, first person | 2,320.0 ms | 3.0 ms | 1,966.7 ms | 83.3 ms |
| Free flight | 1,324.9 ms | 7.1 ms | 683.3 ms | 16.8 ms |

Across the five moving windows, cumulative `getBufferSubData` call time decreases from 38,576.3 to 261.4 ms, a 99.3% reduction. All 201 completed final copies follow an observed signaled fence. Outstanding transfers stay bounded at two, with zero overwrites, abandoned transfers or positive-timeout polls. Main-thread task occupancy rises during movement as the application completes more artwork and advances the simulation farther. The `threadTicks` metric excludes blocked time and software-GPU worker CPU.

| Stationary play scenario | Original rAF callbacks/s | Optimized rAF callbacks/s | Original main-thread task CPU | Optimized main-thread task CPU | Original foreground GL draws | Optimized foreground GL draws |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Walk, first person | 23.35 | 60.20 | 16.92% | 6.32% | 7,421 | 0 |
| Car, chase | 24.33 | 59.98 | 35.22% | 6.20% | 26,688 | 0 |
| Free flight | 22.74 | 59.98 | 15.64% | 5.63% | 8,188 | 0 |

All three final stationary-play cases record zero foreground draws, depth draws, transfers and art copies. Two additional stationary-car runs record 60.10 callbacks/s, 7.48% and 8.83% task CPU, zero drawing/transfers/copies, and identical before/after screenshots. The default and relief map-idle cases remain around 60 callbacks/s with 1.47–1.92% task CPU and zero drawing/transfers.

Seven of ten initial full screenshots match the original directly. The two car startup differences match the original's same-pose adaptive 3-pixel and 4-pixel variants exactly when cross-compared. The initial idle-relief screenshot differs at pixel size 2 in both builds and settles to an exact match. All five final stationary screenshots match the original. The CSV's `boot_inferred_final_pixel_size` comes from capped startup history and can differ from retained startup artwork. Its `readback_dimensions` and `readback_pixel_sizes` describe actual timed submissions. The CSV records input-window poses. Moving screenshots are captured after key release and may include additional vehicle coasting.

These are individual software-GPU timing samples. The final run still has maximum scheduling gaps of 150–600 ms across moving scenarios. Car-chase and free-flight maxima are higher than in the first optimization run, while all five final maxima improve over the original. The presentation path still copies completed WebGL artwork into Skia storage. Physical-GPU and Safari/iOS timing remain unmeasured. The CSV includes the original, first optimization, final implementation and both stationary-car repeats, with actual durations, main-thread CPU measurements, completed artwork copies, queue observations, poses and harness hashes. [Complete browser measurements](play-browser-benchmarks.csv)

The final Pages artifact is `13364936410` from workflow `41913417612`. Its ZIP SHA-256 is `4659e43cf10cbb760999b383bb42ab3e2fe835c3e5a9dec4e8dd4d099ce98c7c`. The final native WebAssembly SHA-256 is `774a4f3b2c9028806ebabaad4b98f787e54bd6fe89b4fa6715d420eea164eac7`. Artifact verification checks all 69 boot resources and the signaled-only readback policy. After deployment, all 74 checked live files, including the entry point, boot resources and input adapter, match the measured artifact by SHA-256. [Build, browser verification and Pages deployment](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/41913417612)

```sh
npm ci --prefix tests/browser
(cd tests/browser && npx playwright install chromium)
PIXELMAP_REVISION=3777867da454f21ef264c72adb1a65b99ddca95a \
  node tests/browser/play-performance.cjs \
  https://wieslawsoltes.github.io/UnoPixelMap/ artifacts/play-performance
```

### Moving native frames

The paired native comparison renders nine bundled Manhattan vector tiles containing 33,747 features, world-DEM relief at exaggeration 1, Day/Pixel style and a 1200×680 logical viewport at pixel size 4. Each eye-height case warms four frames and measures sixteen deterministic moving frames. The total includes scene preparation, GL submission and `glFinish` completion. Pixel readback, hashing, navigation overlays and Uno/browser presentation are outside that timing. Each reported median uses the upper middle of the sixteen sorted samples. Phase medians are calculated independently.

| Eye height | Original total median | Optimized total median | Speedup | Original preparation median | Optimized preparation median |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1.7 m | 92.55 ms | 37.92 ms | 2.44× | 19.26 ms | 17.19 ms |
| 300 m | 112.94 ms | 75.50 ms | 1.50× | 36.13 ms | 32.99 ms |

| Eye height | Original mean managed allocation per frame | Optimized mean managed allocation per frame | Reduction |
| --- | ---: | ---: | ---: |
| 1.7 m | 2,424,709.5 bytes | 484,912.5 bytes | 80.0% |
| 300 m | 2,645,882.5 bytes | 1,229,196.5 bytes | 53.5% |

| First measured frame | Original submitted indices | Optimized submitted indices | Original roof-edge instances | Optimized roof-edge instances |
| --- | ---: | ---: | ---: | ---: |
| 1.7 m | 1,445,862 | 196,191 | 141,273 | 0 |
| 300 m | 1,445,862 | 215,955 | 141,273 | 26,015 |

All 32 moving RGBA hashes match the original renderer exactly. In the first measured frame, GL draw calls fall from 21 to 7 at street level and from 21 to 11 in flight. Changing model visibility uploads new compact streams. Total uploads across the sixteen moving frames increase from 3,824,216 to 7,132,412 bytes while walking and from 7,115,648 to 18,597,924 bytes while flying. The measured complete-frame totals include this additional transfer work. The reduced edge layout lowers flight uploads from the first optimization's 30,017,572 bytes. [Per-frame timings, geometry, uploads and exact image hashes](play-native-benchmarks.csv)

These native measurements use .NET 10.0.14, disabled tiered compilation, two managed processors and two llvmpipe workers with LLVM 19.1.7. They measure a software GPU. The original Rendering/Data/Routing sources are identical from the preserved `16c8148` binary through `11188d0`. Original renderer SHA-256 is `660433930301b94107d7199db9e456bd9f59ed0fe56bfdff828ca336b1d4c51e`, and final renderer SHA-256 is `e09dec1784a72c3e5d6b8d86f61a03cec87ab383c902bbd6c74fe0fb412981a7`. Both runs use probe SHA-256 `cd3dcd92199e89334d03b39430317b7b5eed434ff49b98fa74d24b5c88bd32c8`. Final application source and project files match `3777867` exactly.

## Retained GPU terrain

Terrain relief and the Elevation layer use a WebGL2/OpenGL ES 3 geometry renderer through Uno 6.7's public `GLCanvasElement`. The existing adaptive quadtree selects at most 64 stitched patches with 17×17 vertices each. One instanced indexed draw projects all selected patches. A 256-layer RG32F texture array retains height and illumination samples, a 96-layer RGBA8 array retains pixel artwork, and a compact per-instance stream supplies rebased homogeneous transforms and array indices. Moving the camera updates roughly 3.5 KiB of instance data for 64 patches. Mesh and artwork layers upload when their revisions change.

Camera-distance request priorities can reorder the same tile objects. GPU artwork invalidation compares source reference identity independently of priority when tile IDs are unique, preserving cached textures through those priority changes while detecting replacement data and membership changes. Reordering duplicate tile IDs rebakes their overlapping artwork. Sixteen source-order permutations for each of Map, Earth and Elevation preserve exact artwork and submit only the warmed instance stream, 2,072 bytes in the Garda fixture. This removes the repeated whole-texture batches observed in the first browser implementation.

Building meshes are retained per vector tile. Even/odd tessellation preserves roof holes, disconnected polygons and self-intersections. Model tables hold physical foundations, material, seed and height data. Vertex shaders apply the selected terrain foundation and camera projection. Fragment shaders reproduce façade lighting, windows, bands and roof edges. Terrain and buildings share a hardware depth attachment at art resolution. The final nearest-neighbor resolve preserves the pixel grid, including overhead Earth photograph blending.

Geometry preparation retains the same LOD selection and stitched height values as CPU rendering. A sparse screen-space triangle index evaluates only the depth samples requested by labels, navigation, effects and pointer input. A bounded pool retains up to two released depth workspaces for reuse, while each live frame keeps its own depth ownership. The six vehicle modes and existing game simulation continue through their established APIs.

The Uno host captures an immutable art-resolution image and presents it with nearest sampling through the existing Skia canvas. Labels, routes, gates, vehicles and effects draw against the same retained depth frame at the canvas's native DPI. Terrain inspection and marker projection use the displayed frame's camera, terrain and logical viewport, including geographic fallbacks while newer camera input is pending. Pending camera changes retain the visible image and overlays until publication. The host suppresses duplicate foreground invalidation and already-scheduled GL paints. Overlay-only animation reuses the terrain image, and the GL visual is hidden while idle to prevent repeated readbacks. Initialization, render and readback failures return the control to CPU rendering. Unload releases managed frames and GPU resources through their owning rendering contexts.

The browser host adapts Uno 6.7's framebuffer hook to a two-slot WebGL2 pixel-pack queue. Each submission owns a 64-bit retained-frame token and its artwork dimensions. A 16 ms timer polls fences with zero timeout and selects the newest signaled transfer. Elapsed time never forces a copy from an unsignaled transfer. Occupied slots bound submission while pending camera input coalesces into the latest request. Completed pixels copy directly into immutable RGBA Skia storage, and the artwork draw flips GL's bottom-up rows on the canvas. The asynchronous host detaches Uno's unused bitmap from its image brush, preventing duplicate image uploads. Cancellation releases managed frame payloads immediately, while unsignaled native transfers retain their slots until a fence signals or a newer valid image completes. Canceled images drain without pixel copies. Polling stops when the queue empties. The adapter scopes interception to the control's GL context and read framebuffer and retains synchronous compatibility for other hosts.

Capturing at art resolution reduces transferred pixels by 4× to 16×. The native capture benchmark measures the portable synchronous path, including GPU completion, readback, channel conversion, byte-buffer transfer, row reversal, immutable image creation, nearest composition and full-resolution overlays. Browser measurements exercise the asynchronous host and include Uno's presentation work. Measurements in this environment use Mesa llvmpipe and Chromium SwiftShader software drivers.

### GPU verification

The native GLES suite exercises 48 combinations of terrain, building geometry, Earth imagery, altitude tint, exaggeration, projection, palette, viewport and pixel size. It also checks 408 terrain intersections, retained A→B→A frame activation, resizing, framebuffer state restoration, complete EGL context recreation and 2× physical-resolution composition. Across the current 48-case fixture, 25 images match exactly and the maximum mean RGB difference is 0.0188 on the 0–255 channel scale. The largest structural difference beyond the one-art-pixel comparison neighborhood is 18 display pixels in a small perspective-model case.

All six vehicle modes pass 24 terrain configurations with seven animation/heading/pause/seek states each. These 168 premultiplied overlays match CPU output byte-for-byte. Six further ridge-occlusion cases cover routes, gates and vehicles. GPU composition differs by at most one channel level in the overlay fixtures. Native high-DPI checks preserve nearest map pixels, scaled glyphs and independent read/draw framebuffer bindings.

Bottom-up RGBA image presentation with a canvas flip matches the existing BGRA row-flipped presentation byte-for-byte across all 48 scenes, all 168 journey frames and the odd-sized 391×233 logical / 782×466 physical viewport fixture.

The compiled browser adapter has 17 real WebGL2 checks covering exact RGBA bytes, full 64-bit frame tokens, two contexts, foreign framebuffer scope, pack-state restoration, resize, bounded transfers, overdue unsignaled transfers, canceled unsignaled transfers, recovery through a later valid frame, context loss and disposal. The standalone fixture compiles the production C source and uses the existing Playwright dependency. With an activated Emscripten toolchain, the checks run independently of the Uno build. The verified compiler is Emscripten 3.1.56.

```sh
readback_out=artifacts/readback
mkdir -p "$readback_out"
emcc -Wall -Wextra -Werror -O0 -g0 \
  tests/browser/readback-fixture.c src/PixelMap.Controls/Browser/pixel_map_gl_readback.c \
  --no-entry -sENVIRONMENT=web -sMODULARIZE=1 -sEXPORT_NAME=PixelMapProof \
  -sEXPORTED_RUNTIME_METHODS='["GL"]' -sMIN_WEBGL_VERSION=2 -sMAX_WEBGL_VERSION=2 \
  -sALLOW_MEMORY_GROWTH=1 -Wl,--wrap=uno_gl_resolve \
  -o "$readback_out/readback-fixture.js"
node tests/browser/readback.cjs "$readback_out"
```

The terrain browser suite checks actual depth-tested WebGL draw calls at art resolution and framebuffer presentation. It also toggles the backend off and on, switches layers, changes relief and exaggeration, picks terrain, restores shared views and exercises handset controls. The browser performance harness records actual art-resolution distributions and `readPixels` synchronization time.

Four settled Chromium views compare real Garda Map, Earth and Elevation in perspective plus overhead Earth. In the local Interpreter build, mean RGB differences range from 0.112 to 0.388 on the 0–255 scale, with 0.74–1.30% of crop pixels changed. The differences occur in small texture-sampling and rasterization groups. The largest connected group whose channel difference exceeds two levels contains 20 logical pixels. Coastlines, mountain silhouettes, roads and label placement agree. The control also passes 2× display-density and resize checks. Blocking creation of the terrain GL context produces CPU fallback with a map hash identical to the normal CPU render. A two-second stationary GPU probe records no additional framebuffer readbacks after the initial image.

```sh
LIBGL_ALWAYS_SOFTWARE=1 EGL_PLATFORM=surfaceless DOTNET_TieredCompilation=0 \
  dotnet run -c Release --project tests/PixelMap.Rendering.Tests -- \
  --gpu-terrain --gpu-benchmark --gpu-output=artifacts/gpu-terrain

node tests/browser/performance.cjs http://localhost:8000/ artifacts/terrain-cpu --motion --terrain
node tests/browser/performance.cjs http://localhost:8000/ artifacts/terrain-gpu --motion --terrain --gpu
```

The native benchmark uses the bundled 3×3 Garda vector, imagery and elevation cluster plus world parents. Each of nine layer/pixel-size conditions uses 16 warmup frames and 32 measured frames from a deterministic pan/orbit sequence at 1100×650. It reports CPU frame time, GPU scene preparation, command submission, capture/composition, upload bytes and retained GPU storage separately. Browser comparisons use matching AOT builds and must be read alongside the adaptive pixel-size distribution.

### Native terrain measurements

The following complete-frame measurements use .NET 10.0.14 on Debian 13, Mesa 25.0.7 llvmpipe, three Mesa workers, `DOTNET_TieredCompilation=0` and a fixed pixel size. The GPU column includes the art-resolution capture and Skia presentation stages described above. The final Uno UI compositor is measured separately in the browser.

| Layer | Pixel size | CPU median | GPU median | Reduction | CPU p95 | GPU p95 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Map | 2 | 24.19 ms | 16.44 ms | 32.1% | 26.91 ms | 21.72 ms |
| Earth | 2 | 24.42 ms | 16.84 ms | 31.0% | 27.33 ms | 20.76 ms |
| Elevation | 2 | 33.01 ms | 17.96 ms | 45.6% | 51.17 ms | 26.57 ms |
| Map | 3 | 19.19 ms | 11.49 ms | 40.1% | 22.09 ms | 14.31 ms |
| Earth | 3 | 17.69 ms | 16.65 ms | 5.9% | 22.50 ms | 34.81 ms |
| Elevation | 3 | 25.97 ms | 11.95 ms | 54.0% | 44.51 ms | 15.37 ms |
| Map | 4 | 13.85 ms | 9.04 ms | 34.7% | 20.13 ms | 11.51 ms |
| Earth | 4 | 12.38 ms | 9.16 ms | 26.1% | 14.85 ms | 11.51 ms |
| Elevation | 4 | 12.37 ms | 8.98 ms | 27.4% | 15.66 ms | 13.07 ms |

All nine medians improved in this batch. Earth at pixel size 3 has a higher p95. The driver runs shader work on CPU workers, and physical-GPU performance remains unmeasured. Scene preparation and GL command submission both consume the calling thread. GPU preparation alone therefore represents only part of main-thread cost. [Phase timings and storage measurements](terrain-gpu-benchmarks.csv)

The retained tile/model resources occupy 24.57 MiB in this scene. Average uploads are 571.67 KiB at pixel size 2, 9.15 KiB at size 3 and 5.45 KiB at size 4, including revisions caused by LOD selection during the camera sequence. Art readback transfers 698.24 KiB, 311.09 KiB and 175.10 KiB per frame respectively. The pixel-size-4 GPU completion/readback median is 0.49–0.50 ms across layers.

The measured renderer SHA-256 is `39d5a7c3ff19b3750118004c4e62fa5642a888aaf8359c9a6063b29494aefee6`. The harness SHA-256 is `8d9b25640ad0d65306294f115436b2d037b4b1f77867338868911ddd3b006674`, and the data assembly SHA-256 is `bd08e03df8977b0eebf87aa237eff7aee358a37874cd0966760e87879b6baee1`.

### Historical browser terrain measurements

These measurements predate the play-mode optimization and use the former 16 ms deadline-based copy policy. The current adapter waits for a signaled fence as described above.

The browser comparison uses the original elevation CPU build `c238352dcb9df7fd2cfb8ba5e86fd20f38169597`, the first synchronous GPU build `274604100ee53e29edccc8bda0871cc5737bc869` and the deployed GPU build `a764a0b9179b6f5c3b0bc42b36285034bd9cc7b6`. Each run uses its exact GitHub Actions Release `InterpreterAndAOT` artifact. Chromium 156.0.8078.4 runs SwiftShader on three AMD EPYC 9V74 logical processors, with a 1200×780 viewport and DPR 1. Runs execute sequentially during a quiet local measurement window. The fixture starts at Lake Garda, zoom 12.3, bearing −25° and perspective pitch 58°. External providers are blocked, and each gesture contains 80 physical pointer moves with a nominal 25 ms delay.

The production adaptive-quality policy stays active. The original CPU cases use 0–1 initial pixel-size-3 uploads before pixel size 4. Final GPU cases use 1–2 pixel-size-3 depth frames before pixel size 4. The reports retain the complete resolution histograms. Main-thread task time uses Chromium's `threadTicks` metric, while elapsed time includes blocking and input dispatch. SwiftShader also consumes separate worker-thread CPU time.

| Gesture | CPU task time | Final GPU task time | CPU elapsed | Final GPU elapsed | CPU art-image uploads/s | Final GPU art-image uploads/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Map pan | 4,731.1 ms | 4,598.6 ms | 8.51 s | 12.69 s | 9.98 | 5.28 |
| Map orbit | 5,674.1 ms | 4,990.9 ms | 6.52 s | 7.23 s | 11.66 | 5.81 |
| Earth pan | 9,962.7 ms | 4,059.2 ms | 13.48 s | 11.88 s | 7.12 | 5.55 |
| Earth orbit | 6,489.1 ms | 5,169.4 ms | 7.38 s | 7.55 s | 9.49 | 5.17 |
| Elevation pan | 4,954.4 ms | 4,351.4 ms | 8.78 s | 11.96 s | 9.79 | 5.68 |
| Elevation orbit | 6,232.7 ms | 4,921.2 ms | 7.02 s | 7.35 s | 10.54 | 5.44 |

The final GPU path reduces main-thread task time in all six first-run samples, with the largest reduction in Earth pan at 59.3%. Art-image upload rates are lower in all six, and five gestures take longer. Completed pixel-pack copies and art-image uploads are separate observations. A completed image can be superseded before upload, and the compositor can upload the same image again. These counters measure pipeline work. Actual display presentation is outside the probe. The first synchronous GPU version also uploaded its unused GLCanvas bitmap, so its raw 2D upload counts include duplicate work.

| Gesture | GPU submissions / completed copies | Completed copies/s | Submit-to-copy median / p95 | Initial GPU artwork uploads | Final GPU artwork uploads |
| --- | ---: | ---: | ---: | ---: | ---: |
| Map pan | 80 / 68 | 5.36 | 105.2 / 212.3 ms | 312.476 MB | 34.341 MB |
| Map orbit | 43 / 40 | 5.53 | 41.1 / 97.4 ms | 186.909 MB | 87.818 MB |
| Earth pan | 74 / 64 | 5.39 | 120.7 / 197.8 ms | 310.903 MB | 34.341 MB |
| Earth orbit | 42 / 38 | 5.04 | 57.8 / 130.4 ms | 176.423 MB | 87.556 MB |
| Elevation pan | 77 / 66 | 5.52 | 101.7 / 196.1 ms | 337.379 MB | 34.341 MB |
| Elevation orbit | 44 / 40 | 5.44 | 48.5 / 181.8 ms | 184.549 MB | 83.362 MB |

Artwork-array uploads fall by 50.4–89.8% across these gestures. These totals include the effect of frame coalescing, so frame counts accompany them. The source-order permutation checks independently verify cache reuse with identical rendered pixels. Height-array uploads are recorded separately and range from 203–705 kB in the final cases. MB and kB in these browser tables use decimal units.

Every final gesture retains at most two outstanding transfers, with zero unsafe buffer overwrites, abandoned transfers or positive-timeout fence polls. Median `getBufferSubData` calls cost 76.3–86.9 ms during pans and 5.2–7.2 ms during orbits. The 16 ms selection budget therefore still encounters substantial synchronization time on SwiftShader. The earlier asynchronous build `7fffd732ea8f7df276194d3ee1a7c31626bf532c` exposed longer presentation delays during diagnosis. The final policy reduces those delays, while the complete CPU comparison above records the remaining software-driver throughput regression.

Repeating the final Map pair with the same artifact and unmodified harness gives 5.66 art-image uploads/s for pan and 5.63 for orbit, compared with 5.28 and 5.81 in the first pair. Completed copies measure 5.50/s in both repeats. Pan submit-to-copy median/p95 is 105.1/187.9 ms, and orbit is 49.7/135.0 ms. The repeats reproduce the throughput regression and satisfy the same bounded-transfer checks.

The exact deployed native WebAssembly SHA-256 is `adf1d76ecec2a38f0cc977bfb0fc268ffaefdfbbd4234b9488cf838223085014`. The common browser harness SHA-256 is `c5d6c2b91a2ef0ec4c48fec0d7f4377ad9977e7830382d705d316fd3bd3f8ae0`. The CSV retains all three six-gesture runs, source and harness identity, runtime mode, upload dimensions, queue accounting, copy timing and the repeated Map measurements. Physical-GPU performance remains unmeasured. [Complete terrain browser measurements](terrain-browser-benchmarks.csv)

## CPU work

- Building models retain normalized edges, facade lighting and ring bounds after visibility checks first require them. Geometry eviction releases these retained data with the tile.
- Complete off-screen ring prisms are rejected within multipart building features. Enclosing rings and courtyard holes participate in the original even/odd roof fill. Each roof ring carries its vertical range so scanlines visit only intersecting rings.
- Orthographic projection reads compact source points. Edge preparation is reserved for visible walls.
- Perspective walls reuse top and bottom homogeneous vertices. Fully accepted clipping planes skip copies, and fully rejected planes terminate processing. Crossing planes preserve the original operation order and interpolation arithmetic.
- Per-tile camera and frustum values are retained for a frame. Perspective sorting moves compact index/depth pairs while model and transform data stay in place.
- Labels retain measured widths and native glyph blobs. Neighbor-shadow discovery runs when the geometry set changes.
- Route projection reuses camera constants. Smooth Earth-image clusters resolve their palette pair once while preserving all Bayer phases.

## Visual equivalence

The native comparison covers 232 cases and 16 moving frames per case, plus cold draws and cached replays. This produces 3,712 moving-frame hashes and 232 cold-frame hashes. Cases include overhead, isometric and three perspective pitches, Map and Earth, all three palettes, pixel sizes 2/3/4 and all six vehicle modes.

Synthetic cases exercise nonstandard vector-tile extents, disconnected footprints, concave roofs, courtyard holes, duplicate and empty rings, raised building parts, enclosing rings, all four screen edges, and near/far-plane crossings. The benchmark compares raw BGRA pixels and saves representative PNGs. Image encoding and hashing occur outside the timing samples.

Separate randomized geometry checks compared 16,000 tile transforms and 320,000 clipped polygons with the baseline. GPU fallback tests also compare 360 software-canvas combinations across mode toggles, cached redraws, palette changes and resizing.

## Native measurements

The benchmark uses a 1100×650 canvas, nine bundled Manhattan vector tiles containing 33,747 features, a downtown view at zoom 16.5 and a wider view at zoom 14.8. Each condition has three fresh-renderer draws, 16 warmup frames, 32 stationary samples and 32 moving samples. Camera movement follows a deterministic 16-frame sequence. Vehicle cases advance their route progress and effects history.

Measurements were collected on Ubuntu 24.04.3, x64, .NET 10.0.14, workstation GC and DejaVu Sans. Both processes used `DOTNET_TieredCompilation=0`. Benchmark processes ran sequentially while local builds and browser probes were paused. Managed allocation measurements cover the calling thread. Cold samples include rendering caches and exclude source decoding and renderer construction.

The final 50-condition sample reduced median moving-frame time by **43.10%** across conditions. Moving p95 improved in all 50 conditions. The representative fixed-2-pixel cases below include map, route, vehicle and gate drawing.

| Scene | Baseline p50 | Optimized p50 | Reduction | Baseline p95 | Optimized p95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Downtown overhead Map | 5.22 ms | 4.20 ms | 19.5% | 5.83 ms | 4.68 ms |
| Downtown isometric Map | 11.63 ms | 10.09 ms | 13.3% | 15.19 ms | 12.85 ms |
| Downtown perspective 65° Map | 53.88 ms | 30.41 ms | 43.6% | 74.57 ms | 40.45 ms |
| Downtown perspective 65° Earth | 59.79 ms | 30.97 ms | 48.2% | 121.07 ms | 43.53 ms |
| Wider overhead Map | 38.39 ms | 13.13 ms | 65.8% | 42.32 ms | 18.89 ms |
| Wider perspective 72° Earth | 93.58 ms | 67.35 ms | 28.0% | 100.35 ms | 78.49 ms |

Two moving medians were flat or slower in that batch. Downtown isometric Earth measured 14.2876→14.3112 ms, and train/Map measured 11.8499→12.2815 ms. Three alternating-order, paired train reruns with 64 moving samples each measured 13.07→10.86 ms, 13.77→12.00 ms and 13.46→9.57 ms, with lower p95 in all three pairs. The complete CSV retains both the batch result and the repeated measurements. [All 50 conditions](renderer-benchmarks.csv) · [Paired train measurements](renderer-train-repeats.csv).

Cold medians improved by a median of 25.25%, with 10 slower conditions. The median increase in managed allocation during a cold draw is 592.6 KiB. Changes range from 187.2 KiB less to 3,062.0 KiB more, with the maximum in the wider isometric Map scene. Cached stationary frames stay around 1 ms, with a median change of +0.39%. In the wider perspective 72° Earth case, moving managed allocation falls from 7,804 to 2,524 bytes per frame. These measurements preserve the original pixel resolution, terrain-cache budget and adaptive-resolution thresholds.

The baseline renderer SHA-256 is `0f48f5e21eea8563358624e8af75eaaad3d9bf431d2fa197ebeb7216c4491706`. The measured optimized renderer is `8ed09ca67b259b6c9588c8824afe0281a0ff41eeee95f3572c1abea72361fe10`. The input-resource SHA-256 is `9abdc9d0e30304804253732cafab36e85f072c766dcb00e3f8107d300c38d47a`.

To reproduce the native comparison, build the benchmark harness, preserve the baseline renderer and its dependencies, and run the same harness with each renderer. The existing eight-argument `Draw` entry point supports this comparison.

```sh
DOTNET_TieredCompilation=0 dotnet run -c Release --project tests/PixelMap.Rendering.Tests -- \
  --benchmark --output artifacts/rendering --samples 32 --warmup 16 --cold-samples 3

dotnet run -c Release --project tests/PixelMap.Rendering.Tests -- \
  --benchmark --visual-only --output artifacts/visual \
  --compare path/to/baseline/report.json
```

Use `--filter` to select case names. Reports include per-frame samples, phase timings, allocation counts, input and renderer hashes, runtime details and the font family.

## Flat-view Skia GPU backend

The flat Pixel GPU backend uses `SKVertices`, `SKRuntimeEffect` and art-resolution GPU images. Model rasterization supplies an opaque-pixel/transparent-background layer and the same depth information used by navigation and picking. Labels and journey graphics continue through the map's Skia canvas.

Uno normally records `SKCanvasElement` drawing into an immutable picture. The control checks the public `CompositionCapabilities.AreEffectsFast()` capability and authorizes deferred GPU rendering. `SKImage.FromPicture` retains the art-sized terrain and composition commands. Skia generates their textures on the actual GPU playback context. Direct GPU canvases use reusable `SKSurface` render targets. The runtime effect samples the pixel grid through a retained two-triangle mesh.

Native OpenGL tests verified zero GL draws or pixel uploads while recording, followed by three GL draws and two framebuffer attachments during shape-only deferred playback, with zero terrain pixel uploads. They also checked retained-picture lifetime, resizing, context abandonment, allocation limits, CPU fallback and mode switching. These tests used Mesa llvmpipe OpenGL ES 3.2.

The GPU path preserves the visual design and features. CPU and GPU blending have small rounding differences. In the native overhead-Earth fixture, mean RGB error was 0.390 on a 0–255 scale and only four of 715,000 pixels differed by more than two channel levels. Isometric vector-map output was exact. Both GPU paths produced identical pixels, and all 35 feature-picking probes per scene agreed with CPU picking.

The app uses Uno 6.7.135 and SkiaSharp 3.119.2. The released SkiaSharp 4.154 binding also exposes `SKVertices` and runtime effects. Programmable `SKMesh` is tracked in the upstream API work item. [SkiaSharp mesh API tracking](https://github.com/mono/SkiaSharp/issues/3777) · [Skia runtime effects](https://skia.org/docs/user/sksl/).

## Browser measurements

The browser harness uses the deployed application and physical pan/orbit input. It records main-thread CPU time, long tasks, frame cadence, actual art-texture sizes and off-screen framebuffer work. Performance pairs use matching Release `InterpreterAndAOT` builds, a 1200×780 viewport, DPR 1 and the same Chromium version. External network requests are blocked so the bundled scene supplies identical data.

Pixel and Minecraft styles adapt between pixel sizes 2, 3 and 4 during interaction. Read the resolution histogram alongside browser CPU totals. The native benchmark provides fixed-resolution comparisons. Browser frame-cadence samples describe browser scheduling between updates.

The recorded production comparison uses baseline `31efd232a2e6e273fd0a56f23e8d50319d29f433` and optimized application `ab93cbc4d2ab1b571a0a9083f2c7e40207f1a68f`, with the menu-focus test helper from `2b2bcadfb9233bd8a9208f3bef745b5e763e27f9`. The optimized application's native WebAssembly integrity hash is `sha256-aQV1JvBTN7zB5A+97dWuvsaNc+OD4/8jOVLQCtHm5e4=`. Baseline, optimized CPU and opt-in GPU ran in that order, in separate browser processes during a quiet local measurement window. Each condition has one measured gesture of 80 physical pointer moves with a nominal 25 ms delay.

Chromium 156.0.8078.4 used SwiftShader on an AMD EPYC 9V74 host. The table records main-thread task CPU time through the browser's `threadTicks` metric. SwiftShader executes GPU work on separate worker threads. The CSV includes elapsed gesture time, long-task counts and durations, and actual texture-upload counts at each pixel size. [All browser measurements](browser-benchmarks.csv).

| Gesture | Previous CPU | Optimized CPU | Opt-in GPU |
| --- | ---: | ---: | ---: |
| Map isometric pan | 2,495.0 ms | 2,360.7 ms | 2,129.7 ms |
| Map isometric orbit | 2,588.1 ms | 2,922.6 ms | 2,037.5 ms |
| Map perspective 65° pan | 9,441.0 ms | 4,813.9 ms | 4,237.2 ms |
| Map perspective 65° orbit | 7,602.0 ms | 4,579.2 ms | 4,357.9 ms |
| Earth isometric pan | 2,688.5 ms | 2,383.4 ms | 2,651.8 ms |
| Earth isometric orbit | 2,399.4 ms | 2,151.9 ms | 2,139.4 ms |
| Earth perspective 65° pan | 11,181.7 ms | 4,642.9 ms | 4,317.4 ms |
| Earth perspective 65° orbit | 7,799.4 ms | 4,501.5 ms | 4,903.1 ms |

The measured CPU Map and Earth perspective pans use closely comparable pixel-size distributions across releases. Their task CPU reductions are 49.0% and 58.5%. The optimized Map isometric orbit instead keeps pixel size 2 for all 80 uploads, while the baseline uses 8, 34 and 41 uploads at pixel sizes 2, 3 and 4. Its increased CPU total accompanies more detailed moving frames. The opt-in GPU orbit also keeps pixel size 2 throughout.

GPU-mode Earth isometric pan uses 40/16/24 uploads at pixel sizes 2/3/4, compared with 2/1/77 for optimized CPU. Its elapsed gesture time rises from 5,669.9 ms to 7,440.0 ms in this software-driver environment. Earth perspective orbit also has higher main-thread CPU time in GPU mode. The optional backend therefore exposes a workload-dependent tradeoff between CPU work, composition cost and adaptive detail. Physical-GPU performance remains unmeasured.

The measured CPU AOT renderer matches all 22 baseline map crops exactly. The first full-canvas comparison matches 20 of 22 canvases. Two small UI ellipses differ by 50 and 48 pixels. Repeating both cases on both builds produces exact original-baseline canvases, identifying run-to-run UI raster variation. The GPU matrix passes backend-status and art-sized framebuffer checks in every case, with zero runtime exceptions. Seven GPU map crops match the CPU baseline exactly. The maximum GPU difference is 1,383 of 297,000 crop pixels, or 0.466%, in overhead Earth. GPU rasterization and blending can change edge pixels.

The browser helper selects the visible GPU menu command by focus and real Enter input. This fixes an intermittent initial-menu-focus error in the first CI attempt. Twelve fresh train launches, a handset case and an Earth-layer case passed the corrected helper. The workflow runs the map and journey suites in both backends before deploying the Pages artifact.

```sh
node tests/browser/performance.cjs http://localhost:8000/ artifacts/browser-visual --visual
node tests/browser/performance.cjs --compare baseline candidate
node tests/browser/performance.cjs http://localhost:8000/ artifacts/browser-motion --motion
node tests/browser/performance.cjs http://localhost:8000/ artifacts/browser-gpu --motion --gpu
PIXELMAP_GPU=1 node tests/browser/smoke.cjs http://localhost:8000/
PIXELMAP_GPU=1 node tests/browser/journeys.cjs http://localhost:8000/
```

Visual reports include screenshots, raw map-pixel hashes and accessibility snapshots. Motion reports retain individual samples and runtime-asset hashes. GPU runs require the public backend status to confirm activation and record framebuffer draws at the map's art resolution.
