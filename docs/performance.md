# Rendering performance

The original CPU optimization measurements compare the renderer with commit `31efd232a2e6e273fd0a56f23e8d50319d29f433`. The retained GPU terrain implementation builds on elevation commit `c238352dcb9df7fd2cfb8ba5e86fd20f38169597`. The CPU backend is the default, and the View menu enables the experimental GPU backend.

## Retained GPU terrain

Terrain relief and the Elevation layer use a WebGL2/OpenGL ES 3 geometry renderer through Uno 6.7's public `GLCanvasElement`. The existing adaptive quadtree selects at most 64 stitched patches with 17×17 vertices each. One instanced indexed draw projects all selected patches. A 256-layer RG32F texture array retains height and illumination samples, a 96-layer RGBA8 array retains pixel artwork, and a compact per-instance stream supplies rebased homogeneous transforms and array indices. Moving the camera updates roughly 3.5 KiB of instance data for 64 patches. Mesh and artwork layers upload when their revisions change.

Building meshes are retained per vector tile. Even/odd tessellation preserves roof holes, disconnected polygons and self-intersections. Model tables hold physical foundations, material, seed and height data. Vertex shaders apply the selected terrain foundation and camera projection. Fragment shaders reproduce façade lighting, windows, bands and roof edges. Terrain and buildings share a hardware depth attachment at art resolution. The final nearest-neighbor resolve preserves the pixel grid, including overhead Earth photograph blending.

Geometry preparation retains the same LOD selection and stitched height values as CPU rendering. A sparse screen-space triangle index evaluates only the depth samples requested by labels, navigation, effects and pointer input. Two reusable storage workspaces avoid repeated large triangle/bin allocations while retained frames keep their own depth ownership. The six vehicle modes and existing game simulation continue through their established APIs.

The Uno host captures an immutable art-resolution image and presents it with nearest sampling through the existing Skia canvas. Labels, routes, gates, vehicles and effects draw against the same retained depth frame at the canvas's native DPI. Pending camera changes keep the displayed image and its overlays synchronized. Overlay-only animation reuses the terrain image, and the GL visual is hidden while idle to prevent repeated readbacks. Initialization, render and readback failures return the control to CPU rendering. Unload releases managed frames and GPU resources with the appropriate context current.

Uno 6.7 performs a synchronous framebuffer readback into a `WriteableBitmap` before XAML composition. The browser host includes a scoped native resolver adapter that converts Uno's BGRA request to RGBA readback and channel conversion. Capturing at art resolution reduces the copied pixel count by 4× to 16×. The native capture benchmark includes GPU completion, readback, channel conversion, byte-buffer transfer, row reversal, immutable image creation, nearest composition and full-resolution overlays. Browser timings also include Uno's presentation work. Measurements in this environment use Mesa llvmpipe and Chromium SwiftShader software drivers.

### GPU verification

The native GLES suite exercises 48 combinations of terrain, building geometry, Earth imagery, altitude tint, exaggeration, projection, palette, viewport and pixel size. It also checks 408 terrain intersections, retained A→B→A frame activation, resizing, framebuffer state restoration, complete EGL context recreation and 2× physical-resolution composition. Across the current 48-case fixture, 25 images match exactly and the maximum mean RGB difference is 0.0188 on the 0–255 channel scale. The largest structural difference beyond the one-art-pixel comparison neighborhood is 18 display pixels in a small perspective-model case.

All six vehicle modes pass 24 terrain configurations with seven animation/heading/pause/seek states each. These 168 premultiplied overlays match CPU output byte-for-byte. Six further ridge-occlusion cases cover routes, gates and vehicles. GPU composition differs by at most one channel level in the overlay fixtures. Native high-DPI checks preserve nearest map pixels, scaled glyphs and independent read/draw framebuffer bindings.

The terrain browser suite checks actual depth-tested WebGL draw calls at art resolution and framebuffer presentation. It also toggles the backend off and on, switches layers, changes relief and exaggeration, picks terrain, restores shared views and exercises handset controls. The browser performance harness records actual art-resolution distributions and `readPixels` synchronization time.

```sh
LIBGL_ALWAYS_SOFTWARE=1 EGL_PLATFORM=surfaceless DOTNET_TieredCompilation=0 \
  dotnet run -c Release --project tests/PixelMap.Rendering.Tests -- \
  --gpu-terrain --gpu-benchmark --gpu-output=artifacts/gpu-terrain

node tests/browser/performance.cjs http://localhost:8000/ artifacts/terrain-cpu --motion --terrain
node tests/browser/performance.cjs http://localhost:8000/ artifacts/terrain-gpu --motion --terrain --gpu
```

The native benchmark uses the bundled 3×3 Garda vector, imagery and elevation cluster plus world parents. Each of nine layer/pixel-size conditions uses 16 warmup frames and 32 measured frames from a deterministic pan/orbit sequence at 1100×650. It reports CPU frame time, GPU scene preparation, command submission, capture/composition, upload bytes and retained GPU storage separately. Browser comparisons use matching AOT builds and must be read alongside the adaptive pixel-size distribution.

### Native terrain measurements

The following complete-frame measurements use .NET 10.0.14 on Debian 13, Mesa 25.0.7 llvmpipe, three Mesa workers, `DOTNET_TieredCompilation=0` and a fixed pixel size. The GPU column includes the art-resolution capture and Skia presentation stages described above. The final Uno UI compositor is measured separately in the browser.

| Layer | Pixel size | CPU median | GPU median | Reduction | CPU p95 | GPU p95 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Map | 2 | 24.19 ms | 16.44 ms | 32.1% | 26.91 ms | 21.72 ms |
| Earth | 2 | 24.42 ms | 16.84 ms | 31.0% | 27.33 ms | 20.76 ms |
| Elevation | 2 | 33.01 ms | 17.96 ms | 45.6% | 51.17 ms | 26.57 ms |
| Map | 3 | 19.19 ms | 11.49 ms | 40.1% | 22.09 ms | 14.31 ms |
| Earth | 3 | 17.69 ms | 16.65 ms | 5.9% | 22.50 ms | 34.81 ms |
| Elevation | 3 | 25.97 ms | 11.95 ms | 54.0% | 44.51 ms | 15.37 ms |
| Map | 4 | 13.85 ms | 9.04 ms | 34.7% | 20.13 ms | 11.51 ms |
| Earth | 4 | 12.38 ms | 9.16 ms | 26.1% | 14.85 ms | 11.51 ms |
| Elevation | 4 | 12.37 ms | 8.98 ms | 27.4% | 15.66 ms | 13.07 ms |

All nine medians improved in this batch. Earth at pixel size 3 has a higher p95. The driver runs shader work on CPU workers, and physical-GPU performance remains unmeasured. Scene preparation and GL command submission both consume the calling thread. GPU preparation alone therefore represents only part of main-thread cost. [Phase timings and storage measurements](terrain-gpu-benchmarks.csv)

The retained tile/model resources occupy 24.57 MiB in this scene. Average uploads are 571.67 KiB at pixel size 2, 9.15 KiB at size 3 and 5.45 KiB at size 4, including revisions caused by LOD selection during the camera sequence. Art readback transfers 698.24 KiB, 311.09 KiB and 175.10 KiB per frame respectively. The pixel-size-4 GPU completion/readback median is 0.49–0.50 ms across layers.

The measured renderer SHA-256 is `39d5a7c3ff19b3750118004c4e62fa5642a888aaf8359c9a6063b29494aefee6`. The harness SHA-256 is `8d9b25640ad0d65306294f115436b2d037b4b1f77867338868911ddd3b006674`, and the data assembly SHA-256 is `bd08e03df8977b0eebf87aa237eff7aee358a37874cd0966760e87879b6baee1`.

## CPU work

- Building models retain normalized edges, facade lighting and ring bounds after visibility checks first require them. Geometry eviction releases these retained data with the tile.
- Complete off-screen ring prisms are rejected within multipart building features. Enclosing rings and courtyard holes participate in the original even/odd roof fill. Each roof ring carries its vertical range so scanlines visit only intersecting rings.
- Orthographic projection reads compact source points. Edge preparation is reserved for visible walls.
- Perspective walls reuse top and bottom homogeneous vertices. Fully accepted clipping planes skip copies, and fully rejected planes terminate processing. Crossing planes preserve the original operation order and interpolation arithmetic.
- Per-tile camera and frustum values are retained for a frame. Perspective sorting moves compact index/depth pairs while model and transform data stay in place.
- Labels retain measured widths and native glyph blobs. Neighbor-shadow discovery runs when the geometry set changes.
- Route projection reuses camera constants. Smooth Earth-image clusters resolve their palette pair once while preserving all Bayer phases.

## Visual equivalence

The native comparison covers 232 cases and 16 moving frames per case, plus cold draws and cached replays. This produces 3,712 moving-frame hashes and 232 cold-frame hashes. Cases include overhead, isometric and three perspective pitches, Map and Earth, all three palettes, pixel sizes 2/3/4 and all six vehicle modes.

Synthetic cases exercise nonstandard vector-tile extents, disconnected footprints, concave roofs, courtyard holes, duplicate and empty rings, raised building parts, enclosing rings, all four screen edges, and near/far-plane crossings. The benchmark compares raw BGRA pixels and saves representative PNGs. Image encoding and hashing occur outside the timing samples.

Separate randomized geometry checks compared 16,000 tile transforms and 320,000 clipped polygons with the baseline. GPU fallback tests also compare 360 software-canvas combinations across mode toggles, cached redraws, palette changes and resizing.

## Native measurements

The benchmark uses a 1100×650 canvas, nine bundled Manhattan vector tiles containing 33,747 features, a downtown view at zoom 16.5 and a wider view at zoom 14.8. Each condition has three fresh-renderer draws, 16 warmup frames, 32 stationary samples and 32 moving samples. Camera movement follows a deterministic 16-frame sequence. Vehicle cases advance their route progress and effects history.

Measurements were collected on Ubuntu 24.04.3, x64, .NET 10.0.14, workstation GC and DejaVu Sans. Both processes used `DOTNET_TieredCompilation=0`. Benchmark processes ran sequentially while local builds and browser probes were paused. Managed allocation measurements cover the calling thread. Cold samples include rendering caches and exclude source decoding and renderer construction.

The final 50-condition sample reduced median moving-frame time by **43.10%** across conditions. Moving p95 improved in all 50 conditions. The representative fixed-2-pixel cases below include map, route, vehicle and gate drawing.

| Scene | Baseline p50 | Optimized p50 | Reduction | Baseline p95 | Optimized p95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Downtown overhead Map | 5.22 ms | 4.20 ms | 19.5% | 5.83 ms | 4.68 ms |
| Downtown isometric Map | 11.63 ms | 10.09 ms | 13.3% | 15.19 ms | 12.85 ms |
| Downtown perspective 65° Map | 53.88 ms | 30.41 ms | 43.6% | 74.57 ms | 40.45 ms |
| Downtown perspective 65° Earth | 59.79 ms | 30.97 ms | 48.2% | 121.07 ms | 43.53 ms |
| Wider overhead Map | 38.39 ms | 13.13 ms | 65.8% | 42.32 ms | 18.89 ms |
| Wider perspective 72° Earth | 93.58 ms | 67.35 ms | 28.0% | 100.35 ms | 78.49 ms |

Two moving medians were flat or slower in that batch. Downtown isometric Earth measured 14.2876→14.3112 ms, and train/Map measured 11.8499→12.2815 ms. Three alternating-order, paired train reruns with 64 moving samples each measured 13.07→10.86 ms, 13.77→12.00 ms and 13.46→9.57 ms, with lower p95 in all three pairs. The complete CSV retains both the batch result and the repeated measurements. [All 50 conditions](renderer-benchmarks.csv) · [Paired train measurements](renderer-train-repeats.csv).

Cold medians improved by a median of 25.25%, with 10 slower conditions. The median increase in managed allocation during a cold draw is 592.6 KiB. Changes range from 187.2 KiB less to 3,062.0 KiB more, with the maximum in the wider isometric Map scene. Cached stationary frames stay around 1 ms, with a median change of +0.39%. In the wider perspective 72° Earth case, moving managed allocation falls from 7,804 to 2,524 bytes per frame. These measurements preserve the original pixel resolution, terrain-cache budget and adaptive-resolution thresholds.

The baseline renderer SHA-256 is `0f48f5e21eea8563358624e8af75eaaad3d9bf431d2fa197ebeb7216c4491706`. The measured optimized renderer is `8ed09ca67b259b6c9588c8824afe0281a0ff41eeee95f3572c1abea72361fe10`. The input-resource SHA-256 is `9abdc9d0e30304804253732cafab36e85f072c766dcb00e3f8107d300c38d47a`.

To reproduce the native comparison, build the benchmark harness, preserve the baseline renderer and its dependencies, and run the same harness with each renderer. The existing eight-argument `Draw` entry point supports this comparison.

```sh
DOTNET_TieredCompilation=0 dotnet run -c Release --project tests/PixelMap.Rendering.Tests -- \
  --benchmark --output artifacts/rendering --samples 32 --warmup 16 --cold-samples 3

dotnet run -c Release --project tests/PixelMap.Rendering.Tests -- \
  --benchmark --visual-only --output artifacts/visual \
  --compare path/to/baseline/report.json
```

Use `--filter` to select case names. Reports include per-frame samples, phase timings, allocation counts, input and renderer hashes, runtime details and the font family.

## Flat-view Skia GPU backend

The optional backend uses `SKVertices`, `SKRuntimeEffect` and art-resolution GPU images. Model rasterization supplies an opaque-pixel/transparent-background layer and the same depth information used by navigation and picking. Labels and journey graphics continue through the map's Skia canvas.

Uno normally records `SKCanvasElement` drawing into an immutable picture. The control checks the public `CompositionCapabilities.AreEffectsFast()` capability and authorizes deferred GPU rendering. `SKImage.FromPicture` retains the art-sized terrain and composition commands. Skia generates their textures on the actual GPU playback context. Direct GPU canvases use reusable `SKSurface` render targets. The runtime effect samples the pixel grid through a retained two-triangle mesh.

Native OpenGL tests verified zero GL draws or pixel uploads while recording, followed by three GL draws and two framebuffer attachments during shape-only deferred playback, with zero terrain pixel uploads. They also checked retained-picture lifetime, resizing, context abandonment, allocation limits, CPU fallback and mode switching. These tests used Mesa llvmpipe OpenGL ES 3.2.

The GPU path preserves the visual design and features. CPU and GPU blending have small rounding differences. In the native overhead-Earth fixture, mean RGB error was 0.390 on a 0–255 scale and only four of 715,000 pixels differed by more than two channel levels. Isometric vector-map output was exact. Both GPU paths produced identical pixels, and all 35 feature-picking probes per scene agreed with CPU picking.

The app uses Uno 6.7.135 and SkiaSharp 3.119.2. The released SkiaSharp 4.154 binding also exposes `SKVertices` and runtime effects. Programmable `SKMesh` is tracked in the upstream API work item. [SkiaSharp mesh API tracking](https://github.com/mono/SkiaSharp/issues/3777) · [Skia runtime effects](https://skia.org/docs/user/sksl/).

## Browser measurements

The browser harness uses the deployed application and physical pan/orbit input. It records main-thread CPU time, long tasks, frame cadence, actual art-texture sizes and off-screen framebuffer work. Performance pairs use matching Release `InterpreterAndAOT` builds, a 1200×780 viewport, DPR 1 and the same Chromium version. External network requests are blocked so the bundled scene supplies identical data.

The control adapts between pixel sizes 2, 3 and 4 during interaction. Read the resolution histogram alongside browser CPU totals. The native benchmark provides fixed-resolution comparisons. Browser frame-cadence samples describe browser scheduling between updates.

The recorded production comparison uses baseline `31efd232a2e6e273fd0a56f23e8d50319d29f433` and optimized application `ab93cbc4d2ab1b571a0a9083f2c7e40207f1a68f`, with the menu-focus test helper from `2b2bcadfb9233bd8a9208f3bef745b5e763e27f9`. The optimized application's native WebAssembly integrity hash is `sha256-aQV1JvBTN7zB5A+97dWuvsaNc+OD4/8jOVLQCtHm5e4=`. Baseline, optimized CPU and opt-in GPU ran in that order, in separate browser processes during a quiet local measurement window. Each condition has one measured gesture of 80 physical pointer moves with a nominal 25 ms delay.

Chromium 156.0.8078.4 used SwiftShader on an AMD EPYC 9V74 host. The table records main-thread task CPU time through the browser's `threadTicks` metric. SwiftShader executes GPU work on separate worker threads. The CSV includes elapsed gesture time, long-task counts and durations, and actual texture-upload counts at each pixel size. [All browser measurements](browser-benchmarks.csv).

| Gesture | Previous CPU | Optimized CPU | Opt-in GPU |
| --- | ---: | ---: | ---: |
| Map isometric pan | 2,495.0 ms | 2,360.7 ms | 2,129.7 ms |
| Map isometric orbit | 2,588.1 ms | 2,922.6 ms | 2,037.5 ms |
| Map perspective 65° pan | 9,441.0 ms | 4,813.9 ms | 4,237.2 ms |
| Map perspective 65° orbit | 7,602.0 ms | 4,579.2 ms | 4,357.9 ms |
| Earth isometric pan | 2,688.5 ms | 2,383.4 ms | 2,651.8 ms |
| Earth isometric orbit | 2,399.4 ms | 2,151.9 ms | 2,139.4 ms |
| Earth perspective 65° pan | 11,181.7 ms | 4,642.9 ms | 4,317.4 ms |
| Earth perspective 65° orbit | 7,799.4 ms | 4,501.5 ms | 4,903.1 ms |

The default Map and Earth perspective pans use closely comparable pixel-size distributions across releases. Their task CPU reductions are 49.0% and 58.5%. The optimized Map isometric orbit instead keeps pixel size 2 for all 80 uploads, while the baseline uses 8, 34 and 41 uploads at pixel sizes 2, 3 and 4. Its increased CPU total accompanies more detailed moving frames. The opt-in GPU orbit also keeps pixel size 2 throughout.

GPU-mode Earth isometric pan uses 40/16/24 uploads at pixel sizes 2/3/4, compared with 2/1/77 for optimized CPU. Its elapsed gesture time rises from 5,669.9 ms to 7,440.0 ms in this software-driver environment. Earth perspective orbit also has higher main-thread CPU time in GPU mode. The optional backend therefore exposes a workload-dependent tradeoff between CPU work, composition cost and adaptive detail. Physical-GPU performance remains unmeasured.

The default AOT renderer matches all 22 baseline map crops exactly. The first full-canvas comparison matches 20 of 22 canvases. Two small UI ellipses differ by 50 and 48 pixels. Repeating both cases on both builds produces exact original-baseline canvases, identifying run-to-run UI raster variation. The GPU matrix passes backend-status and art-sized framebuffer checks in every case, with zero runtime exceptions. Seven GPU map crops match the CPU baseline exactly. The maximum GPU difference is 1,383 of 297,000 crop pixels, or 0.466%, in overhead Earth. GPU rasterization and blending can change edge pixels.

The browser helper selects the visible GPU menu command by focus and real Enter input. This fixes an intermittent initial-menu-focus error in the first CI attempt. Twelve fresh train launches, a handset case and an Earth-layer case passed the corrected helper. The workflow runs the map and journey suites in both backends before deploying the Pages artifact.

```sh
node tests/browser/performance.cjs http://localhost:8000/ artifacts/browser-visual --visual
node tests/browser/performance.cjs --compare baseline candidate
node tests/browser/performance.cjs http://localhost:8000/ artifacts/browser-motion --motion
node tests/browser/performance.cjs http://localhost:8000/ artifacts/browser-gpu --motion --gpu
PIXELMAP_GPU=1 node tests/browser/smoke.cjs http://localhost:8000/
PIXELMAP_GPU=1 node tests/browser/journeys.cjs http://localhost:8000/
```

Visual reports include screenshots, raw map-pixel hashes and accessibility snapshots. Motion reports retain individual samples and runtime-asset hashes. GPU runs require the public backend status to confirm activation and record framebuffer draws at the map's art resolution.
