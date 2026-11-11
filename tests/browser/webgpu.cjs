// Real browser integration checks for the Uno 7 shared-device renderer.
// GPU calls are observed independently of the application's diagnostic counters.
const assert = require("node:assert/strict");
const { mkdir, writeFile } = require("node:fs/promises");
const { join } = require("node:path");
// Playwright exposes browser-process stderr through this debug namespace.
// Set it before loading Playwright and retain any caller-selected namespaces.
process.env.DEBUG = [process.env.DEBUG, "pw:browser"].filter(Boolean).join(",");
const { chromium } = require("playwright");
const { PNG } = require("pngjs");

function parseArguments(args) {
    const addresses = args.filter(value => value !== "--preflight-only");
    assert.ok(addresses.length <= 1 && addresses.every(value => !value.startsWith("--")),
        "Usage: node webgpu.cjs [base URL] [--preflight-only]");
    return { base: addresses[0] || "http://127.0.0.1:8100/UnoPixelMap/", preflightOnly: args.includes("--preflight-only") };
}

const { base, preflightOnly } = parseArguments(process.argv.slice(2));
const output = process.env.PIXELMAP_SHOTS || "webgpu-evidence";
const report = { renderer: "Uno 7 WebGPU", scenarios: [], errors: [], timingMeaning: "CPU preparation/submission only; software GPU timings are not hardware FPS benchmarks." };
const consoleHistory = new WeakMap();

function installProbe() {
    const state = globalThis.__pixelAtlasGpuProbe = {
        modules: [], pipelines: [], errors: [], losses: [], pending: [], devices: [],
        deviceInfo: [], deviceDestroys: [],
        draws: 0, submits: 0, mapSubmits: 0, writeBytes: 0, readbacks: 0,
        buffers: 0, textures: 0, buffersDestroyed: 0, texturesDestroyed: 0, glContexts: 0
    };
    const map = label => /^PixelMap[ /]/.test(label || "");
    const encoders = new WeakSet(), passes = new WeakSet(), commands = new WeakSet();
    const buffers = new WeakSet(), textures = new WeakSet();
    const deviceIndices = new WeakMap();
    function wrap(prototype, name, action) {
        if (!prototype?.[name]) return;
        const original = prototype[name];
        prototype[name] = function (...args) { return action.call(this, original, args); };
    }
    for (const type of [globalThis.HTMLCanvasElement, globalThis.OffscreenCanvas])
        wrap(type?.prototype, "getContext", function (fn, args) {
            if (/webgl/i.test(args[0])) state.glContexts++;
            return Reflect.apply(fn, this, args);
        });
    wrap(globalThis.GPUAdapter?.prototype, "requestDevice", async function (fn, args) {
        const device = await Reflect.apply(fn, this, args);
        const deviceIndex = state.devices.length;
        deviceIndices.set(device, deviceIndex);
        state.devices.push(device);
        // Adapter properties are WebIDL getters, so object spread omits them.
        const info = this.info;
        state.deviceInfo.push({ deviceIndex, label: device.label, timeMs: performance.now(),
            adapter: info ? { vendor: info.vendor, architecture: info.architecture,
                device: info.device, description: info.description, isFallbackAdapter: info.isFallbackAdapter,
                subgroupMinSize: info.subgroupMinSize, subgroupMaxSize: info.subgroupMaxSize } : null,
            features: [...device.features] });
        device.addEventListener("uncapturederror", event => state.errors.push(`Device ${deviceIndex}: ${event.error.message}`));
        device.lost.then(info => state.losses.push({ deviceIndex, label: device.label, timeMs: performance.now(),
            reason: info.reason, message: info.message }));
        return device;
    });
    wrap(globalThis.GPUDevice?.prototype, "destroy", function (fn, args) {
        state.deviceDestroys.push({ deviceIndex: deviceIndices.get(this) ?? null, label: this.label,
            timeMs: performance.now(), stack: new Error("GPUDevice.destroy observed").stack });
        return Reflect.apply(fn, this, args);
    });
    wrap(globalThis.GPUDevice?.prototype, "createShaderModule", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        const label = args[0]?.label || "";
        if (map(label)) state.modules.push(label);
        state.pending.push(result.getCompilationInfo().then(info => {
            for (const message of info.messages)
                if (message.type === "error") state.errors.push(`${label}:${message.lineNum}:${message.linePos} ${message.message}`);
        }).catch(error => state.errors.push(String(error))));
        return result;
    });
    wrap(globalThis.GPUDevice?.prototype, "createRenderPipeline", function (fn, args) {
        const d = args[0], result = Reflect.apply(fn, this, args);
        if (map(d?.label)) state.pipelines.push({ label: d.label, deviceIndex: deviceIndices.get(this) ?? null, samples: d.multisample?.count || 1,
            depth: d.depthStencil?.format || null, depthWrite: d.depthStencil?.depthWriteEnabled || false,
            format: d.fragment?.targets?.[0]?.format || null });
        return result;
    });
    wrap(globalThis.GPUDevice?.prototype, "createBuffer", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        if (map(args[0]?.label)) { buffers.add(result); state.buffers++; }
        return result;
    });
    wrap(globalThis.GPUDevice?.prototype, "createTexture", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        if (map(args[0]?.label)) { textures.add(result); state.textures++; }
        return result;
    });
    wrap(globalThis.GPUBuffer?.prototype, "destroy", function (fn, args) {
        if (buffers.has(this)) { buffers.delete(this); state.buffersDestroyed++; }
        return Reflect.apply(fn, this, args);
    });
    wrap(globalThis.GPUTexture?.prototype, "destroy", function (fn, args) {
        if (textures.has(this)) { textures.delete(this); state.texturesDestroyed++; }
        return Reflect.apply(fn, this, args);
    });
    wrap(globalThis.GPUDevice?.prototype, "createCommandEncoder", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        if (map(args[0]?.label)) encoders.add(result);
        return result;
    });
    wrap(globalThis.GPUCommandEncoder?.prototype, "beginRenderPass", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        if (encoders.has(this)) passes.add(result);
        return result;
    });
    wrap(globalThis.GPUCommandEncoder?.prototype, "finish", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        if (encoders.has(this)) commands.add(result);
        return result;
    });
    wrap(globalThis.GPUCommandEncoder?.prototype, "copyTextureToBuffer", function (fn, args) {
        if (encoders.has(this)) state.readbacks++;
        return Reflect.apply(fn, this, args);
    });
    for (const name of ["draw", "drawIndexed", "drawIndirect", "drawIndexedIndirect"])
        wrap(globalThis.GPURenderPassEncoder?.prototype, name, function (fn, args) {
            if (passes.has(this)) state.draws++;
            return Reflect.apply(fn, this, args);
        });
    wrap(globalThis.GPUQueue?.prototype, "submit", function (fn, args) {
        state.submits++;
        if (args[0]?.some(buffer => commands.has(buffer))) state.mapSubmits++;
        return Reflect.apply(fn, this, args);
    });
    wrap(globalThis.GPUQueue?.prototype, "writeBuffer", function (fn, args) {
        if (buffers.has(args[0])) {
            const data = args[2];
            // Typed-array offsets/sizes are elements; ArrayBuffer/DataView use bytes.
            // Emscripten often supplies the whole WASM heap plus a bounded range.
            const unit = ArrayBuffer.isView(data) ? (data.BYTES_PER_ELEMENT || 1) : 1;
            const offset = (args[3] || 0) * unit;
            state.writeBytes += args[4] === undefined
                ? Math.max(0, (data?.byteLength || 0) - offset)
                : args[4] * unit;
        }
        return Reflect.apply(fn, this, args);
    });
}

async function click(page, name, touch = false) {
    const control = page.getByRole("button", { name, exact: true, disabled: false })
        .or(page.getByRole("checkbox", { name, exact: true, disabled: false }));
    await control.waitFor({ state: "visible" });
    const bounds = await control.boundingBox();
    assert.ok(bounds?.width && bounds?.height, name + " has a usable hit target");
    const x = bounds.x + bounds.width / 2, y = bounds.y + bounds.height / 2;
    if (touch) await page.touchscreen.tap(x, y); else await page.mouse.click(x, y);
}

function menuItem(page, name) {
    return page.getByRole("menuitem", { name, exact: true })
        .or(page.getByRole("menuitemcheckbox", { name, exact: true }));
}

async function menu(page, name, submenu) {
    await click(page, "Map appearance");
    if (submenu) {
        const parent = menuItem(page, submenu);
        await parent.waitFor({ state: "visible" });
        await parent.focus();
        await page.keyboard.press("ArrowRight");
    }
    const item = menuItem(page, name);
    await item.waitFor({ state: "visible" });
    await item.focus();
    await page.keyboard.press("Enter");
    await item.waitFor({ state: "hidden" });
    await page.mouse.move(page.viewportSize().width / 2, 20);
}

async function awaitFrame(page, predicate, argument) {
    await page.waitForFunction(({ predicate, argument }) => {
        const state = globalThis.PixelAtlasWebGpu;
        if (globalThis.PixelAtlasGraphics?.failure || state?.failure)
            throw new Error(globalThis.PixelAtlasGraphics?.failure || state.failure);
        return state?.frames > 0 && new Function("s", "arg", `return (${predicate})(s,arg)`)(state, argument);
    }, { predicate: predicate.toString(), argument }, { timeout: 120000 });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    // Device loss and validation errors can arrive after the frame predicate,
    // including while the two animation-frame callbacks are pending.
    await assertGraphicsHealthy(page, "Frame readiness");
}

async function graphicsSnapshot(page) {
    return page.evaluate(() => {
        const graphics = globalThis.PixelAtlasGraphics;
        const diagnostic = globalThis.PixelAtlasWebGpu;
        const panel = document.getElementById("pixelmap-graphics-error");
        const style = panel && getComputedStyle(panel);
        const bounds = panel?.getBoundingClientRect();
        const source = globalThis.__pixelAtlasGpuProbe;
        const { pending = [], devices = [], ...gpu } = source || {};
        return {
            graphics: graphics ? { state: graphics.state, failure: graphics.failure } : null,
            diagnostic: diagnostic ? { ...diagnostic } : null,
            errorOverlay: {
                visible: !!(panel && bounds.width > 0 && bounds.height > 0 &&
                    style.display !== "none" && style.visibility !== "hidden" && style.visibility !== "collapse"),
                text: panel?.textContent?.trim() || ""
            },
            gpu: source ? { ...gpu, pendingCount: pending.length, deviceCount: devices.length } : null
        };
    });
}

async function assertGraphicsHealthy(page, name) {
    const snapshot = await graphicsSnapshot(page);
    assert.equal(snapshot.graphics?.state, "available", `${name}: WebGPU is available (${snapshot.graphics?.failure || "no availability state"})`);
    assert.ok(!snapshot.graphics?.failure, `${name}: ${snapshot.graphics?.failure}`);
    assert.ok(!snapshot.diagnostic?.failure, `${name}: ${snapshot.diagnostic?.failure}`);
    assert.equal(snapshot.errorOverlay.visible, false, `${name}: the graphics error panel must be hidden (${snapshot.errorOverlay.text})`);
    assert.deepEqual(snapshot.gpu?.errors, [], `${name}: GPU validation remains successful`);
    assert.deepEqual(snapshot.gpu?.losses, [], `${name}: the shared GPU device remains available`);
    return snapshot;
}

async function saveFailure(page, name, error) {
    // Capture without awaiting shader promises or queue completion: a failed
    // device must not prevent the original diagnostic from reaching artifacts.
    const evidence = { name, failure: error.stack || String(error), url: page.url() };
    try { evidence.snapshot = await graphicsSnapshot(page); }
    catch (failure) { evidence.snapshotError = String(failure); }
    try {
        const bytes = await page.screenshot({ path: join(output, name + "-failure.png"), timeout: 30000 });
        console.log("PIXELMAP_PREVIEW " + name + "-failure " + bytes.toString("base64"));
    } catch (failure) { evidence.screenshotError = String(failure); }
    try {
        evidence.accessibility = await page.locator("body").ariaSnapshot({ timeout: 10000 });
        await writeFile(join(output, name + "-failure.txt"), evidence.accessibility);
    } catch (failure) { evidence.accessibilityError = String(failure); }
    try { evidence.finalSnapshot = await graphicsSnapshot(page); }
    catch (failure) { evidence.finalSnapshotError = String(failure); }
    evidence.console = consoleHistory.get(page) || [];
    await writeFile(join(output, name + "-failure.json"), JSON.stringify(evidence, null, 2))
        .catch(failure => console.error("Could not save failure evidence:", failure));
}

async function open(browser, hash = "", { mobile = false, density = 1 } = {}) {
    const context = await browser.newContext({ viewport: mobile ? { width: 390, height: 844 } : { width: 1200, height: 800 },
        deviceScaleFactor: density, isMobile: mobile, hasTouch: mobile });
    const page = await context.newPage();
    page.setDefaultTimeout(30000);
    const history = [];
    consoleHistory.set(page, history);
    const remember = (type, text) => {
        history.push({ type, text: text.slice(0, 3000) });
        if (history.length > 200) history.shift();
    };
    page.on("pageerror", error => { remember("pageerror", error.message); report.errors.push(error.message); console.error(error.message); });
    page.on("crash", () => { remember("crash", "Browser page crashed"); report.errors.push("Browser page crashed"); });
    page.on("console", message => {
        remember(message.type(), message.text());
        if (message.type() === "error") console.error("Browser:", message.text().slice(0, 3000));
    });
    try {
        await page.addInitScript(installProbe);
        for (const host of ["tiles.openfreemap.org", "tiles.maps.eox.at", "routing.openstreetmap.de"])
            await page.route(`https://${host}/**`, route => route.abort());
        const address = new URL(base);
        address.hash = hash;
        await page.goto(address.href, { waitUntil: "domcontentloaded", timeout: 120000 });
        await awaitFrame(page, s => s.patches > 0);
        const accessibility = page.getByRole("button", { name: "Enable accessibility", exact: true });
        if (await accessibility.count()) await accessibility.evaluate(element => element.click());
        await page.getByRole("button", { name: "Map appearance", exact: true }).waitFor({ state: "visible" });
        return { context, page };
    } catch (error) {
        const name = (hash || (mobile ? "mobile" : "desktop")).replace(/[^a-z0-9-]/gi, "-").toLowerCase() + "-startup";
        await saveFailure(page, name, error);
        await context.close().catch(() => {});
        throw error;
    }
}

function inspectPixels(png) {
    const colors = new Set();
    let light = 0, samples = 0;
    for (let y = Math.floor(png.height * .30); y < png.height * .74; y += 2)
        for (let x = Math.floor(png.width * .30); x < png.width * .77; x += 2) {
            const i = (y * png.width + x) * 4;
            colors.add((png.data[i] << 16) | (png.data[i + 1] << 8) | png.data[i + 2]);
            light += (png.data[i] + png.data[i + 1] + png.data[i + 2]) / 3;
            samples++;
        }
    return { colors: colors.size, luminance: light / samples };
}

function difference(a, b) {
    if (a.width !== b.width || a.height !== b.height) return 1;
    let changed = 0, samples = 0;
    for (let y = Math.floor(a.height * .30); y < a.height * .74; y += 3)
        for (let x = Math.floor(a.width * .30); x < a.width * .77; x += 3) {
            const i = (y * a.width + x) * 4;
            if (Math.abs(a.data[i] - b.data[i]) + Math.abs(a.data[i + 1] - b.data[i + 1]) + Math.abs(a.data[i + 2] - b.data[i + 2]) > 15) changed++;
            samples++;
        }
    return changed / samples;
}

async function capture(page, name) {
    await assertGraphicsHealthy(page, name + " before screenshot");
    // CPU frame counters and RAF callbacks do not imply that cold GPU work or
    // presentation has completed. Fence real work before judging its pixels.
    await verifyGpu(page, name);
    const bytes = await page.screenshot({ path: join(output, name + ".png"), timeout: 30000 });
    const { diagnostic } = await assertGraphicsHealthy(page, name + " after screenshot");
    if (name === "desktop-pixel" || name === "style-normal")
        console.log("PIXELMAP_PREVIEW " + name + " " + bytes.toString("base64"));
    const pixels = PNG.sync.read(bytes), measured = inspectPixels(pixels);
    assert.ok(measured.colors > 12 && measured.luminance > 8 && measured.luminance < 250,
        `${name}: central map contains varied rendered geometry, not a blank canvas (${JSON.stringify(measured)})`);
    report.scenarios.push({ name, ...measured, diagnostic });
    console.log(name, JSON.stringify(diagnostic));
    return pixels;
}

async function verifyGpu(page, name) {
    const evidence = await page.evaluate(async () => {
        const s = globalThis.__pixelAtlasGpuProbe;
        let timeout;
        try {
            await Promise.race([
                (async () => {
                    await Promise.all([...s.pending, ...s.devices.map(device => device.queue.onSubmittedWorkDone()
                        .catch(error => s.errors.push("GPU queue completion: " + String(error))))]);
                    // Let completed GPU work reach the compositor before capture.
                    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                    // GPU errors are dispatched as browser tasks.
                    await new Promise(resolve => setTimeout(resolve, 0));
                })(),
                new Promise((_, reject) => {
                    timeout = setTimeout(() => reject(new Error("GPU shader queries, queue work or presentation did not complete within 60 seconds")), 60000);
                })
            ]);
        } finally { clearTimeout(timeout); }
        return { ...s, pending: undefined, devices: undefined };
    });
    await writeFile(join(output, name + "-gpu.json"), JSON.stringify(evidence, null, 2));
    await assertGraphicsHealthy(page, name + " GPU completion");
    assert.deepEqual(evidence.errors, [], name + ": all WGSL modules and GPU work validate");
    assert.deepEqual(evidence.losses, [], name + ": shared device remains available");
    assert.ok(evidence.modules.length >= 2 && evidence.pipelines.length >= 2, "Native map shader modules and pipelines exist");
    assert.ok(evidence.pipelines.some(p => p.depth && p.depthWrite), "Scene geometry uses hardware depth writes");
    assert.ok(evidence.draws > 0 && evidence.mapSubmits > 0, "Map commands reach the actual WebGPU queue");
    assert.equal(evidence.readbacks, 0, "Map presentation performs no texture readback");
    assert.equal(evidence.glContexts, 0, "Production opens no WebGL rendering context");
    return evidence;
}

function inspectRegion(png, { x, y, width, height }) {
    assert.ok(width > 0 && height > 0 && x >= 0 && y >= 0 && x + width <= png.width && y + height <= png.height,
        "Presentation sample region is inside the screenshot");
    const minimum = [255, 255, 255, 255], maximum = [0, 0, 0, 0];
    for (let row = y; row < y + height; row++)
        for (let column = x; column < x + width; column++) {
            const offset = (row * png.width + column) * 4;
            for (let channel = 0; channel < 4; channel++) {
                minimum[channel] = Math.min(minimum[channel], png.data[offset + channel]);
                maximum[channel] = Math.max(maximum[channel], png.data[offset + channel]);
            }
        }
    return { minimum, maximum };
}

function assertRegionColor(measured, expected, name) {
    assert.ok(expected.every((value, index) => measured.minimum[index] >= value - 3 && measured.maximum[index] <= value + 3),
        `${name}: expected opaque RGBA ${JSON.stringify(expected)}, measured ${JSON.stringify(measured)}`);
}

async function presentationPreflight(browser) {
    // A routed localhost document needs no server or published Uno files. Its
    // separate context/device has no map probe and cannot affect map counters.
    const address = "http://127.0.0.1/__pixelmap_webgpu_presentation__.html";
    const context = await browser.newContext({ viewport: { width: 320, height: 240 }, deviceScaleFactor: 1 });
    const page = await context.newPage();
    const evidence = { url: address, expected: { css: [255, 0, 255, 255], gpu: [0, 255, 0, 255] },
        pageErrors: [], console: [], passed: false };
    let pageBytes, canvasBytes;
    page.on("pageerror", error => evidence.pageErrors.push(error.message));
    page.on("crash", () => evidence.pageErrors.push("Presentation page crashed"));
    page.on("console", message => {
        evidence.console.push({ type: message.type(), text: message.text().slice(0, 3000) });
        if (evidence.console.length > 200) evidence.console.shift();
    });
    const screenshotPage = () => page.screenshot({ path: join(output, "presentation-page.png"), timeout: 30000 });
    const screenshotCanvas = () => page.locator("canvas").screenshot({ path: join(output, "presentation-canvas.png"), timeout: 30000 });
    try {
        await page.route(address, route => route.fulfill({ contentType: "text/html", body: `<!doctype html>
            <html><head><title>Independent WebGPU presentation</title><link rel="icon" href="data:,">
            <style>html,body{margin:0;width:100%;height:100%;background:#ff00ff}
            canvas{position:absolute;left:64px;top:48px;width:192px;height:128px;background:#000}</style></head>
            <body><canvas width="192" height="128" aria-label="WebGPU green clear"></canvas></body></html>` }));
        await page.goto(address, { waitUntil: "domcontentloaded", timeout: 30000 });
        await page.evaluate(async () => {
            const state = globalThis.__pixelAtlasPresentation = { errors: [], losses: [], queueCompleted: false };
            let timeout;
            try {
                await Promise.race([
                    (async () => {
                        if (!isSecureContext || !navigator.gpu) throw new Error("Secure-context WebGPU is unavailable");
                        const adapter = await navigator.gpu.requestAdapter({ powerPreference: "high-performance" });
                        if (!adapter) throw new Error("No WebGPU presentation adapter is available");
                        const info = adapter.info;
                        state.adapter = { vendor: info.vendor, architecture: info.architecture, device: info.device,
                            description: info.description, isFallbackAdapter: info.isFallbackAdapter };
                        const device = await adapter.requestDevice({ label: "Independent presentation device" });
                        globalThis.__pixelAtlasPresentationDevice = device;
                        device.addEventListener("uncapturederror", event => state.errors.push(event.error.message));
                        device.lost.then(info => state.losses.push({ reason: info.reason, message: info.message }));
                        const canvas = document.querySelector("canvas");
                        const canvasContext = canvas.getContext("webgpu");
                        if (!canvasContext) throw new Error("A WebGPU canvas context could not be created");
                        // Match Uno 7.0.32's browser canvas presentation format.
                        state.format = "rgba8unorm";
                        canvasContext.configure({ device, format: state.format, alphaMode: "opaque" });
                        const encoder = device.createCommandEncoder({ label: "Independent green clear" });
                        const pass = encoder.beginRenderPass({ colorAttachments: [{
                            view: canvasContext.getCurrentTexture().createView(),
                            clearValue: { r: 0, g: 1, b: 0, a: 1 }, loadOp: "clear", storeOp: "store"
                        }] });
                        pass.end();
                        device.queue.submit([encoder.finish()]);
                        await device.queue.onSubmittedWorkDone();
                        state.queueCompleted = true;
                        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                        await new Promise(resolve => setTimeout(resolve, 0));
                    })(),
                    new Promise((_, reject) => {
                        timeout = setTimeout(() => reject(new Error("Independent WebGPU initialization, queue or presentation timed out after 60 seconds")), 60000);
                    })
                ]);
            } catch (error) { state.failure = String(error); throw error; }
            finally { clearTimeout(timeout); }
        });
        pageBytes = await screenshotPage();
        canvasBytes = await screenshotCanvas();
        evidence.gpu = await page.evaluate(() => globalThis.__pixelAtlasPresentation);
        const wholePage = PNG.sync.read(pageBytes), canvas = PNG.sync.read(canvasBytes);
        evidence.colors = {
            pageCss: inspectRegion(wholePage, { x: 16, y: 16, width: 24, height: 24 }),
            pageGpu: inspectRegion(wholePage, { x: 96, y: 80, width: 64, height: 64 }),
            canvasGpu: inspectRegion(canvas, { x: 8, y: 8, width: canvas.width - 16, height: canvas.height - 16 })
        };
        assert.deepEqual(evidence.pageErrors, [], "Independent presentation has no page exceptions");
        assert.deepEqual(evidence.gpu.errors, [], "Independent WebGPU clear validates");
        assert.deepEqual(evidence.gpu.losses, [], "Independent WebGPU device remains available");
        assert.equal(evidence.gpu.queueCompleted, true, "Independent clear completed on the real GPU queue");
        assertRegionColor(evidence.colors.pageCss, evidence.expected.css, "Full-page CSS background");
        assertRegionColor(evidence.colors.pageGpu, evidence.expected.gpu, "Full-page WebGPU canvas");
        assertRegionColor(evidence.colors.canvasGpu, evidence.expected.gpu, "Canvas screenshot");
        evidence.passed = true;
        report.scenarios.push({ name: "webgpu-presentation", adapter: evidence.gpu.adapter, colors: evidence.colors });
    } catch (error) {
        evidence.failure = error.stack || String(error);
        if (!pageBytes) pageBytes = await screenshotPage().catch(() => null);
        if (!canvasBytes) canvasBytes = await screenshotCanvas().catch(() => null);
        throw error;
    } finally {
        if (!evidence.gpu)
            evidence.gpu = await page.evaluate(() => globalThis.__pixelAtlasPresentation || null)
                .catch(error => ({ snapshotError: String(error) }));
        await writeFile(join(output, "presentation.json"), JSON.stringify(evidence, null, 2));
        console.log("WebGPU presentation:", JSON.stringify(evidence));
        if (pageBytes) console.log("PIXELMAP_PREVIEW presentation-" + (evidence.passed ? "passed " : "failure ") + pageBytes.toString("base64"));
        await context.close();
    }
}

async function desktop(browser) {
    const { context, page } = await open(browser);
    try {
        const first = await capture(page, "desktop-pixel");
        assert.ok(await page.evaluate(() => PixelAtlasWebGpu.triangles > 0), "The opening city contains 3D building geometry");
        await page.mouse.move(720, 410);
        await page.mouse.down();
        await page.mouse.move(810, 445, { steps: 8 });
        await page.mouse.up();
        const frames = await page.evaluate(() => PixelAtlasWebGpu.frames);
        await page.mouse.wheel(0, -170);
        await awaitFrame(page, (s, previous) => s.frames > previous, frames);
        assert.ok(difference(first, await capture(page, "desktop-camera")) > .03, "Pan and wheel change map geometry");

        const styles = [["Normal rendering", "Normal"], ["Minecraft rendering", "Minecraft"], ["Roblox rendering", "Roblox"], ["Pixel art rendering", "Pixel"]];
        let previous;
        for (const [command, style] of styles) {
            await menu(page, command);
            await awaitFrame(page, (s, expected) => s.style === expected, style);
            const image = await capture(page, "style-" + style.toLowerCase());
            if (previous) assert.ok(difference(previous, image) > .005, "Rendering styles produce distinct map output");
            previous = image;
        }
        await menu(page, "Normal rendering");
        for (const quality of ["Balanced", "Ultra", "High"]) {
            await menu(page, quality + " quality", "Rendering quality");
            await awaitFrame(page, (s, expected) => s.quality === expected, quality);
            await capture(page, "quality-" + quality.toLowerCase());
        }
        await click(page, "Earth imagery layer");
        await awaitFrame(page, s => s.imageryCount > 0);
        await capture(page, "earth-imagery");
        await menu(page, "Terrain relief");
        await awaitFrame(page, s => s.relief);
        await capture(page, "terrain-relief");
        await click(page, "Elevation layer");
        await awaitFrame(page, s => s.elevation);
        await capture(page, "elevation-layer");
        await click(page, "Map layer");

        await click(page, "Open free roam game");
        await click(page, "Start free roam at map center");
        await page.getByRole("button", { name: "Pause free roam", exact: true }).waitFor({ state: "visible" });
        const before = await page.evaluate(() => PixelAtlasWebGpu.frames);
        await page.keyboard.down("w");
        try { await awaitFrame(page, (s, frames) => s.frames > frames + 3, before); }
        finally { await page.keyboard.up("w"); }
        await capture(page, "free-roam-walk");
        await click(page, "Pause free roam");
        await click(page, "Exit free roam");
        await page.setViewportSize({ width: 960, height: 640 });
        await awaitFrame(page, s => s.physicalWidth <= 960);
        await capture(page, "resized");
        const gpu = await verifyGpu(page, "desktop");
        assert.ok(gpu.pipelines.some(p => p.samples === 4), "Smooth quality creates multisampled geometry pipelines");
    } catch (error) {
        await saveFailure(page, "desktop", error);
        throw error;
    } finally { await context.close(); }
}

async function journeys(browser) {
    for (const mode of ["Drive", "Bike", "Walk", "Boat", "Train", "Flight"]) {
        const { context, page } = await open(browser, "journey/" + mode);
        try {
            await click(page, "Preview route");
            const before = await page.evaluate(() => PixelAtlasWebGpu.frames);
            await awaitFrame(page, (s, previous) => s.frames > previous + 2, before);
            await click(page, "Pause or resume journey");
            await capture(page, "journey-" + mode.toLowerCase());
            await verifyGpu(page, "journey-" + mode.toLowerCase());
        } catch (error) {
            await saveFailure(page, "journey-" + mode.toLowerCase(), error);
            throw error;
        } finally { await context.close(); }
    }
}

async function mobile(browser) {
    const { context, page } = await open(browser, "", { mobile: true, density: 2 });
    try {
        const before = await capture(page, "mobile-pixel");
        const beforePinch = await page.evaluate(() => ({
            frames: PixelAtlasWebGpu.frames, zoom: PixelAtlasWebGpu.zoom
        }));
        const cdp = await context.newCDPSession(page);
        await cdp.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x: 135, y: 410, id: 0 }, { x: 245, y: 410, id: 1 }] });
        for (let step = 1; step <= 6; step++)
            await cdp.send("Input.dispatchTouchEvent", { type: "touchMove", touchPoints: [{ x: 135 - step * 5, y: 410, id: 0 }, { x: 245 + step * 5, y: 410, id: 1 }] });
        await cdp.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
        await awaitFrame(page, (s, previous) =>
            s.frames > previous.frames && Math.abs(s.zoom - previous.zoom) > 0.01, beforePinch);
        const after = await capture(page, "mobile-pinch");
        assert.equal(await page.evaluate(() => visualViewport.scale), 1, "Pinch does not zoom the page or controls");
        assert.ok(difference(before, after) > .01, "Touch pinch changes the map");
        const bounds = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth }));
        assert.ok(bounds.scrollWidth <= bounds.width + 1, "Mobile layout does not overflow horizontally");
        await menu(page, "Normal rendering");
        await awaitFrame(page, s => s.style === "Normal");
        await capture(page, "mobile-normal-high-dpi");
        await verifyGpu(page, "mobile");
    } catch (error) {
        await saveFailure(page, "mobile", error);
        throw error;
    } finally { await context.close(); }
}

async function unavailable(browser) {
    const context = await browser.newContext();
    try {
        const page = await context.newPage();
        await page.addInitScript(() => Object.defineProperty(navigator, "gpu", { value: undefined, configurable: true }));
        await page.goto(base, { waitUntil: "domcontentloaded" });
        await page.getByRole("heading", { name: "WebGPU is unavailable" }).waitFor({ state: "visible" });
        await page.getByRole("button", { name: "Try again", exact: true }).waitFor({ state: "visible" });
        assert.equal(await page.evaluate(() => PixelAtlasGraphics.state), "unavailable");
        await page.screenshot({ path: join(output, "webgpu-unavailable.png") });
        report.scenarios.push({ name: "webgpu-unavailable", actionableDiagnostic: true });
    } finally { await context.close(); }
}

async function deviceLoss(browser) {
    const { context, page } = await open(browser);
    try {
        await verifyGpu(page, "device-loss-before");
        await page.evaluate(() => {
            const devices = globalThis.__pixelAtlasGpuProbe.devices;
            if (!devices.length) throw new Error("No real application GPUDevice was acquired");
            devices[devices.length - 1].destroy();
        });
        await page.getByRole("heading", { name: "WebGPU is unavailable" }).waitFor({ state: "visible" });
        assert.match(await page.locator("#pixelmap-graphics-detail").innerText(), /device was lost/i);
        await page.waitForFunction(() => !!globalThis.PixelAtlasWebGpu?.failure, null, { timeout: 10000 });
        const stopped = await page.evaluate(async () => {
            const nextFrame = () => new Promise(resolve => requestAnimationFrame(resolve));
            await nextFrame(); await nextFrame();
            const before = globalThis.__pixelAtlasGpuProbe.mapSubmits;
            await nextFrame(); await nextFrame();
            return { before, after: globalThis.__pixelAtlasGpuProbe.mapSubmits,
                failure: globalThis.PixelAtlasWebGpu.failure };
        });
        assert.ok(stopped.failure, "Managed scene receives actual shared-device loss");
        assert.equal(stopped.after, stopped.before, "A failed scene stops submitting map GPU work");
        await page.screenshot({ path: join(output, "device-loss-recovery.png") });
        report.scenarios.push({ name: "device-loss", actionableDiagnostic: true, managed: stopped });
    } finally { await context.close(); }
}

(async () => {
    await mkdir(output, { recursive: true });
    const executablePath = process.env.CHROMIUM_PATH || undefined;
    const channel = executablePath ? undefined : "chromium";
    const headless = process.env.PIXELMAP_HEADLESS === "1";
    report.browserLaunch = { channel: channel || "explicit executable", executablePath, headless,
        gpu: "ANGLE Vulkan; SwiftShader Vulkan compositor and WebGPU", gpuWatchdog: "disabled for software CI",
        enabledFeatures: ["CDPScreenshotNewSurface", "Vulkan"] };
    const browser = await chromium.launch({ executablePath, channel, headless,
        // Replace only Playwright 1.64.1's feature switch with its merged value;
        // retain every other default argument and avoid duplicate switches.
        ignoreDefaultArgs: ["--enable-features=CDPScreenshotNewSurface"],
        // Software rendering can exceed Chromium's GPU watchdog while compiling
        // cold shaders. Tests still fail on loss/errors and bounded GPU waits.
        // Software Vulkan adapter flags follow Chromium 156's pixel tests:
        // https://github.com/chromium/chromium/blob/156.0.8078.4/content/test/gpu/gpu_tests/pixel_test_pages.py
        // Linux WebGPU canvas capture needs a real window (CI provides Xvfb)
        // and ANGLE's Vulkan backend. Headless remains an explicit diagnostic.
        // https://github.com/vercel-labs/agent-browser/blob/main/skill-data/core/references/webgpu.md
        // https://github.com/vercel-labs/vgpu/issues/109
        args: ["--disable-dev-shm-usage", "--disable-gpu-watchdog", "--enable-features=CDPScreenshotNewSurface,Vulkan",
            "--use-angle=vulkan", "--use-vulkan=swiftshader", "--use-webgpu-adapter=swiftshader",
            "--disable-vulkan-surface", "--enable-unsafe-webgpu", "--enable-unsafe-swiftshader"] });
    try {
        report.browser = await browser.version();
        await presentationPreflight(browser);
        if (!preflightOnly) {
            await desktop(browser);
            await journeys(browser);
            await mobile(browser);
            await unavailable(browser);
            await deviceLoss(browser);
        }
        assert.deepEqual(report.errors, [], "No application exceptions occurred");
        report.passed = true;
        console.log(preflightOnly ? "WebGPU presentation preflight passed:" : "WebGPU integration passed:", report.scenarios.length, "scenarios");
    } catch (error) {
        report.passed = false;
        report.failure = error.stack || String(error);
        throw error;
    } finally {
        await writeFile(join(output, "report.json"), JSON.stringify(report, null, 2));
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
