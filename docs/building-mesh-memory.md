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

## Captured-scene native measurements

The same accounting harness loads all 68 captured vector tiles and 96 elevation tiles into the original `9171e95` renderer and the compact `c937ddd` renderer. It measures construction before traversing five fixed camera poses, retains two GPU frame descriptions, and records array ownership and full-GC checkpoints. This is a CoreCLR x64 run without a GL context; it measures construction and retained data, not browser presentation speed.

| Measure | Original | Compact | Change |
| --- | ---: | ---: | ---: |
| Building-constructor allocated bytes, including tessellation | 2,471,008,544 | 1,147,196,088 | −53.6% |
| Retained building-mesh arrays, including model metadata and ranges | 425,732,592 | 313,152,752 | −112,579,840 bytes |
| Prepared CPU edge arrays | 33,730,984 | 0 | −33,730,984 bytes |
| Final tracked array payload across the scene | 524,626,099 | 378,315,275 | −146,310,824 bytes |
| Final managed bytes after full GC | 725,473,784 | 578,913,760 | −146,560,024 bytes |

The direct mesh-plus-edge reduction is **146,310,824 bytes**, or **139.533 MiB**, at every measured camera pose. Vertex payload falls from 337,739,520 to 225,159,680 bytes; index and roof-rim payloads remain unchanged. Source geometry, camera values, depth capacities, bake inputs and resulting shadow paths agree between the two runs. Full-GC object totals include other managed objects and runtime effects, so their difference is not used to define the exact array saving.

The [complete measurement record](verification/building-mesh-memory-c937ddd.json) includes each tile's constructor measurements, all five scene-retention checkpoints, source/binary identities and the 878-assertion export audit. The [reusable harness](verification/building-mesh-harness/README.md) accepts an explicit source revision and body-root path. Its captured union remains a comparison workload, not a reconstruction of the browser's precise retained tile membership or native allocator state at the earlier abort.

## Frozen-corpus AOT replay

The original and compact AOT artifacts each replay the same 165 captured response bodies through the same native input protocol at 1800 × 924 CSS pixels and DPR 2. The protocol covers the initial North Up walker, close Orbit transitions, three additional camera cycles, and acknowledged pause, resume and exit controls.

| Recorded browser result | Original `9171e95` | Compact `c937ddd` |
| --- | ---: | ---: |
| Peak allocated Wasm linear-memory length | 2,077,556,736 bytes | 1,908,146,176 bytes |
| Additional growth below the configured 2 GiB maximum | 66.6875 MiB | 228.25 MiB |
| Completed public-state checkpoints | 27 | 27 |
| Native gesture acknowledgements | 8 | 8 |
| Provider request occurrences / unique bodies | 170 / 165 | 170 / 165 |
| Bounded sampler timeouts | 13 | 6 |

The observed high-water mark falls by **169,410,560 bytes**, or **161.5625 MiB / 8.1543%**. Both runs complete without a recorded runtime abort, failed memory growth, replay failure or input failure. The sampler timeouts are retained as observation failures: successful checkpoints do not imply continuously responsive UI. Memory-growth records use a separate binding and include growth events during timed-out polling intervals.

This is one measurement per artifact, with identical bodies and aggregate gesture deltas. Request order differs at 48 of 170 positions between the two runs; wheel-event packetization and frame scheduling also differ. It is not a frame-by-frame deterministic or statistical timing comparison. Neither replay reproduces the earlier live abort. The difference from the memory ceiling is possible linear-memory growth, not guaranteed allocator free space or a bound on total process/GPU memory. [Pinned comparison and raw observations](verification/corpus-memory-comparison-c937ddd.json) · [Independent 5,174-assertion audit](verification/browser-memory-replay-c937ddd-independent-audit.json)

After deployment, the independent connected-browser replay also passes the original live-provider camera sequence at the same viewport and DPR. Both native scroll requests complete, the picker confirms Orbit, pause/resume acknowledge twice, and exit restores the original ready GPU overview. One bounded DOM inspection times out during the first pause phase; the following independent snapshot shows the paused, ready state. No captured managed exception or native abort occurs. [Live observations](verification/live-rendering-c937ddd.json) · [Close view](verification/live-c937ddd-close-camera.jpg) · [Restored overview](verification/live-c937ddd-manhattan-overview.jpg)

## Verification scope

The legacy builder is preserved as an independent test reference. Regression checks compare every logical original vertex attribute by its IEEE 754 bits, every index and rim endpoint, model metadata and range boundaries. They also check the packed struct's size/offsets, array sharing across foundation updates, lazy edge ownership and allocation behavior on dense geometry.

Image and depth gates cover the consumers of the new representation: the eager depth oracle, ordinary rendering, cache eviction and held-frame replay, building selection, terrain/navigation overlays and GLES output. The pixel-window filtering algorithm is unchanged by this storage pass. Browser validation additionally exercises the captured provider corpus and acknowledged close-camera controls; retained array measurements and Wasm growth measurements remain separate.

The compact-mesh gate compares **845,516 vertices across 22 cases**, including all four visual styles, nonstandard tile extents, dense Manhattan tiles, holes, crossing contours, degenerate edges and signed zero. The full depth oracle compares **6,612,305 queries across 55 frames**. The GLES comparison preserves all **192 case image hashes across 48 cases**; only the expected uploaded/resident byte counters decrease.

See [rendering performance](performance.md), [sparse depth bounds](sparse-visibility.md), and [pixel-window filtering](pixel-window-filtering.md) for the other parts of this rendering pass.
