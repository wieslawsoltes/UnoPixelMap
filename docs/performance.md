# Rendering performance

The original CPU optimization measurements compare the renderer with commit `31efd232a2e6e273fd0a56f23e8d50319d29f433`. The retained GPU terrain implementation builds on elevation commit `c238352dcb9df7fd2cfb8ba5e86fd20f38169597`. GPU rendering and Pixel art are enabled by default. The View menu can switch to the CPU backend.

## Play-mode rendering

The play-mode changes landed in `6757c313a5645e610e7ed7c9d1f8b04dc7788a03` and `3777867da454f21ef264c72adb1a65b99ddca95a`. The comparison below uses the original application at `11188d0c265c9688ca8a169200e2f1573ec9fbeb` and the final implementation at `3777867`.

- Physical cameras select building prisms against conservative homogeneous frustum planes. Visible wall/cap indices and roof-edge instances compact in their original primitive order. Source vertices stay resident, and unchanged model visibility reuses the compact arrays and GPU uploads. The scene-depth index shares whole-model rejection. Vertex shaders exit immediately for invisible caps and roof edges.
- Browser pixel transfers copy only after a fence signals. The two-slot queue retains bounded backpressure, canceled transfers drain after completion, and pending camera changes coalesce into the latest request. The previous elapsed-time copy policy could block `getBufferSubData` for seconds in the software-GPU play fixture.
- A stationary camera reuses identical foreground artwork. Stopped standalone vehicles advance their effects clocks through `TryAdvanceStationaryVehicle` while retaining their picture. First drawing, changed poses, route/gate overlays, mode transitions, clock discontinuities and resumed movement use the ordinary draw path. This preserves the effects timeline when movement resumes after a long stop.
- Roof endpoints contain four floats each, reducing each edge instance from 96 to 32 bytes. CPU scene-depth construction caches clipping codes after face rejection and reuses them for indexed triangles. Projected vertices and model visibility reuse the existing two-workspace pool. The additional retained scratch is bounded at 131,072 projected vertices and 16,384 visibility entries per accepted workspace.
- HUD text and accessible names update on changed values, structural controls update on session transitions, and hidden map readouts refresh when returning from Play. Visible telemetry, scale information and status changes retain their live update paths.

The native regression compares selected streams with complete meshes on 108 identical physical-camera frames across Pixel, Normal, Minecraft and Roblox styles and Map, Earth and Elevation layers. Exact RGBA, terrain intersections, overlay depth, held-stream restoration and unchanged-visibility upload reuse pass. The existing 48-case GLES suite and HiDPI/context-recreation checks also pass. Another 48 cases compare stationary vehicle pixels and complete effects state across all six travel modes, four styles and flat/relief terrain, including long stops and resumed movement. The final renderer also preserves all 232 pinned Pixel cases against `c238352`, including 3,712 moving-frame hashes and 232 cold-frame hashes.

### Browser play measurements

The browser comparison uses the exact GitHub Actions Release `InterpreterAndAOT` artifacts for `11188d0` and `3777867`, Chromium 156.0.8078.4, SwiftShader with an in-process GPU, CPU affinity 0–2, a 1200×780 viewport and DPR 1. External providers are blocked, and the bundled map data supplies both scenes. Each scenario starts in a new browser context, warms for two seconds and requests an eight-second measurement. The actual moving windows span 8,044.6–9,133.4 ms originally and 8,001.9–8,244.9 ms after optimization. Rates use each measured elapsed interval. CPU profiles run in separate passes.

All moving submissions use actual 300×166 art buffers at pixel size 4 in both builds. Completed pixel-pack copies count completed GPU artwork. `requestAnimationFrame` counts browser scheduling callbacks. Final display presentation is outside these probes. Held native keys exercise the same input workload, while the faster simulation travels farther and reaches different final poses. The native comparison below uses identical camera sequences to verify the rendered inputs separately.

| Moving scenario | Original completed art copies/s | Optimized completed art copies/s | Throughput gain | Original rAF callbacks/s | Optimized rAF callbacks/s |
| --- | ---: | ---: | ---: | ---: | ---: |
| Walk, first person | 0.691 | 4.584 | 6.64× | 4.03 | 46.22 |
| Walk, chase | 0.564 | 7.520 | 13.34× | 3.72 | 53.97 |
| Car, chase | 0.657 | 4.444 | 6.76× | 4.27 | 43.70 |
| Car, first person | 0.557 | 3.999 | 7.17× | 2.56 | 41.86 |
| Free flight | 1.243 | 4.249 | 3.42× | 8.70 | 53.86 |

| Moving scenario | Original copy-call p95 | Optimized copy-call p95 | Original rAF interval p95 | Optimized rAF interval p95 |
| --- | ---: | ---: | ---: | ---: |
| Walk, first person | 2,142.4 ms | 5.7 ms | 1,350.0 ms | 66.6 ms |
| Walk, chase | 2,560.6 ms | 3.4 ms | 1,699.9 ms | 16.8 ms |
| Car, chase | 2,538.5 ms | 3.4 ms | 1,016.7 ms | 16.8 ms |
| Car, first person | 2,320.0 ms | 3.0 ms | 1,966.7 ms | 83.3 ms |
| Free flight | 1,324.9 ms | 7.1 ms | 683.3 ms | 16.8 ms |

Across the five moving windows, cumulative `getBufferSubData` call time decreases from 38,576.3 to 261.4 ms, a 99.3% reduction. All 201 completed final copies follow an observed signaled fence. Outstanding transfers stay bounded at two, with zero overwrites, abandoned transfers or positive-timeout polls. Main-thread task occupancy rises during movement as the application completes more artwork and advances the simulation farther. The `threadTicks` metric excludes blocked time and software-GPU worker CPU.

| Stationary play scenario | Original rAF callbacks/s | Optimized rAF callbacks/s | Original main-thread task CPU | Optimized main-thread task CPU | Original foreground GL draws | Optimized foreground GL draws |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Walk, first person | 23.35 | 60.20 | 16.92% | 6.32% | 7,421 | 0 |
| Car, chase | 24.33 | 59.98 | 35.22% | 6.20% | 26,688 | 0 |
| Free flight | 22.74 | 59.98 | 15.64% | 5.63% | 8,188 | 0 |

All three final stationary-play cases record zero foreground draws, depth draws, transfers and art copies. Two additional stationary-car runs record 60.10 callbacks/s, 7.48% and 8.83% task CPU, zero drawing/transfers/copies, and identical before/after screenshots. The default and relief map-idle cases remain around 60 callbacks/s with 1.47–1.92% task CPU and zero drawing/transfers.

Seven of ten initial full screenshots match the original directly. The two car startup differences match the original's same-pose adaptive 3-pixel and 4-pixel variants exactly when cross-compared. The initial idle-relief screenshot differs at pixel size 2 in both builds and settles to an exact match. All five final stationary screenshots match the original. The CSV's `boot_inferred_final_pixel_size` comes from capped startup history and can differ from retained startup artwork. Its `readback_dimensions` and `readback_pixel_sizes` describe actual timed submissions. The CSV records input-window poses. Moving screenshots are captured after key release and may include additional vehicle coasting.

These are individual software-GPU timing samples. The final run still has maximum scheduling gaps of 150–600 ms across moving scenarios. Car-chase and free-flight maxima are higher than in the first optimization run, while all five final maxima improve over the original. The presentation path still copies completed WebGL artwork into Skia storage. Physical-GPU and Safari/iOS timing remain unmeasured. The CSV includes the original, first optimization, final implementation and both stationary-car repeats, with actual durations, main-thread CPU measurements, completed artwork copies, queue observations, poses and harness hashes. [Complete browser measurements](play-browser-benchmarks.csv)

The final Pages artifact is `13364936410` from workflow `41913417612`. Its ZIP SHA-256 is `4659e43cf10cbb760999b383bb42ab3e2fe835c3e5a9dec4e8dd4d099ce98c7c`. The final native WebAssembly SHA-256 is `774a4f3b2c9028806ebabaad4b98f787e54bd6fe89b4fa6715d420eea164eac7`. Artifact verification checks all 69 boot resources and the signaled-only readback policy. After deployment, all 74 checked live files, including the entry point, boot resources and input adapter, match the measured artifact by SHA-256. [Build, browser verification and Pages deployment](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/41913417612)

```sh
npm ci --prefix tests/browser
(cd tests/browser && npx playwright install chromium)
PIXELMAP_REVISION=3777867da454f21ef264c72adb1a65b99ddca95a \
  node tests/browser/play-performance.cjs \
  https://wieslawsoltes.github.io/UnoPixelMap/ artifacts/play-performance
```

### Moving native frames

The paired native comparison renders nine bundled Manhattan vector tiles containing 33,747 features, world-DEM relief at exaggeration 1, Day/Pixel style and a 1200×680 logical viewport at pixel size 4. Each eye-height case warms four frames and measures sixteen deterministic moving frames. The total includes scene preparation, GL submission and `glFinish` completion. Pixel readback, hashing, navigation overlays and Uno/browser presentation are outside that timing. Each reported median uses the upper middle of the sixteen sorted samples. Phase medians are calculated independently.

| Eye height | Original total median | Optimized total median | Speedup | Original preparation median | Optimized preparation median |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1.7 m | 92.55 ms | 37.92 ms | 2.44× | 19.26 ms | 17.19 ms |
| 300 m | 112.94 ms | 75.50 ms | 1.50× | 36.13 ms | 32.99 ms |

| Eye height | Original mean managed allocation per frame | Optimized mean managed allocation per frame | Reduction |
| --- | ---: | ---: | ---: |
| 1.7 m | 2,424,709.5 bytes | 484,912.5 bytes | 80.0% |
| 300 m | 2,645,882.5 bytes | 1,229,196.5 bytes | 53.5% |

| First measured frame | Original submitted indices | Optimized submitted indices | Original roof-edge instances | Optimized roof-edge instances |
| --- | ---: | ---: | ---: | ---: |
| 1.7 m | 1,445,862 | 196,191 | 141,273 | 0 |
| 300 m | 1,445,862 | 215,955 | 141,273 | 26,015 |

All 32 moving RGBA hashes match the original renderer exactly. In the first measured frame, GL draw calls fall from 21 to 7 at street level and from 21 to 11 in flight. Changing model visibility uploads new compact streams. Total uploads across the sixteen moving frames increase from 3,824,216 to 7,132,412 bytes while walking and from 7,115,648 to 18,597,924 bytes while flying. The measured complete-frame totals include this additional transfer work. The reduced edge layout lowers flight uploads from the first optimization's 30,017,572 bytes. [Per-frame timings, geometry, uploads and exact image hashes](play-native-benchmarks.csv)

These native measurements use .NET 10.0.14, disabled tiered compilation, two managed processors and two llvmpipe workers with LLVM 19.1.7. They measure a software GPU. The original Rendering/Data/Routing sources are identical from the preserved `16c8148` binary through `11188d0`. Original renderer SHA-256 is `660433930301b94107d7199db9e456bd9f59ed0fe56bfdff828ca336b1d4c51e`, and final renderer SHA-256 is `e09dec1784a72c3e5d6b8d86f61a03cec87ab383c902bbd6c74fe0fb412981a7`. Both runs use probe SHA-256 `cd3dcd92199e89334d03b39430317b7b5eed434ff49b98fa74d24b5c88bd32c8`. Final application source and project files match `3777867` exactly.

## Retained GPU terrain

Terrain relief and the Elevation layer use a WebGL2/OpenGL ES 3 geometry renderer through Uno 6.7's public `GLCanvasElement`. The existing adaptive quadtree selects at most 64 stitched patches with 17×17 vertices each. One instanced indexed draw projects all selected patches. A 256-layer RG32F texture array retains height and illumination samples, a 96-layer RGBA8 array retains pixel artwork, and a compact per-instance stream supplies rebased homogeneous transforms and array indices. Moving the camera updates roughly 3.5 KiB of instance data for 64 patches. Mesh and artwork layers upload when their revisions change.

Camera-distance request priorities can reorder the same tile objects. GPU artwork invalidation compares source reference identity independently of priority when tile IDs are unique, preserving cached textures through those priority changes while detecting replacement data and membership changes. Reordering duplicate tile IDs rebakes their overlapping artwork. Sixteen source-order permutations for each of Map, Earth and Elevation preserve exact artwork and submit only the warmed instance stream, 2,072 bytes in the Garda fixture. This removes the repeated whole-texture batches observed in the first browser implementation.

Building meshes are retained per vector tile. Even/odd tessellation preserves roof holes, disconnected polygons and self-intersections. Model tables hold physical foundations, material, seed and height data. Vertex shaders apply the selected terrain foundation and camera projection. Fragment shaders reproduce façade lighting, windows, bands and roof edges. Terrain and buildings share a hardware depth attachment at art resolution. The final nearest-neighbor resolve preserves the pixel grid, including overhead Earth photograph blending.

Geometry preparation retains the same LOD selection and stitched height values as CPU rendering. A sparse screen-space triangle index evaluates only the depth samples requested by labels, navigation, effects and pointer input. A bounded pool retains up to two released depth workspaces for reuse, while each live frame keeps its own depth ownership. The six vehicle modes and existing game simulation continue through their established APIs.

The Uno host captures an immutable art-resolution image and presents it with nearest sampling through the existing Skia canvas. Labels, routes, gates, vehicles and effects draw against the same retained depth frame at the canvas's native DPI. Terrain inspection and marker projection use the displayed frame's camera, terrain and logical viewport, including geographic fallbacks while newer camera input is pending. Pending camera changes retain the visible image and overlays until publication. The host suppresses duplicate foreground invalidation and already-scheduled GL paints. Overlay-only animation reuses the terrain image, and the GL visual is hidden while idle to prevent repeated readbacks. Initialization, render and readback failures return the control to CPU rendering. Unload releases managed frames and GPU resources through their owning rendering contexts.

The browser host adapts Uno 6.7's framebuffer hook to a two-slot WebGL2 pixel-pack queue. Each submission owns a 64-bit retained-frame token and its artwork dimensions. A 16 ms timer polls fences with zero timeout and selects the newest signaled transfer. Elapsed time never forces a copy from an unsignaled transfer. Occupied slots bound submission while pending camera input coalesces into the latest request. Completed pixels copy directly into immutable RGBA Skia storage, and the artwork draw flips GL's bottom-up rows on the canvas. The asynchronous host detaches Uno's unused bitmap from its image brush, preventing duplicate image uploads. Cancellation releases managed frame payloads immediately, while unsignaled native transfers retain their slots until a fence signals or a newer valid image completes. Canceled images drain without pixel copies. Polling stops when the queue empties. The adapter scopes interception to the control's GL context and read framebuffer and retains synchronous compatibility for other hosts.

Capturing at art resolution reduces transferred pixels by 4× to 16×. The native capture benchmark measures the portable synchronous path, including GPU completion, readback, channel conversion, byte-buffer transfer, row reversal, immutable image creation, nearest composition and full-resolution overlays. Browser measurements exercise the asynchronous host and include Uno's presentation work. Measurements in this environment use Mesa llvmpipe and Chromium SwiftShader software drivers.

### GPU verification

The native GLES suite exercises 48 combinations of terrain, building geometry, Earth imagery, altitude tint, exaggeration, projection, palette, viewport and pixel size. It also checks 408 terrain intersections, retained A→B→A frame activation, resizing, framebuffer state restoration, complete EGL context recreation and 2× physical-resolution composition. Across the current 48-case fixture, 25 images match exactly and the maximum mean RGB difference is 0.0188 on the 0–255 channel scale. The largest structural difference beyond the one-art-pixel comparison neighborhood is 18 display pixels in a small perspective-model case.

All six vehicle modes pass 24 terrain configurations with seven animation/heading/pause/seek states each. These 168 premultiplied overlays match CPU output byte-for-byte. Six further ridge-occlusion cases cover routes, gates and vehicles. GPU composition differs by at most one channel level in the overlay fixtures. Native high-DPI checks preserve nearest map pixels, scaled glyphs and independent read/draw framebuffer bindings.

Bottom-up RGBA image presentation with a canvas flip matches the existing BGRA row-flipped presentation byte-for-byte across all 48 scenes, all 168 journey frames and the odd-sized 391×233 logical / 782×466 physical viewport fixture.

The compiled browser adapter has 17 real WebGL2 checks covering exact RGBA bytes, full 64-bit frame tokens, two contexts, foreign framebuffer scope, pack-state restoration, resize, bounded transfers, overdue unsignaled transfers, canceled unsignaled transfers, recovery through a later valid frame, context loss and disposal. The standalone fixture compiles the production C source and uses the existing Playwright dependency. With an activated Emscripten toolchain, the checks run independently of the Uno build. The verified compiler is Emscripten 3.1.56.

```sh
readback_out=artifacts/readback
mkdir -p "$readback_out"
emcc -Wall -Wextra -Werror -O0 -g0 \
  tests/browser/readback-fixture.c src/PixelMap.Controls/Browser/pixel_map_gl_readback.c \
  --no-entry -sENVIRONMENT=web -sMODULARIZE=1 -sEXPORT_NAME=PixelMapProof \
  -sEXPORTED_RUNTIME_METHODS='["GL"]' -sMIN_WEBGL_VERSION=2 -sMAX_WEBGL_VERSION=2 \
  -sALLOW_MEMORY_GROWTH=1 -Wl,--wrap=uno_gl_resolve \
  -o "$readback_out/readback-fixture.js"
node tests/browser/readback.cjs "$readback_out"
```

The terrain browser suite checks actual depth-tested WebGL draw calls at art resolution and framebuffer presentation. It also toggles the backend off and on, switches layers, changes relief and exaggeration, picks terrain, restores shared views and exercises handset controls. The browser performance harness records actual art-resolution distributions and `readPixels` synchronization time.

Four settled Chromium views compare real Garda Map, Earth and Elevation in perspective plus overhead Earth. In the local Interpreter build, mean RGB differences range from 0.112 to 0.388 on the 0–255 scale, with 0.74–1.30% of crop pixels changed. The differences occur in small texture-sampling and rasterization groups. The largest connected group whose channel difference exceeds two levels contains 20 logical pixels. Coastlines, mountain silhouettes, roads and label placement agree. The control also passes 2× display-density and resize checks. Blocking creation of the terrain GL context produces CPU fallback with a map hash identical to the normal CPU render. A two-second stationary GPU probe records no additional framebuffer readbacks after the initial image.

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

### Historical browser terrain measurements

These measurements predate the play-mode optimization and use the former 16 ms deadline-based copy policy. The current adapter waits for a signaled fence as described above.

The browser comparison uses the original elevation CPU build `c238352dcb9df7fd2cfb8ba5e86fd20f38169597`, the first synchronous GPU build `274604100ee53e29edccc8bda0871cc5737bc869` and the deployed GPU build `a764a0b9179b6f5c3b0bc42b36285034bd9cc7b6`. Each run uses its exact GitHub Actions Release `InterpreterAndAOT` artifact. Chromium 156.0.8078.4 runs SwiftShader on three AMD EPYC 9V74 logical processors, with a 1200×780 viewport and DPR 1. Runs execute sequentially during a quiet local measurement window. The fixture starts at Lake Garda, zoom 12.3, bearing −25° and perspective pitch 58°. External providers are blocked, and each gesture contains 80 physical pointer moves with a nominal 25 ms delay.

The production adaptive-quality policy stays active. The original CPU cases use 0–1 initial pixel-size-3 uploads before pixel size 4. Final GPU cases use 1–2 pixel-size-3 depth frames before pixel size 4. The reports retain the complete resolution histograms. Main-thread task time uses Chromium's `threadTicks` metric, while elapsed time includes blocking and input dispatch. SwiftShader also consumes separate worker-thread CPU time.

| Gesture | CPU task time | Final GPU task time | CPU elapsed | Final GPU elapsed | CPU art-image uploads/s | Final GPU art-image uploads/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Map pan | 4,731.1 ms | 4,598.6 ms | 8.51 s | 12.69 s | 9.98 | 5.28 |
| Map orbit | 5,674.1 ms | 4,990.9 ms | 6.52 s | 7.23 s | 11.66 | 5.81 |
| Earth pan | 9,962.7 ms | 4,059.2 ms | 13.48 s | 11.88 s | 7.12 | 5.55 |
| Earth orbit | 6,489.1 ms | 5,169.4 ms | 7.38 s | 7.55 s | 9.49 | 5.17 |
| Elevation pan | 4,954.4 ms | 4,351.4 ms | 8.78 s | 11.96 s | 9.79 | 5.68 |
| Elevation orbit | 6,232.7 ms | 4,921.2 ms | 7.02 s | 7.35 s | 10.54 | 5.44 |

The final GPU path reduces main-thread task time in all six first-run samples, with the largest reduction in Earth pan at 59.3%. Art-image upload rates are lower in all six, and five gestures take longer. Completed pixel-pack copies and art-image uploads are separate observations. A completed image can be superseded before upload, and the compositor can upload the same image again. These counters measure pipeline work. Actual display presentation is outside the probe. The first synchronous GPU version also uploaded its unused GLCanvas bitmap, so its raw 2D upload counts include duplicate work.

| Gesture | GPU submissions / completed copies | Completed copies/s | Submit-to-copy median / p95 | Initial GPU artwork uploads | Final GPU artwork uploads |
| --- | ---: | ---: | ---: | ---: | ---: |
| Map pan | 80 / 68 | 5.36 | 105.2 / 212.3 ms | 312.476 MB | 34.341 MB |
| Map orbit | 43 / 40 | 5.53 | 41.1 / 97.4 ms | 186.909 MB | 87.818 MB |
| Earth pan | 74 / 64 | 5.39 | 120.7 / 197.8 ms | 310.903 MB | 34.341 MB |
| Earth orbit | 42 / 38 | 5.04 | 57.8 / 130.4 ms | 176.423 MB | 87.556 MB |
| Elevation pan | 77 / 66 | 5.52 | 101.7 / 196.1 ms | 337.379 MB | 34.341 MB |
| Elevation orbit | 44 / 40 | 5.44 | 48.5 / 181.8 ms | 184.549 MB | 83.362 MB |

Artwork-array uploads fall by 50.4–89.8% across these gestures. These totals include the effect of frame coalescing, so frame counts accompany them. The source-order permutation checks independently verify cache reuse with identical rendered pixels. Height-array uploads are recorded separately and range from 203–705 kB in the final cases. MB and kB in these browser tables use decimal units.

Every final gesture retains at most two outstanding transfers, with zero unsafe buffer overwrites, abandoned transfers or positive-timeout fence polls. Median `getBufferSubData` calls cost 76.3–86.9 ms during pans and 5.2–7.2 ms during orbits. The 16 ms selection budget therefore still encounters substantial synchronization time on SwiftShader. The earlier asynchronous build `7fffd732ea8f7df276194d3ee1a7c31626bf532c` exposed longer presentation delays during diagnosis. The final policy reduces those delays, while the complete CPU comparison above records the remaining software-driver throughput regression.

Repeating the final Map pair with the same artifact and unmodified harness gives 5.66 art-image uploads/s for pan and 5.63 for orbit, compared with 5.28 and 5.81 in the first pair. Completed copies measure 5.50/s in both repeats. Pan submit-to-copy median/p95 is 105.1/187.9 ms, and orbit is 49.7/135.0 ms. The repeats reproduce the throughput regression and satisfy the same bounded-transfer checks.

The exact deployed native WebAssembly SHA-256 is `adf1d76ecec2a38f0cc977bfb0fc268ffaefdfbbd4234b9488cf838223085014`. The common browser harness SHA-256 is `c5d6c2b91a2ef0ec4c48fec0d7f4377ad9977e7830382d705d316fd3bd3f8ae0`. The CSV retains all three six-gesture runs, source and harness identity, runtime mode, upload dimensions, queue accounting, copy timing and the repeated Map measurements. Physical-GPU performance remains unmeasured. [Complete terrain browser measurements](terrain-browser-benchmarks.csv)

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

The flat Pixel GPU backend uses `SKVertices`, `SKRuntimeEffect` and art-resolution GPU images. Model rasterization supplies an opaque-pixel/transparent-background layer and the same depth information used by navigation and picking. Labels and journey graphics continue through the map's Skia canvas.

Uno normally records `SKCanvasElement` drawing into an immutable picture. The control checks the public `CompositionCapabilities.AreEffectsFast()` capability and authorizes deferred GPU rendering. `SKImage.FromPicture` retains the art-sized terrain and composition commands. Skia generates their textures on the actual GPU playback context. Direct GPU canvases use reusable `SKSurface` render targets. The runtime effect samples the pixel grid through a retained two-triangle mesh.

Native OpenGL tests verified zero GL draws or pixel uploads while recording, followed by three GL draws and two framebuffer attachments during shape-only deferred playback, with zero terrain pixel uploads. They also checked retained-picture lifetime, resizing, context abandonment, allocation limits, CPU fallback and mode switching. These tests used Mesa llvmpipe OpenGL ES 3.2.

The GPU path preserves the visual design and features. CPU and GPU blending have small rounding differences. In the native overhead-Earth fixture, mean RGB error was 0.390 on a 0–255 scale and only four of 715,000 pixels differed by more than two channel levels. Isometric vector-map output was exact. Both GPU paths produced identical pixels, and all 35 feature-picking probes per scene agreed with CPU picking.

The app uses Uno 6.7.135 and SkiaSharp 3.119.2. The released SkiaSharp 4.154 binding also exposes `SKVertices` and runtime effects. Programmable `SKMesh` is tracked in the upstream API work item. [SkiaSharp mesh API tracking](https://github.com/mono/SkiaSharp/issues/3777) · [Skia runtime effects](https://skia.org/docs/user/sksl/).

## Browser measurements

The browser harness uses the deployed application and physical pan/orbit input. It records main-thread CPU time, long tasks, frame cadence, actual art-texture sizes and off-screen framebuffer work. Performance pairs use matching Release `InterpreterAndAOT` builds, a 1200×780 viewport, DPR 1 and the same Chromium version. External network requests are blocked so the bundled scene supplies identical data.

Pixel and Minecraft styles adapt between pixel sizes 2, 3 and 4 during interaction. Read the resolution histogram alongside browser CPU totals. The native benchmark provides fixed-resolution comparisons. Browser frame-cadence samples describe browser scheduling between updates.

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

The measured CPU Map and Earth perspective pans use closely comparable pixel-size distributions across releases. Their task CPU reductions are 49.0% and 58.5%. The optimized Map isometric orbit instead keeps pixel size 2 for all 80 uploads, while the baseline uses 8, 34 and 41 uploads at pixel sizes 2, 3 and 4. Its increased CPU total accompanies more detailed moving frames. The opt-in GPU orbit also keeps pixel size 2 throughout.

GPU-mode Earth isometric pan uses 40/16/24 uploads at pixel sizes 2/3/4, compared with 2/1/77 for optimized CPU. Its elapsed gesture time rises from 5,669.9 ms to 7,440.0 ms in this software-driver environment. Earth perspective orbit also has higher main-thread CPU time in GPU mode. The optional backend therefore exposes a workload-dependent tradeoff between CPU work, composition cost and adaptive detail. Physical-GPU performance remains unmeasured.

The measured CPU AOT renderer matches all 22 baseline map crops exactly. The first full-canvas comparison matches 20 of 22 canvases. Two small UI ellipses differ by 50 and 48 pixels. Repeating both cases on both builds produces exact original-baseline canvases, identifying run-to-run UI raster variation. The GPU matrix passes backend-status and art-sized framebuffer checks in every case, with zero runtime exceptions. Seven GPU map crops match the CPU baseline exactly. The maximum GPU difference is 1,383 of 297,000 crop pixels, or 0.466%, in overhead Earth. GPU rasterization and blending can change edge pixels.

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
