# Exact terrain texture copies · 10 November 2026

This experiment changes only `TerrainCache.cs` and `PixelMapRenderer.Terrain.cs`
relative to `ce82b2c6fccee2d77a9e9489e1c6415e04412e8c`. It isolates the terrain
optimization from the independent sparse-visibility changes in the same release.

## Implementation and exactness

A newly selected terrain patch was painted by drawing retained 256×256 source
blocks through an `SKCanvas` into another 256×256 bitmap. When one opaque vector
source covers the complete patch, both grids are aligned to powers of two. The
new path copies the corresponding block or repeats its nearest-neighbor pixels
directly into the existing destination bitmap. Repeated destination rows use span
copies; replicated horizontal runs use span fills.

For source-to-patch zoom difference `d`, the retained source density remains
`lod = min(d, 4)`, exactly matching the existing one-pixel-per-MVT-unit cap. Put
`shift = d - lod`. The source-tile-local patch coordinate is
`localX = patch.X - source.X * (1 << d)`; its cached block is `localX >> shift`,
and its block-local subpatch is `subpatchX = localX & ((1 << shift) - 1)`. A
destination coordinate `x` samples source coordinate
`(256 * subpatchX + x) >> shift`; the same relation applies vertically. Since
all quantities before division are integers, the pixel-center `+0.5` does not
change this nearest-neighbor result. Tile and block offsets stay world aligned.

The shortcut requires a complete opaque vector source. Partial child coverage,
distinct duplicate sources, photographs and transparent overlays keep ordered
Skia compositing. A failed raster-pixel peek also falls back to that path. The
copy honors both native row strides, does not mutate the source image, and adds
no retained image or texel buffer. The existing 32 MiB block-cache and 48 MiB
surface-texture budgets and held-frame ownership remain unchanged.

The dedicated regression compares **768 complete textures** against the original
Skia drawing path. Fixtures use a synthetic world tile and bundled Manhattan
data; patch zooms reach 0–22. Coverage includes every render style, day/night,
shadows, pixel sizes 1/3/8, block boundaries and extreme magnification, BGRA bytes,
padded destination strides, opaque-only eligibility, source priority, partial
children, duplicate IDs and the photographic coverage mask. A cold first-person
fixture has **42 of 64** patches eligible for the shortcut.

[Check output](texture-checks.log) is preserved verbatim. Its original console
label says “source zoom”; the exercised 0–22 range refers to **patch zoom**. The
current test source corrects that label; the frozen reproduction patch retains
the exact measured harness source.

A post-measurement audit added **42 asymmetric cached-BGRA comparisons** at
interior source-pixel boundaries for patch zooms 16–22, including zoom 22 at
local coordinate 131071. The injected immutable block gives every source pixel
a distinct color, so flat terrain cannot conceal a one-pixel rounding error.
The final targeted run passes the original 768 textures plus these 42 cases.
[Its log](texture-boundary-checks.log) and
[supplemental test patch](boundary-regression.patch.gz) are retained separately.
This adds correctness coverage only: production source and measured timings are
unchanged. Apply the supplemental patch after `candidate.patch.gz` to reproduce
the expanded checks; leave it unapplied to rebuild the exact measured harness.

## Native texture-bake measurements

These numbers measure one batch of 16 `BakeTerrainTexture` calls. Source blocks
are warm, and a reusable output bitmap is allocated before timing. Each call
still performs the real source selection and texture-bake implementation. Fixture
construction, output allocation, image hashing and static setup are excluded.
The managed allocations shown are per 16-patch batch.

| Workload | Baseline median | Candidate median | Change | Baseline p95 | Candidate p95 | Mean managed allocation change |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| identity | 0.1941 ms | 0.0766 ms | -60.54% | 0.3086 ms | 0.1246 ms | -1664.00 bytes |
| magnify-2 | 0.4969 ms | 0.4109 ms | -17.30% | 0.6067 ms | 0.5404 ms | -1664.00 bytes |
| magnify-16 | 0.4718 ms | 0.0660 ms | -86.01% | 0.6849 ms | 0.0808 ms | -1664.00 bytes |
| magnify-256 | 0.4722 ms | 0.0606 ms | -87.17% | 0.5829 ms | 0.0789 ms | -1664.00 bytes |

All 16,384 recorded per-texture control hashes agree across the four processes.
Warm isolated batches allocate 3,456 → 1,792 managed bytes, a reduction of 104
bytes per eligible texture. This does not measure the outer texture bitmap
allocation, GPU texel conversion, retained-cache hits or GL upload/presentation.

## Fixed-camera native frame preparation

The second benchmark calls public `PrepareGpuFrame` over nine bundled Manhattan
tiles. Camera positions are determined by sample index, not by elapsed time.
Walking advances 1.5 m per frame; driving advances 6 m per frame. Eye height is
1.7 m for first-person views and 7 m for chase, with pitch 90°/82° respectively.
The renderer uses Pixel style, 1100×650 logical pixels, pixel size 3 and a 6 km
far plane. Every trace stays within the route; requests long enough to clamp at
the endpoint are rejected.

Timing includes terrain texture misses, meshes, building preparation, sparse
depth and label preparation. Fixture setup, cache diagnostics, CPU overlay
playback, image hashing, GL submission and frame disposal are excluded. Every
measured frame retains its exact camera, cache misses, terrain texture hash and
CPU overlay hash.

| Workload | Baseline median | Candidate median | Change | Baseline p95 | Candidate p95 | Mean managed allocation change |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| walk-first-person | 4.7428 ms | 4.4901 ms | -5.33% | 25.5200 ms | 16.3928 ms | -611.00 bytes |
| drive-first-person | 9.7549 ms | 9.3808 ms | -3.83% | 38.3814 ms | 34.5910 ms | -1140.25 bytes |
| drive-chase | 11.0367 ms | 11.0308 ms | -0.05% | 39.6463 ms | 38.3221 ms | -984.94 bytes |
| retained-control | 6.1395 ms | 6.1879 ms | +0.79% | 7.8935 ms | 7.9369 ms | +0.00 bytes |

The two walking process medians overlap: baseline 4.5567/5.0258 ms, candidate
4.5947/4.3806 ms. Its pooled 5.33% reduction is an observation, not a statistically
established improvement. For first-person driving, both candidate process medians
(9.4778/9.3516 ms) are below both baseline medians (9.9337/9.6520 ms). Chase is
essentially flat. The fully retained control is 0.79% slower and has no texture
misses, so it exercises no texture-copy work. All results, including regressions
and individual process distributions, remain in the evidence.

| Trace | Texture misses per 64 frames | Eligible opaque-source misses | Eligible share |
| --- | ---: | ---: | ---: |
| walk-first-person | 183 | 164 | 89.62% |
| drive-first-person | 615 | 587 | 95.45% |
| drive-chase | 621 | 586 | 94.36% |
| retained-control | 0 | 0 | 0.00% |

All 1,024 measured frame controls match exactly across variants and repetitions:
ordered terrain texels, CPU overlay pixels, camera values, and cache-miss
identities. This is native preparation evidence. It does not measure browser or
physical-display FPS, GL execution, browser input/simulation, or device GPUs.
The improvements from this isolated patch cannot simply be added to percentages
from another optimization.

## Protocol and provenance

Each mode uses four fresh processes in baseline → candidate → candidate →
baseline order. Each workload has 16 warmup samples and 64 measured samples per
process; no measured samples are discarded. The runner uses .NET 10.0.14 and SDK
10.0.403 on an AMD EPYC 9V45 96-Core Processor, affinity core 8, two managed
processors and disabled tiered compilation. Other builds and CPU benchmarks were
stopped during the formal sequence.

The full compiled test harness and all 15 other staged runtime files are identical
between variants. Only `PixelMap.Rendering.dll` differs. The runner hashes both
runtime directories before and after the experiment, records every process
command, and preserves the complete runtime-file hashes and environment in
[provenance.json](provenance.json). The exact source delta and harness are in
[candidate.patch.gz](candidate.patch.gz), with [source hashes](source-provenance.json).
No runtime binaries are included here.

[analysis.json](analysis.json) pools both process blocks per variant. The median
averages the two middle values; p95 is nearest rank. Per-process statistics and
all raw values remain available. Raw reports use deterministic gzip compression;
the decoded hashes equal the original process outputs. A file manifest records
both stored and decoded identities.

## Reproduction

To recalculate the tables and verify all controls from the retained files:

```sh
python3 docs/verification/terrain-copy-20261110/analyze.py \
  docs/verification/terrain-copy-20261110 \
  /tmp/terrain-copy-analysis.json
```

The generated JSON is byte-for-byte identical to `analysis.json`.

To repeat the native experiment, create two checkouts of the recorded baseline.
Decompress and apply `candidate.patch.gz` to the candidate checkout and build its rendering test
project in Release. Build the unchanged baseline rendering project in Release.
Copy the candidate's test output to two runtime directories, omit PDB files, and
replace only `PixelMap.Rendering.dll` in the baseline directory with the baseline
renderer. Both directories must contain the same remaining files; this run kept
the Linux x64 native assets and omitted other platforms' unused assets.

```sh
python3 docs/verification/terrain-copy-20261110/run_abba.py \
  --dotnet /absolute/path/to/dotnet \
  --baseline /absolute/path/to/baseline-runtime \
  --candidate /absolute/path/to/candidate-runtime \
  --output /tmp/terrain-copy-repeat \
  --cpu 8 --samples 64
python3 docs/verification/terrain-copy-20261110/analyze.py \
  /tmp/terrain-copy-repeat /tmp/terrain-copy-repeat-analysis.json
```

Choose an available CPU for the new host and stop competing CPU work while
measuring. The source patch intentionally excludes the separate visibility
optimization so this comparison remains attributable to terrain copying.
