# Final-source native performance evidence

This bundle preserves the native comparison of baseline
`a9a284263da76a201114381467bf45aa4823a2d6` with candidate
`4a3b9c412ab7bb114795a6d4e5281e97508f4d56`. The candidate's full tree is
`353460159fc607c7d4c3765a08259f5e0f33781f`, and its production `src` tree is
`cf568d7832d23566d87b188f23f8afb18bab1d2a`.

## Native comparison

The [native job](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43263360230/job/131340439087)
passed on an AMD EPYC 9V74 runner with isolated .NET SDK 10.0.403 and .NET 10.0.15.
The parent workflow failed its separate browser comparison job; the native result
does not change that workflow conclusion.

- `native-ci.zip` is the unmodified GitHub artifact, including all raw samples,
  build logs, source/toolchain manifest and original comparison.
- `native-ci-comparison.json`, `native-ci-manifest.json` and `dotnet-info.txt`
  are byte-for-byte copies of their corresponding archive members.
- `compare-native.py` is the original comparison driver at the measured source,
  matching the driver SHA-256 recorded before the run. Its `compare` function can
  recompute the result without compiling or running .NET code.
- `native-audit.json` records the independent recomputation, every original
  archive-member hash and the small negative Pixel source-priority control.
- `native-artifact.json` preserves the artifact's original GitHub metadata.
- `manifest.json` hashes every other file in this bundle and records source,
  artifact, job and historical-checkpoint provenance.

Both revisions compiled before the ABBA measurement sequence. Each workload uses
32 warm-up samples and 32 measured samples in each of four runs. Pooling runs
1/4 for the baseline and 2/3 for the candidate yields 64 samples per variant for
each of nine moving workloads and eight source-streaming workloads: 2,176 raw
measured samples in total. All 10,944 identity comparisons pass.

Combined metrics sum preparation, overlay and, where specified, twelve picking
probes within each sample. Only then are the median and linearly interpolated p95
computed. Every field of the regenerated comparison equals the original; 118
table distributions were also recalculated directly from their raw values. The
scope is native CPU preparation and source invalidation. No GPU device, graphics
upload, browser or presentation measurement runs in this native harness.

The earlier checkpoint's negative deferred-picking results remain in
[`../webgpu-performance-16048b7`](../webgpu-performance-16048b7/). Separate runs are
never pooled with this final-source comparison.

## Integration and incomplete browser comparison

`integration-status.json` preserves GitHub job conclusions and original terminal
evidence for [integration run 43263360196](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43263360196).
Presentation, native regression, Debug and Release jobs all pass. Debug and Release
each complete 34 scenarios. The actual checkout is PR merge
`d407757af166a61c6dd38d4008c4abfe37870d30`, whose tree equals the candidate's tree.
Original detailed integration reports and captures live in the
[separate integration bundle](../webgpu-performance-4a3b9c4-integration/).

`browser-comparison-failed.zip` is the unmodified artifact from the unsuccessful
[paired comparison job](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43263360230/job/131348103432).
The adjacent failed report, candidate failure JSON and PNG are byte-for-byte
copies from that archive. Baseline `pixel-pan-1` completed; candidate repetition 1
timed out in `sceneProjection` while waiting for menu focus. The failure image
shows the Rendering quality submenu closed. The report records `passed: false`,
one completed run and zero comparisons. Baseline-only timings establish no paired
performance result. The later focus-harness correction and fresh runs are tracked
in the [main performance report](../../webgpu-performance.md).

## Verify the retained bytes and recompute the native result

Run from the repository root:

```sh
python3 - <<'PY'
from pathlib import Path
import hashlib, json, runpy, zipfile

root = Path('docs/verification/webgpu-performance-4a3b9c4')
manifest = json.loads((root / 'manifest.json').read_text())
for name, expected in manifest['files'].items():
    data = (root / name).read_bytes()
    assert len(data) == expected['bytes'], name
    assert hashlib.sha256(data).hexdigest() == expected['sha256'], name
compare = runpy.run_path(str(root / 'compare-native.py'))['compare']
with zipfile.ZipFile(root / 'native-ci.zip') as archive:
    runs = []
    for folder in ('1-baseline', '2-candidate', '3-candidate', '4-baseline'):
        runs.append({
            'frames': json.loads(archive.read(folder + '/frames.json')),
            'streaming': json.loads(archive.read(folder + '/terrain-streaming.json')),
        })
    original = json.loads(archive.read('comparison.json'))
    assert compare(runs) == original
    assert original['passed'] and not original['identityFailures']
    assert original['identityChecks'] == 10944
print('All file hashes and 10,944 identity comparisons verified.')
PY
```
