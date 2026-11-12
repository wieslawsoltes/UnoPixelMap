# Paired WebGPU performance protocol

`webgpu-performance.cjs` compares the current PR base and candidate **Release AOT**
packages with one frozen copy of the candidate harness, one browser version and
one runner. `.github/workflows/webgpu-performance.yml` builds both packages in
parallel, checks package integrity, then executes baseline/candidate/candidate/
baseline (ABBA) for each scene. Application compilation does not overlap timing.

The default scenes are Pixel pan, Normal pan, Normal orbit and paused Normal
first-person walking-camera look. Each uses native CDP wheel input through the
real application adapter, a closed eight-step warm-up and the same measured
trajectory. Quality is High, logical viewport is 1200 × 800 and device scale is 1.
The actual map extent, art grid and physical scene extent are asserted separately.

Every non-local request is blocked, including elevation, vector tiles, imagery,
routing and geocoding. Service workers are disabled. The scene therefore uses
bundled sources. Blocked hosts and counts are retained. This is a repeatable
renderer workload, not a network-streaming or whole-world coverage benchmark.

## Measurements and boundaries

| Field | Meaning |
|---|---|
| `renderCpuMilliseconds` | Sum of managed render callback elapsed time for frames belonging to an input |
| `submissionCpuMilliseconds` | CPU time inside the WebGPU renderer, including uploads, recording and `QueueSubmit` |
| `preparationMilliseconds`, `overlayMilliseconds` | Candidate phase diagnostics; unavailable baseline phases remain null/absent |
| `inputToFirstSubmitMilliseconds` | Trusted native wheel event to first actual map queue submission |
| `inputToFirstQueueCompletionMilliseconds` | Event to the first observed `onSubmittedWorkDone` callback |
| `inputToLastQueueCompletionMilliseconds` | Event to completion callback for the last map submission attributed to the step |
| `mainThreadTaskMilliseconds` | CDP thread-tick task duration over the full replay window, including browser/automation/probe work |
| `bufferUploadBytes`, `textureUploadBytes` | Independently observed production map payload; texture row padding is excluded |
| `submittedMapFramesPerSecond` | Submission rate during the separate held-key run, not presentation FPS |
| `rafIntervalsMilliseconds` | Browser scheduling cadence, not completed map frames |
| `startQueuePrefixMilliseconds`, `endQueueDrainMilliseconds` | Completion-callback latency for work queued at the two throughput boundaries, exposing initial backlog and final drain |

Queue completion is **not a GPU timestamp**: it includes preceding queued work
and callback scheduling. Each serialized input waits for its map submissions to
complete and for a quiet boundary. This measures latency, not maximum throughput.
The benchmark does not add per-frame queue fences during its separate six-second
held-key walking phase; only the start and end boundaries observe completion
without blocking submission. That phase records before/after physical telemetry;
faster runs can travel farther, so it is not a pixel-equivalent workload.

The timing probe wraps device discovery, map command creation/submission,
resource upload entry points and the existing diagnostic import. It does not
wrap draws, passes, bindings or every resource dependency. The full correctness
probe, screenshots, image decoding and share-link queries run outside timing.
The common lightweight probe still has overhead; CPU measurements include it.
No GPU image readback is introduced into application rendering.

## Scene and visual identity

Cross-artifact comparison checks the exact native input trace, submitted camera
projection and terrain transforms before and after, map bounds, style, quality,
density, scene resolution, pixel size, terrain patch count, building triangle
count and layer state. Map cases also compare public v5 physical camera links;
the paused first-person case compares physical HUD telemetry. Opening Share exits
free roam, so that panel is never used during the walking case. A
mismatch fails comparison instead of treating different work as a speedup.
Measured steps are classified by whether they uploaded texture data or allocated
GPU buffers/textures; a run with late artwork, height or geometry work is not
described as entirely warm.

Projection snapshots use the public quality menu to switch Balanced → High,
outside timing, which uploads the unchanged camera. The observer copies only
the projection/view/ground/lens/world words from each 512-byte scene uniform,
the physical eye, and terrain instance projection/world words. Lighting, shadow
metadata and texture/height cache indices are excluded. It validates the frozen
buffer layouts, requires complete captures and prevents capture from overlapping
a measurement window. Each capture ends at High quality after queue completion.
This introduces no payload copying during the timed trace and no GPU readback.

Original map-region PNGs are captured after queue completion, outside timing.
Every A/B pair records changed-pixel fraction, fraction changing by more than two
channel values, mean absolute RGB error, RMS error and maximum error. The gate is
at most 1% of pixels changing by more than 2/255 and mean error at most 0.25/255.
That tolerance permits small raster/shader-rounding edges; it is not reported as
pixel identity. Original images remain available for visual review.

The CI driver is headed Chromium with SwiftShader Vulkan under Xvfb. Its results
do not establish physical-GPU FPS, hardware speedup, display latency or sustained
mobile thermal behavior. Adapter details, browser version, CPU model, package
source commits and harness hashes are recorded in `report.json`.

## Running and troubleshooting

After publishing both artifacts, serve their `wwwroot` directories as `/baseline/`
and `/candidate/` from one local origin. Install the pinned `tests/browser`
dependencies and Chromium runtime described in the browser README, then run:

```sh
xvfb-run -a -s '-screen 0 1600x1200x24' node tests/browser/webgpu-performance.cjs \
  http://127.0.0.1:8100/baseline/ http://127.0.0.1:8100/candidate/ artifacts/webgpu-performance
```

`PERF_CASES` selects comma-separated case names. `PERF_STEPS_PER_SEGMENT` controls
the bounded trajectory length. `PERF_REPETITIONS=1` selects AB for a quick
diagnostic; the default two repetitions provide ABBA. `PERF_MOVING_MS=0` skips
the separate moving workload. `PERF_BASELINE_REVISION` and
`PERF_CANDIDATE_REVISION` record the measured application commits. The CI workflow
sets them from the actual checkout that produced each published package.

`node tests/browser/webgpu-performance-checks.cjs` exercises the observer without
a browser. It checks native-input identity, arbitrary command iterables, upload
units, queue completion, absence of throughput fences, missing phase handling,
invalid measurements, untimed projection-capture ownership and
visual-difference arithmetic. No browser is launched by that contract check.
