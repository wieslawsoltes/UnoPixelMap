# Sparse GPU scene visibility

`GpuSceneDepth` supplies terrain picking and the occlusion queries used by labels,
navigation and game overlays. OpenGL renders the map itself. This index therefore
needs exact answers at the queried art pixels; it does not need to prepare a
software rasterizer for every visible primitive on every moving frame.

The implementation is in [GpuSceneDepth.cs](../src/PixelMap.Rendering/GpuSceneDepth.cs).
The immutable source bounds are part of
[GpuTerrainMesh](../src/PixelMap.Rendering/GpuMapFrame.cs). Label preparation is
handled separately by [PixelMapRenderer](../src/PixelMap.Rendering/PixelMapRenderer.cs).

## Preparing only requested geometry

Frame construction indexes conservative terrain-patch and building bounds. It
also computes the same altitude range used to normalize GL depth, including
submitted terrain and models whose screen bounds or physical-camera selection
reject them. The altitude range must not depend on which overlays subsequently
ask depth questions.

Terrain meshes cache their immutable tile-coordinate bounds and source height
extrema. Each frame applies the original altitude conversion to those extrema.
Relief uses a positive clamped scale, and the flat-ground conversion is constant,
so this avoids rescanning every expanded terrain vertex while retaining the
original minimum/maximum and non-finite fallbacks.

The coarse screen index normally uses 16 × 16 art-pixel cells. Unusually large
artwork doubles the cell size until the grid has at most 16,384 cells:

- An ordinary proxy occupies at most 64 cells.
- A larger proxy occupies one overflow-list entry. Near-plane crossings and
  screen-filling models cannot create a model-by-viewport-cell storage explosion.
- Bounds that cross the near plane, or cannot be projected finitely, use the
  conservative full viewport. The existing exact primitive clipper decides
  their actual coverage later.
- Positive-weight projected bounds expand by two art pixels plus a small
  relative floating-point margin. This includes projection roundoff and the
  roof-rim DDA footprint. Unclipped box coordinates are clamped in floating point
  before conversion to integer cell coordinates.

On an uncached pixel query, the coarse cell and overflow list locate possible
groups. The expanded **pixel bounds** then reject groups that cannot cover that
pixel. The remaining groups project and clip their vertices once, retaining the
exact resulting triangles and rim edges in that frame's workspace.

After eight uncached pixel queries in one cell, that cell prepares all its
contributing groups. This bounds repeated coarse-index work for dense picking or
navigation coverage. Cached pixel answers remain valid when later queries
prepare another group: every group capable of covering the earlier pixel was
already considered by its conservative bounds.

The detailed geometry operations are unchanged: homogeneous near/far/viewport
clipping, face rejection, half-open triangle coverage, lazy interpolation-plane
evaluation and roof-rim DDA sampling. Sparse preparation changes when those
operations run.

## Why primitive sorting is unnecessary

The eager implementation submitted all terrain before all buildings. Within
buildings, faces and roof rims had an ordered depth test. An initial sparse
implementation restored that entire sequence by sorting each queried cell's
primitive list. A sampled profile showed that the indirect comparer dominated
the regression on dense Manhattan scenes, despite a much cheaper constructor.
That version was rejected.

The index returns the nearest depth, the winning terrain triangle, and whether
the nearest surface is terrain. It does not return a building identity or a
building material. Those outputs permit a smaller exact reduction.

For one art-pixel sample, let:

| Value | Meaning |
| --- | --- |
| `T` | Maximum finite covered terrain depth |
| `F` | Maximum finite covered building-face depth |
| `R` | Maximum finite covered roof-rim depth |
| `ε` | `1e-6f` for perspective depth, otherwise zero |

Absent categories have negative-infinity maxima. The stored nearest depth is:

```text
N = max(T, F, R)
```

Terrain is the selected surface exactly when a terrain sample exists and:

```text
F < T  and  R < T - ε
```

The reason is the original pass ordering. After terrain has finished, a model
can change the surface classification from terrain to model. No later primitive
can change it back to terrain. A face at or above `T` is therefore sufficient.
A rim within the original tolerance below `T` is also sufficient. If an earlier
model made the rim fail against a nearer stored depth, that earlier model has
already changed the classification to model. This preserves the rim tolerance
without retaining its traversal order.

Every new global maximum passes the original depth test, so `N` is independent
of traversal order. The implementation continues to use `Math.Max` and the
original finite-depth gate, including signed-zero behavior.

Terrain ties require more information. Two coplanar terrain layers can have the
same rendered depth and different physical heights; picking must select the
last terrain triangle in the original source order. Each triangle retains its
source group order once. A terrain query combines that value with the prepared
triangle index:

```text
high 32 bits: immutable source group order
low 32 bits:  prepared triangle index
```

A terrain group's triangles retain their original order when that group is
prepared. This comparison selects the original last terrain primitive for
equal depths, even when query order prepared a later group first. Building
faces and rims need only their maxima. No per-cell primitive sort or duplicate
rim proxy is necessary.

Cell memberships use a signed 32-bit handle: a nonnegative triangle index or
the bitwise complement of a roof-edge index. Source order is no longer repeated
in every cell a primitive overlaps. This halves exact-cell reference storage
relative to the first sparse implementation. A prepared triangle grows from
80 to 84 bytes to hold that one source-group integer; the saving comes from
removing repeated metadata from potentially many cell memberships, not from
halving every kind of scene storage.

## Active memory pressure and exact fallback

The combined capacities of the exact-cell lists, coarse-cell lists and
large-proxy list are limited to **16 MiB per active depth workspace**. Growth
checks the remaining byte budget before changing `List<int>.Capacity`; an
implicit `List.Add` doubling cannot bypass the limit. Recycled buffers count
at their allocated capacities, including unused cells from earlier views.

An index is an acceleration structure, so exhausting its capacity changes the
query path without discarding geometry. If coarse construction reaches the
budget, preparation scans the conservative group bounds. If exact-cell
construction reaches it, subsequent queries traverse the contiguous triangle
and roof-edge ranges of matching groups. The fallback uses the same coverage,
interpolation, source-order and rim-tolerance operations as indexed queries.
It creates no second unbounded overflow list. A partially indexed primitive is
safe because the query switches to complete group ranges; lists remain intact
while preparation may still be enumerating them.

Pixel bounds continue to reject irrelevant groups in the fallback. Groups are
prepared at most once, and cached earlier answers remain valid because the
conservative bounds already accounted for every group that could cover those
pixels. The fallback trades query time for bounded index-buffer storage.

This limit covers membership buffers, not total application memory. Prepared
geometry scales with source geometry, and frame resources, dictionary storage,
list objects, renderer caches and GPU attachments have separate lifetimes and
budgets. Buffer replacement also temporarily allocates a new array before the
old one becomes collectible. Retained frames can keep multiple active
workspaces alive.

## Frame lifetime and bounded reuse

Each depth index refers to its frame's immutable terrain and model snapshots.
Preparing another camera or changing terrain, style or source tiles does not
replace a pending frame's geometry or cached query results. Reference counting
keeps a workspace out of the pool until both the retained frame and active
renderer release it.

The pool retains at most two ordinary workspaces. The initial triangle capacity
is 1,024 entries, down from the eager implementation's 32,768; a frame with no
depth requests does not reserve a large triangle array up front. Pooled arrays
grow as work is requested.

Pool admission is bounded by capacity, rather than just the current count:

| Storage | Maximum retained capacity per workspace |
| --- | ---: |
| Triangles | 262,144 |
| Roof edges | 65,536 |
| Pixel-sample dictionary | 131,072 |
| Temporary projected vertices | 131,072 |
| Geometry groups | 65,536 |
| Screen-cell array | 16,384 |
| All exact/coarse cell-index buffers and overflow entries combined | 8 MiB |

The pool admission limit counts all 32-bit exact and coarse entries at their
actual allocated capacities. It is deliberately smaller than the active
16 MiB limit. This also bounds accumulated high-water capacities when different
camera positions populate different cells. Oversized workspaces are released
normally instead of entering the shared pool.

The per-frame pixel-answer cache retains at most 65,536 entries. Querying a
larger area can clear and refill that cache; the prepared geometry and completed
cell state remain available.

## Exact compatibility checks

[GpuDepthIndexChecks](../tests/PixelMap.Rendering.Tests/GpuDepthIndexChecks.cs)
compares production queries with an independent copy of the eager implementation
from release `ab765d24c3063c1745867772426720fd47152805`:
[EagerGpuSceneDepth](../tests/PixelMap.Rendering.Tests/Reference/EagerGpuSceneDepth.cs).

The integrated test passes **6,612,305 exact queries across 55 frames**. Coverage
includes all four render styles, orthographic and perspective maps, physical
first-person/chase/free-flight cameras, upward and below-ground views,
near-plane crossings, courtyard roofs, raised and coplanar parts, signed
terrain, repeated worlds and retained A → B → A frames.

Pixel queries are shuffled and revisited, exposing dependencies on preparation
order and stale cached cells. An explicit coplanar terrain fixture prepares
small later layers before the first layer's full-screen overflow proxy. The
layers have different physical heights, so a source-order mistake produces an
observable wrong terrain hit. Separate meshes contain NaN and positive/negative
infinity in coordinates or heights to check the original finite-depth behavior.

The existing interpolation and clipping checks remain in
[GpuDepthChecks](../tests/PixelMap.Rendering.Tests/GpuDepthChecks.cs). Label layout
has its own eager-reference tests in
[LabelPreparationChecks](../tests/PixelMap.Rendering.Tests/LabelPreparationChecks.cs),
including ambiguous unstable-sort ties and real Manhattan labels.

[GpuDepthMemoryChecks](../tests/PixelMap.Rendering.Tests/GpuDepthMemoryChecks.cs)
adds **8,202 exact pressure comparisons** against that same eager reference.
A folded terrain patch creates millions of real clipped-triangle memberships;
a separate fixture uses 65,537 overlapping groups to exhaust the coarse index.
The unchanged `e086f47` renderer fails the same active-capacity check at
**70,508,400 bytes**. The bounded renderer reaches exactly **16,777,216 bytes**
and continues returning the reference answers.

These are synthetic pressure fixtures, not a claim to reproduce the complete
live browser heap. They additionally cover source-order terrain ties, model
rims first demanded after fallback, previously cached answers and cache-cleared
re-evaluation, retained A → B → A frames, actual-capacity accounting and reuse
across 512 × 384 → 4096 × 2160 → 384 × 640 frames. The large frame exercises
adaptive cell size; boundary pixels exercise the changed grid mapping.

Run the focused pressure gate with `--depth-memory-pressure`. The separate
`--depth-memory` diagnostic traverses a flat-ground reconstruction of the close
camera path using nine bundled Manhattan tiles at four viewport/style settings.
It includes poses on both sides of the horizon and records queries, allocations
and index capacities. The live failure used terrain relief and a potentially
larger loaded tile set, so these two kinds of evidence retain distinct scopes.

### Close-camera diagnostic memory results

The diagnostic compares `e086f47` with the bounded-index source tree published
as `6bc776122bdb3a766c9190bfaddf506bde6703b0`. All 32 per-frame query hashes match.
At the observed logical map size, 1800 × 807 with diagnostic pixel size two,
peak index-buffer capacity decreases from **4.608 to 2.399 MiB**. Across the
complete sequence of four viewport/style settings, the peak decreases from
**9.001 to 5.428 MiB**. No candidate frame enters the fallback.

The larger 3600 × 1848 case intentionally stresses a larger *logical* viewport;
it is not the effect of changing DPR on the 1800 × 924 browser viewport.
Individual later frames can retain more capacity than the previous renderer:
the smaller buffers now qualify for pooling, while a previous oversized
workspace would have been discarded. The figures count all live capacities in
the workspace, including unused pooled cells, and are not total heap or GPU
memory measurements.

The evidence retains the [before sequence](verification/depth-memory-horizon-before-20261107.json),
[after sequence](verification/depth-memory-horizon-after-20261107.json) and
[binary identity, checks and limitations](verification/depth-memory-provenance-20261107.json).

## Measurement scope and reproduction

[DepthPreparationBenchmark](../tests/PixelMap.Rendering.Tests/DepthPreparationBenchmark.cs)
uses only public APIs and reflection in its timing entry point. The identical
compiled harness can load the unchanged `ab765d2` renderer or the candidate.
The embedded Manhattan workload contains 33,747 source features across nine
z14 tiles. Synthetic hills exercise Pixel, Minecraft and Roblox terrain.

The complete CPU measurement includes `PrepareGpuFrame` and its labels, followed
by `DrawGpuOverlay`, a Drive route, vehicle, gate and terrain pick. The same
navigation overlay stress runs for every camera preset; the walk-height camera
row is not a simulation of the application's exact free-roam Walk workload.
Browser measurements cover the actual game path separately.

The benchmark also measures independent depth construction and first/repeated
query batches. Ordinary batches contain 144 terrain/occlusion query pairs.
Zero-query and every-art-pixel stress rows show the limits of deferred work.
First-query percentiles describe an entire batch, including lazy expansion,
rather than the latency of a single query. Constructor-only improvements do not
establish a frame-time or FPS improvement.

Native font validity is part of the fixture. The environment's initial default
OpenAI Sans typeface reported zero glyphs and zero measured text width. Those
preliminary runs could not measure meaningful visible-label layout. Final
comparisons use the same explicitly selected valid font in both binaries;
reports record its family and file hash. Tests also require nonblank label
images and compare them exactly with the eager layout.

For example, on Linux with .NET 10 and the repository's native dependencies:

```bash
dotnet build tests/PixelMap.Rendering.Tests -c Release
dotnet tests/PixelMap.Rendering.Tests/bin/Release/net10.0/PixelMap.Rendering.Tests.dll --depth-index

PIXELMAP_TEST_FONT=/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf \
DOTNET_TieredCompilation=0 DOTNET_PROCESSOR_COUNT=2 \
taskset -c 0 dotnet tests/PixelMap.Rendering.Tests/bin/Release/net10.0/PixelMap.Rendering.Tests.dll \
  --depth-benchmark --depth-output=/tmp/pixelmap-depth-candidate
```

The default protocol has 16 warmup and 64 measured frames per ordinary workload.
Zero/full-query stress uses at most four warmup and eight measured frames. Run
baseline → candidate → candidate → baseline without competing builds or native
workloads. Wall time, Linux thread CPU time and managed allocation are recorded
per phase; hashing, fixture loading and reflection-delegate construction are
outside timing. Terrain-query results and overlay images must match across the
same baseline/candidate camera sequence.

These are CPU preparation and overlay measurements. They exclude GL execution,
presentation, tile networking and browser scheduling. The current pass's
end-to-end evidence and deployment results are documented in
[performance.md](performance.md).

## Current native results: compact building meshes

The complete rendering source at `c937ddd192619f48c1a2ec682845b52bc1eebff8`
is measured against the original `ab765d2` renderer using the same frozen native
harness as the earlier comparison. The source tree is
`57356c5b6d841b51c0d913c5ce491996a65eb4ed`; the tested renderer SHA-256 is
`0aa5e2b9dec7f8f12a7cab3a92b905e5e403941b9cacf57303484a34c027fba9`.
The four fresh processes run baseline → candidate → candidate → baseline on CPU 0,
with .NET 10.0.14, SDK 10.0.403, two managed processors, disabled tiered compilation
and the same explicit DejaVu font. No other native workload runs during timing.

The table reports the arithmetic mean of two per-run **nearest-rank medians** for
complete `PrepareGpuFrame` plus the navigation-overlay stress pass. Per-run p95s
are averaged separately. The eight principal workloads use 16 warmup and 64
measured frames; the two independent-query controls use four warmup and eight
measured frames. The logical viewport is 1100 × 650, with requested pixel size
three; Normal and Roblox use their normal pixel-size-one behavior.

| Workload | Baseline median | Current median | Change | Baseline → current mean p95 |
| --- | ---: | ---: | ---: | ---: |
| Manhattan walk | 9.633 ms | 9.097 ms | -5.6% | 11.460 → 10.538 ms |
| Manhattan chase | 13.778 ms | 12.462 ms | -9.5% | 18.969 → 14.901 ms |
| Manhattan free flight | 10.139 ms | 9.705 ms | -4.3% | 12.914 → 12.954 ms |
| Manhattan orbit | 17.205 ms | 16.539 ms | -3.9% | 22.433 → 20.227 ms |
| Manhattan Normal free flight | 10.822 ms | 10.410 ms | -3.8% | 16.428 → 12.443 ms |
| Hills Pixel | 2.397 ms | 1.004 ms | -58.1% | 3.811 → 1.202 ms |
| Hills Minecraft | 3.678 ms | 1.258 ms | -65.8% | 5.998 → 1.635 ms |
| Hills Roblox | 6.556 ms | 1.552 ms | -76.3% | 8.416 → 2.352 ms |
| Manhattan zero-query control | 10.463 ms | 9.909 ms | -5.3% | 29.761 → 29.175 ms |
| Manhattan full-query control | 12.316 ms | 10.034 ms | -18.5% | 29.303 → 34.639 ms |

All 2,112 observations retain identical corresponding query hashes, overlay
hashes, geometry counts and altitude bounds. All 1,056 candidate frames and
independent indices remain indexed; the largest measured membership capacity
is 2,205,744 bytes. Chase allocation averages 2,081,536 → 724,915 bytes per frame.
The [independent audit](verification/native-rendering-c937ddd-independent-audit.json)
recomputes the raw/exported values and passes 97,153 assertions.

These are CPU preparation measurements for the complete patch, including the
[compact mesh representation](building-mesh-memory.md), rather than isolated
mesh-construction timings or displayed frame rates. The corpus accounting that
motivated the mesh change is a different, larger workload. Two repetitions do
not establish confidence intervals. The slightly higher free-flight p95 and
the full-query control's worse tail remain in the report; the latter has only
eight samples per run. Earlier comparisons below remain revision-specific,
including their slower cases.

[All current samples](verification/native-rendering-c937ddd-samples.csv) ·
[Per-run statistics](verification/native-rendering-c937ddd-runs.csv) ·
[Aggregate comparison](verification/native-rendering-c937ddd-comparison.csv) ·
[Source, binary, font, host and protocol](verification/native-rendering-c937ddd-provenance.json)

## Earlier native results: bounded index

The complete comparison was rerun for
`6bc776122bdb3a766c9190bfaddf506bde6703b0`, using the original
`ab765d24c3063c1745867772426720fd47152805` baseline. It follows the protocol
above: four fresh processes in baseline → candidate → candidate → baseline
order; identical compiled harness and valid DejaVu Sans font; .NET 10.0.14 on
Debian 13 and AMD EPYC 9V74; CPU affinity 0; disabled tiered compilation;
1100 × 650 logical viewport with requested pixel size three. Normal and Roblox
retain pixel-size-one behavior.

The later `668bf4b` follow-up changes only journey draft submission and its
browser regression; the renderer and native harness sources are unchanged.
These native measurements retain their `6bc7761` binary provenance.

Each process contributes 528 measured frames across ten workloads, for **2,112
samples**. Ordinary workloads use 16 warmup and 64 measured frames; independent
zero/full-query stress uses four warmup and eight measured frames. All
corresponding query hashes, visible-overlay hashes, terrain counts, retained
building-mesh triangle counts and altitude bounds match across all four runs.

These are complete CPU `PrepareGpuFrame` plus navigation-overlay stress
measurements. The navigation pass includes a Drive route, vehicle, gate and
terrain picking for every camera preset; the walk-height row is not the exact
application free-roam Walk path. GL execution, presentation and browser
scheduling are excluded. The comparison includes the whole rendering pass,
including deferred labels, texture-cache changes and stream reuse, so it does
not isolate the memory-bound patch.

Percentages compare the arithmetic means of two independent process medians.
The p95 column averages the two per-run p95s. Percentiles use nearest rank, and
combined phases are added per sample before calculating their statistics.
These are not pooled percentiles or confidence intervals.

| Workload | Baseline run medians (ms) | Candidate run medians (ms) | Change | Mean p95, baseline → candidate (ms) |
| --- | ---: | ---: | ---: | ---: |
| `manhattan-walk` | 8.966 / 8.831 | 9.066 / 9.249 | +2.9% | 11.855 → 11.068 |
| `manhattan-chase` | 12.207 / 12.059 | 11.796 / 12.498 | +0.1% | 15.087 → 14.074 |
| `manhattan-freeflight` | 9.357 / 10.264 | 9.412 / 9.219 | -5.0% | 12.478 → 10.074 |
| `manhattan-orbit` | 15.899 / 16.212 | 16.738 / 17.367 | +6.2% | 17.238 → 20.219 |
| `manhattan-freeflight-normal` | 9.639 / 9.772 | 10.279 / 10.177 | +5.4% | 12.394 → 12.738 |
| `hills-pixel` | 2.209 / 2.249 | 1.011 / 1.010 | -54.7% | 2.427 → 1.351 |
| `hills-minecraft` | 3.344 / 3.384 | 1.274 / 1.196 | -63.3% | 4.373 → 1.736 |
| `hills-roblox` | 5.676 / 5.674 | 1.610 / 1.524 | -72.4% | 7.440 → 2.122 |
| `manhattan-zero-query` | 9.423 / 10.174 | 9.642 / 9.578 | -1.9% | 27.395 → 27.911 |
| `manhattan-full-query` | 9.993 / 9.662 | 9.809 / 9.993 | +0.7% | 27.705 → 28.539 |

Hills improve by **54.7–72.4%** in this run. Dense-city results remain mixed:
free-flight improves by **5.0%**, chase is effectively flat, and walk, orbit and
Normal free-flight increase by **2.9%, 6.2% and 5.4%**. Walk and chase p95s
improve, while orbit and Normal p95s increase. The separate earlier `fa2cfa7`
run below is retained rather than substituted for these final results.

Chase allocation decreases from **2,081,559 to 725,004 bytes per frame**, a
**65.2%** reduction. Lower allocation is independent of elapsed-time improvement;
the complete chase median remains flat. The raw CSV retains allocation, thread
CPU, every sample, per-run ranges and the slower tails.

No measured candidate workload enters either fallback: all **1,056 candidate frames**
and their independently constructed indexes retain both coarse and exact
indices. The largest measured combined index-buffer capacity is **2,205,744
bytes (2.104 MiB)**, below the 16 MiB active limit. These capacity/flag readings
are collected outside timing. The separate pressure fixtures above deliberately
exhaust both limits.

### Independent construction plus first-query batch

The following phase constructs another index and queries it independently.
It is not added to the application's preparation/overlay phase. Ordinary rows
contain 144 terrain/occlusion query pairs; the zero-query row performs none,
and the full-query row visits every art pixel. A cheap constructor alone would
hide the work deferred into that first batch.

| Workload | Baseline (ms) | Candidate (ms) | Change | Mean p95, baseline → candidate (ms) |
| --- | ---: | ---: | ---: | ---: |
| `manhattan-walk` | 4.726 | 4.640 | -1.8% | 6.683 → 5.309 |
| `manhattan-chase` | 6.156 | 6.194 | +0.6% | 6.382 → 7.009 |
| `manhattan-freeflight` | 7.012 | 6.588 | -6.0% | 8.716 → 7.330 |
| `manhattan-orbit` | 13.294 | 13.490 | +1.5% | 13.817 → 16.168 |
| `manhattan-freeflight-normal` | 7.762 | 7.544 | -2.8% | 9.369 → 8.668 |
| `hills-pixel` | 1.538 | 1.585 | +3.0% | 1.666 → 1.922 |
| `hills-minecraft` | 2.481 | 2.280 | -8.1% | 3.201 → 2.615 |
| `hills-roblox` | 4.887 | 4.606 | -5.7% | 5.632 → 5.318 |
| `manhattan-zero-query` | 6.168 | 0.198 | -96.8% | 6.702 → 0.237 |
| `manhattan-full-query` | 192.088 | 189.625 | -1.3% | 218.655 → 205.878 |

The zero-query limit avoids almost all eager construction work. Full-area
queries still require the geometry and coverage computation; they should be
read as a separate limit case, not an application frame-rate measurement.
The zero/full-query names describe this independent index diagnostic; their
complete preparation/navigation columns above still perform ordinary label and
overlay queries. Their shorter warmup and eight measured frames also make
those columns unsuitable as extra steady-state free-flight estimates.

[All samples](verification/native-depth-bounded-samples-20261107.csv) ·
[Per-run statistics](verification/native-depth-bounded-runs-20261107.csv) ·
[Aggregate comparison and capacities](verification/native-depth-bounded-comparison-20261107.csv) ·
[Source, binary, font, host and audit provenance](verification/native-depth-bounded-provenance-20261107.json)

The candidate renderer SHA-256 is
`db3c43d69e0de4ad984a49439225e830b880110e6196ff873201388cdb779bef`;
the identical harness SHA-256 in all four processes is
`febc5fa183d5bdff4c31945aecc6dbf6fe797cfca32a352f22ccf5f0e9cb8ae9`.
The full native suite passes, and all 48 standard GLES cases retain the preceding
release's entire report, including 192 exact image hashes.

## Earlier native results: complete preparation and navigation overlay stress

The first sparse-index comparison uses release `ab765d24c3063c1745867772426720fd47152805`
as the baseline and the source tree published as
`fa2cfa783dff76d49e3b369064a64169a458ce1d` as the candidate. Both load the same
compiled benchmark and the same DejaVu Sans font with 6,253 glyphs. The runs use
.NET 10.0.14 on Debian 13 on an AMD EPYC 9V74 host, pinned to CPU 0, with tiered
compilation disabled and a reported processor count of two. The viewport is
1,100 × 650, with requested pixel size three; Normal and Roblox retain their
native pixel-size-one behavior.

This table retains the exact `fa2cfa7` binary provenance. It precedes the active
index-memory bound and 32-bit handles, as well as the subsequent CPU
façade-filter fast paths and already-aligned camera-aim fix. It is not a timing
run of those later changes.

The evidence includes
[all per-frame samples](verification/native-depth-samples-20261107.csv),
[separate run summaries](verification/native-depth-runs-20261107.csv),
[the aggregate comparison](verification/native-depth-comparison-20261107.csv)
and [source, binary, font and host provenance](verification/native-depth-provenance-20261107.json).

Run order is baseline 1 → candidate 1 → candidate 2 → baseline 2. Each ordinary
workload has 16 warmup frames and 64 measured frames. The two limit workloads
have four warmup and eight measured frames each. All four runs contain 528
measured frames. Their terrain-query hashes, overlay-image hashes, altitude
ranges, terrain-patch counts and retained building-mesh triangle counts match
exactly for every corresponding frame.

The candidate includes sparse depth preparation, deferred label occlusion,
independent building-stream reuse and the terrain texture-cache fix. The
following table measures their combined effect on **preparation + navigation
overlay stress**. It does not isolate the contribution of one change.

Each pair lists the two independent run medians. The percentage change compares
the arithmetic means of those medians. The p95 columns likewise average the two
per-run p95 values; they are not pooled percentiles or confidence intervals.
Combined phases are added per frame before computing their statistics.

| Workload | Baseline run medians (ms) | Candidate run medians (ms) | Change | Mean p95, baseline → candidate (ms) |
| --- | ---: | ---: | ---: | ---: |
| `manhattan-walk` | 9.958 / 8.862 | 9.044 / 9.381 | -2.1% | 10.915 → 12.915 |
| `manhattan-chase` | 12.741 / 12.610 | 12.190 / 13.038 | -0.5% | 15.207 → 15.657 |
| `manhattan-freeflight` | 9.642 / 9.817 | 8.966 / 9.428 | -5.5% | 10.772 → 10.083 |
| `manhattan-orbit` | 16.642 / 17.904 | 16.625 / 16.027 | -5.5% | 19.837 → 20.078 |
| `manhattan-freeflight-normal` | 9.911 / 10.157 | 10.383 / 10.365 | +3.4% | 13.469 → 11.343 |
| `hills-pixel` | 2.334 / 2.354 | 1.028 / 1.022 | -56.3% | 2.594 → 1.246 |
| `hills-minecraft` | 3.406 / 3.486 | 1.256 / 1.218 | -64.1% | 4.152 → 1.472 |
| `hills-roblox` | 6.096 / 6.229 | 1.716 / 1.541 | -73.6% | 7.615 → 2.247 |
| `manhattan-zero-query` | 9.444 / 12.053 | 10.186 / 9.801 | -7.0% | 29.095 → 28.415 |
| `manhattan-full-query` | 10.151 / 10.200 | 10.026 / 11.555 | +6.1% | 28.621 → 30.027 |

Dense Manhattan remains close to the baseline. Walk and chase changes are
smaller than their variation between runs, and their p95 values increase.
Free-flight and orbit improve by about 5.5% in the mean of medians, while Normal
free-flight increases by 3.4%. These measurements do not support a general claim
of a large city-frame speedup. The limit rows have fewer warmup and measured
frames and visibly larger tails; their complete-frame columns should not be
treated as additional steady-state free-flight estimates.

The hills workloads show a consistent reduction of 56–74%. Their actual overlay
requests prepare about 6% of the eager triangle count, whereas the Manhattan
workloads still require most of that geometry. The difference explains why
deferring work has a much larger effect on hills than on dense city overlays.
The separate artificial 144-probe batches below deliberately touch much more of
the hills scene, so their improvement is smaller.

Managed allocation also changes independently of elapsed time. Chase preparation
plus navigation falls from 2,081,542 to 724,952 allocated bytes per measured
frame, a 65.2% reduction. That result includes the terrain-cache and independent
selection-stream fixes; it is not attributed to the sparse index alone.

### Independent depth construction and first-query batch

This measurement constructs an additional depth index from the same frame, then
runs the first query batch against it. It is separate from the complete phase
above and must not be added to that phase as if the application did both.
Ordinary rows use 144 terrain/occlusion pairs. The zero-query row asks no depth
questions; the full-query row visits all 79,639 art pixels. The table includes
construction and the complete first batch, so it exposes deferred work rather
than reporting only the inexpensive constructor.

The median and p95 entries use the same arithmetic-mean-of-run-statistics
aggregation as the complete-phase table.

| Workload | Baseline (ms) | Candidate (ms) | Change | Mean p95, baseline → candidate (ms) |
| --- | ---: | ---: | ---: | ---: |
| `manhattan-walk` | 5.011 | 4.990 | -0.4% | 5.787 → 6.868 |
| `manhattan-chase` | 6.369 | 6.563 | +3.0% | 8.039 → 7.764 |
| `manhattan-freeflight` | 7.023 | 6.819 | -2.9% | 7.727 → 7.479 |
| `manhattan-orbit` | 14.152 | 13.875 | -2.0% | 16.644 → 15.992 |
| `manhattan-freeflight-normal` | 7.947 | 8.038 | +1.1% | 8.888 → 8.649 |
| `hills-pixel` | 1.632 | 1.447 | -11.4% | 1.908 → 1.672 |
| `hills-minecraft` | 2.458 | 2.193 | -10.8% | 2.773 → 2.449 |
| `hills-roblox` | 5.238 | 4.490 | -14.3% | 6.035 → 4.958 |
| `manhattan-zero-query` | 6.614 | 0.208 | -96.8% | 8.211 → 0.237 |
| `manhattan-full-query` | 202.586 | 198.914 | -1.8% | 219.433 → 218.655 |

The zero-query limit demonstrates avoided work. The full-query limit requires
the same final triangle coverage and has similar total cost. In ordinary dense
city rows, the first batch absorbs most of the old construction cost; those
small mixed results are consistent with the complete workload above. In
particular, the walk batch's p95 increases and the chase and Normal medians
increase modestly. These are batch latencies, not individual-pixel latencies.
