# WebGPU frame-throughput optimization

## Baseline and objective

This work starts from main commit `a9a284263da76a201114381467bf45aa4823a2d6`,
the merged Uno 7 / WebGPU migration. The objective is to remove rendering stalls
and increase interactive throughput while preserving the current image quality,
camera behavior, terrain/elevation, map styles and GPU resource ownership.

The existing migration browser captures establish correctness, not throughput.
Their instrumentation includes command tracing, presentation fences and captures.
CPU frame preparation/submission measurements must not be presented as GPU time.

## Confirmed work to investigate

- Incremental vector or imagery source changes currently invalidate the complete
  terrain-artwork cache. Scope invalidation to affected geographic patches,
  including painter order, wrapping and baked neighboring building shadows.
- Camera frames prepare visibility more than once and eagerly index sparse depth
  geometry. Measure this path independently before choosing a replacement.
- Terrain mip generation emits one texture copy per dirty physical layer and mip.
  Coalesce consecutive physical layers without changing bytes or dependencies.
- Sample-count/target changes rebuild pipelines that can be retained separately.
  Cache compatible variants and initialize shadow resources only when required.
- Distinguish static-camera retention, warm navigation, dynamic overlays and tile
  streaming. A fast idle frame is not evidence of fast movement or loading.

## Measurement and acceptance

1. Run one frozen native benchmark harness against baseline and candidate source
   trees. Use the production preparation path with `prepareLegacyOverlay: false`
   and native WebGPU overlays. Report distributions, allocations and useful work.
2. Build both browser packages in Release AOT and run the same frozen browser
   benchmark on the same runner/browser with controlled bundled sources. Keep
   screenshots and detailed validation probes outside timed windows.
3. Separate CPU preparation, CPU command recording/submission, input-to-submit,
   queue completion and sustained input delivery. Software-adapter timings do
   not establish physical-GPU FPS.
4. Check deterministic terrain bytes, retained source identity, camera endpoints,
   geometry/picking results, resource lifetimes and original browser scenarios.
   Preserve quality presets, native display density, shadows, bloom and sampling.
5. Record reproducible before/after results and limitations here as the work
   completes. Commit each independently reviewable optimization separately.

## Results

Implementation and new baseline/candidate measurements are in progress. No new
speedup result is claimed by this initial planning commit.
