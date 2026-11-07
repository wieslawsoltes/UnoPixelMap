# CPU window-filter measurement

This standalone executable measures the complete CPU `PixelMapRenderer.Draw` call, including scene preparation, building rasterization and artwork presentation into an RGBA8888 Skia bitmap. It also includes `SKCanvas.Flush`. It uses public APIs so the same compiled executable can run with the original `ab765d2` renderer, the initial filtered `fa2cfa7` renderer, and its optimized `e086f47` follow-up. It does not isolate the filter branch.

The wall geometry, camera distances and bearings match `PixelWindowChecks`: nearby (64 m, 0°), distant (320 m, 0°), grazing (160 m, 75°), and far (720 m, 20°). These cases use pixel size 4. A fifth case renders the nearby wall at native resolution, pixel size 1. All use the Night palette at 768 × 576 and move through 16 lateral camera phases. Every draw changes the camera, including cycle boundaries, to prevent the retained-picture cache from skipping rasterization. There are no labels, photographs or elevation tiles in this fixture.

Fixture creation, image hashes, PNG capture and sample-record allocation are outside the measured interval. Each process renders image controls, then performs 32 warmups and 64 measured draws per scene. Four sequential processes run baseline / candidate / candidate / baseline. The recorded environment uses .NET 10.0.14, CPU affinity 0, `DOTNET_TieredCompilation=0`, `DOTNET_PROCESSOR_COUNT=2`, and `LP_NUM_THREADS=2`. The processor is an AMD EPYC 9V74. Each variant contributes 128 timed draws per scene; medians use the upper-middle sorted sample, and p95 uses `ceil(0.95*N)-1`.

## Final recorded result at e086f47

| Scene | Pixel size | Original CPU median | e086f47 CPU median | Added time | Change |
|---|---:|---:|---:|---:|---:|
| Nearby | 4 | 2.723 ms | 2.732 ms | 0.008 ms | +0.3% |
| Distant | 4 | 2.740 ms | 4.188 ms | 1.448 ms | +52.9% |
| Grazing | 4 | 2.866 ms | 3.544 ms | 0.678 ms | +23.7% |
| Far | 4 | 2.692 ms | 4.735 ms | 2.043 ms | +75.9% |
| Nearby, native resolution | 1 | 5.380 ms | 5.455 ms | 0.075 ms | +1.4% |

The follow-up proves when an entire triangle can use point sampling, prepares palette vectors once, prepares the footprint once for constant reciprocal W, and skips a redundant large-footprint coordinate calculation. All 128 CPU and 128 GPU quality-frame hashes remain identical to the previous filtered implementation. All 32 complete nearby controls remain identical to the original renderer.

Nearby cost is close to the original renderer; the differences are small relative to block variation. Minified quality still adds 0.678–2.043 ms in these wall-heavy CPU scenes. The far case's p95 rises from 2.911 to 5.447 ms. Thread CPU tracks wall time, and managed allocation remains equal. [Final samples](../pixel-window-cpu-cost-e086f47.csv) · [Final provenance and image controls](../pixel-window-cpu-provenance-e086f47.json)

The initial and final comparisons below are separate ABBA sequences with their own original-renderer controls. Their within-sequence comparisons are the reported results; direct subtraction between the two candidate medians would mix different runs.

## Initial recorded result at fa2cfa7

| Scene | Pixel size | Original CPU median | fa2cfa7 CPU median | Added time | Change |
|---|---:|---:|---:|---:|---:|
| Nearby | 4 | 2.828 ms | 2.867 ms | 0.039 ms | +1.4% |
| Distant | 4 | 2.861 ms | 4.664 ms | 1.803 ms | +63.0% |
| Grazing | 4 | 2.966 ms | 3.853 ms | 0.887 ms | +29.9% |
| Far | 4 | 2.774 ms | 5.260 ms | 2.486 ms | +89.6% |
| Nearby, native resolution | 1 | 5.432 ms | 7.199 ms | 1.767 ms | +32.5% |

Thread CPU measurements closely track wall time, and managed allocations are identical between variants. The nearby pixel-size-4 difference is small compared with block-to-block variation. Both candidate blocks reproduce the larger minified and native-resolution costs. These are wall-heavy native CPU stress results, not application frame-rate measurements or browser timings.

All 16 nearby pixel-size-4 frames and all 16 nearby native-resolution frames are byte-identical across the two renderers. The 48 minified frames differ as expected. All 128 pixel-size-4 artwork hashes across both variants match the earlier quality captures. Repeated blocks match their own image controls, and the final timed frame matches the corresponding untimed control. The source, dependency, binary and per-frame identities are recorded in the [provenance](../pixel-window-cpu-provenance-fa2cfa7.json); [all 1,280 timing samples](../pixel-window-cpu-cost-fa2cfa7.csv) include thread CPU time and managed allocations.

## Reproduction

Build the rendering test project at each source revision in separate working/output directories and preserve both complete Release output directories. Set `pixel_baseline_bin` and `pixel_candidate_bin` to their absolute paths. The original renderer SHA-256 is `4fcc7c7dc4fa75f168039b2b2271748f1462527c33ba46b0190410c7dc851703`. The initial fa2cfa7 renderer is `c102c33e40d38afc94e8d5182524c6f1c7053f9c4374f01de89ad1b310eeb213`; the final e086f47 source snapshot is `5726392b23388fc3c34d97704afcba9e14dc0b8ff7cd412fe155990b3f59dba8`. The Data, Routing, managed Skia and native Skia binaries are identical between the recorded variants.

From the repository root, compile the harness once against the original public API:

```sh
dotnet build docs/verification/pixel-window-cpu-harness/PixelWindowCpu.csproj \
  -c Release -m:1 -nr:false -p:UseSharedCompilation=false \
  -p:RendererDirectory="$pixel_baseline_bin" -o /tmp/pixel-window-cpu/build

cp -a /tmp/pixel-window-cpu/build /tmp/pixel-window-cpu/baseline
cp -a /tmp/pixel-window-cpu/build /tmp/pixel-window-cpu/candidate
cp "$pixel_candidate_bin/PixelMap.Rendering.dll" /tmp/pixel-window-cpu/candidate/
```

Run the following command four times, setting `pixel_block` to `0`, `1`, `2`, `3` and `pixel_variant` to `baseline`, `candidate`, `candidate`, `baseline`, respectively. Keep other native workloads idle during the sequence.

```sh
DOTNET_TieredCompilation=0 DOTNET_PROCESSOR_COUNT=2 LP_NUM_THREADS=2 \
  taskset -c 0 dotnet "/tmp/pixel-window-cpu/$pixel_variant/PixelWindowCpu.dll" \
  --variant="$pixel_variant" --block="$pixel_block" --warmups=32 --samples=64 \
  --output="/tmp/pixel-window-cpu/block-$pixel_block-$pixel_variant"
```

Each output directory contains `cpu-window.json`, every timed sample, full-canvas/artwork hashes for all moving phases, and the first frame of each scene as PNG. Verify that the renderer/dependency identities and camera hashes match the intended pair before comparing timings.
