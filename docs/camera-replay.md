# Historical GL submitted-camera replay

This document describes the retained Uno 6 / GL benchmark protocol. Its WebGL
observer and commands apply only to matching historical application artifacts;
they cannot measure the Uno 7 WebGPU renderer. The former Pages play-benchmark
job has been removed. Current shader, presentation and input validation uses the
[WebGPU browser suite](../tests/browser/README.md), with its evidence explained
in the [migration document](uno7-webgpu-migration.md).

The held-key play benchmark measures the application as it runs, including its
simulation, input handling, camera, UI and rendering. Its trajectories depend on
how much simulation time advances during the measured window. The controlled
camera replay provides a second comparison: both application artifacts must
submit exactly the same ordered projection data before their timings can be
compared.

The replay uses the existing native browser input adapter. It does not add a
production export, change simulation time, or dispatch synthetic DOM events.
The same observer therefore works with previously published application builds.

## Workloads

| Case | Input sequence | Scope |
| --- | --- | --- |
| `map-pan-relief` | Four directions, eight wheel inputs per direction | Immediate camera pans over bundled Manhattan relief |
| `map-orbit-relief` | Orbit and tilt, eight inputs per segment | Immediate bearing and pitch changes over the same scene |
| `walk-look-firstperson` | Yaw and pitch, eight inputs per segment | Paused first-person look with unchanged physical telemetry |

Each case has 32 ordered inputs at integral CSS coordinates. External map
providers are blocked and the bundled data supplies the scene. Viewport,
resolution, browser, CPU identity, affinity-visible parallelism, browser flags
and harness hashes are recorded. The existing moving play cases remain separate.
The historical browser build pinned SDK 10.0.403 and its .NET/WASM 10.0.14 runtime to match the
retained baseline. Refresh the paired baseline deliberately when changing that
toolchain; a runtime upgrade is a separate variable from renderer source changes.

## What constitutes the same camera path

Every step must receive exactly one trusted native wheel event with the specified
position, deltas and modifier bits. It retains every compositor-context map
resolve in submission order and observes each resolve's own fence becoming
signaled. The public tile refresh timer can reorder resident tiles after a camera
change, legitimately producing another frame for that input. The observer keeps
that frame; it does not sort, merge or discard submissions.

The step completes after every observed fence has signaled and no resolve has
occurred for 250 ms, followed by two animation frames and a repeated completion
check. This quiet period exceeds the application's 60 ms tile refresh interval.
The wait is limited to 20 seconds; the contract limits each step to 32 submissions.
Raw records retain the last-resolve timestamp and completed quiet interval.
Missing, extra or reordered frames between compared runs fail exact identity,
including timing-dependent coalescing differences. Such runs are not comparable.

The observer tracks the values passed to the existing WebGL calls, including
cached uniforms whose values are unchanged between draws. It records all map
projection uniforms, artwork dimensions, draw counts and instance counts. For
each terrain instance it retains the 12 submitted transform floats. The two
trailing cache-layer indices identify resource slots and are excluded from pose
identity. Contexts and programs have independent tracked state.

The schema 2 comparator verifies the protocol, input hashes, projection hashes,
complete per-input submission sequences and runner/harness identity. Every
submitted projection value must compare exactly; there is no numeric tolerance.
It also rejects empty or incomplete reports. Identical projection data establishes a
controlled workload, not pixel equivalence by itself. The rendering oracle and
direct-versus-detached image comparisons provide the separate pixel checks.

## Timing interpretation

Per-step latency starts when the trusted wheel event is observed. Three values
are retained: time to the first map submission, time to that first frame's fence
signal, and time to the final fence signal across the complete settled sequence.
The report names these `inputToFirstSubmission`, `inputToFirstFenceSignal` and
`inputToSettledFenceSignal`. Quiet and animation-frame barriers precede the next
input and are outside its latency interval. The raw record still includes them.

Fence-signal latency includes the application's polling cadence and software-GPU
execution when SwiftShader is used. It is not a hardware GPU timer or physical
display-presentation measurement. Main-thread task CPU includes observer and
automation overhead. Barrier-paced submissions do not measure maximum throughput
or simulation FPS. The paused walking case does not measure locomotion.

## Running and comparing

First run the observer and contract checks:

```sh
node tests/browser/camera-replay-checks.cjs
```

Serve the exact unpacked Pages artifacts at separate URLs, then execute fresh
browser processes with the same browser and CPU affinity:

```sh
CHROMIUM_PATH=/path/to/chrome-headless-shell \
PIXELMAP_REVISION=<baseline-source-sha> \
CAMERA_REPLAY_CASES=map-pan-relief \
node tests/browser/camera-replay.cjs \
  http://127.0.0.1:8100/baseline/UnoPixelMap/ /tmp/replay-baseline

CHROMIUM_PATH=/path/to/chrome-headless-shell \
PIXELMAP_REVISION=<candidate-source-sha> \
CAMERA_REPLAY_CASES=map-pan-relief \
node tests/browser/camera-replay.cjs \
  http://127.0.0.1:8100/candidate/UnoPixelMap/ /tmp/replay-candidate

node tests/browser/camera-replay.cjs --compare \
  /tmp/replay-baseline/report.json /tmp/replay-candidate/report.json \
  /tmp/replay-comparison.json
```

Omit `CAMERA_REPLAY_CASES` to run all three historical cases. The retired Pages
play-benchmark job ran each case in baseline/candidate/candidate/baseline order
in fresh processes. It compared all three later captures with the first baseline's exact sequence,
checking both cross-artifact identity and repeatability. The same-artifact
preflight runs two independent captures before using a changed observer for
performance conclusions.

Each case retains the ordered input trace, per-step projection data and hashes,
raw WebGL/performance probes, summary metrics and before/after screenshots.
Failure diagnostics retain completed steps and pending probe state so a missing
input, draw, or fence can be distinguished from a projection mismatch.
