# Uno Platform 7 and WebGPU migration

## Scope and status

This branch migrates Pixel Atlas from Uno.Sdk 6.7.30 to the published Uno.Sdk
7.0.5 release and replaces the browser map's OpenGL/Skia presentation bridge
with Uno's shared-device `WebGpuCanvasElement`.

The migration is in progress. This document is updated with each implementation
and verification stage. The quality target is a modern real-time 3D renderer;
that target is not a claim that this branch already meets a commercial AAA
game's content, lighting, hardware coverage, or performance requirements.

## Verified upstream contract

- [Uno 7 migration guide](https://platform.uno/docs/articles/migrating-to-uno-7.html).
- [WebGPU rendering backend](https://platform.uno/docs/articles/features/using-webgpu-rendering.html).
- [WebGpuCanvasElement](https://platform.uno/docs/articles/controls/WebGpuCanvasElement.html).
- [Uno.Sdk 7.0.5](https://www.nuget.org/packages/Uno.Sdk/7.0.5).

Uno's WebGPU backend is explicitly described as experimental upstream. The map
uses the public shared device/queue/context API, not reflected host internals.
The `WebGpuCanvasContext` API and its borrowed handles belong to Uno. Application
resources must be released on teardown; context handles must never be released
by the map. The render target is valid only within `RenderOverride`.

## Migration stages

1. Upgrade the SDK, library references, browser host, and build validation.
2. Replace the map surface with `WebGpuCanvasElement` and remove the production
   GL framebuffer/readback path.
3. Implement retained WebGPU terrain and building resources and WGSL shaders.
4. Move route, vehicle, gate, and label presentation into the WebGPU pipeline.
5. Validate projection, depth, rendering styles, input, resource lifetime,
   package contents, and published browser startup.

## Preservation requirements

Preserve vector and Earth imagery layers; elevation, relief, and terrain picking;
Pixel, Normal, Minecraft, and Roblox styles; map and free cameras; route planning;
all journey and free-roam vehicle modes; mobile/desktop input; feature selection;
and existing application packaging/cache identity protections.

GPU-unavailable startup must present an actionable message. A shader or device
failure must not silently switch the application to the removed renderer.

## Verification record

The restored package graph has been verified: Uno.Sdk 7.0.5 resolves Uno.WinUI,
Composition.WebGpu and Graphics3DWebGpu 7.0.32, Silk.NET.WebGPU 2.23.0, SkiaSharp
4.151.1 and the .NET 10 browser bootstrap 10.1.1. No Uno 6 graphics package or
Silk.NET.OpenGL dependency remains in the production project graph.

SkiaSharp is retained for CPU asset preparation (tile textures, image decoding,
and glyph atlases) and reference-renderer tests. The browser registers only the
WebGPU graphics provider. The `Skia` feature supplies the matching native asset
libraries; it does not select a Skia rendering or presentation fallback.

The previous optional paired GL/compositor/readback benchmark job has been
retired because its instrumentation measures APIs removed by this migration.
Its historical scripts and measurements remain available for interpreting old
results. PR and Pages validation now run the WebGPU browser probe, alongside
the existing native data, rendering-reference, routing, simulation, input,
audio, and package-identity checks.

Native restores succeed with .NET SDK 10.0.403 and the matching wasm-tools
workload. Browser execution and rendering measurements are pending. No frame-rate,
speedup, or visual-parity claim is established by restore success.
