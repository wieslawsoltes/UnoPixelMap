# Browser release identity

The browser publish pipeline versions its bootstrap package from the finalized deployment contents. This makes C#, native WebAssembly, packaged assets and browser-adapter changes select new resource URLs when a release is published.

## Why the extra publish step is needed

The installed Uno.Wasm.Bootstrap 10.0.98 computes its package-folder hash before generating `uno-config.js`. The static assets can therefore retain the same package path while the generated configuration selects a different fingerprinted .NET loader. The ordering is visible in the pinned SDK's [ShellTask implementation](https://github.com/unoplatform/Uno.Wasm.Bootstrap/blob/5b87c821e575a61f1ea12826f03a80fef1c1f27f/src/Uno.Wasm.Bootstrap/ShellTask.cs).

This occurred during verification of the camera/rendering release: the preceding deployment and the new artifact both used `package_b15685d25e9696411d12c8dce8322288e85f8f1b`, and the connected browser continued executing the old native bundle after an ordinary reload. A matching bootstrap-folder name was insufficient to establish which C# release was running. PWA support was disabled in these artifacts.

The project-owned fix is in [VersionWasmPackage.targets](../build/VersionWasmPackage.targets) and [VersionWasmPackage.cs](../build/VersionWasmPackage.cs), introduced by [1829855](https://github.com/wieslawsoltes/UnoPixelMap/commit/1829855ea07d1fd43b66d4478210398a605bb3c9).

## Publish sequence

The app imports `Uno.Sdk` props and targets explicitly, then imports the project finalizer last. Its `AfterTargets="Publish"` callback follows Uno's nested AOT publish, configuration update, WebGL/worker fixups and the SDK's other publish callbacks. Inner and cross-targeting publishes skip it.

The finalizer resolves the active package from the generated index and computes a SHA-256 identity over sorted logical files: the finalized `_framework` contents, active package assets and configuration, generated index and service worker, and the two root browser adapters. File paths and content hashes are length-prefixed. Its package references and adapter-version parameters are normalized so the result is independent of the current package name and output directory. Compressed transport representations are excluded from identity inputs.

The resulting `package_<64 hex digits>` name appears in the generated package references. The index also receives a matching `pixelmap-build` query parameter for `audio/journey-audio.js` and `input/map-input.js`.

## Output and failure handling

Framework files are read without being rewritten; their SDK filenames and integrity hashes remain valid. Rewritten generated text loses its obsolete `.gz` and `.br` siblings. When reusing an existing matching package, its logical files must agree, and compressed representations are refreshed from the current SDK output.

The task stages the complete package and root entry-point changes before committing them. Read-only entry points and conflicting destination contents fail before mutation. Root replacements and their compressed siblings have backups for rollback. The former active generated package is retired only after successful commit; unrelated stale package directories remain untouched. If rollback itself cannot finish, the build fails and reports the location of the retained recovery files.

Publishing requires the installed .NET SDK and its Roslyn task factory. The finalizer has no Node or Python runtime dependency.

## Verification

The focused regression uses Node's standard library to invoke the production MSBuild task:

```sh
node tests/packaging/package-identity.cjs
```

It covers deterministic identities, repeated finalization, repeated SDK publishing into the same directory, runtime/adapter/asset changes, rewritten references, real gzip/Brotli reuse, missing-loader and destination conflicts, and read-only failure followed by a successful retry.

The Pages workflow verifies the already finalized output after ordinary publishing:

```sh
dotnet publish src/PixelMap.App -c Release -f net10.0-browserwasm -o artifacts/atlas
node tests/packaging/package-identity.cjs artifacts/atlas/wwwroot
```

The artifact verifier first requires the 64-digit package identity and both adapter version parameters. It snapshots the entire artifact, runs the task twice, and requires both passes to preserve every path and byte. It then verifies the loader's boot-resource hashes and decompresses the remaining gzip/Brotli files for comparison with their logical content. A skipped or incorrectly ordered publish hook therefore fails verification.

Applying the finalizer to the exact rendering artifact from run `42291311815` produced `package_0c8dc2c18e3de69f54efa23b7bc3750b1cc94303e46edc0827e7e6ded4a8aff6`. All 72 boot-resource hashes and all 219 framework files, including compressed variants, were preserved. All 250 remaining compressed representations decoded to their corresponding logical files, and both verification passes were byte-for-byte no-ops. These are packaging checks against that exact artifact; the paired rendering measurements remain documented separately in [performance.md](performance.md).
