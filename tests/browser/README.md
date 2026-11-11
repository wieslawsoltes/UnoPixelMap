# WebGPU browser checks

Install the pinned test dependencies and Chromium from the repository root:

```sh
npm ci --prefix tests/browser
cd tests/browser
npx playwright install --with-deps chromium
cd ../..
```

Check the browser presentation path without building Uno or starting a server:

```sh
PIXELMAP_SHOTS=webgpu-preflight node tests/browser/webgpu.cjs --preflight-only
```

The preflight fulfills a document on a secure localhost origin. A magenta CSS background surrounds a green WebGPU clear using Uno's `rgba8unorm`/opaque canvas configuration. It checks both regions in the page screenshot and checks the separate canvas screenshot. Its context and device are isolated from the application's GPU probe.

Run the application scenarios against a published application served over HTTPS or localhost:

```sh
PIXELMAP_SHOTS=webgpu-evidence node tests/browser/webgpu.cjs http://127.0.0.1:8100/UnoPixelMap/
```

The same preflight runs before application scenarios. `--preflight-only` may appear before or after the optional base URL. `CHROMIUM_PATH` can select an explicit browser executable; otherwise the test uses pinned full Chromium.

The software CI profile aligns ANGLE, the Vulkan compositor and WebGPU on SwiftShader. It preserves Playwright's screenshot feature. Actual adapter metadata is recorded; software timing is not a hardware performance measurement.

Application captures await shader queries, GPU queue completion and compositor frames within a joint 60-second timeout. Errors, lost devices, visible failure panels, transparent/blank output and unexpected readbacks fail the checks. The deliberate device-loss scenario separately requires the managed scene to report failure and stop submitting map work.

`PIXELMAP_SHOTS` selects the evidence directory. It contains `presentation.json`, page/canvas preflight images, per-capture GPU records, application screenshots and `report.json`. Failures also preserve graphics state, device identities/destruction stacks, console history and the accessibility tree.
