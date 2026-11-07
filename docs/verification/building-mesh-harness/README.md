# Native building and scene memory accounting

This standalone diagnostic references the repository's unchanged Data and Rendering projects. It writes reports only to the requested output path. Use an isolated artifacts directory so the diagnostic does not replace application or test outputs.

The measurement associated with `building-mesh-memory-c937ddd.json` compares source `9171e95efab06db930a97dde212d754b8da48f60` with `c937ddd192619f48c1a2ec682845b52bc1eebff8`. The two frozen renderer assemblies run the identical accounting executable; the JSON records all assembly/source hashes. The harness supports both the old float-array vertex layout and the new packed struct.

## Build

From the repository root, with .NET10 installed:

```sh
dotnet build docs/verification/building-mesh-harness/SceneMemory.csproj \
  -c Release --artifacts-path /tmp/pixelmap-memory-artifacts \
  -m:1 -nodeReuse:false -p:UseSharedCompilation=false
```

The executable is `/tmp/pixelmap-memory-artifacts/bin/SceneMemory/release/SceneMemory.dll`. Native Linux runs use the SkiaSharp native-assets package. Supply a valid font with `--font` if DejaVu Sans is unavailable at `/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf`.

## Captured data

```sh
dotnet /tmp/pixelmap-memory-artifacts/bin/SceneMemory/release/SceneMemory.dll \
  --source-revision=c937ddd192619f48c1a2ec682845b52bc1eebff8 \
  --manifest=/absolute/capture/corpus-manifest.json \
  --output=/tmp/pixelmap-memory/frame-after.json
```

`--source-revision` is required; it must identify the source of the loaded renderer assembly. The actual DLL SHA256 is also recorded. A provided source label does not replace checking that binary against its build provenance.

When a reconstructed manifest lives in a separate analysis directory, add `--body-root=/absolute/original/capture`. Otherwise, relative body paths resolve beside the manifest. The captured response headers do not control decoding: HTTP bodies may already be decompressed, while raw gzip PBF is still detected by its bytes and bounded to the production8MiB expanded limit.

The manifest accepts a `requests` array, a `tiles` array, or a top-level array. Saved HTTP200 vector/elevation rows supply `url`, `body`, `sha256`, and response timing/order fields. Catalog and failed/unsaved rows are skipped. XYZ coordinates come from `/z/x/y[.pbf|.mvt|.png]` URL paths or explicit `{tile:{z,x,y}}` fields. Every supplied hash is verified before decoding.

If network body-completion times are present for every row, ingestion sorts by `timing.startTime + timing.responseEnd`. Otherwise it uses complete observed finish timestamps, or falls back to header response order with a warning. Duplicate tile IDs use the last decoded body. This does not reproduce parsing completion, UI publication, request cancellation, or exact viewport cache membership.

`--directory=/absolute/path` also accepts recursively stored `z/x/y.pbf[.gz]`, `z/x/y.png`, and `z-x-y` or `z_x_y` filenames. Directory and manifest inputs are mutually exclusive.

## Two diagnostic modes

The default scene mode calls the real public `PrepareGpuFrame`, including terrain baking, mesh preparation, labels and demanded depth geometry. It creates no GL context. It retains two frame snapshots by default and prepares five derived views at logical1800×807, pixel size2: opening, orbit, horizon, close orbit, and a reverse view. These poses are diagnostic views derived from the failure region, not the captured live wheel/adaptive-resolution timeline. In particular, the default extreme target uses look176.4°, while the captured native gesture produced look133.2° and adaptive p2/p3/p4 transitions.

Without a manifest/directory it uses the nine embedded Manhattan vectors and the embedded z0 DEM. `--flat` disables relief. Other options are `--width`, `--height`, `--pixel-size`, `--held-frames` (1–4), `--max-tiles` (default512), and `--poses=/path/poses.json`. Pose files contain an array of `{Name, Camera, Width?, Height?, PixelSize?}` using the public `MapCamera` JSON representation.

To isolate constructor allocations, add:

```sh
--mesh-construct-only
```

This creates each `TileGeometry` outside the measured interval, then measures `GC.GetAllocatedBytesForCurrentThread` around its one mesh constructor. It includes tessellation, output/staging arrays and equal reflection-invocation overhead. All mesh/geometry results remain retained until the final GC snapshot. Per-tile and aggregate payloads are recorded. This is one structural/allocation pass, not a timing benchmark.

For a baseline/candidate comparison, compile this harness once. Copy that same executable, PDB and runtime/dependency manifests into two otherwise complete runtime directories containing the separately frozen baseline/candidate Data, Routing and Rendering DLLs plus native dependencies. Do not rebuild or replace the frozen renderer while measuring. Run the same corpus/options in both directories with their respective source identities. Record each DLL's SHA256 as the report does.

## Interpretation

The reports include exact per-tile SKPath point/verb counts, source-material and merged-shadow input maxima, unique retained managed array payloads, depth capacities/index state, and GC/process allocator snapshots. The bake-input enumeration reproduces the selected terrain patch/block clip transforms. It sums whole source paths for candidate shadow unions without allocating another large merged path. These inputs include cached blocks and precede stroke/dash expansion; they are not intercepted native draw-call counts.

`SKPath.ApproximateBytesUsed` is unavailable in this SkiaSharp version and is reported as null. `8×points + verbs` describes logical linear point/verb payload only; it excludes native capacity, headers, aliasing and raster edge storage. Array totals deduplicate references and exclude separately reported depth workspaces and uncounted managed object graphs. Range/struct sizes are native x64 sizes.

Full GC checkpoints alter lifetime/scheduling. The corpus remains held after renderer disposal. `mallinfo2` measures process-wide libc allocation, not Skia alone. Do not add it to RSS or GC committed memory. Native x64 layout, allocator and Skia execution differ from wasm32: these figures are not the browser's linear-memory occupancy or growth capacity.

The recorded capture contains68 external vector tiles and96 DEM tiles. The nine embedded Manhattan vectors are absent from provider HTTP capture. All captured vectors are supplied simultaneously for this accounting envelope; it is not an assertion that all68 were in the live current Display set. The application-level regression uses a separate frozen-corpus browser replay.

A companion `*.progress.ndjson` is flushed before each production scene preparation and after each completed pose, preserving the last reached stage if native preparation terminates before final JSON output.
