# WebGPU browser checks

Install the pinned test dependencies and Chromium from the repository root:

```sh
npm ci --prefix tests/browser
cd tests/browser
npx playwright install --with-deps chromium
cd ../..
sudo apt-get install -y xvfb xauth libvulkan1 mesa-vulkan-drivers
```

WebGPU screenshots use headed full Chromium. On a Linux server or CI runner, keep Xvfb alive for the entire test command as shown below. On an interactive desktop, the `xvfb-run` wrapper can be omitted.

Check the browser presentation path without building Uno or starting a server:

```sh
PIXELMAP_SHOTS=webgpu-preflight xvfb-run -a -s '-screen 0 1600x1200x24' node tests/browser/webgpu.cjs --preflight-only
```

The preflight fulfills a document on a secure localhost origin. A magenta CSS background surrounds a green WebGPU clear using Uno's `rgba8unorm`/opaque canvas configuration. It checks both regions in the page screenshot and checks the separate canvas screenshot. Its context and device are isolated from the application's GPU probe.

Run the application scenarios against a published application served over HTTPS or localhost:

```sh
PIXELMAP_SHOTS=webgpu-evidence xvfb-run -a -s '-screen 0 1600x1200x24' node tests/browser/webgpu.cjs http://127.0.0.1:8100/UnoPixelMap/
```

The same preflight runs before application scenarios. `--preflight-only` may appear before or after the optional base URL. `CHROMIUM_PATH` can select an explicit browser executable; otherwise the test uses pinned full Chromium.

The software CI profile uses ANGLE's Vulkan backend and an explicit SwiftShader Vulkan compositor/WebGPU adapter. It preserves Playwright's screenshot feature. Actual adapter metadata and browser mode are recorded; software timing is not a hardware performance measurement.

`PIXELMAP_HEADLESS=1` is available for diagnosis. It retains all strict pixel assertions, so transparent WebGPU captures fail. The headed Linux configuration follows the [verified WebGPU capture guidance](https://github.com/vercel-labs/agent-browser/blob/main/skill-data/core/references/webgpu.md) and [Playwright/Xvfb reproduction](https://github.com/vercel-labs/vgpu/issues/109).

Application captures await shader queries, GPU queue completion and compositor frames within a joint 60-second timeout. Errors, lost devices, visible failure panels, transparent/blank output and unexpected readbacks fail the checks. The deliberate device-loss scenario separately requires the managed scene to report failure and stop submitting map work.

The shadow probe follows actual depth textures, per-layer views and submitted render passes. Balanced must draw one layer, High two and Ultra four; allocated but unused layers and repeatedly drawing into layer zero fail. Dusk, night and daylight captures exercise lighting changes. Paused close-up walking captures retain High and Ultra output for visual review.

Journey checks select the real 0.25× playback control, require motion, pause before the destination and require unchanged progress throughout GPU completion and capture. They also verify that Pause's accessibility state changes from disabled to enabled. Independent suites and route modes retain every failure and continue through their remaining cases; any failure still fails the run.

The mobile context requests DPR 2 and explicitly checks the actual browser density, native map-target dimensions, scene-buffer dimensions and physical screenshot width. An initial Normal capture retains a visible map label before the pinch changes its visibility. A DPR-2 screenshot containing an upscaled 1× map target fails this gate.

`PIXELMAP_SHOTS` selects the evidence directory. It contains `presentation.json`, page/canvas preflight images, per-capture GPU records, application screenshots and `report.json`. Failures also preserve graphics state, device identities/destruction stacks, console history and the accessibility tree.
