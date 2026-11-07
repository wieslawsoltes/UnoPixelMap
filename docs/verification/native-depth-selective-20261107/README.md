# Selective depth pruning: native verification

This bundle preserves the selected native CPU measurement sequence. The candidate keeps the original 16-pixel grid, replaces repeated integer division with shifts, iterates primitive candidates through spans, and applies exact pixel-center pruning only to projected triangles whose bounds are at most one pixel wide or high. Ordinary faces keep the original geometric-bin path. There is no per-vertex projection cache.

The baseline is `c937ddd192619f48c1a2ec682845b52bc1eebff8`. The frozen candidate renderer SHA-256 is `72434f75685028de2d90979113d7c54c0a9bb3d0cf217b70fe46fbca65cc2f0d`. [Source equivalence](../source-release-53e9c68.json) independently verifies the measured source against published commit `53e9c684a45ff1224c91ebeeefbb4d321c1c8c8b`, without rewriting the measurement identities.

## Measurement scope

Four fresh processes run baseline → candidate → candidate → baseline, pinned to CPU 0 with tiered compilation disabled and two managed processors. The same harness and dependencies are used; only Rendering.dll differs. Eight ordinary workloads have 16 warmup frames and 64 measured frames per process. The two control workloads have 4 warmups and 8 measured frames.

Total time is per-frame PrepareGpuFrame, including labels, plus navigation overlays/vehicle/gate/picking. Every camera runs that overlay stress. Independent depth construction/query phases are measured separately. GL, browser presentation and free-roam simulation are outside the measurement.

## Ordinary workload results

| Workload | Baseline median (ms) | Candidate median (ms) | Change | Baseline → candidate p95 (ms) |
| --- | ---: | ---: | ---: | ---: |
| manhattan-walk | 8.9250 | 6.9148 | -22.52% | 10.3123 → 9.9563 |
| manhattan-chase | 12.1925 | 9.8855 | -18.92% | 14.1184 → 12.3519 |
| manhattan-freeflight | 9.2437 | 9.3068 | +0.68% | 10.9395 → 12.6397 |
| manhattan-orbit | 15.9121 | 16.1028 | +1.20% | 18.0794 → 22.2703 |
| manhattan-freeflight-normal | 10.1261 | 10.4410 | +3.11% | 12.1448 → 14.8408 |
| hills-pixel | 1.0033 | 1.0219 | +1.85% | 1.3113 → 1.2139 |
| hills-minecraft | 1.3639 | 1.2074 | -11.47% | 2.0282 → 2.2060 |
| hills-roblox | 1.4968 | 1.5203 | +1.57% | 2.2980 → 2.1526 |

Normal freeflight is slower in both candidate processes. Flight/orbit/Normal tail increases also appear in thread CPU. Walking/chase improvements do not establish a universal latency improvement. See [audit.json](audit.json) for every process median/p95, CPU comparison, exact verification and resource control.

## Short controls

| Control | Baseline median (ms) | Candidate median (ms) | Change |
| --- | ---: | ---: | ---: |
| manhattan-zero-query | 10.0374 | 11.2505 | +12.09% |
| manhattan-full-query | 10.6648 | 9.8703 | -7.45% |

The zero-query control still prepares labels and navigation; only the independent probe batch is empty. Its increase is mainly frame preparation and is also present in thread CPU. Four warmups cover camera phases 0–3, followed by measured phases 4–11. With 16 samples per variant, p95 is the maximum recorded sample. These short controls should not be described as steady-state frame-rate measurements.

## Files and verification

- [summary.json](summary.json) and [identity.json](identity.json) are unchanged originals.
- [samples.csv](samples.csv) preserves all 2,112 raw sample rows in a reviewable table. [phase-comparison.csv](phase-comparison.csv) contains independently recomputed phase statistics.
- Four block directories contain complete raw reports as deterministic `.json.gz` files and their original logs.
- [candidate-source.patch](candidate-source.patch), [GpuSceneDepth.cs](GpuSceneDepth.cs), and [run.py](run.py) preserve the exact candidate and timing runner. Running the latter requires the separately built/frozen runtime directories described by identity.json; binary payloads are not included in this evidence bundle.
- [manifest.json](manifest.json) records each exported file hash/size and every gzip report’s decoded JSON hash/size. gzip headers use mtime 0 and an empty original filename.

To read a raw report without changing the bundle:

```sh
gzip -cd 0-baseline/depth-preparation.json.gz
```

The independent audit reconstructed the CSV byte for byte, checked 1,120 per-block summary fields and all aggregate statistics, verified 74 runtime file hashes, and passed 11,088 cross-block exact fingerprint comparisons. All measured index flags remain true; candidate maximum recorded index capacity is 2,139,184 bytes, below the 8 MiB pooling and 16 MiB active index limits.

Rejected experiments are preserved separately in [native-depth-prototypes-20261107](../native-depth-prototypes-20261107/README.md). Their measurements must be compared with their own controls.
