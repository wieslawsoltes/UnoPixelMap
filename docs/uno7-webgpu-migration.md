# Uno Platform 7 and native WebGPU migration

## Status and scope

This branch migrates Pixel Atlas from Uno.Sdk 6.7.30 to Uno.Sdk 7.0.5 and replaces
every production map presentation path with Uno's shared-device
`WebGpuCanvasElement`. The Uno UI also uses the WebGPU graphics provider.

The production renderer, migration checks and native regression validation are
present. Published-browser integration and visual acceptance remain required
gates; their latest outcome is recorded in the PR and linked CI runs below.
The quality target is a modern real-time 3D renderer. Implemented
graphics features do not by themselves establish commercial AAA visual quality,
hardware frame rates, or a measured improvement over the previous release.

## Verified dependency graph

| Dependency | Previous | Migration |
| --- | --- | --- |
| Uno.Sdk | 6.7.30 | 7.0.5 |
| Uno core / graphics | 6.7.135 GL add-in | 7.0.32 core, Composition.WebGpu and Graphics3DWebGpu |
| .NET SDK used for verification | 10.0.403 | 10.0.403 |
| Target frameworks | net10.0 / net10.0-browserwasm | unchanged |
| Browser bootstrap | driven by Uno 6 SDK | 10.1.1 from Uno 7 SDK |
| SkiaSharp asset processing | 3.119.2 | 4.151.1, managed and native aligned |
| Silk rendering API | OpenGL 2.16.0 | WebGPU 2.23.0 |
| GL reference tests | production renderer | test-only OpenGL 2.23.0 dependency |

Uno 7 removes the old native rendering hosts. All Uno-dependent projects are
rebuilt against the new SDK. No Uno 6 graphics package or Silk.NET.OpenGL
dependency remains in the production graph.

Authoritative upstream references:

- [Uno 7 migration guide](https://platform.uno/docs/articles/migrating-to-uno-7.html).
- [WebGPU rendering backend](https://platform.uno/docs/articles/features/using-webgpu-rendering.html).
- [WebGpuCanvasElement](https://platform.uno/docs/articles/controls/WebGpuCanvasElement.html).
- [Uno.Sdk 7.0.5](https://www.nuget.org/packages/Uno.Sdk/7.0.5).
- [Released canvas source](https://github.com/unoplatform/uno/blob/7.0.32/src/AddIns/Uno.WinUI.Graphics3DWebGpu/WebGpuCanvasElement.cs).
- [Released browser device initialization](https://github.com/unoplatform/uno/blob/7.0.32/src/Uno.UI.Composition.WebGpu/ts/Runtime/WebGpuInit.ts).

Uno explicitly describes its WebGPU backend as experimental. This migration uses
published APIs and pinned releases, rather than private host reflection.

Bootstrap 10.1.1 moves `uno-config.js` beside `index.html` at the publish root.
The deployment finalizer follows the selected bootstrapper's config import,
checks its package identity, and includes the root configuration in transactional
hashing and rewrites. The packaging gate still verifies loader integrity,
compressed representations and byte-preserving repeated finalization. Its
root-layout, incremental-publish, rollback and retry fixtures pass. See
[publishing details](publishing.md).

## Host and control migration

The application and control library declare `Skia;WebGpu`. SkiaSharp remains
necessary for CPU image decoding, cached map artwork and glyph-atlas generation.
The host registers **only** `WebGpuBackend.CreateGraphicsProvider()`. There is
no Skia UI provider or CPU/GL map fallback selected at runtime.

The previous implementation depended on private Uno 6 browser renderer types,
native GL resolver interception, and either same-context Skia texture adoption
or a separate GL canvas followed by framebuffer transfer. The migration removes:

- `GpuTerrainSurface.cs` and its alternative presentation paths.
- `BrowserGpuContext.cs`, `BrowserGpuSurface.cs` and `BrowserGpuReadback.cs`.
- `pixel_map_gl_readback.c`, the native compile item and resolver linker flag.
- The `GLCanvas` feature and old Graphics3DGL add-in reference.
- Production compilation of `GlMapRenderer.cs` and `GlMapShaders.cs`; these
  files are linked into the test assembly as historical regression references.

`PixelMapControl.MapSurface` now derives from `WebGpuCanvasElement`.
`WebGpuMapRenderer` receives Uno's `context.Api`, borrowed device and queue.
It renders into the supplied texture view. No second browser graphics context,
texture-to-CPU copy, image upload for final presentation, or full-frame CPU
overlay bitmap participates in the production path.

The public `IsGpuRenderingEnabled` switch was removed. Its replacement is
`RenderingQuality` with `Balanced`, `High` and `Ultra` values.
`IsGpuRenderingActive` remains a read-only status indicator. The View menu
exposes the three quality choices and the four existing rendering styles.

## Render pipeline

```mermaid
flowchart TD
    Sources["Vector tiles, imagery and DEM"] --> Prepare["Retained geometry and asset preparation"]
    Prepare --> Shadow["Sun shadow depth pass"]
    Prepare --> Scene["HDR terrain and building pass"]
    Shadow --> Scene
    Navigation["Routes, vehicles, gates and particles"] --> World["Depth-tested world overlays"]
    Scene --> World
    World --> Resolve["Tone mapping, bloom and pixel resolve"]
    Atlas["Cached Unicode label atlas"] --> Labels["GPU screen labels"]
    Resolve --> Labels
    Labels --> Uno["Uno shared WebGPU target"]
```

### Geometry, precision and depth

Terrain uses retained height/artwork texture arrays, instanced patch data, and
shared grid or block index buffers. Buildings retain 32-byte vertices and model
storage buffers. The scene uses camera-relative transforms to avoid geographic
coordinate precision loss. Perspective clipping and depth mapping follow the
existing camera and terrain conventions.

A `GpuMapFrame` owns immutable geometry/source references. Controls advance
the displayed frame and the interaction/picking state together. A failed GPU
submission invalidates interaction queries instead of exposing geometry that
was prepared but never displayed. Existing sparse
CPU depth queries remain available for label visibility, picking and terrain
interaction without reading a GPU depth buffer back to the CPU.

Each frame uses a 512-byte aligned uniform slot; building draws select their
slot through a dynamic offset. Terrain instances occupy 80 bytes.
Resource limits come from the actual shared device. Unsupported limits result
in an explicit diagnostic instead of an invalid draw.

### Lighting and image quality

Normal and Roblox styles implement:

- A directional sun shadow map with 3 × 3 percentage-closer filtering.
- GGX-based material lighting and sky illumination.
- A linear-light RGBA16F scene target.
- Height-dependent atmospheric fog.
- ACES-fit tone mapping and a small bright-pass bloom resolve.
- Quality-dependent multisampling and edge antialiasing.
- Correct final encoding for linear and sRGB target formats.

Roblox vertical terrain faces use explicit outward world-space normals; their
zero vertical component cannot orient a derivative normal reliably. Top and
bevel faces retain their geometric normals.

The shadow projection is a single stabilized sun projection, not a cascaded
shadow system. Terrain and building geometry supply the shadow casters. Vehicle
and route overlays use hardware scene depth; they do not currently cast or
receive the full physical-lighting shadow treatment.

The renderer retains a separate complete building index stream for sun casters,
so camera-frustum culling does not erase shadows from loaded offscreen buildings.
An unchanged immutable frame and quality preset reuse the previous shadow map.
The same frame also retains terrain instances, building uniforms and draw
selection while route or vehicle overlays animate. Physical/raster dimensions,
quality and shadow-size changes invalidate this scene state. Residency age
advances with scene preparation, so animated overlays cannot evict buffers they
still use. Reuse is committed after queue submission and invalidated on failure.

Pixel and Minecraft retain their logical pixel grid, palette treatment, window
details and nearest-neighbor resolve. Their prior baked ground-shadow appearance
is retained. Smooth styles disable the old baked shadow so it is not doubled
with the new sun pass. Primitive-edge and color-rounding differences from the
GL renderer are possible; full-frame bit identity has not been claimed.

| Quality | Smooth geometry samples | Sun map | Requested scene-pixel cap | Bloom |
| --- | --- | --- | --- | --- |
| Balanced | 1 | 1024 × 1024 | 2,073,600 | off |
| High (default) | 4 | 1536 × 1536 | 3,686,400 | enabled |
| Ultra | 4 | 2048 × 2048 | 8,294,400 | enabled |

The actual smooth resolution is constrained by both the pixel cap and a
256 MiB attachment budget. Ultra may therefore render below its requested pixel
cap, particularly with multisampling. Physical display density is included;
logical projection coordinates remain unchanged. Pixel styles preserve the
logical grid; Balanced alone may increase their moving pixel size under CPU
submission pressure.

### Native scene overlays

`WebGpuSceneOverlay` produces 48-byte vertices and indexed triangle streams for
three batches: ordinary depth-tested world geometry, occluded navigation
silhouettes, and full-resolution labels after tone mapping.

Routes retain provider-distance progress, terrain draping, wrapped world copies,
antimeridian handling, stop flags and checkpoint gates. Walking, bicycle, car,
boat, train and aircraft models retain physical and miniature representations.
World effects use a fixed 56-particle buffer and preserve pause, seek, teleport,
resume and continuity behavior.

An unchanged stationary standalone vehicle advances its simulation timestamp
without rebuilding geometry or submitting an identical frame. Routes and gates
retain their animation updates. Orthographic route depth bias is clamped to the
WebGPU clip interval so annotations raised above the terrain remain visible.

Labels reuse the existing Unicode placement, ordering, collision and occlusion
rules. A bounded 2048 × 2048 atlas caches font assets; only changed atlas regions
are uploaded. Labels are GPU quads, not a rasterized viewport overlay.
Label buffers have a separate revision and remain resident across vehicle-only
updates. Retained-frame reactivation restores the matching camera and label
sources, including an A → B → A → B sequence.
Smooth styles rasterize glyph assets at the physical display density, quantized
in quarter steps and bounded to 3×. Logical text metrics and collision boxes stay
unchanged. Pixel and Minecraft keep their original 1× glyph artwork. Segmented
long names include populated filtering guards to prevent atlas-boundary seams.
`PrepareGpuFrame(..., prepareLegacyOverlay: false)` avoids recording the old
Skia label display list. The default parameter remains true for regression
callers that exercise the historical renderer.

## Retention, ownership and failure handling

The map does not release Uno's API, device, queue, target texture or target view.
It owns and releases its own GPU buffers, views, textures, samplers, bind groups,
pipeline layouts, pipelines, shader modules, encoders and command buffers.
Owned buffers and textures are explicitly destroyed before their native handles
are released; reclamation does not depend on JavaScript garbage collection.
Previously submitted work retains the resource lifetime guaranteed by WebGPU.

The target is valid only during `RenderOverride`. Resize recreates the
extent-dependent attachments. Unload releases scene resources and GPU resources
through the canvas lifecycle. Partial initialization cleans up explicitly because
Uno does not invoke `OnDestroy` when a derived `Init` throws.

Retained mesh storage is bounded to 128 MiB. Intermediate attachments are bounded
separately to 256 MiB. Tile arrays, atlas storage, uniforms, pipelines and driver
allocations are additional. `ResidentBytes` is a bounded-resource estimate, not
total process or GPU-driver memory; `AttachmentBytes` is reported separately.
The resident estimate includes the glyph atlas, retained overlay buffer capacity,
uniforms, shared indices and material textures. Driver layout, padding and
pipeline storage are not measurable from those nominal allocation sizes.

Unavailable WebGPU, absent adapters, startup exceptions, uncaptured GPU errors
and device loss show an accessible HTML recovery panel with a reload action.
This panel does not depend on the failed GPU device. The canvas obtains the
actual shared browser device through Uno's public `WebGpuJsInterop.GetJsObject`
API and observes that device's loss/error events. A managed callback stops map
submissions, clears the displayed scene used by picking, and reports failure to
the UI. No global device-creation hook or additional device is needed.
The device and its existing JavaScript proxy remain borrowed. Each canvas owns
only its subscription token; unload, initialization failure and teardown detach
the callback and error listener. One pending loss continuation per device avoids
accumulation across reloads, and a generation guard rejects stale notifications.
The HTML panel remains available if managed error reporting itself fails.

## Verification and evidence

### Passed

The restored package graph matches the versions above. Rendering-library Release
compilation succeeded. The full native regression job also passed on GitHub for
[fee37c5](https://github.com/wieslawsoltes/UnoPixelMap/commit/fee37c54d188f49cedd50494b51ac3588b67750c):
[workflow run](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43107888651).
The browser application also completed managed compilation, native linking and
AOT compilation before reaching a custom publish-versioning layout mismatch.

That job includes data, elevation, imagery, routing, simulation, input and audio
checks; the complete CPU rendering corpus; and the retained GLES reference
renderer. Selected corpus sizes include 11,935,261 sparse/eager depth queries,
9,063,600 projected depth comparisons, 1,122 physical-camera building-cache
poses, 24 exact label fixtures and 16 real Manhattan label views. These validate
preserved geometry/interaction behavior and the reference renderer, not the new
WebGPU scene's entire raster output.

The new `--webgpu-overlay` checks passed locally: Unicode atlas/halo composition,
all six vehicle modes across four styles, route and gate geometry, antimeridian
and near-plane clipping, finite vertices, valid indices, atlas reuse, particle
capacity and lifecycle. Follow-up checks covering retained-frame reactivation,
independent label revisions, empty label sets, orthographic route depth and idle
timestamp advancement passed locally and in the current native CI job.
The high-DPI follow-up also passed exact 1×, 2× and 3× comparisons against direct
scaled text, including a segmented Unicode name, density-only invalidation,
bucket reuse, the atlas density cap and unchanged pixel-style glyph density.
The scene-retention guard covers 240 changing overlays, retry after failed
preparation and eviction after 181 distinct scene preparations. This validates
the managed retention policy; it does not measure GPU completion time.

The browser observer lifecycle regression exercises 100 reloads with one pending
loss continuation, exact-device notification, shared subscriptions, deterministic
callback/listener release, stale and unrelated devices, cancellation of queued
loss notifications, partial attachment failure and HTML diagnostics during
managed teardown. It validates the observer protocol; the real browser suite
separately destroys Uno's actual shared device and checks that map work stops.

Exact production WGSL and 22 pipeline variants were accepted by native Dawn
validation on SwiftShader. This is shader/layout validation only.

### Validation investigations

The first native complete-frame fixture aborted in SwiftShader after submission.
A sky-only variant also aborted, while independent clear and constant-triangle
fixtures succeeded. No complete-frame native screenshot or timing is established
by that run. Local Chromium cannot start because this execution environment
denies its required process sockets. Published browser CI is the authoritative
integration gate.

[Run 7](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43110751306)
on `998bdaf` successfully published both the interpreted application and the full
Release AOT application and passed their strict package-identity checks.
Chromium then reported a lost graphics device
with `A valid external Instance reference no longer exists.` before a usable
scene screenshot was captured. Its frame counters establish submission only;
the captured recovery panel is not accepted rendering evidence. The application
and Uno source audits found balanced instance/device ownership. The next browser
configuration tests full Chromium with the software-GPU watchdog disabled and
bounded completion timeouts, while retaining every device-loss failure.

[Run 8](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43112585477)
captured the earlier failure more precisely: Chromium could not create the
shared image backing its application canvas, followed by device loss. It used
one GPU device and reported no map readback or WebGL contexts. CI now aligns
ANGLE, Vulkan presentation and Dawn with the
[Vulkan SwiftShader configuration from Chromium 156's own pixel tests](https://github.com/chromium/chromium/blob/156.0.8078.4/content/test/gpu/gpu_tests/pixel_test_pages.py).
Adapter identities and explicit device-destruction stacks are retained in the
evidence; no device-loss or shader errors are filtered out.

[Run 9](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43114129603)
published both Debug and Release applications and passed package verification. Its
aligned software adapter reported no device loss or GPU error, but the whole
application screenshot was transparent, including the Uno toolbar. Its native
map commands had been submitted; GPU completion had not yet been checked before
capture. The next test revision jointly bounds shader queries and queue fences
before screenshots, preserves Chromium's screenshot feature when enabling
Vulkan, and separately verifies a small WebGPU canvas before building the app.
Transparent captures are rejected and do not establish rendering correctness.

[Run 10](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43116317023)
reproduced the missing canvas on an independent page: ordinary CSS pixels were
correct, while a WebGPU green clear remained transparent despite completed queue
work and no GPU errors. The application builds were skipped by that preflight.
This matches the headless Linux presentation limitation documented in the
[agent-browser WebGPU implementation notes](https://github.com/vercel-labs/agent-browser/blob/main/skill-data/core/references/webgpu.md)
and its [published Xvfb reproduction](https://github.com/vercel-labs/vgpu/issues/109).
CI now runs full Chromium with a window under Xvfb, installs the Vulkan loader
and Mesa runtime, and uses the same SwiftShader Vulkan driver for ANGLE and
WebGPU. The independent opaque-pixel test must pass before Uno builds start.

The WebGPU browser suite observes actual shader compilation, pipeline depth
and multisample state, map queue submissions and readback calls. Its intended
coverage includes all styles/quality presets, imagery/elevation/relief, pan/zoom,
all six route demonstrations, walking, resize, high-DPI mobile pinch, unavailable
WebGPU and device loss. It retains screenshots, errors and bounded counters.
The suite must pass before this migration is considered ready.
Screenshot checks reject the recovery panel before and after capture and retain
graphics state, GPU errors/losses, console history and accessibility on failure.

PR CI first checks a small independent WebGPU canvas using the same Chromium
configuration, including browser composition and screenshot pixels. This quick
gate has no Uno or application renderer dependency. PR CI then runs a Debug
interpreted publish for earlier startup/shader feedback and
the Release AOT publish used for delivery. Both run the same package-identity and
browser scenarios. Superseded builds are cancelled per configuration;
each configuration retains its own screenshots and exact published files,
including files useful for diagnosing a failed packaging check.

`frameMilliseconds` measures CPU preparation and submission.
`submissionMilliseconds` measures renderer-side CPU work. Neither is a GPU
completion measurement or display FPS. No hardware speedup is asserted.

## Historical tests and remaining quality boundaries

The old GL/compositor/readback benchmark workflow and CPU/GPU browser matrix are
retired because they observe APIs removed by this migration. Their source and
previous evidence remain available for historical comparisons. They are not
valid WebGPU benchmark results.

The renderer does not implement cascaded shadows, temporal antialiasing,
screen-space reflections, global illumination, volumetric clouds, photogrammetric
city assets, or GPU-driven streaming/culling. Map content is still derived from
the existing vector, imagery and elevation providers. A future AAA acceptance
gate must specify target hardware, scene/content quality, frame-time percentiles,
memory limits and visual baselines. This migration supplies concrete shader and
resource infrastructure without declaring those broader targets already met.
