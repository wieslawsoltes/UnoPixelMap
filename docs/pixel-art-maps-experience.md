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

Implementation details and measured outcomes will be recorded here as changes
land. Validation includes rendering and navigation regression checks, focused
pixel-art algorithm fixtures, browser shader checks, desktop/mobile interaction
checks, representative before/after captures, and Release AOT measurements.
The pull request will be merged only after the completed implementation passes
its applicable checks and the final interface has been visually inspected.
