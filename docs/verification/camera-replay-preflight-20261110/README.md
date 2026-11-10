# Submitted-camera replay: passing preflight

[CI run 42906521344](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42906521344)
completed two independent browser captures of the same deployed application and
passed exact comparison of every ordered input and submitted projection. This
validates repeatability of the replay protocol. It is not a comparison of the
production optimizations or a browser FPS result.

| Case | Inputs per capture | Map submissions per capture | Submission counts per input |
| --- | ---: | ---: | --- |
| `map-pan-relief` | 32 | 34 | 30 single-frame steps; 2 two-frame steps |
| `map-orbit-relief` | 32 | 32 | 32 single-frame steps |
| `walk-look-firstperson` | 32 | 36 | 28 single-frame steps; 4 two-frame steps |

Across both captures, **192 trusted native inputs** and **204 individually fenced
map submissions** pass the raw contract again during offline verification. The
smallest completed quiet interval is **284.4 ms**. The walking case is paused and
preserves its public physical telemetry.

## Identity and protocol

The application is the exact `555990d0e086ffb9908d4d98ec30a73a245a6c6e` Pages
artifact **13710017486** from [run 42696985047](https://github.com/wieslawsoltes/UnoPixelMap/actions/runs/42696985047).
Its ZIP SHA256 is
`04abb6b24c35df6d6e5f6b3bbb80e211f1fe48c2d6edc948ada4138ac3163b8e`.
The source run and artifact metadata are retained unchanged.

The preflight harness commit is `2d1c43ad4355f6a7843083fe69086c35fe24d41c`;
its measured source files match the later main commit
`dbde48e9fd5318ef309bf31ed4b9b7e1596ec7d6` by their recorded SHA256 values.
The runner uses Chromium **156.0.8078.4**, SwiftShader, an AMD EPYC 9V74 CPU,
three affinity-visible processors, a 1200×780 viewport and DPR 1.
[Provenance](provenance.json) records the complete identities, flags and case
hashes; the exact [preflight workflow](preflight-workflow.yml) is retained.

Schema 2 (`native-wheel-resolve-fence-v2`) keeps every map submission per input,
including resident-tile order updates from the public tile refresh timer. Each
submission must observe its own signaled fence. A step completes after at least
250 ms without another resolve and two animation frames; a later submission
invalidates that animation barrier. Each step has a 20-second wait limit and a
maximum of 32 submissions. No frame is sorted, merged or discarded. Missing,
extra or reordered frames and any numeric projection difference fail comparison.
Terrain cache-slot IDs are excluded; all 12 submitted transform floats per
terrain instance, geometry transforms, draw counts and artwork dimensions remain.

The timing fields distinguish first submission, first fence and the final fence
of the settled sequence. They include application scheduling and fence polling;
task CPU includes observer and automation overhead. Barriers do not measure
maximum throughput or physical display presentation. Report medians use the
upper middle sample, and p95 uses sorted index `floor(n * 0.95)`.

## Retained files and offline verification

Both `repetition-*` directories contain the complete report, all three raw probe
files and the original console log. Gzip preserves the original JSON bytes,
including every projection, timing, WebGL/performance observation and CDP metric.
The complete job and HTTP server logs are also compressed. Screenshots are
omitted; this archive verifies submitted workload identity, while separate
rendering oracles provide pixel checks. Earlier failed harness-development runs
are not performance evidence and are not required to verify this passing run.

The original result artifact is **13805432585**, 3,688,328 bytes, SHA256
`18bb83f62b664fad5dabd8eb9160881e4459ce2b1598f9d5f44d848591352a9f`.
The extracted source bytes were checked against that ZIP before archiving.
`manifest.json` records stored and decompressed identities; `SHA256SUMS` also
covers the JSON manifest. An unchanged copy of the validated contract is retained
as `reference-contract.cjs` so later harness changes do not alter this audit.

From the repository root, run:

```sh
node docs/verification/camera-replay-preflight-20261110/verify.cjs \
  > /tmp/camera-replay-preflight-verification.json
cmp docs/verification/camera-replay-preflight-20261110/verification.json \
  /tmp/camera-replay-preflight-verification.json
(cd docs/verification/camera-replay-preflight-20261110 && sha256sum -c SHA256SUMS)
```

The verifier needs only Node. It verifies every stored/decoded hash, reconstructs
each step from the raw observations, checks that the two independent probes agree
on all submission counts and dimensions, reruns exact cross-capture comparison,
and independently recalculates latency distributions and task CPU. The result is
byte-for-byte identical to [verification.json](verification.json), and the full
recomputed comparison equals [comparison.json](comparison.json). Browser capture
can be repeated using the recorded workflow, exact application artifact and
pinned browser; offline verification launches no browser.
