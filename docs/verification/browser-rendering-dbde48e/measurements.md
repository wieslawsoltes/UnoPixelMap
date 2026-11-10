# Paired browser analysis

Audit: **PASS**.

Baseline `555990d0e086ffb9908d4d98ec30a73a245a6c6e` (run 42696985047); candidate `dbde48e9fd5318ef309bf31ed4b9b7e1596ec7d6` (run 42907891884).

This is a paired comparison within one CI runner, with fresh serial browser processes. Primary summaries are medians across independent process values; every process value and range is retained. Task CPU uses main-thread threadTicks; occupancy divides TaskDuration by the CDP Timestamp metric interval, which includes probe-result transfer. Map submissions and rAF rates divide counts by the probe capture interval. Map submissions are submitted map images; rAF is callback cadence; neither is physical display FPS. Fence values are the application's observed signal/polling delays, not actual GPU completion or display latency. They include polling cadence and SwiftShader; a long fence value does not establish visible lag. Held-key fence distributions include only signals observed in the capture: carry-in signals and unobserved tail submissions are counted separately. Held-key trajectories may diverge: retain public HUD poses/distances and art dimensions. One stationary or two moving processes per variant support descriptive comparisons, not confidence intervals. CPU profiles are separate later passes, only the seven preselected cases in repetition one. Replay comparisons require all four captures to retain the same ordered 32 trusted inputs and every projection/draw; otherwise comparative replay latency is withheld. Barrier-paced replay latency is not throughput or FPS. Replay identity is workload identity, not pixel equivalence. CPU includes observer and automation work.

## Held-key and stationary processes

Each cell retains repetition 1/2. Deltas use the median across process values; all signs are retained.

| Scenario | Task CPU ms, baseline → candidate | Δ CPU | Submitted maps/s, baseline → candidate | Δ maps/s | rAF/s, baseline → candidate | Δ rAF/s |
|---|---:|---:|---:|---:|---:|---:|
| map-idle-default | 66.84 → 68.72 | 2.82% | 0.00 → 0.00 | —% | 60.08 → 60.07 | -0.01% |
| map-idle-relief | 118.27 → 110.30 | -6.74% | 0.00 → 0.00 | —% | 60.10 → 60.09 | -0.01% |
| walk-stationary-firstperson | 349.14 → 381.39 | 9.24% | 0.00 → 0.00 | —% | 60.10 → 59.99 | -0.18% |
| walk-moving-firstperson | 2580.65/2527.86 → 2412.85/2474.47 | -4.33% | 9.10/8.87 → 8.87/8.75 | -1.95% | 59.73/59.57 → 59.61/59.98 | 0.24% |
| walk-moving-chase | 3292.39/3471.35 → 3148.18/3172.54 | -6.55% | 13.23/13.68 → 13.97/14.11 | 4.38% | 56.41/56.07 → 58.14/58.19 | 3.42% |
| car-stationary-chase | 493.17 → 473.84 | -3.92% | 0.00 → 0.00 | —% | 60.09 → 60.10 | 0.01% |
| car-moving-chase | 4240.12/4091.07 → 3375.75/3381.97 | -18.89% | 10.00/9.74 → 9.99/9.34 | -2.03% | 51.85/52.67 → 56.84/56.89 | 8.81% |
| car-moving-firstperson | 3529.79/3590.68 → 2864.56/2809.44 | -20.31% | 7.08/7.32 → 7.50/7.87 | 6.78% | 53.50/54.07 → 58.10/58.22 | 8.13% |
| freeflight-stationary | 381.78 → 382.37 | 0.15% | 0.00 → 0.00 | —% | 59.98 → 60.12 | 0.23% |
| freeflight-moving | 2823.65/2760.01 → 2638.54/2601.84 | -6.15% | 10.37/10.25 → 11.24/10.12 | 3.63% | 57.96/57.73 → 58.97/59.22 | 2.15% |

CPU occupancy, interval/fence p95, per-process ranges, paired repetition deltas, within-variant drift, HUD distance, resolution counts, and pipeline safeguards are retained in analysis.json and samples.csv.

### Secondary aggregates and formulas

- Process median: statistics.median(process values); with two processes this is their arithmetic mean.
- Weighted CPU occupancy: 100 × sum(TaskDuration ms) / sum(CDP metric interval ms).
- Weighted map/rAF rate: 1000 × sum(count) / sum(probe capture ms).
- Pooled interval distributions combine only intervals already formed inside each process; no cross-process interval is invented.
- Within-process distributions match the harness: sorted[n//2] median and sorted[min(n−1, floor(0.95n))] p95.
- Percent change: 100 × (candidate/baseline − 1). Occupancy percentage points are reported separately.

## Fixed-camera replay

Comparative latency appears only after all four raw captures and ordered identities pass.

| Scenario | Comparable | Task CPU/step ms, baseline → candidate | First submission median ms | Observed settled-fence signal median ms |
|---|---|---:|---:|---:|
| map-pan-relief | Yes | 75.67/74.16 → 59.37/57.93 | 25.10/24.80 → 22.10/20.50 | 1002.40/999.90 → 992.90/977.70 |
| map-orbit-relief | Yes | 105.34/97.08 → 68.13/66.05 | 32.00/30.80 → 28.00/28.80 | 1076.60/1047.90 → 1027.10/1027.30 |
| walk-look-firstperson | Yes | 112.47/110.93 → 68.47/70.52 | 72.40/73.30 → 40.60/39.10 | 216.90/200.80 → 159.60/156.00 |

Audited profiles: 14/14, from separate first-repetition passes. Symbolication requires each exact package's symbols; numeric WASM function indices are not stable cross-package method identities.

## Audit limits

Artifact metadata and preserved baseline resource identity are checked. Pages binaries are not present here: the workflow's package-byte verification is not independently repeated. These files do not authenticate GitHub API responses or physical display presentation.

