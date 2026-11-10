# Auditor for the dbde48e paired browser artifact

This offline bundle audits `atlas-play-performance` from run **42907891884**. Its defaults pin baseline **555990d0e086ffb9908d4d98ec30a73a245a6c6e / 42696985047** and candidate **dbde48e9fd5318ef309bf31ed4b9b7e1596ec7d6 / 42907891884**. It does not run a benchmark or predict a speedup.

## Run

Python 3.10+ and Node.js are sufficient. No npm install, Playwright, browser, .NET SDK, or build is needed.

```sh
python3 docs/verification/browser-rendering-dbde48e/auditor/analyze.py \
  docs/verification/browser-rendering-dbde48e \
  /tmp/unopixelmap-dbde48e-play-analysis
```

The input must be an extracted artifact directory containing `provenance.json` and `environment.json`. Plain JSON/profile files and their gzip forms are supported. Derived output must be outside the input directory. A failed audit returns a nonzero exit code and writes explicit errors plus the independently valid observations. It never changes input evidence or production source.

Run the command above from the repository root. The analyzer code and frozen helpers are portable; they require only Python and Node. The checked-in `analysis.json` records the original uncompressed archive members. Reanalysis of this retained directory produces identical measurement sections and CSV bytes, with different `audited_input_files` entries for the deterministic gzip containers. `play-members.json` records that original-to-retained mapping.

For a newly downloaded ZIP and the raw GitHub API artifact object, `extract-artifact.py` first verifies the declared archive SHA256, byte size, source and run. It rejects unsafe/duplicate ZIP names and symlinks, refuses to overwrite an existing destination, preserves all member bytes, and emits a member-hash acquisition record. Passing `--pages-artifact-metadata /workspace/scratch/c64783d771e9/browser-validation/release-dbde48e/pages-artifact.json` additionally matches the capture provenance to the independently downloaded candidate Pages artifact's ID, digest, size, source and run.

Outputs are `analysis.json`, `samples.csv` (30 process records), `profiles.csv` (14 separate profile passes), `replay-samples.csv` (12 process records), and a readable `README.md`. JSON retains every sign, repetition, range, observed regression, paired change, and within-variant drift. There is no outlier removal or selected-case speedup score.

## Provenance and raw validation

The auditor checks exact expected source SHAs, run IDs, run URLs, Pages artifact identity/digest syntax/size, runtime mode, nonempty unique boot resources and all recorded content hashes. The baseline must equal `known-baseline-artifact.json`, copied from the previous verification package's candidate section, including all resource hashes. The prior run's timings are **never** used in the new comparison.

It checks complete scenario/execution ordering, successful process exit status, timestamps, fresh-process output paths, report revisions, CPU identity/affinity-visible parallelism, viewport, DPR, Chromium version/flags, and each recorded harness hash. Frozen source files are copied verbatim from `dbde48e`; `source-provenance.json` records their SHA256 and Git blob IDs. Their hashes are checked before use. No current working-tree helper is substituted.

`verify-raw.cjs` loads only the frozen pure `distribution`, `summarizeProbe`, `summarizeProfile`, and replay contract functions. All capture fields, all CPU components, transfer/readback/fence distributions and counters, art-copy counts, rAF rates, CPU occupancy, and the recorded top-45 profile tables are recomputed exactly from raw evidence. Profiles additionally require a valid acyclic node graph, referenced sample IDs, complete finite nonnegative deltas, and sensible start/end times. File hashes record both stored bytes and decompressed content.

The analyzer checks recorded Pages metadata but does not have the Pages archive bytes. The workflow independently checked downloaded package resources before measurement. That workflow check is not falsely presented as an independent byte check by this script. Downloading the performance ZIP, matching its SHA256 to the GitHub artifact digest, and retaining the API artifact metadata are separate acquisition steps. CPU and browser identity checks compare the complete `lscpu` model and complete four-component browser version, not partial substrings.

## Held-key and stationary metrics

The ten predefined scenarios produce 30 browser processes: stationary cases use AB; moving cases use ABBA. Each variant has one stationary or two moving observations. Comparisons stay on this one runner/driver/browser; there are no confidence intervals from these small samples.

| Quantity | Formula / interpretation |
|---|---|
| Task CPU ms | `1000 × (after.TaskDuration − before.TaskDuration)`, main-thread `threadTicks`. |
| Metric interval ms | `1000 × (after.Timestamp − before.Timestamp)`; includes probe result serialization/transfer. |
| CPU occupancy | `100 × Task CPU ms / metric interval ms`. Report both relative percentage change and percentage-point change. |
| Submitted maps/s | `1000 × number of map resolves / probe.durationMs`. Submitted images, not physical display FPS. |
| rAF/s | `1000 × number of recorded callbacks / probe.durationMs`. Browser callback cadence, not renderer or physical display FPS. |
| Map / rAF intervals | Differences between adjacent submissions/callbacks inside each process only. |
| Observed submit-to-fence signal | Application-observed fence polling delays. Includes polling schedule and SwiftShader, not actual GPU completion or display latency. Includes only signals observed during the capture, which can include carry-in work and omit tail submissions. |
| Pixel-pack art copies | Detached pipeline work. Zero copies on compositor rendering does not mean zero submitted maps. |
| HUD and art dimensions | Full before/after public telemetry, distance changes, pixel sizes and submitted widths/heights are retained to expose workload differences. |

Primary aggregates are **medians across process values**, with both raw values and min/max retained. For two process observations the median equals their arithmetic mean. A process's p95 is summarized across processes separately from the pooled distribution. Within each process, the published harness uses the upper-middle order statistic `sorted[n//2]` and p95 `sorted[min(n−1, floor(0.95n))]`; this convention is preserved.

The older analyzer's measures remain in `secondary` for transparent comparison: arithmetic mean Task CPU, weighted occupancy `100 × sum(cpuMs)/sum(metricMs)`, and weighted map/rAF rate `1000 × sum(count)/sum(captureMs)`. Pooled distributions concatenate existing within-process intervals, never a synthetic interval spanning processes. All percentage changes are `100 × (candidate/baseline − 1)`; zero baseline rates produce `null`, not infinity or an invented improvement.

Held-key trajectories can diverge because simulation timing and travelled distance differ. The paired input protocol is reproducible, but these measurements do not prove an identical submitted camera path or isolate one source patch's causal contribution.

Held-key fence observations are censored by the capture boundary. Every process records submitted IDs, observed signal IDs, signals associated with submissions from outside the capture (carry-in), and submitted IDs without an observed signal. For example, the archived baseline freeflight-moving repetition 2 submitted 37 maps and observed 36 signals, comprising 35 in-window submissions plus one carry-in signal, while two tail submissions had no signal observed in the window. The recorded latency distribution is retained as observed, without calling it completion latency for every submitted map. The fixed-camera contract below independently waits for every step's own fence and therefore does not use this incomplete held-key sample as its replay completion measure.

## CPU profile selection and limits

Only repetition one is profiled, separately **after** its timing capture, for exactly these seven scenarios and both variants: map-idle-relief, walk-stationary-firstperson, walk-moving-firstperson, walk-moving-chase, car-moving-chase, car-moving-firstperson, and freeflight-moving. All 14 requested profiles must exist and no extra process profile can be silently selected.

The harness requests a 1000 µs sampling interval but actual `timeDeltas` determine recorded weights. The auditor preserves sample count, node count, profile duration, delta sum, the duration/delta residual, idle/program/GC self weight, and all originally reported top-45 self/inclusive rows. It does not assume the requested interval equals every observed sample delta. A zero delta is retained exactly in audit weights and flagged because the original table code uses `delta || 1000`.

A profile is not the preceding timed CPU window. Inclusive time is not additive across ancestors. Cross-package WASM function indices are not stable method IDs: symbolication needs the exact baseline/candidate binaries and corresponding symbols. The script does not infer cross-build hotspot changes from numeric indices, expand the selected profile scope, or pool profiles into the elapsed timing captures.

## Fixed-camera replay

The three replay scenarios each use four processes in ABBA order and 32 native inputs per process. The frozen schema-2 contract validates each trusted input, all ordered 1–32 map submissions per step, every projection/draw dimension and transform, its own observed fence signal, the 250 ms quiet interval, and compositor barrier completion. The analyzer reconstructs every report step from raw probes, verifies that all full-capture submissions belong to the input scopes, and matches the independent probes' ordered dimensions/contexts. Paused look telemetry must remain exact.

All four reports are compared against the first baseline. Missing, extra, reordered, or changed projections are failures; no sorting, deduplication, epsilon, or frame deletion is allowed. A failed process, raw check, cross-report contract, or workflow comparison withholds **all comparative replay timings for that scenario** while keeping the valid individual observations and error reason. Replay identity proves submitted-workload identity, not image equality.

The primary camera interpretations are Task CPU per input and trusted-input-to-first-submission latency. The three recorded latency series (first submission, first observed fence signal, and last observed fence signal) and their per-process median/p95/max remain available. Fence values are observed **application polling delays**, not direct measurements of actual GPU completion or visible response; in particular a roughly one-second signal observation cannot establish one second of visible map lag. All raw signals remain retained. Barrier pacing prevents throughput/FPS interpretation; the walking case is paused look/render work, not locomotion. CPU includes observation and automation overhead.

## Source interpretation

The published production changes concern lazy CPU visibility preparation in `src/PixelMap.Rendering/GpuSceneDepth.cs` and exact nearest-neighbor reuse of eligible opaque vector-terrain texels in `TerrainCache.cs` / `PixelMapRenderer.Terrain.cs`. They do not change the benchmark's requested duration, scene definitions, browser flags, or sampling selection. The new replay harness uses the public native wheel path and independently checks the complete submitted path. Native benchmark results and deterministic CPU-work reductions are separate evidence; browser performance must come from this new artifact.

## Local verification before the new run

The archived run was audited using its own explicit identities, preserved old baseline metadata, and the old `explore.cjs` hash. The frozen summary helpers are byte-identical between the two releases. All 30 timed reports and 14 profiles pass exact raw reconstruction. Both passing replay preflight processes also reconstruct successfully: 192 inputs and 204 submitted frames in total. The preflight establishes repeatability on the same baseline artifact, not candidate speed or pixel equivalence.

The retained `validation/selfcheck.json` records the compatibility and corruption checks. Large legacy-derived validation outputs are omitted. These checks are never additional benchmark observations for the new comparison.
