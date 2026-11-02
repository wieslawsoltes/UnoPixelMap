# UnoPixelMap · Pixel Atlas

[Explore the live map](https://wieslawsoltes.github.io/UnoPixelMap/) · [Earth in 3D](https://wieslawsoltes.github.io/UnoPixelMap/#v2/40.704100/-74.016300/15.750/-18.00/55.00/Day/Earth) · [Earth in 2D](https://wieslawsoltes.github.io/UnoPixelMap/#v2/40.704100/-74.016300/15.750/-18.00/0.00/Day/Earth)

Pixel Atlas is an interactive Uno Platform map control for the browser. It streams worldwide vector tiles and turns building footprints, height and minimum-height attributes into explorable isometric pixel art. A world overview and the opening Lower Manhattan neighborhood are bundled with the app.

![Pixel Atlas displaying Lower Manhattan in a 1200 × 780 Chromium viewport](docs/manhattan.png)

[Explore the East River waterfront in detail](docs/seaport.png) · [See a live search result for Racibórz, Poland](docs/raciborz.png)

## Explore

Drag to pan, scroll or pinch to zoom, and right-drag to orbit and tilt. Double-click to zoom at the pointer. Click a roof, façade, street or park to inspect it. Use Map or Earth to choose the ground layer, then choose 2D or 3D independently. Earth transforms natural-color satellite imagery into pixel art and keeps map labels and buildings in the same coordinate system. The View menu selects the Day, Dusk or Night palette. Share a view to preserve its exact camera and lighting.

Search by place name, address or decimal latitude and longitude. The city guide includes Lower Manhattan, Kraków, Shibuya, Barcelona, Paris and Singapore. With the map focused, use the arrow keys to pan, +/− to zoom, Q/E to rotate, P to switch projection, Home to return to Manhattan and Escape to clear a marker.

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

| Project | Responsibility |
| --- | --- |
| PixelMap.Data | Bounded MVT protobuf decoding, source catalog, request coalescing, cancellation and separate byte-bounded caches for vector tiles and satellite imagery. Photon geocoding with an injectable endpoint. |
| PixelMap.Rendering | Wrapped Web Mercator camera, cached terrain blocks, palette shaders, orthographic building rasterization with depth-tested walls, roof holes, shaded façades, map labels and feature picking. |
| PixelMap.Controls | Uno input and accessible keyboard navigation, camera animation, camera and palette dependency properties, viewport tile scheduling and the Skia drawing surface. During motion the control adjusts pixel resolution to measured frame cost, then returns to 2-DIP pixels at rest. |
| PixelMap.App | Responsive XAML explorer, search, city guide, compass, scale, lighting and shareable camera links. |

## Build and test

```sh
dotnet workload install wasm-tools
dotnet run -c Release --project tests/PixelMap.Data.Checks
dotnet run -c Release --project tests/PixelMap.Rendering.Tests
dotnet publish src/PixelMap.App -f net10.0-browserwasm -c Release -o artifacts/browser
python3 -m http.server 8000 --directory artifacts/browser/wwwroot
```

Open http://localhost:8000/. To measure native renderer time on the bundled scene, append `-- --benchmark` to the renderer test command. The Pages workflow builds the browser app from main and publishes the static WebAssembly output.

## Sources and coverage

[OpenFreeMap](https://openfreemap.org/) serves [OpenMapTiles](https://openmaptiles.org/) vector data derived from [OpenStreetMap contributors](https://www.openstreetmap.org/copyright). See [the seed attribution and regeneration instructions](src/PixelMap.Data/Seed/README.md). Coverage and recorded building heights vary. Footprints without height data use an 8 m rendering fallback. Windows and lighting are synthesized by the renderer. The zoom-14 source tiles are overscaled for close inspection.

Earth uses the EOxCloudless 2016 mosaic from EOX IT Services GmbH, containing modified Copernicus Sentinel data from 2016 and 2017. Each tile is processed for the pixel-art view. Imagery has approximately 10 m source resolution. Buildings and labels use independent vector data, so features can differ between acquisition dates. The app includes imagery for the world overview and its Manhattan opening. [EOxCloudless](https://cloudless.eox.at/) · [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) · [Imagery attribution and source](src/PixelMap.Data/Imagery/README.md).

Address and place search uses [Photon](https://photon.komoot.io/). The public service permits moderate use. Configure a dedicated or self-hosted Photon endpoint for a high-volume deployment.
