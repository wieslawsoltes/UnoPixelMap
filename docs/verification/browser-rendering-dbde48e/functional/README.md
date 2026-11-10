# Functional browser validation · source dbde48e

The `verify` job passed for source
[`dbde48e9fd5318ef309bf31ed4b9b7e1596ec7d6`](https://github.com/wieslawsoltes/UnoPixelMap/commit/dbde48e9fd5318ef309bf31ed4b9b7e1596ec7d6)
in [run 42907891884](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42907891884),
[job 130158519794](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42907891884/job/130158519794).
This directory preserves functional validation of that exact built package.
Timed play/replay comparisons and live deployment observations have separate
evidence in the parent directory.

## Results and scope

The six direct-compositor/detached presentation pairs cover Pixel, Normal,
Minecraft, Roblox, Earth overhead and elevation. Every pair has zero differing
RGBA map pixels and identical full captures. Each map crop is 675 × 440 pixels;
the six cases compare **1,782,000 map pixels**. The complete 1200 × 780 captures
compare **5,616,000 pixels**. The crops are part of those full captures, so these
populations must not be added as independent pixels.

The independent audit recomputed all saved PNG pixel hashes and checked each
saved map crop against its full capture. Direct rendering uses the compositor
context with zero recorded readbacks. The detached path uses its separate context
and asynchronous pixel-pack readback. These comparisons establish equality
between presentation paths within this release; they do not compare two source
revisions or measure display FPS.

The complete verification log records:

- Trusted native browser input checks and both CPU/GPU smoke runs.
- Eight journey scenario completions: driving, ground modes, route planning and
  phone boat controls, each with CPU and GPU rendering.
- Both CPU/GPU terrain suites, including relief, Map/Earth/elevation layers,
  2D/3D views, picking, pending displayed-frame ownership, orbit, drag,
  exaggeration, sharing, resize and phone controls.
- Exploration with four rendering styles, all five camera rigs, walking and
  terrain contact, pointer lock, trackpad/WebKit gestures, physical vehicles,
  mobile takeover and gesture cancellation.
- Nine DPR2 close-camera checkpoints, ending with recovery to the map, with no
  recorded failures. The native canvas is 3600 × 1848 at a logical 1800 × 924.

The visual reports identify Chromium **156.0.8078.4**, SwiftShader, DPR 1 and
Release **InterpreterAndAOT**. The tests use bundled map fixtures and deliberately
block external providers. The original full-download audit found no failure
artifacts or unhandled-error markers.

## Artifact and package identity

| Artifact | GitHub artifact ID | Bytes | ZIP SHA-256 |
| --- | ---: | ---: | --- |
| `atlas-browser-screens` | 13807507180 | 29,156,949 | `b5844aabb1bdd1bcd6a250fa1163a92f60e71cf9ebc042559041f9a6d72def61` |
| `github-pages` | 13807570605 | 88,462,283 | `d1439991dabcd2e9cdd266accc57c723cc79b630080d61afdb1fecbe49d22f3d` |

Both downloaded ZIPs matched the API digests and sizes. Their API metadata is
preserved in [screens-artifact.json](screens-artifact.json) and
[pages-artifact.json](pages-artifact.json). The selected source is also retained
in `source-run.initial.json.gz`; its in-progress status is an early identity
snapshot, not the final workflow conclusion. [verify-job.json](verify-job.json)
records the completed verification job and its successful steps.

The checked package is
`package_801cbc2830341569438a9ce8c44febc16626f1501278ed456482a888efe98347`.
Both presentation reports have identical runtime metadata. Their **23 recorded
bootstrap, loader, native WASM and relevant assembly hashes** match that Pages
package. [package-linkage.json](package-linkage.json) retains those identities.
[source-tests.json](source-tests.json) records the browser test source hashes at
the exact release commit, including the visual harness hash recorded by both
presentation reports.

## What is retained

The original browser artifact contains 154 files. This directory retains its
**24 presentation PNG members and four report members**; the other 126 artifact
members are omitted. Their identities remain in the original
[audit.json](audit.json), which records the earlier audit of the complete
downloaded archive. Full browser-build and verification logs are also retained.

| File | Preserved content |
| --- | --- |
| `comparison.json` | Original six-case direct/detached comparison, unchanged. |
| `gpu-direct-visual.json.gz`, `gpu-transfer-visual.json.gz` | Original complete presentation reports, compressed without changing their decompressed bytes. |
| `high-dpi-close-camera.json.gz` | Original nine-checkpoint DPR2 report. |
| `verify-job.log.gz`, `browser-build.log.gz` | Complete downloaded job logs, with their original UTF-8 bytes retained. |
| `presentation-oracles.zip`, `oracle-images.json` | Exact selected PNG bytes with explicit mapping to all original members. |
| `audit.json`, `package-linkage.json` | Original independent audit results, unchanged. |
| `capture-auditors/` | Exact compressed sources and output of the original capture-time audit scripts. |
| `retention.json` | Original/stored hashes, sizes, compression and origins for the selected records, plus the image archive identity. |
| `retained-audit.json`, `download-audit.json` | Results from the portable auditors described below. |

### Exact PNG deduplication

The direct and detached PNG files themselves are byte-identical for each pair.
The 24 source image members contain **7,936,746 bytes**, represented by **12 unique
original PNG streams totaling 3,968,373 bytes**. The deterministic image ZIP is
**3,850,160 bytes**. No image pixels, PNG chunks or PNG compression streams were
edited or re-encoded.

`oracle-images.json` maps every original artifact member to
`images/<original-PNG-SHA-256>.png` inside the ZIP, retaining the original byte
size, SHA-256 and dimensions. The same mapping is included as `members.json`
inside the archive. The portable auditor verifies those two mapping copies,
every stored PNG byte stream, all 24 original-member hashes against the
full-download inventory, decoded RGBA hashes and full-capture crops.

## Reproduce the retained checks

Use Python 3.11 or newer with Pillow installed; this audit was run with Pillow
13.0.0. From the repository root:

```sh
python3 docs/verification/browser-rendering-dbde48e/functional/audit.py \
  > /tmp/pixelmap-functional-retained.json
cmp /tmp/pixelmap-functional-retained.json \
  docs/verification/browser-rendering-dbde48e/functional/retained-audit.json
```

`audit.py` resolves all inputs relative to its own file, so it also works when
invoked by absolute path from another working directory. It reads the committed
selected evidence without accessing the network, starting a browser or modifying
the evidence. Its output reproduces `retained-audit.json` byte for byte.

This auditor checks the retained original reports and images, the complete saved
job logs, DPR2 checkpoints and consistency of recorded API/package metadata. It
does **not** re-inspect omitted UI captures or recompute the digests of the two
uncommitted full artifact ZIPs. The successful complete-download findings in
`audit.json` and `package-linkage.json` remain explicitly historical records.

## Recheck original downloads

When the original ZIPs are available locally, the standard-library-only auditor
can verify the complete browser inventory and the package linkage directly:

```sh
python3 docs/verification/browser-rendering-dbde48e/functional/audit_downloads.py \
  --screens-zip /absolute/path/to/atlas-browser-screens.zip \
  --pages-zip /absolute/path/to/github-pages.zip \
  > /tmp/pixelmap-functional-downloads.json
cmp /tmp/pixelmap-functional-downloads.json \
  docs/verification/browser-rendering-dbde48e/functional/download-audit.json
```

Either ZIP argument can be used independently. With both provided, the output
reproduces `download-audit.json` byte for byte. The auditor verifies both complete
ZIP sizes and SHA-256 digests against the retained API records, every one of the
154 browser artifact members against the original audit inventory, the absence
of failure-named members, and all 23 recorded package resources directly inside
`artifact.tar`. It performs no extraction, browser run or performance measurement.
