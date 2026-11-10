# Exact-package CPU profile diagnostics

All **14 preselected post-timing profiles** from run `42907891884` are retained,
covering seven scenarios for baseline `555990d` and candidate `dbde48e`. These
captures follow the timing window in repetition one. They are not the samples
used to calculate the preceding Task CPU or submission-rate measurements.

The [complete archive](profiles-symbolicated.zip) contains the original profiles,
their named counterparts, both symbolication manifests, every method-weight
summary, the source provenance, exact symbolicator and coordinating audit script.
Its SHA-256 and per-profile identities are in [validation.json](validation.json).

Thirteen profiles contain native WASM frames. The candidate idle-relief profile
contains none and remains byte-identical. Across all 14 outputs, **92,416 samples**
and their time deltas are preserved; **20,289 node names** change. Independent
structural comparison restores only those names before comparing every field of
each profile, including IDs, child edges, timestamps, samples and all remaining
call-frame attributes.

## Package identity and naming

The audit first verifies each published site's index and all **72 boot-resource
hashes** against the paired workflow provenance. Symbolication uses each exact
native WASM module and all 66 matching Webcil assemblies. It checks the native
module filename and each referenced function's byte offset, then resolves normal
Mono AOT MethodDef slots through that package's function table.

| Variant | Native module SHA-256 |
| --- | --- |
| Baseline `555990d` | `9d99319804c946006f4efe983f00830d430bd841a2170a3a09d09dfb2d9c295f` |
| Candidate `dbde48e` | `5b3d43dd28d2e03eeb31703ed03bedda44cf9fae46e41609b143e90654f2d6f4` |

Generic AOT extras and native/runtime functions can remain unnamed. Folded method
aliases remain explicit because a sampled native function cannot distinguish
them. Numeric function indices are not stable method identities between builds.
A rebuilt package from the same source commit is not an interchangeable input.

## Reading the diagnostic weights

[profile-analysis.json](profile-analysis.json) preserves each profile's complete
sample weight, duration, sample spacing, mapping coverage, top named self costs,
top application inclusive costs and selected stack categories. A category counts
each sampled stack at most once, including recursion and multiple matching
ancestors. Categories overlap: label drawing can contain depth visibility, and
terrain baking can contain the integer-copy routine. Their inclusive weights
must not be added.

The denominator for a category's percentage is the complete recorded profile
sample-weight sum, including idle, program and runtime work. Actual `timeDeltas`
determine weights; the requested sampling interval is not substituted. A folded
alias matches a category if any retained alias matches its documented pattern.

Candidate moving profiles still show label/depth work and GL-state capture.
For example, the separately captured walking-first-person profile includes
989.905 ms of sampled label-stack weight, of which 864.081 ms intersects the
depth-visibility category, within 8,119.354 ms of complete recorded weight. This
identifies remaining work for investigation. It is not an invocation count, a
per-frame cost, or a causal comparison with the preceding benchmark window.

Moving trajectories and their rendered-frame counts can differ between these
separate profiling passes. Do not divide a profile's weights by submission counts
from the earlier timing capture or use profile differences as isolated function
speedup measurements. Controlled camera CPU/latency results are reported
separately in the release's measured replay evidence.

## Reproduction

From the repository root, provide the exact unpacked Pages sites and either the
original extracted play evidence or this release's preserved gzip evidence:

```sh
python3 docs/verification/browser-rendering-dbde48e/profiles/profile_audit.py \
  docs/verification/browser-rendering-dbde48e \
  /path/to/exact-baseline/UnoPixelMap \
  /path/to/exact-candidate/UnoPixelMap \
  scripts/symbolicate-browser-profile.py \
  /tmp/unopixelmap-profile-audit-dbde48e
```

The output directory must not already exist. No browser or .NET execution is
needed. The archive includes the exact symbolicator used for this capture; its
hash is recorded in the analysis. The coordinating script never changes input
evidence and refuses mismatched package resources or altered profile structure.
