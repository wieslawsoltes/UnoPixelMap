# Building mesh memory and exact construction

The close Manhattan live replay exposed a second memory problem after depth-index membership had been bounded. The `9171e95` application aborted inside Skia's CPU path rasterizer in the connected browser. A later diagnostic exercised the unchanged AOT artifact with the same live providers, captured the tile responses, and completed its controls while growing Wasm memory to **2,115,305,472 bytes**. That is 98.50% of its configured 2 GiB maximum, leaving **32,178,176 bytes of additional linear-memory growth**. Allocated linear-memory length does not report free allocator blocks, live managed objects or GC reclaimability. The diagnostic did not reproduce the original abort. [Original failure](verification/live-native-raster-failure-9171e95.json) · [Complete frozen-artifact capture](verification/live-native-capture-83980ac.json)

The captured response corpus contains 68 vector tiles, 96 elevation tiles and a catalog. A native x64 accounting run over the union of those vector tiles retains 425,002,440 bytes in building vertices, indices and roof-rim endpoints. The original growing lists plus their final `ToArray` copies allocate 1,611,295,080 bytes cumulatively for those three streams alone. These figures exclude tessellator topology and other renderer state. The union is useful for comparing construction of identical geometry; it is not the exact browser `Display` membership, and it omits the application's additional bundled tiles. Native x64 process totals are not substituted for Wasm heap measurements.

## Lossless 32-byte vertices

The former vertex used twelve floats, or 48 bytes. Several values duplicated information already present in the model or edge:

- Height is exactly the model's stored top or bottom height.
- Along-edge distance is exactly zero or the stored edge length.
- The model index and face kind are integers.
- The stored height-span component was unused by both the shader and depth preparation.

The new sequential `GpuBuildingVertex` keeps all seven independent floating-point values unchanged and packs only integer identity and endpoint choices. No coordinate, height, lighting value or length is quantized.

| Byte offset | Type | Value |
| ---: | --- | --- |
| 0 | `float` | Tile X |
| 4 | `float` | Tile Y |
| 8 | `float` | Original edge length |
| 12 | `float` | Original orthographic light |
| 16 | `float` | Original perspective light |
| 20 | `float` | Original edge ΔX |
| 24 | `float` | Original edge ΔY |
| 28 | `uint` | Model index and endpoint/face flags |

The integer word stores the face kind in bits 0–1, bottom-height selection in bit 2, nonzero along-edge selection in bit 3, and the model index in bits 4–31. Checked allocation of the eight-float model table also bounds its index below the 28-bit packing limit.

The GL vertex array supplies the seven floats through three ordinary attributes and the packed word through `VertexAttribIPointer`. The shader receives a `uint`, so integer identity never passes through a floating-point conversion. It already fetches the model's metadata for materials and foundations; top/bottom height therefore uses those existing values. The CPU depth path selects the same immutable model heights once per range and preserves the original projection arithmetic.

For `V` vertices the persistent vertex payload falls from `48V` to `32V`, a one-third reduction. Indices, rim endpoints, triangle order, even/odd tessellation, model order and foundation metadata retain their existing representation and semantics. The separate GL cache accounts for the actual uploaded byte size.

## Exact output allocation

The builder processes each model in two phases:

1. Count valid wall and rim edges, tessellate its caps once, and retain only cap XY coordinates and polygon indices. Compute checked prefix offsets for the final vertex, triangle-index and rim arrays.
2. Allocate those three arrays at their exact required lengths. Emit each model's walls and caps in the original order, then release that model's compact cap staging references.

Cap tessellation is not repeated. Its original `ElementCount`, undefined-index filtering, interleaved roof/underside order and winding remain explicit. Compact staging does not retain the tessellator's topology graph or `ContourVertex.Data` references. This avoids the growth history of three large `List` buffers and the final full-stream copies.

The counting and emission passes use the same edge eligibility rule, including the tile-cut test and the original normalized-length threshold. The existing two lighting formulas remain in their original coordinate spaces: using a normalized length for both can change float results when an MVT tile has a nonstandard extent.

## Keep the CPU edge table lazy

The CPU rasterizer still requests `Building.Vertices` lazily. GPU construction now consumes one prepared edge at a time through the shared pure preparation helper. It no longer materializes a complete second edge array for every GPU building. The captured native scene previously retained another 33,730,984 bytes in those prepared edge arrays.

This changes ownership without changing the source features. A later CPU fallback can still materialize the same table. Ground paths, shadows, labels and source tiles remain available, and GPU frames keep immutable vertex/index/rim arrays while foundation updates replace only the small model metadata array.

## Verification scope

The legacy builder is preserved as an independent test reference. Regression checks compare every logical original vertex attribute by its IEEE 754 bits, every index and rim endpoint, model metadata and range boundaries. They also check the packed struct's size/offsets, array sharing across foundation updates, lazy edge ownership and allocation behavior on dense geometry.

Image and depth gates cover the consumers of the new representation: the eager depth oracle, ordinary rendering, cache eviction and held-frame replay, building selection, terrain/navigation overlays and GLES output. The pixel-window filtering algorithm is unchanged by this storage pass. Browser validation additionally exercises the captured provider corpus and acknowledged close-camera controls; retained array measurements and Wasm growth measurements remain separate.

See [rendering performance](performance.md), [sparse depth bounds](sparse-visibility.md), and [pixel-window filtering](pixel-window-filtering.md) for the other parts of this rendering pass.
