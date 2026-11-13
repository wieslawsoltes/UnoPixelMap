# Exact-head native replication: f159983

The [successful native job](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/43405625411/job/131819284919)
compares clean baseline `a9a284263da76a201114381467bf45aa4823a2d6` with
`f159983c68a72bd4fc8d27d592ba3721bf4689c3` on an **AMD EPYC 7763** runner, using
isolated **.NET SDK 10.0.403** and **.NET 10.0.15**. The candidate's full source tree
is `35584c004b091eedc72198bf15afe8a7f5215dfa`; its production `src` tree is
`cf568d7832d23566d87b188f23f8afb18bab1d2a`.

The production source matches the earlier `4a3b9c4` measurement. This replication
remains **independent of the EPYC 9V74 results** in
[the earlier bundle](../webgpu-performance-4a3b9c4/). Its samples and timings are not
pooled with that run. No benchmark was rerun to produce this evidence bundle.

## Measurements

Preparation plus overlay decreases **21.8–53.9%** across all nine workloads.
Including all twelve terrain-picking probes, elapsed CPU time decreases
**23.6–55.6%**. All **10,944 identity comparisons pass**, with zero failures.

| Workload | Combined median, baseline → candidate ms | Time reduction | Combined p95, baseline → candidate ms |
|---|---:|---:|---:|
| pan-pixel | 15.841 → 10.618 | 33.0% | 16.704 → 12.145 |
| walk-pixel | 6.008 → 3.135 | 47.8% | 6.562 → 3.667 |
| orbit-pixel | 16.530 → 10.853 | 34.3% | 17.298 → 12.937 |
| pan-normal | 16.901 → 11.798 | 30.2% | 17.743 → 12.402 |
| walk-normal | 6.658 → 2.954 | 55.6% | 7.141 → 3.615 |
| orbit-normal | 17.022 → 11.529 | 32.3% | 17.977 → 13.097 |
| pan-roblox | 17.255 → 13.189 | 23.6% | 18.002 → 13.998 |
| walk-roblox | 8.627 → 4.535 | 47.4% | 9.266 → 5.048 |
| orbit-roblox | 17.485 → 12.997 | 25.7% | 18.280 → 14.811 |

Both source-priority controls rebuild zero visible textures. Their near-neutral
results include the small negative Pixel control; neither is omitted:

| Control | Median, baseline → candidate ms | Elapsed change | p95, baseline → candidate ms |
|---|---:|---:|---:|
| source-priority-control-pixel | 0.74255 → 0.74405 | +0.202% | 0.76471 → 0.76147 |
| source-priority-control-normal | 0.76250 → 0.76095 | -0.203% | 0.78439 → 0.80656 |

Both sources compiled before ABBA replay. Each workload has 32 warm-up samples and
32 measured samples per run; runs 1/4 form the baseline and 2/3 the candidate,
yielding 64 measured samples per variant/workload. The archive retains 2,176 raw
samples across nine moving and eight source-streaming workloads. Combined metrics
sum phases **within each raw sample**, then calculate the median and linearly
interpolated p95. The regenerated full comparison equals the original exactly;
36 combined distributions were independently checked from raw phase values.

These are native CPU preparation, overlay, picking and source-invalidation
measurements. No GPU device, texture upload, browser or display timing occurs in
the native harness. Browser performance conclusions belong to their own paired
evidence; this bundle asserts only the native job's successful conclusion.

## Retained evidence

- `native-original.zip`: unaltered artifact **14027586572**, **285,496 bytes**,
  SHA-256 `e4121c50b06b4049c8458b5516969b80cb9fe36bb3865f4429036ea4062c60ff`.
- `comparison.json`, `run-manifest.json`, `dotnet-info.txt`: byte-for-byte original
  archive members. The archive also retains every raw sample and build log.
- `compare-native.py`: frozen original comparison driver, matching the recorded
  source hash; importing its `compare` function requires no .NET execution.
- `artifact.json`, `provenance.json`: original artifact metadata, complete source
  identities, runner, toolchain, font and frozen-harness identity.
- `audit.json`: verified recomputation, all nine combined distributions and both
  controls. `manifest.json` hashes every other retained file and archive member.

To verify the files and recompute the original comparison without a benchmark,
run from the repository root:

```sh
python3 - <<'CHECK'
from pathlib import Path
import hashlib, json, runpy, zipfile
root = Path('docs/verification/webgpu-performance-f159983-native')
manifest = json.loads((root / 'manifest.json').read_text())
for name, expected in manifest['files'].items():
    data = (root / name).read_bytes()
    assert len(data) == expected['bytes'], name
    assert hashlib.sha256(data).hexdigest() == expected['sha256'], name
compare = runpy.run_path(str(root / 'compare-native.py'))['compare']
with zipfile.ZipFile(root / 'native-original.zip') as archive:
    runs = []
    for folder in ('1-baseline', '2-candidate', '3-candidate', '4-baseline'):
        runs.append({
            'frames': json.loads(archive.read(folder + '/frames.json')),
            'streaming': json.loads(archive.read(folder + '/terrain-streaming.json')),
        })
    original = json.loads(archive.read('comparison.json'))
    assert compare(runs) == original
    assert original['passed'] and original['identityFailures'] == []
    assert original['identityChecks'] == 10944
print('Retained hashes and 10,944 identity comparisons verified.')
CHECK
```
