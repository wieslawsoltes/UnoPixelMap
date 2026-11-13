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
const { installRenderGraphProbe } = require("./webgpu-rendergraph-probe.cjs");
const { runShaderFixtures } = require("./webgpu-shader-fixtures.cjs");

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
        deviceInfo: [], deviceDestroys: [], shadowTextures: [],
        draws: 0, submits: 0, mapSubmits: 0, writeBytes: 0, readbacks: 0,
        buffers: 0, textures: 0, buffersDestroyed: 0, texturesDestroyed: 0, glContexts: 0
    };
    const map = label => /^PixelMap[ /]/.test(label || "");
    const encoders = new WeakSet(), passes = new WeakSet(), commands = new WeakSet();
    const buffers = new WeakSet(), textures = new WeakSet();
    const deviceIndices = new WeakMap();
    const shadowTextures = new WeakMap(), shadowViews = new WeakMap(), shadowPasses = new WeakMap();
    const shadowEncoders = new WeakMap(), shadowCommands = new WeakMap();
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
        if (args[0]?.label === "PixelMap sun shadow array") {
            const record = { id: state.shadowTextures.length + 1, label: result.label,
                width: result.width, height: result.height, layers: result.depthOrArrayLayers,
                format: result.format, samples: result.sampleCount, usage: result.usage,
                submittedPasses: {}, submittedDraws: {}, invalidRenderViews: 0, destroyed: false };
            state.shadowTextures.push(record);
            shadowTextures.set(result, record);
        }
        return result;
    });
    wrap(globalThis.GPUTexture?.prototype, "createView", function (fn, args) {
        const result = Reflect.apply(fn, this, args), texture = shadowTextures.get(this);
        if (texture) {
            const d = args[0] || {}, baseArrayLayer = d.baseArrayLayer ?? 0;
            const dimension = d.dimension ?? (texture.layers > 1 ? "2d-array" : "2d");
            const arrayLayerCount = d.arrayLayerCount ?? (dimension === "2d" ? 1 : texture.layers - baseArrayLayer);
            shadowViews.set(result, { texture, baseArrayLayer, arrayLayerCount });
        }
        return result;
    });
    wrap(globalThis.GPUBuffer?.prototype, "destroy", function (fn, args) {
        if (buffers.has(this)) { buffers.delete(this); state.buffersDestroyed++; }
        return Reflect.apply(fn, this, args);
    });
    wrap(globalThis.GPUTexture?.prototype, "destroy", function (fn, args) {
        if (textures.has(this)) { textures.delete(this); state.texturesDestroyed++; }
        const shadow = shadowTextures.get(this);
        if (shadow) shadow.destroyed = true;
        return Reflect.apply(fn, this, args);
    });
    wrap(globalThis.GPUDevice?.prototype, "createCommandEncoder", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        if (map(args[0]?.label)) { encoders.add(result); shadowEncoders.set(result, []); }
        return result;
    });
    wrap(globalThis.GPUCommandEncoder?.prototype, "beginRenderPass", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        if (encoders.has(this)) {
            passes.add(result);
            const shadow = shadowViews.get(args[0]?.depthStencilAttachment?.view);
            if (shadow) {
                const record = { ...shadow, draws: 0 };
                shadowPasses.set(result, record);
                shadowEncoders.get(this).push(record);
            }
        }
        return result;
    });
    wrap(globalThis.GPUCommandEncoder?.prototype, "finish", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        if (encoders.has(this)) { commands.add(result); shadowCommands.set(result, shadowEncoders.get(this)); }
        return result;
    });
    wrap(globalThis.GPUCommandEncoder?.prototype, "copyTextureToBuffer", function (fn, args) {
        if (encoders.has(this)) state.readbacks++;
        return Reflect.apply(fn, this, args);
    });
    for (const name of ["draw", "drawIndexed", "drawIndirect", "drawIndexedIndirect"])
        wrap(globalThis.GPURenderPassEncoder?.prototype, name, function (fn, args) {
            if (passes.has(this)) state.draws++;
            const shadow = shadowPasses.get(this);
            if (shadow) shadow.draws++;
            return Reflect.apply(fn, this, args);
        });
    wrap(globalThis.GPUQueue?.prototype, "submit", function (fn, args) {
        state.submits++;
        if (args[0]?.some(buffer => commands.has(buffer))) state.mapSubmits++;
        const result = Reflect.apply(fn, this, args);
        // Count only passes in command buffers that actually reach the queue.
        // Unsubmitted encoders and unused texture-array layers prove nothing.
        for (const command of args[0] || [])
            for (const pass of shadowCommands.get(command) || []) {
                const { texture, baseArrayLayer: layer, arrayLayerCount, draws } = pass;
                texture.submittedPasses[layer] = (texture.submittedPasses[layer] || 0) + 1;
                texture.submittedDraws[layer] = (texture.submittedDraws[layer] || 0) + draws;
                if (arrayLayerCount !== 1 || layer < 0 || layer >= texture.layers)
                    texture.invalidRenderViews++;
            }
        return result;
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
        await page.addInitScript({ content: `(${installProbe.toString()})();(${installRenderGraphProbe.toString()})();` });
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
    const { diagnostic } = await assertGraphicsHealthy(page, name + " GPU completion");
    assert.deepEqual(evidence.errors, [], name + ": all WGSL modules and GPU work validate");
    assert.deepEqual(evidence.losses, [], name + ": shared device remains available");
    assert.ok(evidence.modules.length >= 2 && evidence.pipelines.length >= 2, "Native map shader modules and pipelines exist");
    assert.ok(evidence.pipelines.some(p => p.depth && p.depthWrite), "Scene geometry uses hardware depth writes");
    assert.ok(evidence.draws > 0 && evidence.mapSubmits > 0, "Map commands reach the actual WebGPU queue");
    assert.equal(evidence.readbacks, 0, "Map presentation performs no texture readback");
    assert.equal(evidence.glContexts, 0, "Production opens no WebGL rendering context");
    if (diagnostic.style === "Normal" || diagnostic.style === "Roblox")
        verifyShadowLayers(evidence, diagnostic.quality, name);
    verifyRenderGraph(evidence, diagnostic, name);
    return evidence;
}

function verifyRenderGraph(evidence, diagnostic, name) {
    const graph = evidence.renderGraph;
    assert.ok(graph?.submissions > 0, name + ": render-graph commands reach the real queue");
    assert.deepEqual(graph.errors, [], name + ": every submitted resource dependency remains valid across frame eviction");
    const live = graph.textures.filter(texture => !texture.destroyed);
    const frame = graph.frames.at(-1), passes = frame.passes;
    const smooth = diagnostic.style === "Normal" || diagnostic.style === "Roblox";
    if (!smooth) assert.equal(passes.filter(pass => /^PixelMap sun shadow /.test(pass.label)).length, 0,
        name + ": pixel styles submit no unused shadow clears or draws");
    const maximumLevels = smooth ? ({ Balanced: 0, High: 5, Ultra: 6 }[diagnostic.quality]) : 0;
    assert.notEqual(maximumLevels, undefined, name + ": bloom quality is recognized");
    const scene = live.findLast(texture => texture.label === "PixelMap scene color");
    assert.ok(scene, name + ": scene color has an independently observed allocation");
    const photo = live.filter(texture => texture.label === "PixelMap photograph");
    const boundTexture = (pass, binding) => pass.inputs.find(view => view.group === 0 && view.binding === binding && view.textureId !== undefined);
    const expectTexture = (view, textureId, description) => {
        assert.ok(view, name + ": " + description + " has a bound texture view");
        assert.equal(view.textureId, textureId, name + ": " + description + " uses the exact submitted producer");
        assert.deepEqual([view.mip, view.mips, view.layer, view.layers], [0, 1, 0, 1],
            name + ": " + description + " addresses the complete single-level image");
    };
    let width = scene.width, height = scene.height;
    const sizes = [];
    while (sizes.length < maximumLevels && (width > 1 || height > 1)) {
        width = Math.max(1, Math.ceil(width / 2));
        height = Math.max(1, Math.ceil(height / 2));
        sizes.push([width, height]);
    }
    const bloom = passes.filter(pass => /^PixelMap bloom (down|up) \d+$/.test(pass.label));
    const expectedOrder = sizes.map((_, index) => `PixelMap bloom down ${index}`)
        .concat(sizes.slice(1).map((_, index) => `PixelMap bloom up ${sizes.length - 2 - index}`));
    assert.deepEqual(bloom.map(pass => pass.label), expectedOrder, name + ": the submitted bloom pyramid is complete and ordered");
    const presentation = passes.findIndex(pass => pass.label === "PixelMap presentation pass");
    assert.ok(presentation >= 0, name + ": the final composition pass is submitted");
    const bloomOutputs = new Map();
    for (const pass of bloom) {
        assert.equal(pass.draws, 1, name + ": every bloom level executes one fullscreen draw");
        assert.ok(passes.indexOf(pass) < presentation, name + ": bloom completes before composition");
        assert.equal(pass.colors.length, 1, name + ": bloom writes one HDR target");
        const output = pass.colors[0].view;
        assert.ok(output, name + ": bloom writes an independently observed texture view");
        const texture = graph.textures[output.textureId - 1];
        const index = Number(pass.label.match(/\d+$/)[0]);
        assert.equal(texture.label, pass.label, name + ": each bloom pass writes its own allocated level");
        expectTexture(output, texture.id, pass.label + " output");
        assert.deepEqual([texture.width, texture.height], sizes[index], name + ": bloom keeps exact odd-sized extents");
        assert.equal(texture.format, "rgba16float", name + ": bloom remains in HDR");
        assert.equal(texture.samples, 1, name + ": postprocessing has no redundant MSAA storage");
        assert.equal(pass.colors[0].store, "store", name + ": bloom output survives for its consumer");
        const source = boundTexture(pass, 0), detail = boundTexture(pass, 1);
        const down = pass.label.startsWith("PixelMap bloom down ");
        const expectedSource = down ? (index === 0 ? scene.id : bloomOutputs.get(`PixelMap bloom down ${index - 1}`)) :
            bloomOutputs.get(index + 2 === sizes.length ? `PixelMap bloom down ${index + 1}` : `PixelMap bloom up ${index + 1}`);
        const expectedDetail = down ? (index === 0 ? photo[0]?.id ?? scene.id : expectedSource) :
            bloomOutputs.get(`PixelMap bloom down ${index}`);
        assert.notEqual(expectedSource, undefined, name + ": bloom source was produced earlier in this submission");
        assert.notEqual(expectedDetail, undefined, name + ": bloom detail was produced earlier in this submission");
        expectTexture(source, expectedSource, pass.label + " source");
        expectTexture(detail, expectedDetail, pass.label + " detail");
        assert.ok(pass.inputs.every(view => view.textureId !== output.textureId ||
            view.mip + view.mips <= output.mip || output.mip + output.mips <= view.mip),
            name + ": bloom never samples the subresource it writes");
        bloomOutputs.set(pass.label, output.textureId);
    }
    assert.equal(live.filter(texture => /^PixelMap bloom (down|up) \d+$/.test(texture.label)).length,
        Math.max(0, sizes.length * 2 - 1), name + ": only the active quality owns bloom attachments");
    const composition = passes[presentation];
    expectTexture(boundTexture(composition, 1), scene.id, "composition scene");
    expectTexture(boundTexture(composition, 2), photo[0]?.id ?? scene.id, "composition photograph");
    expectTexture(boundTexture(composition, 5), sizes.length ?
        bloomOutputs.get(sizes.length === 1 ? "PixelMap bloom down 0" : "PixelMap bloom up 0") : scene.id,
        "composition bloom");
    const scenePasses = passes.filter(pass => pass.label === "PixelMap scene pass");
    assert.ok(scenePasses.length === 1 || scenePasses.length === 2, name + ": scene has one pass plus an optional photograph continuation");
    assert.equal(photo.length,
        scenePasses.length - 1, name + ": photograph storage exists only when its copy is needed");
    const finalSceneIndex = passes.indexOf(scenePasses.at(-1));
    assert.ok(finalSceneIndex < presentation && bloom.every(pass => passes.indexOf(pass) > finalSceneIndex),
        name + ": the completed scene precedes every bloom and presentation consumer");
    for (const [index, pass] of scenePasses.entries()) {
        const final = index === scenePasses.length - 1;
        assert.equal(pass.colors.length, 1, name + ": scene writes one HDR image");
        assert.ok(pass.depth?.view && pass.colors[0].view, name + ": scene owns observed color and depth attachments");
        const color = pass.colors[0], attachment = graph.textures[color.view.textureId - 1];
        expectTexture(attachment.samples > 1 ? color.resolve : color.view, scene.id, "resolved scene");
        assert.equal(color.load, index === 0 ? "clear" : "load", name + ": photographic continuation preserves scene color");
        assert.equal(pass.depth.load, index === 0 ? "clear" : "load", name + ": photographic continuation preserves scene depth");
        assert.equal(pass.depth.store, final ? "discard" : "store", name + ": depth is stored only across the photograph continuation");
        assert.equal(color.store, final && attachment.samples > 1 ? "discard" : "store",
            name + ": the final multisample attachment is discarded after resolve");
    }
    const photoCopies = passes.filter(pass => pass.kind === "copy" && pass.destination?.label === "PixelMap photograph");
    assert.equal(photoCopies.length, photo.length, name + ": the optional photograph has exactly one submitted copy");
    if (photo.length) {
        const copy = photoCopies[0], position = passes.indexOf(copy);
        expectTexture(copy.source, scene.id, "photograph copy source");
        expectTexture(copy.destination, photo[0].id, "photograph copy destination");
        const extent = Array.isArray(copy.extent) || ArrayBuffer.isView(copy.extent) ? Array.from(copy.extent) :
            [copy.extent.width, copy.extent.height ?? 1, copy.extent.depthOrArrayLayers ?? 1];
        assert.deepEqual([extent[0], extent[1] ?? 1, extent[2] ?? 1], [scene.width, scene.height, 1],
            name + ": photograph copy preserves the complete resolved image");
        assert.ok(passes.indexOf(scenePasses[0]) < position && position < passes.indexOf(scenePasses[1]),
            name + ": photograph is copied between the terrain pass and its loaded continuation");
    }
    const attachmentLabels = /^(PixelMap (scene color|scene depth|scene multisample|photograph)|PixelMap bloom (down|up) \d+)$/;
    const attachmentBytes = live.filter(texture => attachmentLabels.test(texture.label)).reduce((sum, texture) =>
        sum + texture.width * texture.height * texture.layers * texture.samples *
            (texture.format === "rgba16float" ? 8 : 4), 0);
    assert.ok(attachmentBytes <= 256 * 1024 * 1024, name + ": observed attachments stay inside the 256 MiB budget");
    const artwork = live.find(texture => texture.label === "PixelMap terrain artwork");
    assert.ok(artwork, name + ": terrain artwork exists");
    assert.equal(artwork.mips, 9, name + ": terrain has a complete 256-to-1 mip chain");
    const terrainPasses = scenePasses.filter(pass => pass.terrainInputs?.length);
    if (diagnostic.patches > 0) assert.equal(terrainPasses.length, 1,
        name + ": a submitted scene terrain draw exposes its actual sampled resources");
    for (const pass of terrainPasses) {
        const sampled = pass.terrainInputs.find(entry => entry.group === 1 && entry.binding === 0 && entry.textureId !== undefined);
        const sampler = pass.terrainInputs.find(entry => entry.group === 1 && entry.binding === 2)?.sampler;
        assert.ok(sampled && sampler, name + ": terrain draw binds its artwork view and sampler together");
        assert.equal(sampled.textureId, artwork.id, name + ": terrain samples the retained artwork allocation");
        assert.deepEqual([sampled.mip, sampled.mips, sampled.layer, sampled.layers],
            [0, smooth ? 9 : 1, 0, artwork.layers], name + ": the submitted terrain view exposes exactly the selected style's mip range");
        assert.equal(sampled.format, smooth ? "rgba8unorm-srgb" : "rgba8unorm",
            name + ": smooth terrain decodes linear light while art styles preserve encoded mip-zero values");
        assert.deepEqual([sampler.magFilter ?? "nearest", sampler.minFilter ?? "nearest", sampler.mipmapFilter ?? "nearest"],
            smooth ? ["linear", "linear", "linear"] : ["nearest", "nearest", "nearest"],
            name + ": the actual terrain draw uses the style's spatial and mip filters");
        assert.equal(sampler.maxAnisotropy ?? 1, smooth ? 8 : 1,
            name + ": the actual terrain draw uses bounded anisotropy only for smooth styles");
        if (smooth) assert.equal(sampler.lodMaxClamp, 8,
            name + ": the submitted smooth sampler reaches the complete terrain mip chain");
    }
    if (smooth) for (const [key, version] of Object.entries(artwork.uploads)) {
        const layer = Number(key.split(":")[1]);
        for (let mip = 1; mip < artwork.mips; mip++)
            assert.equal(artwork.copiedVersions[`${mip}:${layer}`], version,
                `${name}: terrain mip ${mip} layer ${layer} contains the latest uploaded artwork`);
    }
    for (const pass of passes.filter(pass => /^PixelMap terrain mip /.test(pass.label))) {
        assert.equal(pass.kind, "compute", name + ": terrain mips are generated by compute");
        assert.equal(pass.dispatches.length, 1, name + ": each mip batches its dirty layers in one dispatch");
        const source = pass.inputs.find(view => view.binding === 0), destination = pass.inputs.find(view => view.binding === 1);
        assert.ok(source && destination, name + ": mip pass binds source and destination views");
        assert.equal(source.label, "PixelMap terrain artwork", name + ": reduction reads the retained artwork array");
        assert.equal(destination.label, "PixelMap terrain mip scratch", name + ": compute writes the reusable storage array");
        assert.equal(source.format, "rgba8unorm-srgb", name + ": input decoding occurs in linear light");
        assert.equal(destination.format, "rgba8unorm", name + ": encoded output uses the storage-compatible format");
        assert.equal(source.mips, 1, name + ": source mip view cannot overlap the destination");
        assert.equal(destination.mips, 1, name + ": destination addresses exactly one mip");
        assert.ok(passes.indexOf(pass) < passes.indexOf(scenePasses[0]), name + ": mips complete before scene sampling");
        const nextCompute = passes.findIndex((candidate, index) => index > passes.indexOf(pass) && candidate.kind === "compute");
        const copies = passes.slice(passes.indexOf(pass) + 1, nextCompute < 0 ? passes.indexOf(scenePasses[0]) : nextCompute)
            .filter(candidate => candidate.kind === "copy" && candidate.destination?.label === "PixelMap terrain artwork");
        assert.equal(copies.reduce((sum, copy) => sum + copy.extent[2], 0), pass.dispatches[0][2],
            name + ": every dispatched dirty layer reaches the final artwork mip");
        const runs = pass.dirtyLayers.reduce((count, layer, index, layers) =>
            count + (index === 0 || layer !== layers[index - 1] + 1 ? 1 : 0), 0);
        assert.equal(copies.length, runs, name + ": consecutive dirty layers share one array copy");
        for (const copy of copies) {
            assert.equal(copy.source.textureId, destination.textureId, name + ": copy consumes the compute scratch array");
            assert.equal(copy.destination.textureId, source.textureId, name + ": copy returns to the sampled artwork array");
            assert.equal(copy.destination.mip, source.mip + 1, name + ": copied reduction becomes the next source mip");
        }
    }
}

function verifyShadowLayers(evidence, quality, name) {
    const presets = { Balanced: { size: 1024, layers: 1 }, High: { size: 1536, layers: 2 }, Ultra: { size: 2048, layers: 4 } };
    const expected = presets[quality];
    assert.ok(expected, name + ": shadow quality is recognized");
    const texture = evidence.shadowTextures.findLast(record => !record.destroyed);
    assert.ok(texture, name + ": a live sun shadow texture exists");
    assert.equal(texture.format, "depth32float", name + ": shadow storage uses depth");
    assert.equal(texture.samples, 1, name + ": sun layers use independently sampled depth");
    assert.equal(texture.width, expected.size, name + ": shadow width matches its quality budget");
    assert.equal(texture.height, expected.size, name + ": shadow height matches its quality budget");
    assert.equal(texture.layers, expected.layers, name + ": the allocated cascade count matches its quality");
    assert.equal(texture.invalidRenderViews, 0, name + ": each shadow pass addresses one valid array layer");
    for (let layer = 0; layer < expected.layers; layer++) {
        assert.ok(texture.submittedPasses[layer] > 0, `${name}: cascade ${layer} receives a submitted render pass`);
        assert.ok(texture.submittedDraws[layer] > 0, `${name}: cascade ${layer} receives actual geometry draws`);
    }
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
        const initialPipelines = await page.evaluate(() => globalThis.__pixelAtlasGpuProbe.pipelines);
        assert.equal(initialPipelines.length, 8, "Pixel startup compiles only its six scene and two presentation pipelines");
        assert.equal(initialPipelines.filter(pipeline => /sun shadows/.test(pipeline.label)).length, 0,
            "Pixel startup does not compile unused shadow pipelines");
        await page.mouse.move(720, 410);
        await page.mouse.down();
        await page.mouse.move(810, 445, { steps: 8 });
        await page.mouse.up();
        const frames = await page.evaluate(() => PixelAtlasWebGpu.frames);
        await page.mouse.wheel(0, -170);
        await awaitFrame(page, (s, previous) => s.frames > previous, frames);
        assert.ok(difference(first, await capture(page, "desktop-camera")) > .03, "Pan and wheel change map geometry");

        const styles = [["Normal rendering", "Normal"], ["Minecraft rendering", "Minecraft"], ["Roblox rendering", "Roblox"], ["Pixel art rendering", "Pixel"]];
        let previous, smoothPipelines;
        for (const [command, style] of styles) {
            await menu(page, command);
            await awaitFrame(page, (s, expected) => s.style === expected, style);
            const image = await capture(page, "style-" + style.toLowerCase());
            if (previous) assert.ok(difference(previous, image) > .005, "Rendering styles produce distinct map output");
            previous = image;
            const pipelines = await page.evaluate(() => globalThis.__pixelAtlasGpuProbe.pipelines.length);
            if (style === "Normal") smoothPipelines = pipelines;
            else assert.equal(pipelines, smoothPipelines,
                style + ": switching between warmed sample-count variants reuses all compiled pipelines");
        }
        await menu(page, "Normal rendering");
        let lightingImage, ultraPipelines;
        for (const quality of ["Balanced", "Ultra", "High"]) {
            await menu(page, quality + " quality", "Rendering quality");
            await awaitFrame(page, (s, expected) => s.quality === expected, quality);
            lightingImage = await capture(page, "quality-" + quality.toLowerCase());
            const pipelines = await page.evaluate(() => globalThis.__pixelAtlasGpuProbe.pipelines);
            if (quality === "Balanced") assert.equal(pipelines.length, smoothPipelines,
                "Balanced reuses its 1x scene and the first existing shadow cascade");
            if (quality === "Ultra") {
                ultraPipelines = pipelines.length;
                assert.equal(ultraPipelines, smoothPipelines + 4,
                    "Ultra compiles only the two newly needed shadow-cascade pairs");
            }
            if (quality === "High") assert.equal(pipelines.length, ultraPipelines,
                "Returning to High reuses compiled pipelines");
        }
        // Dusk changes the sun's elevation, exercising a different caster-depth
        // fit; night also exercises emissive windows through the HDR resolve.
        for (const [command, palette] of [["Golden hour", "dusk"], ["After dark", "night"], ["Daylight", "day"]]) {
            const frames = await page.evaluate(() => PixelAtlasWebGpu.frames);
            await menu(page, command);
            await awaitFrame(page, (s, previous) => s.frames > previous, frames);
            const image = await capture(page, "lighting-" + palette);
            assert.ok(difference(lightingImage, image) > .01, "Changing the lighting palette changes the rendered city");
            lightingImage = image;
        }
        await click(page, "Earth imagery layer");
        await awaitFrame(page, s => s.imageryCount > 0);
        await capture(page, "earth-imagery");
        // The photograph continuation is only active below 30 degrees. Exercise
        // it through the real camera UI in both HDR and art styles, then verify
        // that leaving it retires the otherwise unused full-size attachment.
        await menu(page, "Camera angles and orbit");
        await click(page, "North-up plan");
        await click(page, "Close panel");
        await awaitFrame(page, s => s.imageryCount > 0 &&
            globalThis.__pixelAtlasGpuProbe.renderGraph.textures.some(texture =>
                texture.label === "PixelMap photograph" && !texture.destroyed));
        await capture(page, "photograph-normal");
        await menu(page, "Pixel art rendering");
        await awaitFrame(page, s => s.style === "Pixel");
        await capture(page, "photograph-pixel");
        await menu(page, "Normal rendering");
        await awaitFrame(page, s => s.style === "Normal");
        await menu(page, "Camera angles and orbit");
        await click(page, "Isometric · 55°");
        await click(page, "Close panel");
        await awaitFrame(page, () => !globalThis.__pixelAtlasGpuProbe.renderGraph.textures.some(texture =>
            texture.label === "PixelMap photograph" && !texture.destroyed));
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
        await click(page, "Pause free roam");
        await page.getByRole("button", { name: "Resume free roam", exact: true }).waitFor({ state: "visible" });
        // Freeze the physical camera before comparing nearby High/Ultra shadows.
        await capture(page, "free-roam-walk");
        await menu(page, "Ultra quality", "Rendering quality");
        await awaitFrame(page, s => s.quality === "Ultra");
        await capture(page, "free-roam-walk-ultra");
        await menu(page, "High quality", "Rendering quality");
        await awaitFrame(page, s => s.quality === "High");
        await click(page, "Exit free roam");
        await page.setViewportSize({ width: 960, height: 640 });
        await awaitFrame(page, s => s.physicalWidth <= 960);
        await capture(page, "resized");
        const gpu = await verifyGpu(page, "desktop");
        assert.ok(gpu.pipelines.some(p => p.samples === 4), "Smooth quality creates multisampled geometry pipelines");
        assert.equal(gpu.pipelines.length, ultraPipelines,
            "Lighting, photography, walking and resize reuse the established pipeline cache");
    } catch (error) {
        await saveFailure(page, "desktop", error);
        throw error;
    } finally { await context.close(); }
}

async function journeys(browser) {
    const modes = [["Drive", "CAR"], ["Bike", "BICYCLE"], ["Walk", "WALKING"],
        ["Boat", "BOAT"], ["Train", "TRAIN"], ["Flight", "AIRCRAFT"]];
    const failures = [];
    for (const [mode, badge] of modes) {
        const name = "journey-" + mode.toLowerCase();
        let context, page;
        try {
            ({ context, page } = await open(browser, "journey/" + mode));
            await page.getByRole("group", { name: badge, exact: true }).waitFor({ state: "visible" });
            await page.getByRole("group", { name: "Trip ready", exact: true }).waitFor({ state: "visible" });
            const progress = page.getByRole("slider", { name: "Journey progress", exact: true });
            const position = async () => {
                const value = await progress.getAttribute("aria-valuenow");
                assert.ok(value !== null && value.trim() !== "" && Number.isFinite(Number(value)),
                    name + ": accessible journey progress is numeric");
                return Number(value);
            };
            assert.equal(await position(), 0, name + ": the selected demo starts at its origin");
            // Choose the real slow-motion control. Keep the production 1x default;
            // this test needs time for software rendering and input round trips.
            const speed = page.getByRole("combobox", { name: "Playback speed", exact: true });
            const openSpeed = async () => {
                const bounds = await speed.boundingBox();
                assert.ok(bounds?.width && bounds?.height, name + ": playback speed has a usable hit target");
                await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
                await page.waitForFunction(() => document.querySelector('[aria-label="Playback speed"]')?.getAttribute("aria-expanded") === "true");
                await page.waitForFunction(() => document.activeElement?.getAttribute("elementtype") === "ScrollContentPresenter");
            };
            await openSpeed();
            await page.keyboard.press("End");
            await page.keyboard.press("Enter");
            await page.getByRole("listbox").waitFor({ state: "hidden" });
            // Reopen to verify the committed selection, rather than assuming that
            // keyboard focus in the popup changed the session's playback speed.
            await openSpeed();
            const quarterSpeed = page.getByRole("option", { name: "0.25×", exact: true });
            await quarterSpeed.waitFor({ state: "visible" });
            const quarterSpeedId = await quarterSpeed.getAttribute("id");
            assert.ok(quarterSpeedId, name + ": the quarter-speed option has an accessibility identity");
            // Uno 7.0.32 publishes the selected ComboBoxItem as the head's
            // active descendant when realizing a freshly reopened dropdown.
            await page.waitForFunction(id => document.querySelector('[aria-label="Playback speed"]')?.getAttribute("aria-activedescendant") === id, quarterSpeedId);
            await page.keyboard.press("Escape");
            await page.getByRole("listbox").waitFor({ state: "hidden" });
            // Compile and finish the static route before starting its short preview.
            // A software GPU can take longer than the whole route to render three
            // frames: observe simulation progress, then pause before any GPU fence.
            await verifyGpu(page, name + "-ready");
            const before = await page.evaluate(() => PixelAtlasWebGpu.frames);
            const slider = await progress.elementHandle();
            assert.ok(slider, name + ": the route progress control exists");
            // The panel keeps its action-row geometry while the telemetry changes.
            // Cache the actual hit target before playback; do not spend the short
            // preview re-querying an accessibility enabled flag or layout bounds.
            const pauseButton = page.getByRole("button", { name: "Pause or resume journey", exact: true });
            assert.equal(await pauseButton.isDisabled(), true, name + ": pause is semantically disabled before playback");
            const pauseBounds = await pauseButton.boundingBox();
            assert.ok(pauseBounds?.width && pauseBounds?.height, name + ": pause has a usable hit target");
            await click(page, "Preview route");
            try {
                await page.waitForFunction(element => {
                    const failure = globalThis.PixelAtlasGraphics?.failure || globalThis.PixelAtlasWebGpu?.failure;
                    if (failure) throw new Error(failure);
                    const value = Number(element.getAttribute("aria-valuenow"));
                    if (value >= 1000) throw new Error("Journey finished before pause could be exercised");
                    return value > 0;
                }, slider, { polling: 50, timeout: 15000 });
            } finally { await slider.dispose(); }
            await page.mouse.click(pauseBounds.x + pauseBounds.width / 2, pauseBounds.y + pauseBounds.height / 2);
            const paused = page.getByRole("group", { name: "Journey paused", exact: true });
            await paused.waitFor({ state: "visible" });
            await page.getByRole("button", { name: "Pause or resume journey", exact: true, disabled: false }).waitFor({ state: "visible" });
            assert.equal(await pauseButton.isDisabled(), false, name + ": pause/resume publishes its enabled accessibility state");
            const pausedProgress = await position();
            assert.ok(pausedProgress > 0 && pausedProgress < 1000,
                name + ": preview advances along its route and pauses before the destination");
            await awaitFrame(page, (s, previous) => s.frames > previous, before);
            await capture(page, name);
            await paused.waitFor({ state: "visible" });
            const capturedProgress = await position();
            assert.equal(capturedProgress, pausedProgress, name + ": paused motion stays fixed through GPU completion and capture");
            report.scenarios.at(-1).journey = { mode, badge, playbackRate: .25, pausedProgress, capturedProgress };
        } catch (error) {
            if (page) await saveFailure(page, name, error);
            failures.push(new Error(name + ": " + error.message, { cause: error }));
            console.error(name + " failed:", error);
        } finally {
            if (context) await context.close().catch(error => failures.push(new Error(name + " cleanup: " + error.message, { cause: error })));
        }
    }
    if (failures.length) throw new AggregateError(failures, failures.map(error => error.message).join("\n"));
}

async function mobile(browser) {
    const { context, page } = await open(browser, "", { mobile: true, density: 2 });
    try {
        assert.equal(await page.evaluate(() => devicePixelRatio), 2, "The mobile browser exposes the requested device-pixel ratio");
        const captureNativeResolution = async name => {
            const pixels = await capture(page, name);
            const display = await page.evaluate(() => {
                const canvas = document.getElementById("uno-canvas");
                return { dpr: devicePixelRatio, viewportWidth: innerWidth, viewportHeight: innerHeight,
                    canvasWidth: canvas?.width, canvasHeight: canvas?.height, diagnostic: PixelAtlasWebGpu };
            });
            const s = display.diagnostic;
            assert.equal(display.dpr, 2, name + ": device-pixel ratio stays at two");
            assert.equal(s.style, "Normal", name + ": resolution is checked in the continuous style");
            assert.equal(s.quality, "High", name + ": the full-resolution mobile case uses High quality");
            // Logical diagnostic bounds are rounded up once. At DPR2 that can
            // differ from rounding physical bounds by at most one pixel.
            assert.ok(Math.abs(s.physicalWidth - s.width * display.dpr) <= 1,
                `${name}: native target width ${s.physicalWidth} must cover ${s.width} logical pixels at DPR${display.dpr}`);
            assert.ok(Math.abs(s.physicalHeight - s.height * display.dpr) <= 1,
                `${name}: native target height ${s.physicalHeight} must cover ${s.height} logical pixels at DPR${display.dpr}`);
            assert.ok(s.physicalWidth * s.physicalHeight <= 3_686_400, name + ": this viewport fits the High-quality scene budget");
            assert.equal(s.renderWidth, s.physicalWidth, name + ": map color/depth buffers keep the native target width");
            assert.equal(s.renderHeight, s.physicalHeight, name + ": map color/depth buffers keep the native target height");
            assert.equal(pixels.width, display.viewportWidth * display.dpr, name + ": screenshot retains physical display pixels");
            report.scenarios.at(-1).display = display;
            return pixels;
        };
        const before = await capture(page, "mobile-pixel");
        // The initial camera includes the Financial District label. Keep a
        // continuous, native-density view of that label before the pinch zooms in.
        await menu(page, "Normal rendering");
        await awaitFrame(page, s => s.style === "Normal");
        await captureNativeResolution("mobile-normal-initial-high-dpi");
        await menu(page, "Pixel art rendering");
        await awaitFrame(page, s => s.style === "Pixel");
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
        await captureNativeResolution("mobile-normal-high-dpi");
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
            // Each suite owns and closes its browser contexts. Preserve every
            // failure while letting independent input/recovery checks finish.
            const failures = [];
            report.failures = [];
            for (const [name, run] of [["shader-fixtures", async instance =>
                report.scenarios.push(await runShaderFixtures(instance, base, output))],
                ["desktop", desktop], ["journeys", journeys],
                ["mobile", mobile], ["unavailable", unavailable], ["device-loss", deviceLoss]]) {
                try { await run(browser); }
                catch (error) {
                    failures.push(error);
                    report.failures.push({ name, failure: error.stack || String(error) });
                    console.error(name + " suite failed:", error);
                }
            }
            if (failures.length) throw new AggregateError(failures,
                report.failures.map(failure => failure.name).join(", ") + " browser suites failed");
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
