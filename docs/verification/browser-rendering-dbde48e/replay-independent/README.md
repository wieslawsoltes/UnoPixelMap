# Independent camera replay audit

Run [42907891884](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42907891884) passed the complete replay audit: **12 fresh browser processes, 384 trusted inputs, 408 individually fenced map submissions and 9 exact cross-capture comparisons**. The candidate is `dbde48e9fd5318ef309bf31ed4b9b7e1596ec7d6`; the baseline is `555990d0e086ffb9908d4d98ec30a73a245a6c6e`.

The captures ran serially in baseline/candidate/candidate/baseline order for each case. Each of the four captures contains the same 32 native wheel inputs. Pan produces 34 map resolves, orbit 32, and paused first-person look 36. Every ordered per-input sequence matches exactly across all four captures, including the additional resolves; no frames are removed, sorted, deduplicated or accepted with a numeric tolerance. The projection hashes also match the earlier same-artifact preflight. Preflight timing is excluded because its runner CPU differs.

## CPU and latency results

The runner used Chromium **156.0.8078.4**, SwiftShader, an **AMD EPYC 9V45 96-Core Processor**, CPU affinity `0,1,2`, a 1200 × 780 viewport and DPR 1. The complete browser flags, executable hash and environment are retained in the parent evidence directory.

CPU/input is the capture’s total CDP `TaskDuration` divided by 32. It includes work across the complete controlled trace and its barriers. The two numbers in each process column are repetitions 1 and 2.

| Case | Baseline CPU/input, both processes (ms) | Candidate CPU/input, both processes (ms) | Change of process mean |
|---|---:|---:|---:|
| Relief pan | 75.668, 74.164 | 59.369, 57.935 | -21.71% |
| Relief orbit | 105.344, 97.084 | 68.131, 66.048 | -33.72% |
| Paused first-person look | 112.470, 110.932 | 68.472, 70.519 | -37.78% |

The latency values below are the arithmetic mean of the two process medians, p95 values or maxima. They are **not pooled percentiles**. A process median is the sorted sample at index `floor(n/2)`; its p95 is the sample at `floor(0.95*n)`, capped at the last sample. All displayed timings are rounded only for this table.

| Case | Observation | Median baseline → candidate (ms) | p95 baseline → candidate (ms) | Max baseline → candidate (ms) |
|---|---|---:|---:|---:|
| Relief pan | Input → first submission | 24.95 → 21.30 | 151.15 → 62.35 | 182.20 → 64.25 |
| Relief pan | Input → final fence observation | 1001.15 → 985.30 | 2016.60 → 1952.70 | 2055.95 → 1994.65 |
| Relief orbit | Input → first submission | 31.40 → 28.40 | 235.05 → 75.85 | 270.00 → 95.40 |
| Relief orbit | Input → final fence observation | 1062.25 → 1027.20 | 1241.60 → 1089.55 | 1242.75 → 1102.65 |
| Paused first-person look | Input → first submission | 72.85 → 39.85 | 268.05 → 117.25 | 347.70 → 165.65 |
| Paused first-person look | Input → final fence observation | 208.85 → 157.80 | 456.05 → 338.30 | 490.75 → 353.95 |

All recorded CPU/input and latency median, p95, maximum and mean values improve in both paired process repetitions. The JSON retains each process value, both paired percentage changes and the central percentage change for every metric.

## Slower matched inputs

Aggregate improvement does not mean every input improves. The audit compares each repetition’s baseline and candidate at the same zero-based input index. Counts use the strict sign of the unrounded `candidate - baseline` value, with **no threshold or exclusions**. None of the positive differences is below 0.01 ms. There is one exactly equal first-submission observation in pan; all other comparisons differ.

| Case | Observation | Slower / 64 | Largest increase: repetition, input index | Baseline → candidate (ms) | Increase (ms) |
|---|---|---:|---|---:|---:|
| Relief pan | Input → first submission | 12 / 64 | 2, 28 | 19.3 → 27.1 | +7.8 |
| Relief pan | Input → first resolve’s fence observation | 19 / 64 | 1, 10 | 950.1 → 1032.3 | +82.2 |
| Relief pan | Input → final fence observation | 19 / 64 | 1, 10 | 950.1 → 1032.3 | +82.2 |
| Relief orbit | Input → first submission | 11 / 64 | 2, 21 | 25.8 → 37.8 | +12.0 |
| Relief orbit | Input → first resolve’s fence observation | 20 / 64 | 2, 14 | 956.3 → 1037.0 | +80.7 |
| Relief orbit | Input → final fence observation | 20 / 64 | 2, 14 | 956.3 → 1037.0 | +80.7 |
| Paused first-person look | Input → first submission | 11 / 64 | 1, 6 | 24.3 → 42.1 | +17.8 |
| Paused first-person look | Input → first resolve’s fence observation | 9 / 64 | 1, 2 | 157.6 → 204.4 | +46.8 |
| Paused first-person look | Input → final fence observation | 9 / 64 | 2, 24 | 254.7 → 352.4 | +97.7 |

The JSON retains all 576 timing pairs (192 matched inputs × three metrics), including their unrounded deltas and identities. These adverse observations are not dismissed as proven noise. Two processes per variant support descriptive comparisons; they do not establish statistical significance or explain an individual difference.

## What is independently checked

- Every consumed input is pinned to its original decoded byte count and SHA256: 12 reports, 12 complete raw probes, 9 comparison documents, provenance and environment (35 files, 59,508,542 bytes). The original ZIP was also rehashed and all consumed ZIP members checked against extraction during this audit.
- Source revisions, complete Chromium version, CPU model, affinity count, viewport, DPR, flags and all five harness hashes match the recorded environment. Process timestamps confirm serial ABBA execution.
- The unchanged, locally retained contract reconstructs every report step from trusted native inputs and raw resolve/fence observations. It checks the complete ordered recorded projection floats, draw dimensions, camera/lens uniforms and all terrain transforms. Cache-slot identifiers excluded by the published probe remain excluded; this check does not claim to hash every geometry-buffer byte.
- Each resolve is individually signaled before its step completes. The full performance probe independently reports exactly the same number and order of map resolves, matching dimensions, one created fence and one observed signal for each resolve ID. Nested probes timestamp calls at different points; their observation order is checked without requiring their clock samples to be equal.
- Every input completes its quiet and animation-frame barriers before the next input begins. The minimum observed quiet interval is 284.19999999995343 ms, above the required 250 ms. The paused first-person HUD stays unchanged.
- CPU totals and all three latency distributions are recomputed from raw data. All nine full CI comparison documents are reproduced exactly, including their summaries.

Fence observations establish map-submission completion through `clientWaitSync`; they do not measure browser presentation or physical display FPS. The final-fence metric is the latest recorded fence observation for any resolve caused by that input. It excludes the remaining quiet/compositor barrier time after the last fence. Public inputs run with barriers between them and bundled scene data. These results do not measure simulation advance, held-key play trajectories, external tile fetches or hardware-GPU performance. The separate realistic play benchmarks remain in the parent evidence directory.

## Reproduce offline

Node.js 18 or later is sufficient; no browser, npm packages, build or network is required. From the repository root:

```sh
node docs/verification/browser-rendering-dbde48e/replay-independent/audit.cjs > /tmp/camera-independent-audit.json
cmp docs/verification/browser-rendering-dbde48e/replay-independent/camera-independent-audit.json /tmp/camera-independent-audit.json
```

The default input root is this directory’s parent. The reader accepts original `.json` files or their lossless `.json.gz` copies, hashes the decoded bytes, and limits gzip expansion to the pinned input size. It emits JSON to stdout and exits nonzero on any mismatch. The frozen reference contract avoids importing future harness changes.

To audit an original extracted artifact, and optionally recheck its complete ZIP hash and size:

```sh
node docs/verification/browser-rendering-dbde48e/replay-independent/audit.cjs /path/to/extracted/play-evidence /path/to/atlas-play-performance.zip > /tmp/camera-independent-audit-original.json
```

The second path is optional. Omitting it rechecks the retained input bytes against the acquisition manifest; it does not rehash an absent ZIP. Both input layouts reproduced the saved audit JSON byte-for-byte. A modified decoded input was also confirmed to fail the integrity gate.

## Files and provenance

- `audit.cjs`: portable offline checker and complete recomputation.
- `reference-contract.cjs`: unchanged contract with SHA256 `69430f8b256d1e088adcb8174c18a2540f7d452eba964c02e49644e7647a3a48`.
- `input-manifest.json`: original acquisition hashes, fixed source/harness identities and preflight trace/projection reference hashes.
- `camera-independent-audit.json`: full reproducible result, per-process summaries and all matched input timing observations.
- `validation.json`: integrity, original-versus-retained reproduction and negative integrity check results.

Performance artifact **13808810774**, run **42907891884**, is 13,798,778 bytes; its SHA256 is `b0feae7904d7c8350f7445424dce6fb9313d65fd52b9c238bc914e2a14efa5de`. The parent `play-evidence-acquisition.json`, `play-artifact.json` and `play-members.json` retain API provenance and original-to-retained member hashes. Raw projection data remains in the parent `camera-replay/` tree; this audit adds no duplicate raw files.
