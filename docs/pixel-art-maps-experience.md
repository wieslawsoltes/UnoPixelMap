# Pixel art and map experience

This change improves the visual language and execution cost of Pixel rendering,
and replaces the surrounding application chrome with a map-first desktop and
mobile experience. It builds on `d014060af60b0b961b0b5e52f39359525da2435b`.

## Rendering goals

- Deliberate, readable pixel clusters rather than high-frequency texture noise.
- Coherent material colors, restrained detail, and stable shading as the camera
  moves through real three-dimensional terrain and buildings.
- A faster Pixel-specific rendering path with explicit work and memory counts.
- Native-density text and usable interaction at every supported quality level.
- Independent correctness coverage for Normal, Minecraft, and Roblox rendering.

Visual changes are intentional in Pixel mode. Performance comparisons must retain
their raw samples and distinguish geometric/camera correctness from the new
artwork's appearance; historical image equality is not a visual-quality target.

## Interface goals

The map occupies the full viewport. Desktop search, place information, and route
planning use a floating sidebar. Phones use a bottom sheet with accessible
collapsed and expanded states. Compact, grouped controls expose location,
orientation, projection, layers, and appearance without a full-width toolbar.

The design takes its interaction hierarchy from Apple's public Maps guidance:

- [Maps human interface guidance](https://developer.apple.com/design/human-interface-guidelines/maps)
- [Maps on iPhone](https://support.apple.com/guide/iphone/iph6ef6ef377/ios)
- [Maps on Mac](https://support.apple.com/guide/maps/welcome/mac)

Pixel Atlas keeps its own name, artwork, and implementation. Existing search,
guides, sharing, directions, terrain/imagery layers, quality presets, camera tools,
and free-roam simulations remain accessible. Touch targets, safe areas, keyboard
focus, and panel gestures must work without interfering with map gestures.

## Validation

### Implemented rendering

Pixel has separate Day, Dusk and Night palettes. The twelve building material
slots, geometry, seeds, native-density label colors and picking contract remain
unchanged. Four world-facing wall tones separate warm light from cool shade;
camera rotation does not rotate the light. Window details form connected one-,
two- or four-room clusters according to their projected footprint. Unresolved
walls become quiet material planes. Readable roofs receive sparse service panels
and world-lit trim; edges shorter than two art pixels are omitted.

The Pixel fragment path performs one final material lookup and at most one
integer lamp lookup inside a resolved window. It avoids the previous far-window
mip filtering and continuous wall mixtures. A 17-column material texture retains
the original seven columns verbatim for the other styles and precomputes Pixel's
wall, accent and roof ramps.

Pixel scene and optional photograph attachments store encoded palette bytes in
RGBA8. Other styles keep RGBA16F, their existing resolutions and antialiasing.
Three retained scene pipeline variants cover Pixel RGBA8 1x, HDR 1x and HDR 4x.
This preserves dark palette colors and reduces nominal scene attachment storage:

| Pixel attachments | Previous bytes/art pixel | New bytes/art pixel | Reduction |
| --- | ---: | ---: | ---: |
| Color and depth | 12 | 8 | 33.3% |
| Color, depth and photograph | 20 | 12 | 40.0% |

These are format-derived storage counts, not hardware FPS measurements.

Terrain motifs use connected ripples, grass and canopies on a fixed geographic
grid: one motif texel spans 2^-25 of the Mercator world. Signed phase arithmetic
keeps neighboring tiles and mixed source zoom levels aligned. Pixel roads have
stronger casing hierarchy and water receives shoreline underpaint. Normal,
Minecraft and Roblox retain their legacy paint operations and contour order.

Ground classification returns a small value instead of a yield iterator. Road
casings and fills share immutable paths while retaining separate paint passes.
The frozen compatibility fixture keeps all 334 paint operations, reduces native
paths from 334 to 285 and retained contour points from 7,848 to 7,488. Across
73,872 classifications, measured managed allocations fall from 5,318,784 bytes
to zero. This is the isolated classification cost, not total frame allocation.

### Implemented interface

Desktop uses a hideable 360px floating sidebar; mobile uses a full-width bottom
sheet with compact, medium and expanded detents. Dragging, handle activation,
Up/Down, Home/End and Escape operate the same sheet. Search focus expands it.
Directions, place information, city guides, sharing and camera tools occupy the
same surface, while a grouped rail exposes location, compass, 2D/3D, zoom,
base layers and appearance. Free-roam and route surfaces share the light palette.

Controls use 44px or larger touch targets. Chrome gestures do not move the map,
and changing panel height never changes its physical viewport. An event-driven
browser adapter supplies safe areas and the visual viewport's height and origin,
so controls remain visible when a software keyboard shrinks or pans that region.
Detach invalidates outstanding location callbacks and clears drag suppression.

### Verification and evidence

The native checks cover authored palette/motif fixtures, signed world/LOD seams,
allocation-free motif writes and classification, and exact legacy paint/contour
parity over 4,617 feature combinations. Nearby CPU images retain both 32 new
palette goldens and all 32 historical hashes after exact semantic-color
substitution. Existing depth, picking, packed geometry, terrain-copy and
lifecycle suites remain enabled.

The independent WebGPU fixture executes the production Pixel WGSL helpers and
resolve. It checks connected clusters, unresolved surfaces, every encoded gray,
all three palettes, photographs, premultiplied overlays, UNORM/sRGB targets and
exact nearest-neighbour blocks. Browser integration exercises desktop panels,
mobile detents and native touch dragging, pinch, styles, quality, terrain,
journeys, device loss and native-density labels. CI retains PNG captures and raw
reports for Debug and Release AOT packages on [PR #3](https://github.com/wieslawsoltes/UnoPixelMap/pull/3).

The paired benchmark matches the physical 1200x700 map extent and map-relative
native input despite the changed UI origins. It retains full screenshots and
compares a declared 688x440 map region, with a guard that rejects overlapping
controls. Camera, geometry, picking and unchanged-style image gates remain
strict. Only Pixel's cross-revision artwork changes are declared against the
exact original baseline in `tests/pixel-art-transition.json`; repeat images
within each revision must still agree. The ABBA reports retain numerical image
differences, raw timings, real upload/allocation work and zero-sample windows.
Software GPU timing results must not be described as physical hardware FPS.
