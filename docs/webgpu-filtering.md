# WebGPU filtering and HDR quality

This follow-up to the Uno 7 migration improves three concrete image-quality paths:
terrain minification, facade material filtering and HDR light scattering. It also
removes unused photograph storage and unnecessary final attachment stores.

## Validation status

Source `5af429f94d016364f840df0f22d0fc8c02e010c4` passed all four jobs in
[run 18](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43167877049):
the full native/reference regression, independent presentation preflight, Debug
interpreted browser build and Release AOT browser build. Both published builds
passed all **34 scenarios**, package integrity and two byte-preserving finalization
passes. The existing Skia obsolete-API warnings remain.

The [permanent verification record](verification/webgpu-filtering-5af429f/README.md)
preserves both original reports, identical numerical shader results, selected
original Release screenshots, compressed original GPU traces and file hashes.
Earlier migration evidence remains in [the baseline record](verification/uno7-webgpu-dad16fc/README.md).
An additional Dawn Null check compiled all ten production WGSL modules and
validated 32 explicit pipeline contracts with no messages or validation errors;
the browser checks separately establish actual GPU execution and raster output.

| Actual GPU fixture | Observed result in both configurations |
| --- | --- |
| Terrain reduction | 266,256 compared bytes; maximum difference one encoded byte |
| Original artwork and untouched layer | 1,048,576 base bytes preserved exactly; clean layer unchanged |
| Facade material reference | 558 cases / 4,464 channels; maximum absolute error 0.000622724 |
| Filtering continuity | 108 pairs; maximum channel delta 0.000150681 |
| HDR bloom | Six fixtures / 22,832 channels; maximum absolute error 0.0015625; constant fields exact |

Application captures report one shared GPU device and zero unexpected GPU errors,
losses, persistent render-graph errors, map readbacks or WebGL contexts. The
intentional device-loss scenarios stop new map submissions. A retained Debug
walking frame uploads only 64 bytes and submits no terrain mip work or shadow
passes; Release's walking capture still contains late tile uploads, so the warm
walking measurement belongs specifically to Debug. Both configurations verify
physical layer reuse and current mip generations through independent queue probes.

Visual review found intact facade/roof geometry, restrained night bloom, coherent
photograph composition and sharp high-DPI labels. Close-up ground artwork still
reveals source texels, and Roblox roofs retain fine procedural stud patterns.
The captures are stylized map content and do not prove temporal stability or a
commercial AAA acceptance result. Cross-configuration walking views differ
(31 metres travelled in Release, 38 in Debug); they are qualitative comparisons.

## Terrain filtering

The old terrain path forced mip zero for every style. Normal and Roblox now sample
a complete 256-to-1 nine-level chain with explicit UV gradients, trilinear filtering
and requested 8x anisotropy. A compatible sRGB texture view makes filtering operate
in linear light. The shader receives linear RGB directly for ordinary Normal
terrain; existing encoded-color elevation and Roblox effects convert their modified
colors once before physical lighting. Pixel and Minecraft retain their original
uploaded bytes, unorm view, nearest sampler and explicit level-zero lookup. Heights
remain exact integer-coordinate texture loads.

New or reassigned artwork layers become dirty. On the next smooth draw, eight
compute passes batch those physical layers using 8x8x1 workgroups. Each pass reads
the preceding sRGB mip, averages four linear samples, encodes RGB into an unorm
storage array and copies the dirty layers into their final mip. The next dispatch
sees the completed copies through ordinary command ordering. All work remains in
the existing frame encoder and single submission. No queue wait, CPU image transfer
or extra GPU device is involved. Successful submission acknowledges generations;
failed encoding and intervening Pixel frames preserve outstanding work.

The reusable scratch array is necessary because Silk.NET 2.23's public texture-view
descriptor has no usage override. The final sRGB sampling texture therefore has no
storage usage. Its separate 128x128 scratch array has one level and is reused for
every reduction. This uses supported public APIs without changing Uno's ABI.

At 96 layers, the complete artwork chain occupies 33,554,304 bytes. The first smooth
dirty batch lazily adds 6,291,456 scratch bytes and a 384-byte layer table. All are
included in nominal resident bytes. See [the detailed design](terrain-mipmaps.md).
Anisotropy is a request; the implementation may clamp the effective quality.

Mipmaps improve minification and oblique sampling. They cannot add detail absent
from the 256x256 source artwork, so close street views can still expose its texels.

## Facade materials

Normal previously filtered window color while retaining binary glass roughness,
metallicity and night-emission decisions. Roblox used binary panes and a local
`fract()` tint. Those channels could change abruptly during camera motion.

The new shader analytically integrates the periodic rectangular window over the
axis-aligned derivative footprint. Normal uses that coverage for linear albedo,
metallic response, night emission and coverage-weighted squared roughness (GGX
alpha). The material still evaluates one GGX lighting lobe. For footprints no larger
than one room per axis, covered-window centroids make the room texture's bilinear
weights match the actual overlap with neighboring lamps. Wider footprints smoothly
transition to the retained room mip chain.

Roblox uses the same coverage approach and integrates the first moment of each
visible window interval. This preserves the mean vertical pane tint when a pixel
spans a seam or several stories. No new allocation, upload or rendering pass is
needed. Pixel and Minecraft window paths are preserved.

The footprint is an axis-aligned reconstruction box, not an exact projected pixel
polygon. Normal lamp filtering beyond one room uses an isotropic, quantized mip
approximation; it is not an exact local integral for a highly elongated footprint.
The blended single GGX lobe is also an approximation to separate wall/glass lobes.
Tests distinguish exact reference cases from continuity checks of that approximation.

## HDR bloom and attachment lifetime

The old resolve gathered nine full-resolution bright-pass samples at a fixed small
radius. The new filter operates before exposure and tone mapping on linear HDR
radiance, using a hierarchy of separate RGBA16F textures:

| Stage | Operation |
| --- | --- |
| First downsample | Composite the optional photograph, then apply a normalized 13-tap filter |
| Remaining downsample levels | Ceil-half each dimension, stopping at 1x1 or the quality limit |
| Reconstruction | Nine-tap tent filter of the coarser level, mixed with fine detail using a 0.65 scatter weight |
| Final resolve | Mix original and filtered HDR radiance using the quality's scattered-light fraction, then exposure, ACES and output encoding |

High uses at most five levels and a 0.035 final fraction; Ultra uses six and 0.055.
There is no brightness threshold or per-channel subtraction. Normalized kernels
preserve constant radiance and fixed color ratios. Clamp-to-edge and odd-grid
resampling remain boundary approximations: this is an approximation to optical
scattering, not a calibrated lens response or a proof of exact finite-image flux.

Balanced, Pixel and Minecraft allocate no bloom targets and submit no bloom passes.
They also avoid compiling bloom pipelines until a mode that uses them is selected.
Screen labels are drawn after bloom; world overlays are part of the HDR scene.
Separate source/output textures prevent read/write feedback within a render pass.

The renderer now allocates its full-size photograph texture only when needed.
Without it, base scene attachments cost 56 bytes per pixel at 4x MSAA or 12 bytes
at one sample, saving eight bytes per pixel before bloom is added. At 1920x1080,
High's bloom pyramid adds 11,033,280 bytes. Total scene plus bloom is 127,154,880
bytes without a photograph, or 143,743,680 with one.

The unchanged 256 MiB attachment cap includes every actual odd-sized pyramid
allocation, optional photograph and sample count. Resolution fitting evaluates
that exact inventory. Shadow storage has its separate 64 MiB cap; cached assets
and the borrowed Uno target are accounted separately. These are nominal payload
budgets, not driver allocation or total process-memory measurements.

The last scene pass discards multisample color after resolve and discards depth.
The first photographic pass stores both for the continuation's load. Resolved
single-sample color and shadow depth remain stored. These operations remove
unneeded storage/bandwidth requirements; no hardware frame-rate gain is claimed.

## Independent verification

Native tests check independent facade integrals against point supersampling,
periodic energy, resolved rooms, kernel normalization/symmetry, HDR and hue
invariants, exact odd-size resource inventories, 600 bounded resolution fits and
terrain dirty-generation behavior across failure/reassignment/style changes.

The browser runs synthetic production-WGSL fixtures on a separate test device:

- 558 facade reference cases, comparing all eight material outputs with documented tolerances.
- 108 pairs around one-, two- and four-room filtering transitions, comparing both smooth styles.
- Six bloom cases: dim/HDR constants, photographs, odd extents, a one-axis pyramid and impulses.
- Twelve terrain readbacks across dirty/clean layers and three mip levels, comparing 266,256 bytes and preserving all 1,048,576 original base bytes.

Only these synthetic fixtures read pixels back. The live application must still
submit through Uno's one shared device with zero texture readbacks or WebGL calls.
An independent probe follows texture/view/bind-group identity through actual
submitted compute, copy and render operations. It checks mip freshness, sampled
formats and samplers, bloom source wiring, photograph lifetime, store/load choices
and observed attachment bytes. The existing style, shadow, navigation, DPI, input
and device-loss checks remain active.

These tests establish correctness and bounded resource use for the implemented
features. They do not measure target-hardware GPU frame times or establish a broad
AAA visual acceptance result. No temporal accumulation, SSR, global illumination,
photogrammetric assets or GPU-driven streaming is added by this follow-up.

## Primary references

- [WGSL textureSampleGrad and derivative rules](https://www.w3.org/TR/WGSL/).
- [WebGPU sampler anisotropy, texture formats and usage scopes](https://gpuweb.github.io/gpuweb/).
- [Jorge Jimenez: Next Generation Post Processing in Call of Duty: Advanced Warfare](https://www.iryoku.com/next-generation-post-processing-in-call-of-duty-advanced-warfare/), for normalized multiscale HDR bloom and threshold-free scattering.
- [Silk.NET 2.23 TextureViewDescriptor](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/WebGPU/Silk.NET.WebGPU/Structs/TextureViewDescriptor.gen.cs), for the public ABI limitation behind the scratch-array design.
