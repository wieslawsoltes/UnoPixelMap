# Rejected native depth prototypes

These are separate ABBA experiments, each compared with its own c937 baseline. The records preserve all workload medians, p95, thread CPU, process spread, exact-output audit status, source patches, renderer hashes, and common dependency identities. They are compact records of rejected experiments; their scratch raw reports were not replaced or modified.

| Prototype | Record | Source patch | Reason it was not selected |
| --- | --- | --- | --- |
| Unconditional 8-pixel grid | [fixed8.json](fixed8.json) | [fixed8.patch](fixed8.patch) | Full-resolution Normal and hills Roblox regressions; sequence-specific walk tail cost. |
| Density-adaptive grid | [adaptive.json](adaptive.json) | [adaptive.patch](adaptive.patch) | Remaining orbit and Normal preparation cost. |
| Exact per-vertex projection cache | [projection.json](projection.json) | [projection.patch](projection.patch) | Pixel freeflight slows in both candidate processes despite exact output preservation. |
| Fixed 16-pixel grid, range calculations on every triangle | [fixed16.json](fixed16.json) | [fixed16.patch](fixed16.patch) | Hills workloads regress substantially. |

All four audits passed measured query/overlay fingerprints, source/binary identities, raw CSV reconstruction and recorded index-cap controls. Their rejection concerns workload cost, not a numerical-output failure. A larger cached Vertex is a possible performance tradeoff; the measurements do not prove that it caused the projection-cache regression.

[common-runtime.json](common-runtime.json) preserves every unchanged runtime file hash and size. Each experiment records its distinct renderer hash and source patch. The [source-equivalence proof](../source-release-53e9c68.json) connects the original local base to its published tree without changing the original identities.

The earlier four-warmup prune-only diagnostic is not the fixed16 experiment represented here. The [selected selective-pruning sequence](../native-depth-selective-20261107/README.md) has its own complete raw evidence. Do not combine samples or subtract medians across these sequences.
