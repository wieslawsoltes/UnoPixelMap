# UnoPixelMap · Pixel Atlas

[Explore the live map](https://wieslawsoltes.github.io/UnoPixelMap/) · [Earth in 3D](https://wieslawsoltes.github.io/UnoPixelMap/#v2/40.704100/-74.016300/15.750/-18.00/55.00/Day/Earth) · [Earth in 2D](https://wieslawsoltes.github.io/UnoPixelMap/#v2/40.704100/-74.016300/15.750/-18.00/0.00/Day/Earth)

Pixel Atlas is an interactive Uno Platform map control for the browser. It streams worldwide vector tiles and turns building footprints, height and minimum-height attributes into explorable pixel art. Choose an overhead, isometric or perspective camera, plan a journey, preview its directions, or drive a pixel vehicle through the map. A world overview, the opening Lower Manhattan neighborhood, and six route demonstrations are bundled with the app.

![Pixel Atlas displaying Lower Manhattan in a 1200 × 780 Chromium viewport](docs/manhattan.png)

[Explore the East River waterfront in detail](docs/seaport.png) · [See a live search result for Racibórz, Poland](docs/raciborz.png)

## Explore

Drag to pan, scroll or pinch to zoom, and right-drag or Shift-drag to orbit and tilt. Two fingers can pinch, twist, pan or tilt in parallel. Double-click to zoom at the pointer. Click a roof, façade, street or park to inspect it. Use Map or Earth to choose the ground layer, then choose 2D or 3D independently. Earth transforms natural-color satellite imagery into pixel art and keeps map labels and buildings in the same coordinate system. The View menu selects the Day, Dusk or Night palette and the perspective lens. Share a view to preserve its camera, focus altitude, projection and lighting. Existing v1 and v2 view links continue to work.

Search by place name, address or decimal latitude and longitude. The city guide includes Lower Manhattan, Kraków, Shibuya, Barcelona, Paris and Singapore. With the map focused, use the arrow keys to pan, +/− to zoom, Q/E to rotate, P to switch projection, Home to return to Manhattan and Escape to clear a marker.

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

Road routes use separate FOSSGIS OSRM foot, bicycle and car networks. Train and boat routes follow connected mapped tracks, ferry lines or fairways within a bounded local corridor. Their times are estimates. Flight routes follow great-circle courses. Timetables, passenger transfers, vessel clearance, flight procedures, live traffic, elevation terrain, and photogrammetric city meshes require additional data providers. The simulation uses mapped geometry with simplified vehicle dynamics. [Routing coverage and source details](src/PixelMap.Routing/README.md)

## How Earth becomes pixel art

| Earth in 3D | Earth in 2D |
| --- | --- |
| ![Isometric Earth at Battery Park with extruded buildings](docs/earth-isometric.png) | ![The same Battery Park waterfront in overhead Earth mode](docs/earth-overhead.png) |

The renderer decodes each satellite tile once. It averages colors in linear light, merging low-contrast 2×2 areas while retaining individual high-contrast samples along shorelines and field boundaries. It selects neighboring colors from an 89-color palette in OKLab and applies a world-aligned 4×4 ordered dither. Panning or rotating leaves that pattern attached to the ground.

Zoom levels 13 and 14 preserve the full 256×256 source grid. Lower zoom levels use 128×128 art tiles. A 24 MiB LRU holds processed textures. In overhead view, translucent building footprints preserve the imagery underneath. Tilting into the isometric projection reveals the extruded walls.

[Explore Lake Garda in Earth mode](https://wieslawsoltes.github.io/UnoPixelMap/#v2/45.687000/10.737000/13.000/30.00/55.00/Day/Earth) · [View the Lake Garda browser capture](docs/earth-lake-garda.png) · [See Earth on a phone-sized viewport](docs/earth-phone.png)

## Reuse the control

Reference `src/PixelMap.Controls/PixelMap.Controls.csproj` from a Skia-rendered Uno app. The browser app uses Uno.Sdk 6.7.30 on .NET 10.

```xml
<Grid xmlns:maps="using:PixelMap.Controls">
    <maps:PixelMapControl x:Name="Atlas" />
</Grid>
```

```csharp
using PixelMap.Rendering;

Atlas.IsEarthImageryEnabled = true;
Atlas.Palette = PixelStyle.Dusk;
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

`IRouteProvider` supplies additional routing backends. `VehicleProfile` defines compatible vehicle dynamics, and `JourneySession.SelectVehicle` installs a profile for the current route. `IJourneyWorld` supplies collision rules. `IJourneyAudioDevice` supplies an audio backend. The browser host deploys `PixelMap.Audio/Browser/journey-audio.js` and attaches a `BrowserAudioDevice`. Other hosts can use the same simulation without browser audio.

| Project | Responsibility |
| --- | --- |
| PixelMap.Data | Bounded MVT protobuf decoding, source catalog, request coalescing, cancellation and separate byte-bounded caches for vector tiles and satellite imagery. Photon geocoding with an injectable endpoint. |
| PixelMap.Rendering | Wrapped Web Mercator camera, cached terrain blocks, palette shaders, orthographic and perspective building rasterization, depth-tested walls and roof holes, map labels, feature picking, route overlays, vehicle sprites and world-anchored particles. Perspective uses homographic ground projection, finite clipping and reciprocal-depth interpolation. |
| PixelMap.Routing | Provider-separated routes and alternatives, bounded local A* rail/ferry searches, great-circle flight paths, ordered stops, maneuver metadata and shared provider-distance route sampling. |
| PixelMap.Simulation | Fixed 60 Hz clock, six vehicle profiles, manual inputs, preview transport, cruise, checkpoint scoring and recovery, spatially indexed mapped collisions and replaceable world rules. |
| PixelMap.Audio | Coalesced motion parameters and discrete cues through an injectable sound device. The browser adapter owns a bounded Web Audio graph and releases completed transient sounds. |
| PixelMap.Controls | Uno input and accessible keyboard navigation, animated cameras and follow rigs, viewport tile scheduling, journey orchestration and the Skia drawing surface. During motion the control adjusts pixel resolution to measured frame cost, then returns to 2-DIP pixels at rest. |
| PixelMap.App | Responsive XAML explorer, search, city guide, compass, scale, lighting, shareable camera links and the journey planner with touch driving controls. |

## Rendering backends

The View menu includes **GPU rendering (experimental)**. It uses Uno's GPU-backed Skia canvas to project cached terrain and Earth textures into a pixel-resolution render target. An indexed `SKVertices` mesh and an SkSL runtime effect resolve that artwork onto the display. Building rasterization supplies a transparent model layer and the depth information used by routes, picking and vehicle effects. GPU snapshots also preserve the photographed ground beneath translucent roofs in overhead Earth mode.

Uno's recording canvas uses lazy picture images whose textures are generated on the GPU during playback. The control uses the public composition-capability check to select that path. Direct GPU canvases use reusable off-screen surfaces. The status bar reports **GPU active** or **CPU fallback** when the option is selected. The compiled shader is reused during adaptive-resolution changes, and the reusable control exposes `IsGpuRenderingEnabled` and `IsGpuRenderingActive`. CPU and GPU blending can have small rounding differences. [Performance methodology and results](docs/performance.md).

## Build and test

```sh
dotnet workload install wasm-tools
dotnet run -c Release --project tests/PixelMap.Data.Checks
dotnet run -c Release --project tests/PixelMap.Rendering.Tests
dotnet run -c Release --project tests/PixelMap.Routing.Tests
dotnet run -c Release --project tests/PixelMap.Simulation.Tests
node tests/browser/audio.cjs
dotnet publish src/PixelMap.App -f net10.0-browserwasm -c Release -o artifacts/browser
python3 -m http.server 8000 --directory artifacts/browser/wwwroot
```

Open http://localhost:8000/. Append `-- --benchmark --output artifacts/rendering` to the renderer test command for fixed-resolution timing and exact image hashes across the bundled scene, camera modes, palettes and vehicle journeys. Add `--visual-only` to capture images without timing, `--filter persp65` to select cases, or `--compare path/to/baseline/report.json` to check a previous capture. The routing tests use deterministic data by default. Append `-- --live` for explicit public-provider integration checks.

```sh
npm ci --prefix tests/browser
(cd tests/browser && npx playwright install chromium)
node tests/browser/smoke.cjs http://localhost:8000/
node tests/browser/journeys.cjs http://localhost:8000/
node tests/browser/performance.cjs http://localhost:8000/ artifacts/browser-visual --visual
node tests/browser/performance.cjs http://localhost:8000/ artifacts/browser-motion --motion
node tests/browser/performance.cjs http://localhost:8000/ artifacts/browser-gpu --motion --gpu
```

The Pages workflow builds the AOT-enabled browser app from main, runs the engine checks, exercises the exact publish artifact with Chromium, and publishes it after verification. Journey browser checks cover all six modes, route alternatives, waypoint picking, perspective projection, manual keyboard/touch inputs and Web Audio activation. Set `PIXELMAP_SHOTS` to save browser captures.

## Sources and coverage

[OpenFreeMap](https://openfreemap.org/) serves [OpenMapTiles](https://openmaptiles.org/) vector data derived from [OpenStreetMap contributors](https://www.openstreetmap.org/copyright). See [the seed attribution and regeneration instructions](src/PixelMap.Data/Seed/README.md). Coverage and recorded building heights vary. Footprints without height data use an 8 m rendering fallback. Windows and lighting are synthesized by the renderer. The zoom-14 source tiles are overscaled for close inspection.

Earth uses the EOxCloudless 2016 mosaic from EOX IT Services GmbH, containing modified Copernicus Sentinel data from 2016 and 2017. Each tile is processed for the pixel-art view. Imagery has approximately 10 m source resolution. Buildings and labels use independent vector data, so features can differ between acquisition dates. The app includes imagery for the world overview and its Manhattan opening. [EOxCloudless](https://cloudless.eox.at/) · [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) · [Imagery attribution and source](src/PixelMap.Data/Imagery/README.md).

Address and place search uses [Photon](https://photon.komoot.io/). The public service permits moderate use. Configure a dedicated or self-hosted Photon endpoint for a high-volume deployment.
