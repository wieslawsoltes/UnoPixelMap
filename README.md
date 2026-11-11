# UnoPixelMap · Pixel Atlas

[Explore the live map](https://wieslawsoltes.github.io/UnoPixelMap/) · [Lake Garda elevation](https://wieslawsoltes.github.io/UnoPixelMap/#v4/45.875000/10.850000/12.300/-25.00/58.00/Day/Elevation/Perspective/0.0/Relief/1.00) · [Earth in 3D](https://wieslawsoltes.github.io/UnoPixelMap/#v2/40.704100/-74.016300/15.750/-18.00/55.00/Day/Earth) · [Earth in 2D](https://wieslawsoltes.github.io/UnoPixelMap/#v2/40.704100/-74.016300/15.750/-18.00/0.00/Day/Earth)

Pixel Atlas is an interactive Uno Platform 7 map control with a native WebGPU renderer for the browser. It streams worldwide vector tiles and turns building footprints, height and minimum-height attributes into explorable pixel art. Choose an overhead, isometric or perspective camera, plan a journey, preview its directions, or explore freely on foot, in a vehicle or with a flying camera. The UI and every map style share one WebGPU device. Pixel art is the default style; the View menu offers Balanced, High and Ultra rendering quality. Normal, Minecraft and Roblox rendering styles are available independently of the map layer. A world overview, the opening Lower Manhattan neighborhood, northern Lake Garda terrain and six route demonstrations are bundled with the app.

![Pixel Atlas displaying Lower Manhattan in a 1200 × 780 Chromium viewport](docs/manhattan.png)

The gallery images document the earlier renderer. The [Uno 7 migration record](docs/uno7-webgpu-migration.md) identifies the new implementation and its separate verification evidence.

[Explore the East River waterfront in detail](docs/seaport.png) · [See a live search result for Racibórz, Poland](docs/raciborz.png)

## Explore

Drag to pan, use a mouse wheel or trackpad pinch to zoom, and right-drag or Shift-drag to orbit and tilt. Continuous two-axis trackpad scrolling pans the map. Shift-scroll orbits and Alt-scroll tilts. Two touch contacts can pinch, twist, pan or tilt in parallel. Map gestures preserve the page scale and UI layout. Double-click to zoom at the pointer. Click a roof, façade, street or park to inspect it. Use Map, Earth or Elevation to choose the ground layer, then choose 2D or 3D independently. Terrain relief can be enabled for all three layers. In Pixel style, Earth transforms natural-color satellite imagery into pixel art and keeps map labels and buildings in the same coordinate system. The View menu selects rendering style, the Day, Dusk or Night palette and the perspective lens. Share a view to preserve its camera, physical eye, projection, rendering style, lighting, terrain relief and vertical exaggeration. The v5 link reader also supports v1–v4 views and preserves a free camera's eye when opened on a different viewport.

Search by place name, address or decimal latitude and longitude. The city guide includes Lower Manhattan, Kraków, Shibuya, Barcelona, Paris, Singapore and the Lake Garda mountains. With the map focused, use the arrow keys to pan, +/− to zoom, Q/E to rotate, P to switch projection, Home to return to Manhattan and Escape to clear a marker.

## Rendering styles and free roam

| Style | Terrain and buildings | Image sampling |
| --- | --- | --- |
| Pixel | Continuous terrain with palette-quantized lighting, pixel façades and roofs | Adaptive pixel-art resolution with nearest-neighbor presentation |
| Normal | Continuous terrain, sun shadows, GGX lighting, atmospheric fog and unprocessed Earth imagery | Bounded physical resolution, HDR resolve and quality-dependent antialiasing |
| Minecraft | Quantized terrain cells with flat tops and vertical walls, stair-stepped building footprints and block materials | Pixel-art resolution |
| Roblox | Bevelled terrain blocks, simplified building forms, physical lighting and sun shadows | Bounded physical resolution, HDR resolve and quality-dependent antialiasing |

Perspective Pixel windows now retain their average glass and lamp contribution as façades become distant or turn away from the camera. Well-resolved nearby windows keep their original pixels. [Algorithm, visual comparison and measured cost](docs/pixel-window-filtering.md)

Open **Play** to start a route-independent session at the map center. Walk, car, bicycle, boat, aircraft and train share the fixed-step simulation, terrain sampling, collision world, audio and effects. Free flight provides an independent camera with forward, lateral and vertical movement plus yaw and pitch. Changing vehicles retains the current geographic position. Trains attach to nearby mapped tracks, follow connected segments, and wait for additional track coverage where continuation is unavailable. Boats wait on dry or uncovered starts and resume when navigable water is available. The route planner remains available after leaving free roam.

Third-person chase, first-person, cockpit, orbit and north-up rigs follow the active vehicle. First-person cameras use metre-scaled eye heights and an 8 cm near plane. Perspective pitch extends through the horizon toward an upward view, and longitude wraps around the globe. The projection retains Web Mercator's latitude boundary. Terrain-relative camera clearance uses the rendered surface, including block-style steps, while vehicle physics uses the underlying elevation data.

| Free-roam input | Action |
| --- | --- |
| W/S or ↑/↓ | Forward and reverse, or aircraft throttle |
| A/D or ←/→ | Walk/free-flight strafe, vehicle steering |
| Q/E | Walk/free-flight yaw |
| Drag or right touch stick | Look and orbit |
| Click the map in first person | Capture relative mouse movement |
| Space | Jump on foot, brake a vehicle, ascend in free flight |
| Page Up / Page Down | Climb and descend |
| Shift | Run or boost |
| C | Toggle first-person and third-person view |
| R | Respawn at the session origin |
| Escape or P | Pause or resume, releasing captured mouse input |
| Left touch stick | Independent movement while the right stick controls look |

Mouse-wheel zoom changes chase distance. In free flight it changes movement speed. Pausing, changing modes, hiding the page or leaving the game releases held inputs. Sound remains opt-in through the game's sound control.

## Journeys and driving

[Drive through Manhattan](https://wieslawsoltes.github.io/UnoPixelMap/#journey/drive) · [Ride the subway](https://wieslawsoltes.github.io/UnoPixelMap/#journey/train) · [Cross the East River](https://wieslawsoltes.github.io/UnoPixelMap/#journey/boat) · [Fly between airports](https://wieslawsoltes.github.io/UnoPixelMap/#journey/flight) · [Cycle](https://wieslawsoltes.github.io/UnoPixelMap/#journey/bike) · [Walk](https://wieslawsoltes.github.io/UnoPixelMap/#journey/walk)

Open Journeys to choose a travel mode and edit 2–24 ordered stops. Search for a stop, enter its coordinates or pick it on the map. Stops can be reordered, reversed and removed. Two-stop walking, cycling and driving requests expose alternative routes when the provider supplies them. The itinerary includes per-leg distance, duration, turn instructions and roundabout exits. Preview compresses the journey into a 12–80 second tour with pause, scrubbing and adjustable playback speed. The next maneuver updates with distance along the route. Manual driving displays speed from the vehicle simulation.

Take manual control to start a checkpoint challenge. Each vehicle has its own acceleration, braking, drag, steering response, sprite animation and synthesized sound. The train follows its track, the boat carries turning inertia, and the aircraft supports throttle-dependent lift and altitude. Mapped building and shoreline contacts constrain free movement. Ordered gates award points, and recovery returns the vehicle to its last checkpoint. Cruise follows the planned path. Chase, north-up, cockpit and orbit camera rigs keep the vehicle in the usable viewport beside the journey panel.

| Input | Action in manual mode |
| --- | --- |
| W or ↑ | Throttle |
| A/D or ←/→ | Steer |
| S, ↓ or Space | Brake |
| Page Up / Page Down | Aircraft climb / descend |
| H | Horn or signal |
| On-screen pad | Touch throttle, steering, brake and aircraft lift |

Sound starts muted and is activated by the sound button. The Web Audio mixer synthesizes motors, wind, wheel rhythms, footsteps, horns and checkpoint cues. It suspends on pause and when the document is hidden. Each map has an independent, disposable audio graph.

Road routes use separate FOSSGIS OSRM foot, bicycle and car networks. Train and boat routes follow connected mapped tracks, ferry lines or fairways within a bounded local corridor. Their times are estimates. Flight routes follow great-circle courses. Timetables, passenger transfers, vessel clearance, flight procedures, live traffic and photogrammetric city meshes require additional data providers. The simulation uses mapped geometry with simplified vehicle dynamics. [Routing coverage and source details](src/PixelMap.Routing/README.md)

## How Earth becomes pixel art

| Earth in 3D | Earth in 2D |
| --- | --- |
| ![Isometric Earth at Battery Park with extruded buildings](docs/earth-isometric.png) | ![The same Battery Park waterfront in overhead Earth mode](docs/earth-overhead.png) |

The renderer decodes each satellite tile once. It averages colors in linear light, merging low-contrast 2×2 areas while retaining individual high-contrast samples along shorelines and field boundaries. It selects neighboring colors from an 89-color palette in OKLab and applies a world-aligned 4×4 ordered dither. Panning or rotating leaves that pattern attached to the ground.

Zoom levels 13 and 14 preserve the full 256×256 source grid. Lower zoom levels use 128×128 art tiles. A 24 MiB LRU holds processed textures. In overhead view, translucent building footprints preserve the imagery underneath. Tilting into the isometric projection reveals the extruded walls.

[Explore Lake Garda in Earth mode](https://wieslawsoltes.github.io/UnoPixelMap/#v2/45.687000/10.737000/13.000/30.00/55.00/Day/Earth) · [View the Lake Garda browser capture](docs/earth-lake-garda.png) · [See Earth on a phone-sized viewport](docs/earth-phone.png)

## Elevation and terrain relief

[Explore the pixel-art elevation map](https://wieslawsoltes.github.io/UnoPixelMap/#v4/45.875000/10.850000/12.300/-25.00/58.00/Day/Elevation/Perspective/0.0/Relief/1.00) · [Explore Earth over the same mountains](https://wieslawsoltes.github.io/UnoPixelMap/#v4/45.875000/10.850000/12.300/-25.00/58.00/Day/Earth/Perspective/0.0/Relief/1.00)

Elevation combines altitude colors with quantized hill shading and the existing map artwork. **Terrain relief** in View lifts the ground into a real height field for Map, Earth and Elevation. **Camera angles and orbit** includes a 1×–4× vertical exaggeration slider. Altitude readouts report unexaggerated metres above sea level. Overhead and oblique projections, camera orbit, feature inspection and saved views share these settings.

Mapzen Terrain Tiles provide worldwide, keyless Terrarium elevation data. The decoder retains signed heights and bilinearly samples adjoining tiles, with loaded parents and a world overview supplying progressive coverage. A bounded screen-adaptive quadtree stitches neighboring mesh edges and projects cached pixel artwork with perspective-correct texture coordinates. Buildings, labels, routes, vehicle effects and picking share terrain depth. Building height and vehicle clearance retain their physical scale above the terrain.

Ground vehicles and trains follow the sampled surface. Ocean surfaces use sea level, while land and inland water retain signed DEM elevations. Aircraft use absolute altitude and terrain-relative clearance, with swept ground contacts, taxiing and landing handling. Rendered navigation objects follow the selected mesh while the simulation retains physical DEM heights. The camera keeps a local terrain datum, supports terrain-anchored gestures and adjusts perspective eye clearance above slopes.

The default GPU backend renders terrain and buildings with retained geometry, vertex and fragment shaders, and hardware depth testing. Sparse CPU visibility queries keep labels, routes, vehicles and feature inspection aligned with the selected mesh. The production control uses WebGPU for every view; CPU and GL implementations remain regression references. Source DEM detail varies by region, and the height field uses adaptive sampling at the current art resolution. [Elevation provider, fixtures and attribution](src/PixelMap.Data/Elevation/README.md)

Building meshes use lossless 32-byte vertices and exact output allocation. On the captured dense Manhattan corpus, this reduces constructor allocation by 53.6% and retained mesh/edge payload by 146.3 MB while preserving the original geometry and rendered pixels. The fixed-corpus AOT replay also lowers peak allocated Wasm memory by 161.56 MiB while completing all 27 camera/control checkpoints. [Mesh layout and memory evidence](docs/building-mesh-memory.md) · [Current measurements and limitations](docs/performance.md#current-release-browser-measurements)

## Reuse the control

Reference `src/PixelMap.Controls/PixelMap.Controls.csproj` from an Uno 7 app. The browser app pins Uno.Sdk 7.0.5 and .NET 10. All Uno-dependent libraries must be rebuilt for Uno 7.

```xml
<PropertyGroup>
    <UnoFeatures>Skia;WebGpu</UnoFeatures>
</PropertyGroup>
<ItemGroup>
    <Content Include="../PixelMap.Controls/Browser/map-input.js"
             Link="input/map-input.js" UnoDeploy="Root" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

`Skia` supplies CPU asset decoding, tile artwork and font-atlas native libraries. Select only the WebGPU UI provider on the host:

```csharp
using Uno.UI.Hosting;
using Uno.UI.Composition.WebGpu;

await UnoPlatformHostBuilder.Create()
    .App(() => new App())
    .GraphicsBackend(WebGpuBackend.CreateGraphicsProvider())
    .UseWebAssembly()
    .Build()
    .RunAsync();
```

The control no longer needs a native GL shim, a resolver linker override, host reflection, or a separate graphics context. WebGPU and a secure browser origin (HTTPS or localhost) are required. The application includes accessible HTML recovery UI for unavailable adapters and device loss.

Load `input/map-input.js` before Uno's bootstrap script in the browser host:

```html
<script src="input/map-input.js"></script>
```

This control asset preserves native trackpad deltas, scopes browser gesture cancellation to the map after XAML hit testing, manages pointer capture and releases input on focus loss. It also supplies frame diagnostics and per-canvas observation of Uno's shared WebGPU device. Device loss stops map submissions and invalidates picking without requiring the sample application's HTML. A host can optionally define `globalThis.PixelAtlasGraphics.fail(message)` to display its own recovery panel; the sample application's host demonstrates that accessible UI.

```xml
<Grid xmlns:maps="using:PixelMap.Controls">
    <maps:PixelMapControl x:Name="Atlas" />
</Grid>
```

```csharp
using PixelMap.Rendering;

Atlas.IsEarthImageryEnabled = true;
Atlas.IsTerrainEnabled = true;
Atlas.RenderingQuality = WebGpuQuality.High;
Atlas.TerrainExaggeration = 1;
Atlas.Palette = PixelStyle.Dusk;
Atlas.RenderStyle = MapRenderStyle.Pixel;
Atlas.FlyTo(new MapCamera(50.0617, 19.9373, 16.8, Bearing: -35, Pitch: 55));
Atlas.SetMarker(50.0617, 19.9373, "Rynek Główny");
```

```csharp
using PixelMap.Controls;
using PixelMap.Routing;

var planner = new RoutePlanner(httpClient, Atlas.VectorTiles);
var route = await planner.PlanAsync(TravelMode.Drive,
[
    new RouteStop("Wall Street", new RoutePoint(40.7068, -74.0090)),
    new RouteStop("City Hall", new RoutePoint(40.7128, -74.0060))
], cancellationToken);

Atlas.ShowRoute(route);
Atlas.CameraRig = JourneyCameraRig.Chase;
Atlas.Journey.Preview();
```

```csharp
using PixelMap.Controls;
using PixelMap.Rendering;
using PixelMap.Routing;
using PixelMap.Simulation;

Atlas.RenderStyle = MapRenderStyle.Minecraft;
Atlas.IsTerrainEnabled = true;
Atlas.StartFreeRoam(TravelMode.Walk);
Atlas.CameraRig = JourneyCameraRig.FirstPerson;
Atlas.SetFreeRoamInput(new FreeRoamInput(Forward: 1, Boost: true));
Atlas.LookFreeRoam(bearingDelta: 15, pitchDelta: -5);
// A gamepad adapter can update this input independently of held keyboard controls.
Atlas.SetFreeRoamInput(default);
```

`IRouteProvider` supplies additional routing backends. `VehicleProfile` defines compatible vehicle dynamics, and `JourneySession.SelectVehicle` installs a profile for the current route. `IJourneyWorld` supplies collision rules and surface heights. `ElevationTileSource` accepts a custom HTTPS Terrarium XYZ provider, and `ElevationSnapshot` supplies raw and water-aware height sampling. `IJourneyAudioDevice` supplies an audio backend. The browser host deploys `PixelMap.Audio/Browser/journey-audio.js` and attaches a `BrowserAudioDevice`. Other hosts can use the same simulation without browser audio.

| Project | Responsibility |
| --- | --- |
| PixelMap.Data | Bounded MVT protobuf decoding, source catalog, request coalescing, cancellation and separate byte-bounded caches for vector tiles, satellite imagery and elevation. Bounded Terrarium PNG decoding, immutable height snapshots and indexed water surfaces. Photon geocoding with an injectable endpoint. |
| PixelMap.Rendering | Wrapped Web Mercator camera, cached terrain blocks, palette shaders, orthographic and perspective rendering, depth-tested walls and roof holes, map labels, feature picking, route overlays, vehicle sprites and world-anchored particles. The production renderer uses retained WebGPU geometry, WGSL shaders, hardware depth, shadow maps, HDR composition and native GPU overlays. Sparse CPU queries support label layout and picking against the same terrain. |
| PixelMap.Routing | Provider-separated routes and alternatives, bounded local A* rail/ferry searches, great-circle flight paths, ordered stops, maneuver metadata and shared provider-distance route sampling. |
| PixelMap.Simulation | Fixed 60 Hz clock, vehicle profiles, free roaming and flight, jumping, rail following, manual inputs, preview transport, cruise, checkpoint scoring and recovery, spatially indexed mapped collisions and replaceable world rules. |
| PixelMap.Audio | Coalesced motion parameters and discrete cues through an injectable sound device. The browser adapter owns a bounded Web Audio graph and releases completed transient sounds. |
| PixelMap.Controls | Uno input and accessible keyboard navigation, scoped browser gestures, physical-eye cameras and follow rigs, viewport tile scheduling, journey orchestration, and direct shared-device WebGpuCanvasElement presentation. Balanced pixel styles adapt resolution to CPU submission cost and return to 2-DIP pixels at rest; High and Ultra preserve the 2-DIP grid. |
| PixelMap.App | Responsive XAML explorer, search, city guide, compass, scale, lighting, shareable camera links, journey planner and free-roam HUD with independent movement/look sticks. |

## WebGPU rendering

`WebGpuMapRenderer` draws into the texture supplied by Uno's `WebGpuCanvasElement`. Its device and queue are borrowed; the map owns and releases its pipelines, bind groups, buffers and intermediate textures. Terrain, buildings, route ribbons, vehicle models, checkpoint gates, particles and map labels are GPU draws. There is no production WebGL context, framebuffer readback, image transfer, or per-frame Skia overlay bitmap.

Terrain geometry, elevation layers, artwork and building models are retained across camera changes. Terrain patches are instanced; building indices use the existing conservative visibility selection. Camera-relative coordinates preserve precision around the globe. The renderer queries the device limits, bounds attachment and mesh memory, and releases replaced resources on resize and unload.

Normal and Roblox styles add a directional sun shadow map with percentage-closer filtering, GGX-based material lighting, sky illumination, height fog, an HDR intermediate target, tone mapping and a small bloom resolve. High and Ultra use 4× multisampling for smooth geometry. Pixel and Minecraft preserve their logical pixel grid and use nearest-neighbor resolve. These are implemented rendering features; they do not establish a measured hardware frame-rate or commercial AAA visual-quality claim.

Map labels reuse the existing Unicode layout, collision and visibility rules. A bounded cached font atlas supplies texture samples to GPU glyph quads. Route and vehicle geometry uses the map's homogeneous projection and depth convention. Data decoding, glyph rasterization on cache misses, terrain artwork preparation, label layout and sparse picking still run on the CPU. Those asset and interaction operations are separate from the WebGPU rendering passes.

The status bar reports **WebGPU active**. `RenderingQuality`, `IsGpuRenderingActive` and `RenderStyle` describe the public control state. The former `IsGpuRenderingEnabled` switch was removed because the control now has one rendering backend. Uno describes its WebGPU backend as experimental; the exact package versions, lifecycle contract, remaining quality boundaries and validation results are documented in the [migration guide](docs/uno7-webgpu-migration.md).

The earlier [performance methodology and results](docs/performance.md) describe the pre-migration CPU/GL/Skia paths. Their timing and memory improvements must not be read as a measured WebGPU speedup.

## Build and test

```sh
dotnet workload install wasm-tools
dotnet run -c Release --project tests/PixelMap.Data.Checks
dotnet run -c Release --project tests/PixelMap.Rendering.Tests
dotnet run -c Release --project tests/PixelMap.Rendering.Tests -- --webgpu-overlay
dotnet run -c Release --project tests/PixelMap.Routing.Tests
dotnet run -c Release --project tests/PixelMap.Simulation.Tests
node tests/browser/audio.cjs
node tests/browser/input-adapter.cjs
node tests/browser/webgpu-lifecycle.cjs
dotnet publish src/PixelMap.App -f net10.0-browserwasm -c Release -o artifacts/browser
python3 -m http.server 8000 --directory artifacts/browser/wwwroot
```

Open http://localhost:8000/. Append `-- --benchmark --output artifacts/rendering` to the renderer test command for fixed-resolution timing and exact image hashes across the bundled scene, camera modes, palettes and vehicle journeys. Add `--visual-only` to capture images without timing, `--filter persp65` to select cases, or `--compare path/to/baseline/report.json` to check a previous capture. The routing tests use deterministic data by default. Append `-- --live` for explicit public-provider integration checks.

The browser checks use headed Chromium. Follow the [browser setup and presentation preflight](tests/browser/README.md) to install its pinned dependencies and, on a Linux server, Xvfb and the Vulkan runtime. With the application server still running, use:

```sh
PIXELMAP_SHOTS=artifacts/webgpu xvfb-run -a -s '-screen 0 1600x1200x24' \
  node tests/browser/webgpu.cjs http://localhost:8000/
```

On an interactive desktop, omit the `xvfb-run` wrapper. The independent presentation preflight checks that WebGPU pixels reach browser screenshots before exercising the application.

The retained GL renderer is excluded from the production assembly and compiled into the regression test assembly. On Linux with EGL and Mesa installed, the opt-in GLES reference checks exercise compiled terrain and building shaders, hardware depth, CPU/GPU image comparisons, navigation overlays, retained-frame lifetime and high-DPI composition. Add `--gpu-benchmark` for fixed-resolution CPU/GPU timings that include synchronous framebuffer readback.

```sh
LIBGL_ALWAYS_SOFTWARE=1 EGL_PLATFORM=surfaceless dotnet run -c Release \
  --project tests/PixelMap.Rendering.Tests -- --gpu-terrain --gpu-benchmark \
  --gpu-output=artifacts/gpu-terrain
```

The PR workflow publishes the AOT-enabled Uno 7 browser app and exercises that exact artifact. The Pages workflow applies the same checks to main before deployment. The WebGPU probe observes real shader compilation, pipeline creation, depth state, queue submissions and readback calls; screenshots and diagnostics are retained as CI artifacts. It exercises the four styles, quality presets, imagery/elevation/relief, camera movement, all six route demonstrations, walking, resize, high-DPI mobile pinch, unsupported-browser recovery and shared-device loss. Native reference tests continue to cover projection, sparse visibility, route/simulation semantics, terrain and label compatibility. `PIXELMAP_SHOTS` selects the browser evidence directory.

The old `smoke.cjs`, `journeys.cjs`, `terrain.cjs`, `explore.cjs` and GL-oriented `performance.cjs` scripts are historical pre-migration probes and are no longer the production CI entry points. They assume the retired CPU/GPU switch and presentation paths. `npm test --prefix tests/browser -- http://localhost:8000/` runs the current WebGPU integration suite with the same headed browser setup; prefix it with `xvfb-run -a -s '-screen 0 1600x1200x24'` on a Linux server.

## Sources and coverage

[OpenFreeMap](https://openfreemap.org/) serves [OpenMapTiles](https://openmaptiles.org/) vector data derived from [OpenStreetMap contributors](https://www.openstreetmap.org/copyright). See [the seed attribution and regeneration instructions](src/PixelMap.Data/Seed/README.md). Coverage and recorded building heights vary. Footprints without height data use an 8 m rendering fallback. Windows and lighting are synthesized by the renderer. The zoom-14 source tiles are overscaled for close inspection.

Earth uses the EOxCloudless 2016 mosaic from EOX IT Services GmbH, containing modified Copernicus Sentinel data from 2016 and 2017. Each tile is processed for the pixel-art view. Imagery has approximately 10 m source resolution. Buildings and labels use independent vector data, so features can differ between acquisition dates. The app includes imagery for the world overview, its Manhattan opening and northern Lake Garda. [EOxCloudless](https://cloudless.eox.at/) · [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) · [Imagery attribution and source](src/PixelMap.Data/Imagery/README.md).

Address and place search uses [Photon](https://photon.komoot.io/). The public service permits moderate use. Configure a dedicated or self-hosted Photon endpoint for a high-volume deployment.
