# Frozen native WebGPU frame benchmark

This harness measures the production `PrepareGpuFrame`, `ActivateGpuFrame`,
`PrepareWebGpuOverlay` and sparse terrain-picking paths without creating a graphics
device. Nine deterministic camera workloads cover pan, first-person walking and
orbit in Pixel, Normal and Roblox styles over nine bundled Manhattan tiles. Eight
additional workloads isolate source-streaming invalidation in Pixel and Normal.

`compare.py` copies **one identical set of harness sources** into two temporary
build directories and compiles it against the requested baseline and candidate.
Both builds finish before any timed workload begins. It then executes baseline,
candidate, candidate, baseline (ABBA), recording raw samples and comparing every
frame's exact geometry/projection, terrain texels, overlay buffers, font atlas and
sparse-picking result. The comparison fails if identity changes. The source
streaming tests separately compare every presented terrain-texture hash while
counting actual rebaked cache objects and their base-level byte payload.

```sh
python3 tests/PixelMap.Rendering.Performance/compare.py \
  /absolute/baseline-checkout /absolute/candidate-checkout artifacts/native-performance
```

The output directory must not already exist. `--dotnet` chooses the SDK executable,
`--font` chooses an explicit readable TTF/OTF file, and `--samples` changes the
default 32 measured frames per workload and run. Warm-up covers all 32 camera
poses. Tiered compilation is disabled for both processes so mid-replay JIT tier
changes cannot masquerade as an optimization; this native comparison does not
model browser AOT execution. Run without competing builds or other benchmarks.

The same frozen code uses the renderer's existing test friend assembly name,
`PixelMap.Rendering.Tests`, to inspect immutable frame payloads on either revision.
It does not require adding diagnostic code to the baseline renderer. Font hashing,
scene hashing, fixture loading and report generation are outside the timed regions.

`comparison.json` aggregates medians and interpolated p95 values from both runs
per variant, retains separate preparation, overlay and picking timings, and reports
managed allocations. Linux `CLOCK_THREAD_CPUTIME_ID` adds actual calling-thread CPU
time; it is explicitly null on other platforms or if unavailable. Process/renderer
identity, the exact harness-source hashes, explicit font hash, CPU and affinity are
stored beside the raw results. Clean committed application source is required by
default, and checkout identity is checked again after both builds. `--allow-dirty`
permits explicitly exploratory runs whose uncommitted paths are recorded; these
runs should not be used as published evidence.

These measurements establish CPU cost and exact prepared output for the listed
workloads. They do not measure GPU rasterization, browser scheduling, display FPS,
network decoding or sustained device thermals. The paired Release AOT workflow in
`tests/browser/WEBGPU_PERFORMANCE.md` measures browser input/render/queue latency
separately and retains original screenshots for visual comparison.
