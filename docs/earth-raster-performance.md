# Exact Earth texture preprocessing

The Earth raster keeps its existing linear-light averaging, contrast threshold,
89-color palette, OKLab nearest-pair lookup and world-aligned Bayer pattern.
This change reduces the arithmetic needed to produce the same image. It changes
neither the decoded-image cache budget nor the artwork resolution.

## Sampling and cluster emission

The decoder selects the source-grid relationship once. A source image matching
the requested artwork dimensions still reads each source pixel directly. A
source image with exactly twice the artwork width and height uses a fixed 2×2
linear-light box sum. Every channel remains a nonnegative 12-bit integer, so
four samples fit safely in an `int`; summing in source order and dividing by
four exactly reproduces the generic box integral and its final truncation.
The generic path still handles other dimensions and scaling ratios.

The common 256×256 JPEG to 128×128 artwork case therefore avoids repeated
integer divisions for box coordinates and area, along with the small nested
box loops. The same path also handles a 512×512 source producing 256×256 art.
It introduces no additional image buffer or retained allocation.

Smooth 2×2 artwork regions already share one average color and resolved palette
pair. The emitter now unpacks that pair once, then selects the four colors from
their original Bayer thresholds. Both artwork dimensions, 128 and 256, are
multiples of the four-pixel Bayer period. A tile's integer origin consequently
contributes zero to the dither phase, including when longitude wraps. Cluster
origins advance by two on each axis, so their four thresholds remain in the
same 4×4 period. High-contrast regions retain their four independent palette
resolutions and samples.

## Correctness

The regression retains a separate copy of the raster and palette from
`a4d80e0a4dfa84b7b420c04e3c03deef4f8b96bb`. Only its type names and equivalent
safe bitmap-span access differ from that source; it does not call the new
sampling or cluster helpers.

The ordinary rendering suite compares **74 complete textures containing
3,989,462 pixels**. This includes all 19 bundled JPEGs, natural imagery,
smooth/noisy/high-contrast fixtures, 128- and 256-pixel output, direct and 2:1
sampling, non-square dimensions, a 1×1 image, and a 2048×1 image. It also checks
decoded-image reuse, replacement of an existing tile's payload, malformed
image data and the existing maximum-dimension guard. Every compared byte is
equal to the frozen implementation.

Another **131,072 cluster comparisons** cover all 32,768 palette input keys and
all four even Bayer origins. They compare every emitted sample against the
independent scalar sampler and check that surrounding destination pixels remain
untouched. Existing world-wrap and neighboring-tile dither checks remain active.

[Regression](../tests/PixelMap.Rendering.Tests/EarthRasterChecks.cs) ·
[Frozen raster](../tests/PixelMap.Rendering.Tests/Reference/LegacyEarthRaster.cs)

## Native measurements

The isolated comparison starts with clean `a4d80e0` production sources. The
candidate changes only `EarthRaster.cs`. An identical compiled harness runs
with each renderer, and all other runtime files are byte-identical.

Four processes run baseline → candidate → candidate → baseline on CPU 8,
using .NET 10.0.14, two managed processors and disabled tiered compilation.
Each process performs nine warmup passes followed by 64 measured passes over
the same 19 JPEGs and 14 synthetic textures. That produces **8,448 warm samples**
plus 132 separately recorded first-pass samples. Each pass creates a fresh
decoded-image cache; the static palette lookup table stays warm. Fixture
creation, cache construction, delegate setup, image hashing and result-object
allocation are outside the measured interval.

The measurement covers `EarthRaster.Get`: decoding, resampling, palette
resolution, dithering, immutable image creation and cache insertion or
replacement. Each grouped sample sums the individual tile times within one
pass. Medians average the two middle samples; p95 uses the nearest rank.

| Warm preprocessing workload | Baseline median | Candidate median | Change | Baseline p95 | Candidate p95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| World tile | 1.6676 ms | 1.3472 ms | −19.21% | 2.3637 ms | 1.9448 ms |
| Lake Garda, nine tiles | 14.9205 ms | 11.8351 ms | −20.68% | 20.6590 ms | 16.6499 ms |
| Manhattan, nine tiles | 20.0956 ms | 17.5198 ms | −12.82% | 28.5236 ms | 24.7967 ms |
| All 19 bundled tiles | 36.6291 ms | 30.9744 ms | −15.44% | 51.4324 ms | 42.9544 ms |
| All 14 synthetic textures | 27.7952 ms | 23.7946 ms | −14.39% | 38.3065 ms | 32.5320 ms |

The 33 image-control hashes match across all four processes. Measured managed
allocations are unchanged: 12,848 bytes per complete bundled batch and 7,616
bytes per synthetic batch. These counters do not include native codec or Skia
allocations.

The 512→128 synthetic fixture, which uses the generic 4:1 sampling path,
has a **1.08% median regression**. Its raw samples remain in the evidence.
The first cold world call also measures slower in the candidate pair:
20.5940/23.3941 ms for the baseline and 21.8807/24.2551 ms for the candidate.
Those first calls include JIT and static palette initialization; two processes
per variant are insufficient to establish a cold-start improvement.

These are native CPU texture-preparation measurements. They do not measure
browser frame rate, GPU execution, application startup, or retained-cache hits.
The per-block medians, complete per-texture results, cold samples, allocation
histograms, source/binary hashes and unchanged runtime-file manifest are retained
in the evidence. No measured timing samples are discarded.

[All samples](verification/earth-raster-20261109.csv) ·
[Provenance, controls and statistics](verification/earth-raster-20261109.json) ·
[Reproduction harness](verification/earth-raster-harness/README.md)
