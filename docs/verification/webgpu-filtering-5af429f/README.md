# WebGPU filtering verification — run 18

This bundle preserves the original CI screenshots, reports and selected GPU
traces for the terrain, facade and HDR bloom changes in
[`5af429f`](https://github.com/wieslawsoltes/UnoPixelMap/commit/5af429f94d016364f840df0f22d0fc8c02e010c4).
Both **Debug and Release passed all 34 browser scenarios** in
[run 18](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43167877049).

## Provenance and integrity

The workflow checked out GitHub's PR merge commit
`51e4a6babf33fd320e072fe16665d099ebc4c295`. Its verified tree
`2dc51e10eb271af91f40fe6e13bf33cda6c10413` equals the source commit's tree.
Its parents are base `c6a26075b5767e2aa82e87e949194e82c0e9cb1e` and source
`5af429f94d016364f840df0f22d0fc8c02e010c4`.
[GitHub commit metadata](https://api.github.com/repos/wieslawsoltes/UnoPixelMap/git/commits/51e4a6babf33fd320e072fe16665d099ebc4c295)
records that relationship.

| Job | Result |
|---|---|
| [Native regression](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43167877049/job/131013902327) | Passed |
| [Browser presentation](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43167877049/job/131013902682) | Passed |
| [Debug browser](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43167877049/job/131014155874) | 34 scenarios passed |
| [Release browser](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43167877049/job/131014155567) | 34 scenarios passed |

[manifest.json](manifest.json) records original artifact IDs, verified ZIP
SHA-256 digests, publish package IDs, every retained file's byte count and hash,
and the hash of every unmodified JSON payload in [gpu-traces.zip](gpu-traces.zip).
The archive contains 11 Release and four Debug traces under configuration
subdirectories. PNGs and original reports were copied byte-for-byte; no image
editing, resampling or JSON reserialization was applied to those originals.

The original Debug and Release `shader-fixtures.json` files are byte-identical:
`d5af65aced48c6189aed990b32150d2d7814a99c38e4d25d0b70bf28f1ab8280`.
Both source hashes are recorded even though only one copy is retained.
[metrics.json](metrics.json) is explicitly derived; the unchanged reports and
trace payloads are the primary evidence.

ZIP entry modification times are retained in the manifest. ZIP does not encode
a timezone, and these filesystem timestamps are not GPU execution timings.
The selected report frame counts match their named trace submission IDs.
`deviceInfo.timeMs` records relative device creation time, not screenshot time.

## Submitted GPU work

Every inspected application context has **one shared device**, **zero GPU or
render-graph errors**, **zero unexpected device losses**, **zero image readbacks**
and **zero GL contexts**. The final desktop traces record 48 submissions / 1,169
draws in Debug and 45 submissions / 1,103 draws in Release. Both mobile traces
record 12 submissions, with 89 Debug and 88 Release draws. These accumulated
counts reflect different asynchronous frame arrival and are not performance
comparisons.

The first Normal capture batches all 96 pending physical artwork layers into
eight compute dispatches and 768 copies. At the final smooth desktop and mobile
captures, all 768 lower-mip subresources match their latest base-upload versions.
Actual terrain draw bindings show a nine-level sRGB view with linear minification,
magnification and mip filtering, requested 8× anisotropy and LOD clamp 0–8.
Pixel draws bind a single-level unorm view with nearest filtering and LOD zero.

High submits five bloom downsample and four upsample passes; Ultra submits six
downsample and five upsample passes. These precede final composition. Photograph
captures preserve color/depth across the first scene pass, then discard final
MSAA/depth attachments. Single-sample Pixel color remains stored for composition.

**Warm walking proof belongs to Debug submission 43:** 64 uploaded bytes, zero
mip dispatches/copies, zero shadow passes, and 11 render passes comprising the
scene, nine bloom passes and presentation. Release walking submission 40 still
processes eight dirty layers and uploads 2,438,148 bytes; it is not the same warm
sample. Both configurations independently record a 64-byte, zero-mip-work mobile
pinch frame at submission 11. Ultra quality changes and the final desktop captures
also reuse their terrain mip chains.

## Allocation bounds

These independently observed attachment totals agree in Debug and Release.
They include bloom and any active photograph texture, and exclude resident
meshes, artwork arrays, shadow maps and implementation-specific metadata.

| Capture | Scene size | Attachment bytes | Bloom bytes included |
|---|---:|---:|---:|
| Normal High | 1200 × 699 | 51,443,488 | 4,470,688 |
| Normal Ultra | 1200 × 699 | 51,451,848 | 4,479,048 |
| Normal Balanced | 1200 × 699 | 10,065,600 | 0 |
| Normal photograph High | 1200 × 699 | 58,153,888 | 4,470,688 |
| Pixel photograph | 600 × 350 | 4,200,000 | 0 |
| Walking High | 1200 × 683 | 50,266,288 | 4,368,688 |
| Initial mobile Normal, DPR 2 | 780 × 1472 | 70,407,600 | 6,110,640 |
| Mobile Pixel after pinch | 195 × 368 | 861,120 | 0 |

The largest observed attachment allocation is 70,407,600 bytes, below the
268,435,456-byte attachment budget. The 96-layer artwork chain occupies
33,554,304 bytes; its lazily allocated scratch texture adds 6,291,456 bytes and
its index buffer adds 384 bytes. The peak reported Release resident allocation
is 207,719,882 bytes; adding its attachments gives 257,994,530 nominal bytes.
The attachment budget is not a global GPU-memory ceiling.

## Numerical shader checks

| Executed GPU fixture | Comparisons | Maximum observed error |
|---|---:|---:|
| Terrain mip reduction | 266,256 encoded bytes | 1 encoded byte |
| HDR bloom, six cases | 22,832 channels | 0.0015625 absolute |
| Facade filtering, 558 cases | 4,464 channels | 0.000622724 absolute |
| Facade continuity, 108 pairs | 108 boundaries | 0.000150681 delta |

Terrain fixtures preserve 1,048,576 base-level bytes exactly, retain a clean
sentinel layer, and exercise nonsequential dirty IDs plus unused buffer capacity.
The black/white checkerboard matches the linear-light reference exactly at all
sampled levels. Facade normalized error reaches 0.311362 of its allowed tolerance.
Debug and Release produced identical numerical reports.

The separate original [Dawn Null report](null-validation.json) validates ten WGSL
modules and 32 pipeline layouts. Null performs no pixel rendering; actual shader
execution and screenshots are established by the browser reports above. The
intentional device-loss scenarios stop submissions at 3 → 3 in Debug and 2 → 2
in Release and display the recovery message.

## Original Release images and qualitative limits

| Image | What it records |
|---|---|
| [Normal](style-normal.png) | Smooth city terrain, facades and High bloom |
| [Roblox](style-roblox.png) | Coverage-filtered facades and remaining roof-stud detail |
| [Night](lighting-night.png) | Emissive windows and HDR scattering |
| [Normal photograph](photograph-normal.png) | Photograph continuation with smooth composition |
| [Pixel photograph](photograph-pixel.png) | Nearest artwork and preserved photograph path |
| [Walking High](free-roam-walk.png) | Street-level facade and ground magnification |
| [Walking Ultra](free-roam-walk-ultra.png) | Additional shadow and bloom levels |
| [Initial mobile Normal](mobile-normal-initial-high-dpi.png) | DPR 2 output at 780 × 1688 screenshot pixels |

The walking HUD records **31 m travelled in Release and 38 m in Debug**. These
are different simulation positions, not an aligned visual comparison; they are
travel distances, not camera elevations. The ground still reveals magnified,
limited-resolution baked artwork, and the Roblox roofs retain dense procedural
stud detail. Improving those sources and filtering roof-stud footprints remains
separate quality work. The facade and bloom fixtures do not prove those details
are solved, temporal stability across all motion, or AAA engine visual parity.

All browser rendering here uses Chromium 156 / SwiftShader. Recorded CPU
preparation/submission counters are not hardware FPS or GPU-duration benchmarks.
