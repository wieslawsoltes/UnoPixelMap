# Earth preprocessing measurement

This standalone harness measures the complete internal `EarthRaster.Get` call
through a bound delegate. Binding and construction happen outside the timed
region. The same compiled executable loads either preserved renderer assembly;
only `PixelMap.Rendering.dll` changes between its two runtime directories.

The input consists of the 19 JPEG resources embedded in `PixelMap.Data.dll`
and 14 deterministic PNG fixtures generated before measurement. The PNGs cover
smooth, checker, gradient, noise, irregular, tiny and 512-pixel inputs at both
artwork sizes. The synthetic fixtures deliberately reuse tile keys to include
normal payload replacement. A fresh raster cache is created for each pass,
while the process-wide palette lookup stays warm after nine untimed passes.

Each call includes codec decoding, linear-light averaging, palette resolution,
dithering, image creation and cache maintenance. Result allocation and image
hashing are outside the measurement. The first cold pass is retained with
`Sample = -9`; it is excluded from warm timing statistics. The other eight
warmup passes are excluded from reporting. Managed allocation counts cover the calling thread
and exclude native allocations.

## Reproduce

Build the rendering test project from baseline and candidate sources in
separate directories and preserve their Release output. Set `earth_baseline_bin`
and `earth_candidate_bin` to those absolute output paths. The recorded baseline
is `a4d80e0a4dfa84b7b420c04e3c03deef4f8b96bb`; the candidate is that source with
only the `EarthRaster.cs` change. Its exact source and assembly identities are
in [the provenance](../earth-raster-20261109.json).

Compile the harness once against the baseline:

```sh
dotnet build docs/verification/earth-raster-harness/EarthCost.csproj \
  -c Release -m:1 -nr:false -p:UseSharedCompilation=false \
  -p:RendererDirectory="$earth_baseline_bin" -o /tmp/earth-cost/baseline

cp -a /tmp/earth-cost/baseline /tmp/earth-cost/candidate
cp "$earth_candidate_bin/PixelMap.Rendering.dll" /tmp/earth-cost/candidate/
```

Run four sequential processes, using variants baseline, candidate, candidate,
baseline and block numbers 0, 1, 2, 3. Keep competing work off the chosen core.
The recorded comparison uses CPU 8; select an available core on another host.

```sh
DOTNET_TieredCompilation=0 DOTNET_PROCESSOR_COUNT=2 \
  taskset -c 8 dotnet "/tmp/earth-cost/$earth_variant/EarthCost.dll" \
  "/tmp/earth-cost/results/$earth_block-$earth_variant" 64
```

Every directory receives `earth-cost.json` with all measured calls, 33 raw-image
hashes and runtime/assembly identities. Verify equality of the controls and all
runtime files except the renderer before comparing times. For grouped results,
sum matching per-tile measurements within each pass, pool the two blocks for
each variant, and compute the conventional median and nearest-rank p95. Do not
mix the first-pass samples into warm results.

The checked-in [CSV](../earth-raster-20261109.csv) contains all four processes,
including the 132 cold samples and 8,448 warm samples. The
[statistics](../earth-raster-20261109.json) retain both process medians,
per-texture outcomes, allocation histograms, source hashes, image hashes,
input hashes and the shared runtime-file manifest. The complete interpretation,
including the generic-path regression and cold-call limits, is in
[the performance note](../../earth-raster-performance.md).
