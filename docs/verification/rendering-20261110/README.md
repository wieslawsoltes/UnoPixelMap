# Integrated native validation · 10 November 2026

The source identities in `validation.json` identify the three production files
containing the visibility and terrain-copy changes. Both changes were enabled
for the integrated checks; their performance experiments remain isolated in the
neighboring evidence directories.

`native-results.json` records each build/test invocation and exit status. All
rendering, GLES, data, routing, simulation, audio, browser-input and initial replay
observer/contract checks passed. The individual logs retain the complete output.
The replay observer continued through separate real-browser preflight validation;
its later browser results are recorded with the release evidence.

`gpu-checks.json` is byte-identical to the unchanged baseline report, SHA-256
`bd4674613629c8530e52784f3cd1368d492f5ddcab16b83e70988448a115c58a`.
It includes 48 standard native GLES cases and their 192 image hashes, navigation,
source-order, retained-state and context-recreation checks.

`final-texture-checks.log` records the later targeted run with all 768 original
texture cases plus 42 asymmetric interior-pixel boundary cases. Those tests add
coverage at extreme zooms without changing production code. The full integrated
run preceded that test-only addition; the final CI suite exercises the addition
as part of the ordinary terrain checks.

The native runtime is .NET 10.0.14, SDK 10.0.403; GLES uses the local software
renderer. These are correctness checks, not performance measurements. Measured
native timings and their raw samples are documented separately:

- [Visibility proof and isolated benchmark](../occlusion-20261110/README.md)
- [Terrain exactness and isolated benchmarks](../terrain-copy-20261110/README.md)
