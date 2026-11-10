# Exact early occlusion: native verification

This comparison isolates `GpuSceneDepth.cs` against source commit
`ce82b2c6fccee2d77a9e9489e1c6415e04412e8c`. The candidate has lower native
preparation/navigation medians in all five city workloads. The three hill
workloads have higher medians in the final sequence; those results, process
spread and all exploratory runs remain in this bundle. This is not a browser
frame-rate measurement or evidence of a universal speedup.

## Why early return is exact

The old Boolean visibility query first prepared every potentially contributing
model. The candidate resolves all contributing terrain, samples already prepared
primitives through the existing exact cell index, then prepares additional
models only while their answer can matter.

Let `T` be maximum terrain depth at the artwork-pixel center, `M` maximum model
depth, `E` maximum rim depth, and `N = max(T, M, E)`. The existing renderer uses
the terrain's continuous depth at the caller's subpixel coordinates only when
terrain exists, `M < T`, and `E < T - rimTolerance`. Otherwise it uses `N` and the
original pixel-center comparison threshold `Q`.

After every contributing terrain group has been resolved, an answer is
permanently covered when `N > Q` and the terrain substitution is disabled.
Subsequent model/rim contributions can only increase `M`, `E` and `N`; they
cannot restore that substitution. The candidate therefore preserves the original
comparison arithmetic, including perspective epsilon, roof-rim tolerance and
source-order terrain ties.

A successful early return caches no partial `PixelHit` and does not mark the
cell complete. Later picking, height queries or a different requested altitude
still demand omitted geometry. The eighth uncached query uses the existing
complete-cell path. Triangle storage remains 84 bytes; active/pooled index
budgets remain 16/8 MiB. Exhaustion still falls back to complete group ranges.

The production patch and exact source hashes are retained in
[candidate-source.patch](candidate-source.patch) and [provenance.json](provenance.json).
The frozen renderer source differs from the baseline in this one file only.

## Fixed-camera native protocol

The final sequence contains **2,112 measured frames** in four fresh processes:
baseline → candidate → candidate → baseline. Each of eight ordinary workloads
uses 16 warmup frames and 64 measured frames. Two independent-query controls use
four warmups and eight measured frames. No timing samples are excluded.

Both variants use the identical compiled baseline harness and runtime files;
only `PixelMap.Rendering.dll` and its PDB differ. The host is an AMD EPYC 9V45
with .NET 10.0.14 / SDK 10.0.403 on Debian 13. Processes are pinned to CPU 8,
report two managed processors and disable tiered compilation. Labels use
DejaVu Sans; the font file hash is recorded.

The 1,100 × 650 viewport requests pixel size 3; Normal and Roblox resolve to
pixel size 1. Each ordinary workload repeats the same 16 camera poses. City
fixtures use nine bundled Manhattan vector tiles, with walking, chase, flight,
orbit and Normal-flight views. Three synthetic hill fixtures use Pixel,
Minecraft and Roblox styles. Every workload calls the same driving-route,
vehicle, gate and terrain-pick APIs; this does not imply visible navigation
geometry in every fixture. These are fixed rendering/overlay workloads, not
wall-clock free-roam simulations.

The principal measurement sums `PrepareGpuFrame` (including label preparation)
and the navigation/overlay pass for each frame. Independent depth construction,
first sparse queries and repeated queries are recorded separately. There is no
separate label-only timer. GL submission, presentation and browser execution are
excluded. Hashing and result-object creation are outside measured intervals.

Derived medians average the two middle samples; p95 uses nearest rank. Original
harness summaries retain their lower-middle median estimator. The auditor pairs
equal frame/camera indices in the adjacent AB and BA blocks and exports every
pair, plus per-phase changes, wall/thread-CPU times and managed allocation.

## Final measurements

| Workload | Baseline median (ms) | Candidate median (ms) | Change | Baseline → candidate p95 (ms) | Thread-CPU median change |
| --- | ---: | ---: | ---: | ---: | ---: |
| Manhattan walking | 5.9254 | 4.6848 | −20.94% | 7.7791 → 6.7336 | −20.89% |
| Manhattan chase | 10.1424 | 7.5728 | −25.33% | 14.0868 → 10.8716 | −25.10% |
| Manhattan free flight | 7.3768 | 6.4162 | −13.02% | 8.4976 → 8.4488 | −13.14% |
| Manhattan orbit | 13.4515 | 11.4957 | −14.54% | 17.9846 → 16.2061 | −14.35% |
| Manhattan Normal flight | 7.9400 | 7.0940 | −10.66% | 11.3329 → 9.2692 | −10.94% |
| Hills Pixel | 0.7276 | 0.7901 | **+8.58%** | 1.0106 → 1.2285 | +7.35% |
| Hills Minecraft | 0.9016 | 1.0459 | **+16.00%** | 1.3553 → 1.8219 | +15.95% |
| Hills Roblox | 1.2286 | 1.3969 | **+13.71%** | 2.1471 → 2.5241 | +13.77% |
| Zero independent-query control | 7.4294 | 6.8670 | −7.57% | 30.7724 → 25.5725 | −7.42% |
| Full independent-query control | 7.9962 | 8.0453 | +0.61% | 26.6439 → 26.1156 | +0.62% |

The control rows still include labels and navigation; “zero” refers only to the
independent query batch. Their eight camera phases and short sample count make
them controls rather than broad steady-state measurements.

Phase-matched city medians improve in all 16 camera phases for walking, chase,
flight and Normal flight, and 15 of 16 for orbit. The median changes across
those phase comparisons are respectively −22.50%, −25.31%, −12.99%, −10.28%
and −13.15%. They are descriptive comparisons of the recorded processes,
not confidence intervals.

Process spread is material:

| Workload | Baseline 1 | Candidate 1 | Candidate 2 | Baseline 2 |
| --- | ---: | ---: | ---: | ---: |
| Manhattan orbit (ms) | 12.5878 | 10.7530 | 14.1573 | 15.2521 |
| Hills Pixel (ms) | 0.7469 | 0.7055 | 0.9378 | 0.7167 |
| Hills Minecraft (ms) | 0.9158 | 0.8699 | 1.3231 | 0.8783 |
| Hills Roblox (ms) | 1.2018 | 1.0114 | 1.7405 | 1.2286 |

The second candidate process also has higher times in unchanged depth-constructor
work. This is evidence of runtime/host variation, but does not prove that every
hill regression is environmental: the ordinary sampling path was also refactored
and its JIT layout changed. The final hill regressions remain part of the result.

Managed allocation is effectively unchanged in the ordinary cases. For example,
flight's mean is 1,411,365.875 bytes/frame in both variants; Pixel and Minecraft
hills retain exactly 38,337.9375 and 78,213.375 mean bytes/frame. Small changes in
other cases are retained in the summary and are not claimed as an allocation win.

## Deterministic work reduction and applicability

Across the same measured city frames, total retained prepared-triangle counts
after labels/navigation change by −1.56% walking, −2.04% chase, −9.33% flight,
−12.46% orbit and −10.24% Normal flight. Per-camera counts remain in the raw
reports; an aggregate reduction does not mean every camera prepares less geometry.
All three hill counts are identical. Independent probe counts/geometry also agree.

A separate diagnostic-only build records actual model candidates at each new
Boolean traversal. It is excluded from all performance statistics:

| Workload, 16 camera phases | Traversals | Early returns | Median coarse model candidates | Median models overlapping the exact point |
| --- | ---: | ---: | ---: | ---: |
| Walking | 3,387 | 2,490 | 111 | 91 |
| Chase | 3,815 | 2,774 | 106 | 85 |
| Free flight | 3,649 | 3,508 | 109 | 64 |
| Orbit | 5,169 | 5,048 | 143 | 93 |
| Normal flight | 6,381 | 6,023 | 237 | 61 |
| Hills Pixel / Minecraft / Roblox | 0 / 0 / 0 | 0 / 0 / 0 | — | — |

Every observed city traversal has at least two overlapping models. None of the
hill fixtures enters `CoversBeforeComplete`: they have no labels, and their
independent probes request terrain before visibility, so visibility reads a
complete cached sample. A model-count guard around this new traversal therefore
cannot remove the measured hill cost. No style- or fixture-specific guard was
added. These counters rule out additional Boolean-traversal work as the cause
in those hill fixtures, while leaving other code-layout/runtime effects open.

## Exactness and earlier experiments

The final auditor verifies **3,168 query/overlay hash comparisons** and **7,920
frame, geometry-count and altitude-bound comparisons** across the four processes.
All index flags remain true in this ordinary benchmark; deliberate exhaustion is
covered separately by the pressure suite.

The correctness logs retain:

- 11,935,261 exact sparse/eager comparisons, including visibility before picking.
- 15,037 pressure comparisons, including visibility-triggered budget exhaustion.
- 9,063,600 projected-raster comparisons across 562 frames.

A 48-layer building fixture additionally proves that an early visible obstruction
leaves later groups unprepared and that subsequent altitude/terrain queries still
see them. All tests use the frozen eager oracle; diagnostic counters are not an
oracle for visible correctness.

Earlier sequences are preserved separately and never pooled into final timings:

- `exploratory-screen/`: one 4-warmup/8-sample baseline/candidate screen. The test
  assembly hashes differ because candidate correctness tests were added; this is
  exploratory evidence only.
- `exploratory-32/`: the original 16-warmup/32-sample ABBA. City medians improve
  7.59–23.97%; Minecraft hills regress 26.78%. The complete process spread remains.
- `exploratory-minecraft/`: an isolated 32-warmup/64-sample Minecraft ABBA prompted
  by that regression. Its combined median is 0.9714 → 0.95665 ms (−1.52%). The
  baseline's own two process medians are 1.1497 and 0.92505 ms. This follow-up does
  not erase the later final-sequence regression.

## Files and reproduction

`final/` contains the four original reports under deterministic gzip compression,
original logs, environment/runtime identities, the audited `summary.json` and
`phase-pairs.csv.gz`. The latter holds all 7,392 paired metric rows. `provenance.json`
records production/harness sources and font identity; `final/original-identity.json`
records every runtime file. The exact harness sources are under `harness/`; no
binaries are included. `manifest.json`
records every retained file hash, including decoded hashes for gzip content.

Rebuild two clean checkouts from the same baseline, applying only the retained
production patch to the candidate. Use .NET SDK 10.0.403 to match the recorded
compiler/runtime. With the checkout paths substituted:

```sh
git worktree add --detach /tmp/occlusion-base ce82b2c6fccee2d77a9e9489e1c6415e04412e8c
git worktree add --detach /tmp/occlusion-candidate ce82b2c6fccee2d77a9e9489e1c6415e04412e8c
git -C /tmp/occlusion-candidate apply /absolute/path/to/candidate-source.patch
dotnet build /tmp/occlusion-base/tests/PixelMap.Rendering.Tests/PixelMap.Rendering.Tests.csproj -c Release -m:1 --disable-build-servers
dotnet build /tmp/occlusion-candidate/tests/PixelMap.Rendering.Tests/PixelMap.Rendering.Tests.csproj -c Release -m:1 --disable-build-servers
python3 run.py --dotnet /absolute/path/to/dotnet \
  --baseline-runtime /tmp/occlusion-base/tests/PixelMap.Rendering.Tests/bin/Release/net10.0 \
  --candidate-renderer /tmp/occlusion-candidate/src/PixelMap.Rendering/bin/Release/net10.0/PixelMap.Rendering.dll \
  --output /tmp/occlusion-reproduction --cpu 8
python3 analyze.py /tmp/occlusion-reproduction /tmp/occlusion-derived
```

`run.py` copies the compiled baseline runtime twice and replaces only the candidate
renderer/PDB, then checks every other file hash before executing. Use a new output
directory. A different host or build path need not reproduce binary hashes or
absolute timings; exact query/image controls and recorded environment identify
what was compared.

To reproduce the committed derived results directly from retained raw reports:

```sh
python3 analyze.py final /tmp/occlusion-audit
python3 analyze.py exploratory-32 /tmp/occlusion-exploratory-audit
python3 analyze.py exploratory-minecraft /tmp/occlusion-minecraft-audit
python3 analyze_counts.py query-counts/original-report.json.gz /tmp/occlusion-count-audit
```

These derived files reproduce their committed counterparts byte for byte.
`query-counts/diagnostic-source.patch` applies to the clean baseline and contains
both the production optimization and diagnostic instrumentation. Its provenance
records the compiled diagnostic identities and invocation; its timing fields are
never used for a performance conclusion.
