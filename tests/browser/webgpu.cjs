// Real browser integration checks for the Uno 7 shared-device renderer.
// GPU calls are observed independently of the application's diagnostic counters.
const assert = require("node:assert/strict");
const { mkdir, writeFile } = require("node:fs/promises");
const { join } = require("node:path");
const { chromium } = require("playwright");
const { PNG } = require("pngjs");

const base = process.argv[2] || "http://127.0.0.1:8100/UnoPixelMap/";
const output = process.env.PIXELMAP_SHOTS || "webgpu-evidence";
const report = { renderer: "Uno 7 WebGPU", scenarios: [], errors: [], timingMeaning: "CPU preparation/submission only; software GPU timings are not hardware FPS benchmarks." };

function installProbe() {
    const state = globalThis.__pixelAtlasGpuProbe = {
        modules: [], pipelines: [], errors: [], losses: [], pending: [], devices: [],
        draws: 0, submits: 0, mapSubmits: 0, writeBytes: 0, readbacks: 0,
        buffers: 0, textures: 0, buffersDestroyed: 0, texturesDestroyed: 0, glContexts: 0
    };
    const map = label => /^PixelMap[ /]/.test(label || "");
    const encoders = new WeakSet(), passes = new WeakSet(), commands = new WeakSet();
    const buffers = new WeakSet(), textures = new WeakSet();
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
        state.devices.push(device);
        device.addEventListener("uncapturederror", event => state.errors.push(event.error.message));
        device.lost.then(info => state.losses.push({ reason: info.reason, message: info.message }));
        return device;
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
        if (map(d?.label)) state.pipelines.push({ label: d.label, samples: d.multisample?.count || 1,
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
        if (buffers.has(args[0])) state.writeBytes += args[2]?.byteLength || 0;
        return Reflect.apply(fn, this, args);
    });
}

async function click(page, name, touch = false) {
    const control = page.getByRole("button", { name, exact: true })
        .or(page.getByRole("checkbox", { name, exact: true }));
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
}

async function open(browser, hash = "", { mobile = false, density = 1 } = {}) {
    const context = await browser.newContext({ viewport: mobile ? { width: 390, height: 844 } : { width: 1200, height: 800 },
        deviceScaleFactor: density, isMobile: mobile, hasTouch: mobile });
    const page = await context.newPage();
    page.setDefaultTimeout(30000);
    page.on("pageerror", error => { report.errors.push(error.message); console.error(error.message); });
    page.on("console", message => {
        if (message.type() === "error") console.error("Browser:", message.text().slice(0, 3000));
    });
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
    const bytes = await page.screenshot({ path: join(output, name + ".png"), timeout: 30000 });
    const pixels = PNG.sync.read(bytes), measured = inspectPixels(pixels);
    assert.ok(measured.colors > 12 && measured.luminance > 8 && measured.luminance < 250,
        `${name}: central map contains varied rendered geometry, not a blank canvas (${JSON.stringify(measured)})`);
    const diagnostic = await page.evaluate(() => ({ ...globalThis.PixelAtlasWebGpu }));
    report.scenarios.push({ name, ...measured, diagnostic });
    console.log(name, JSON.stringify(diagnostic));
    return pixels;
}

async function verifyGpu(page, name) {
    const evidence = await page.evaluate(async () => {
        const s = globalThis.__pixelAtlasGpuProbe;
        await Promise.all(s.pending);
        return { ...s, pending: undefined, devices: undefined };
    });
    await writeFile(join(output, name + "-gpu.json"), JSON.stringify(evidence, null, 2));
    assert.deepEqual(evidence.errors, [], name + ": all WGSL modules and GPU work validate");
    assert.deepEqual(evidence.losses, [], name + ": shared device remains available");
    assert.ok(evidence.modules.length >= 2 && evidence.pipelines.length >= 2, "Native map shader modules and pipelines exist");
    assert.ok(evidence.pipelines.some(p => p.depth && p.depthWrite), "Scene geometry uses hardware depth writes");
    assert.ok(evidence.draws > 0 && evidence.mapSubmits > 0, "Map commands reach the actual WebGPU queue");
    assert.equal(evidence.readbacks, 0, "Map presentation performs no texture readback");
    assert.equal(evidence.glContexts, 0, "Production opens no WebGL rendering context");
    return evidence;
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
        await page.screenshot({ path: join(output, "desktop-failure.png") }).catch(() => {});
        await writeFile(join(output, "desktop-failure.txt"), await page.locator("body").ariaSnapshot().catch(() => "unavailable"));
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
        } finally { await context.close(); }
    }
}

async function mobile(browser) {
    const { context, page } = await open(browser, "", { mobile: true, density: 2 });
    try {
        const before = await capture(page, "mobile-pixel");
        const cdp = await context.newCDPSession(page);
        await cdp.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x: 135, y: 410, id: 0 }, { x: 245, y: 410, id: 1 }] });
        for (let step = 1; step <= 6; step++)
            await cdp.send("Input.dispatchTouchEvent", { type: "touchMove", touchPoints: [{ x: 135 - step * 5, y: 410, id: 0 }, { x: 245 + step * 5, y: 410, id: 1 }] });
        await cdp.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
        const after = await capture(page, "mobile-pinch");
        assert.equal(await page.evaluate(() => visualViewport.scale), 1, "Pinch does not zoom the page or controls");
        assert.ok(difference(before, after) > .01, "Touch pinch changes the map");
        const bounds = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth }));
        assert.ok(bounds.scrollWidth <= bounds.width + 1, "Mobile layout does not overflow horizontally");
        await menu(page, "Normal rendering");
        await awaitFrame(page, s => s.style === "Normal");
        await capture(page, "mobile-normal-high-dpi");
        await verifyGpu(page, "mobile");
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
        await page.evaluate(() => {
            const devices = globalThis.__pixelAtlasGpuProbe.devices;
            if (!devices.length) throw new Error("No real application GPUDevice was acquired");
            devices[devices.length - 1].destroy();
        });
        await page.getByRole("heading", { name: "WebGPU is unavailable" }).waitFor({ state: "visible" });
        assert.match(await page.locator("#pixelmap-graphics-detail").innerText(), /device was lost/i);
        await page.screenshot({ path: join(output, "device-loss-recovery.png") });
        report.scenarios.push({ name: "device-loss", actionableDiagnostic: true });
    } finally { await context.close(); }
}

(async () => {
    await mkdir(output, { recursive: true });
    const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
        args: ["--disable-dev-shm-usage", "--enable-unsafe-webgpu", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"] });
    try {
        report.browser = await browser.version();
        await desktop(browser);
        await journeys(browser);
        await mobile(browser);
        await unavailable(browser);
        await deviceLoss(browser);
        assert.deepEqual(report.errors, [], "No application exceptions occurred");
        report.passed = true;
        console.log("WebGPU integration passed:", report.scenarios.length, "scenarios");
    } catch (error) {
        report.passed = false;
        report.failure = error.stack || String(error);
        throw error;
    } finally {
        await writeFile(join(output, "report.json"), JSON.stringify(report, null, 2));
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
