// node performance.cjs <site URL> <output directory> [--visual|--motion] [--gpu] [--case=<name>]
// node performance.cjs --compare <baseline directory> <candidate directory>
// Run baseline and candidate serially on the same machine, browser and viewport.
const assert = require("node:assert/strict");
const { createHash } = require("node:crypto");
const { mkdir, readFile, writeFile } = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const { chromium } = require("playwright");
const { PNG } = require("pngjs");
const { enableGpuRendering } = require("./gpu.cjs");

const viewport = { width: 1200, height: 780 };
const browserArgs = ["--disable-dev-shm-usage", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"];
const requestGpu = process.argv.includes("--gpu");
const expectCpuFallback = process.argv.includes("--expect-cpu-fallback");
let runtimeMetadata;
const views = [];
for (const layer of ["Map", "Earth"])
for (const style of ["Day", "Night"])
for (const [angle, pitch, lens] of [["overhead", 0, "Orthographic"], ["iso55", 55, "Orthographic"],
    ["perspective45", 45, "Perspective"], ["perspective65", 65, "Perspective"]]) {
    views.push({ name: `${layer.toLowerCase()}-${style.toLowerCase()}-${angle}`,
        fragment: `v3/40.708700/-74.010400/16.500/-15.00/${pitch}/${style}/${layer}/${lens}/0.0`,
        journey: false });
}
for (const mode of ["drive", "bike", "walk", "boat", "train", "flight"])
    views.push({ name: "journey-" + mode, fragment: "journey/" + mode, journey: true });

const digest = bytes => createHash("sha256").update(bytes).digest("hex");
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

function crop(frame, area) {
    const output = new PNG({ width: area.width, height: area.height });
    PNG.bitblt(frame, output, area.x, area.y, area.width, area.height, 0, 0);
    return output;
}

function distribution(values) {
    if (values.length === 0) return { samples: 0 };
    const ordered = [...values].sort((a, b) => a - b);
    return { samples: ordered.length, medianMs: ordered[Math.floor(ordered.length / 2)],
        p95Ms: ordered[Math.min(ordered.length - 1, Math.floor(ordered.length * .95))],
        maxMs: ordered.at(-1), meanMs: ordered.reduce((a, b) => a + b, 0) / ordered.length };
}

// Observe browser scheduling and native texture uploads while preserving every call.
// The map resolves to ceil(map width / pixel size) by ceil(map height / pixel size).
function installProbe({ observeDraws = false } = {}) {
    const state = globalThis.__pixelAtlasPerf = { active: false, frames: [], uploads: [], bootUploads: [], bootAllocations: [],
        bootDraws: [], framebufferAttachments: 0, longTasks: [], width: 0, height: 0, quality: null, start: 0 };
    const quality = (width, height) => {
        for (const pixels of [2, 3, 4])
            if (width === Math.ceil(state.width / pixels) && height === Math.ceil(state.height / pixels)) return pixels;
        return null;
    };
    const record = (width, height, allocation) => {
        if (!Number.isInteger(width) || !Number.isInteger(height) || width < 128 || height < 100) return;
        if (allocation) {
            if (state.bootAllocations.length < 2000) state.bootAllocations.push([width, height]);
            return;
        }
        const pixels = quality(width, height);
        if (pixels !== null) state.quality = pixels;
        if (state.active) state.uploads.push([performance.now(), width, height, pixels]);
        else if (state.bootUploads.length < 2000) state.bootUploads.push([width, height]);
    };
    for (const type of [globalThis.WebGLRenderingContext, globalThis.WebGL2RenderingContext]) {
        if (!type) continue;
        for (const name of ["texImage2D", "texSubImage2D"]) {
            const original = type.prototype[name];
            if (original.__pixelAtlasObserved) continue;
            const observed = function (...args) {
                const result = Reflect.apply(original, this, args);
                if (args.length >= 9) {
                    const index = name === "texImage2D" ? 3 : 4;
                    record(args[index], args[index + 1], observeDraws && args[8] === null);
                }
                return result;
            };
            observed.__pixelAtlasObserved = true;
            type.prototype[name] = observed;
        }
    }
    if (observeDraws) {
        const contexts = new WeakMap(), attached = new WeakSet();
        for (const type of [globalThis.WebGLRenderingContext, globalThis.WebGL2RenderingContext]) {
            if (!type) continue;
            for (const name of ["bindFramebuffer", "framebufferTexture2D", "viewport", "drawArrays", "drawElements",
                "drawArraysInstanced", "drawElementsInstanced"]) {
                const original = type.prototype[name];
                if (typeof original !== "function" || original.__pixelAtlasObserved) continue;
                const observed = function (...args) {
                    const result = Reflect.apply(original, this, args);
                    let context = contexts.get(this);
                    if (!context) { context = { framebuffer: null, width: 0, height: 0 }; contexts.set(this, context); }
                    if (name === "bindFramebuffer") {
                        if (args[0] === this.FRAMEBUFFER || args[0] === this.DRAW_FRAMEBUFFER) context.framebuffer = args[1];
                    } else if (name === "framebufferTexture2D") {
                        if (context.framebuffer && args[3]) { attached.add(context.framebuffer); state.framebufferAttachments++; }
                    } else if (name === "viewport") {
                        context.width = args[2]; context.height = args[3];
                    } else if (context.framebuffer && attached.has(context.framebuffer) && state.bootDraws.length < 10000) {
                        state.bootDraws.push([context.width, context.height]);
                    }
                    return result;
                };
                observed.__pixelAtlasObserved = true;
                type.prototype[name] = observed;
            }
        }
    }
    state.gpuEvidence = (width, height) => {
        state.width = width; state.height = height;
        const draws = {}, uploads = {}, allocations = {}, viewports = {};
        for (const [w, h] of state.bootDraws) {
            viewports[`${w}x${h}`] = (viewports[`${w}x${h}`] || 0) + 1;
            const size = quality(w, h);
            if (size !== null) draws[size] = (draws[size] || 0) + 1;
        }
        for (const [w, h] of state.bootUploads) {
            const size = quality(w, h);
            if (size !== null) uploads[size] = (uploads[size] || 0) + 1;
        }
        for (const [w, h] of state.bootAllocations) {
            const size = quality(w, h);
            if (size !== null) allocations[size] = (allocations[size] || 0) + 1;
        }
        return { textureFramebufferAttachments: state.framebufferAttachments,
            artSizedOffscreenDrawsByPixelSize: draws, artSizedCpuUploadsByPixelSize: uploads,
            artSizedTextureAllocationsByPixelSize: allocations,
            allOffscreenViewportDraws: viewports };
    };
    state.resetGpuEvidence = () => {
        state.bootUploads = [];
        state.bootAllocations = [];
        state.bootDraws = [];
        state.framebufferAttachments = 0;
    };
    new PerformanceObserver(list => {
        if (state.active)
            for (const entry of list.getEntries()) state.longTasks.push({ start: entry.startTime, duration: entry.duration });
    }).observe({ type: "longtask", buffered: false });
    state.begin = (width, height) => {
        state.width = width;
        state.height = height;
        for (const [w, h] of state.bootUploads) {
            const pixels = quality(w, h);
            if (pixels !== null) state.quality = pixels;
        }
        state.frames = [];
        state.uploads = [];
        state.longTasks = [];
        state.start = performance.now();
        state.active = true;
        const frame = time => {
            if (!state.active) return;
            state.frames.push([time, state.quality]);
            requestAnimationFrame(frame);
        };
        requestAnimationFrame(frame);
    };
    state.finish = () => {
        state.active = false;
        return { durationMs: performance.now() - state.start, frames: state.frames,
            uploads: state.uploads, longTasks: state.longTasks, finalPixelSize: state.quality };
    };
}

async function open(browser, base, view, errors, observe, output) {
    const context = await browser.newContext({ viewport, deviceScaleFactor: 1 });
    const page = await context.newPage();
    const blocked = {};
    page.on("pageerror", error => errors.push(view.name + " " + error.message));
    page.on("console", message => {
        if (message.type() === "error" && !message.text().includes("ERR_FAILED"))
            console.error("Browser console", view.name, message.text().slice(0, 2000));
    });
    if (observe || requestGpu) await page.addInitScript(installProbe, { observeDraws: requestGpu && !observe });
    const origin = new URL(base).origin;
    await page.route("**/*", route => {
        const address = new URL(route.request().url());
        if (["http:", "https:"].includes(address.protocol) && address.origin !== origin) {
            blocked[address.hostname] = (blocked[address.hostname] || 0) + 1;
            return route.abort();
        }
        return route.continue();
    });
    const address = new URL(base);
    address.hash = view.fragment;
    await page.goto(address.href, { waitUntil: "domcontentloaded", timeout: 120000 });
    await page.waitForFunction(() => {
        const canvas = document.getElementById("uno-canvas");
        return canvas?.width > 0 && document.elementFromPoint(innerWidth / 2, innerHeight / 2) === canvas;
    }, undefined, { timeout: 100000 });
    await page.waitForLoadState("networkidle", { timeout: 45000 });
    await page.screenshot();
    const enable = page.getByRole("button", { name: "Enable accessibility", exact: true });
    if (await enable.count()) await enable.evaluate(element => element.click());
    if (view.journey)
        await page.getByRole("button", { name: "Preview route", exact: true }).waitFor({ state: "attached", timeout: 30000 });
    await page.waitForTimeout(1800);
    if (!runtimeMetadata) runtimeMetadata = await readRuntimeMetadata(page);
    let actualBackend = null;
    if (requestGpu) try {
        await page.evaluate(() => __pixelAtlasPerf.resetGpuEvidence());
        actualBackend = await enableGpuRendering(page, expectCpuFallback ? "CPU fallback" : "GPU active");
        await page.waitForTimeout(500);
    } catch (error) {
        await page.screenshot({ path: path.join(output, view.name + "-gpu-setup-failure.png") }).catch(() => {});
        const snapshot = await page.locator("body").ariaSnapshot().catch(() => "Accessibility snapshot unavailable");
        await writeFile(path.join(output, view.name + "-gpu-setup-failure.txt"), snapshot);
        await context.close();
        throw error;
    }
    const map = await page.locator('[aria-label="Interactive world map"]').boundingBox();
    assert.ok(map && map.width > 500 && map.height > 300, "The accessible map has a settled viewport");
    return { context, page, map, blocked, address: address.href, actualBackend };
}

async function readRuntimeMetadata(page) {
    const bootstrap = await page.locator('script[src*="uno-bootstrap.js"]').getAttribute("src");
    assert.ok(bootstrap, "Uno bootstrap script is declared");
    const bootstrapUrl = new URL(bootstrap, page.url());
    const config = await (await page.request.get(new URL("uno-config.js", bootstrapUrl).href)).text();
    const loaderName = /config\.dotnet_js_filename\s*=\s*"([^"]+)"/.exec(config)?.[1];
    assert.ok(loaderName, "The .NET loader filename is declared");
    const loader = await (await page.request.get(new URL("../_framework/" + loaderName, bootstrapUrl).href)).text();
    const manifest = /\/\*json-start\*\/([\s\S]*?)\/\*json-end\*\//.exec(loader)?.[1];
    assert.ok(manifest, "The .NET loader contains its resource integrity manifest");
    const boot = JSON.parse(manifest);
    const environment = name => new RegExp('\\["' + name + '"\\]\\s*=\\s*"([^\\"]+)"').exec(config)?.[1] || null;
    return { mode: environment("UNO_BOOTSTRAP_MONO_RUNTIME_MODE"),
        configuration: environment("UNO_BOOTSTRAP_MONO_RUNTIME_CONFIGURATION"),
        profiledAot: environment("UNO_BOOTSTRAP_MONO_PROFILED_AOT"),
        bootstrapConfigSha256: digest(config), loaderName, loaderSha256: digest(loader),
        resourceManifestHash: boot.resources.hash, nativeWasm: boot.resources.wasmNative,
        nativeJavaScript: boot.resources.jsModuleNative, runtimeJavaScript: boot.resources.jsModuleRuntime,
        assemblies: [...(boot.resources.coreAssembly || []), ...(boot.resources.assembly || [])]
            .filter(asset => /^(PixelMap\.|SkiaSharp\.|Uno\.UI\.)/.test(asset.virtualPath)) };
}

async function settledFrame(page, name, minimumColors = 12) {
    // This inset excludes search, journey controls, compass, footer and tile-status text.
    const area = { x: 390, y: 155, width: 675, height: 440 };
    let previous, unchanged = 0;
    const deadline = Date.now() + 30000;
    while (Date.now() < deadline) {
        const bytes = await page.screenshot({ timeout: 15000 });
        const frame = PNG.sync.read(bytes);
        const map = crop(frame, area);
        const hash = digest(map.data);
        unchanged = hash === previous ? unchanged + 1 : 0;
        if (unchanged >= 3) {
            const colors = new Set();
            for (let i = 0; i < map.data.length; i += 16) colors.add(map.data.readUInt32LE(i));
            assert.ok(colors.size > minimumColors, name + " contains rendered map geometry");
            return { bytes, frame, map, area, mapHash: hash, canvasHash: digest(frame.data) };
        }
        previous = hash;
        await page.waitForTimeout(300);
    }
    throw new Error("Map pixels did not settle for " + name);
}

async function capture(browser, base, view, output, errors) {
    const opened = await open(browser, base, view, errors, false, output);
    try {
        // The airport-to-airport flight fits beyond the detailed offline district and
        // deliberately exercises its coarse world fallback plus the flight trace.
        const image = await settledFrame(opened.page, view.name, view.name === "journey-flight" ? 5 : 12);
        const accessibility = await opened.page.locator("body").ariaSnapshot();
        assert.doesNotMatch(accessibility, /Loading map|Loading Earth/, view.name + " completed bundled tile loading");
        if (view.journey) assert.match(accessibility, /Trip ready/, "The journey remains at its deterministic ready position");
        const gpuEvidence = requestGpu ? await opened.page.evaluate(({ width, height }) => __pixelAtlasPerf.gpuEvidence(width, height), opened.map) : null;
        if (requestGpu && !expectCpuFallback)
            assert.ok(Object.values(gpuEvidence.artSizedOffscreenDrawsByPixelSize).some(count => count > 0),
                "GPU mode rendered into a texture-backed framebuffer at the art resolution");
        await writeFile(path.join(output, view.name + ".png"), image.bytes);
        await writeFile(path.join(output, view.name + ".map.png"), PNG.sync.write(image.map));
        await writeFile(path.join(output, view.name + ".txt"), accessibility);
        console.log("Captured", view.name, image.mapHash, gpuEvidence ? JSON.stringify(gpuEvidence) : "");
        return { name: view.name, fragment: view.fragment, mapBounds: opened.map, crop: image.area,
            mapHash: image.mapHash, canvasHash: image.canvasHash, expectedRestPixelSize: 2,
            actualBackend: opened.actualBackend, gpuEvidence, blocked: opened.blocked };
    } catch (error) {
        await opened.page.screenshot({ path: path.join(output, view.name + "-failure.png") }).catch(() => {});
        await writeFile(path.join(output, view.name + "-failure.txt"), await opened.page.locator("body").ariaSnapshot()).catch(() => {});
        throw error;
    } finally { await opened.context.close(); }
}

const metrics = response => Object.fromEntries(response.metrics.map(entry => [entry.name, entry.value]));
function summarizeProbe(probe, before, after) {
    const intervals = [], byQuality = {}, uploads = {}, dimensions = {};
    for (let i = 1; i < probe.frames.length; i++) {
        const interval = probe.frames[i][0] - probe.frames[i - 1][0];
        intervals.push(interval);
        const size = probe.frames[i][1];
        if (size !== null && size === probe.frames[i - 1][1]) (byQuality[size] ??= []).push(interval);
    }
    for (const [, width, height, size] of probe.uploads) {
        dimensions[`${width}x${height}`] = (dimensions[`${width}x${height}`] || 0) + 1;
        if (size !== null) uploads[size] = (uploads[size] || 0) + 1;
    }
    const cpu = {};
    for (const key of ["TaskDuration", "ScriptDuration", "LayoutDuration", "RecalcStyleDuration"])
        if (key in before && key in after) cpu[key + "Ms"] = 1000 * (after[key] - before[key]);
    return { durationMs: probe.durationMs, raf: distribution(intervals),
        rafByObservedPixelSize: Object.fromEntries(Object.entries(byQuality).map(([key, values]) => [key, distribution(values)])),
        mapTextureUploadsByPixelSize: uploads, allTextureUploadDimensions: dimensions,
        finalObservedPixelSize: probe.finalPixelSize, longTasks: probe.longTasks.length,
        longTaskDurationMs: probe.longTasks.reduce((sum, entry) => sum + entry.duration, 0), cpu };
}

async function motion(browser, base, view, gesture, output, errors) {
    const opened = await open(browser, base, view, errors, true, output);
    const { page, map } = opened;
    try {
        await settledFrame(page, view.name);
        const cdp = await opened.context.newCDPSession(page);
        await cdp.send("Performance.enable", { timeDomain: "threadTicks" });
        const before = metrics(await cdp.send("Performance.getMetrics"));
        await page.evaluate(({ width, height }) => __pixelAtlasPerf.begin(width, height), map);
        await page.mouse.move(700, 420);
        const button = gesture === "orbit" ? "right" : "left";
        await page.mouse.down({ button });
        let moving, after;
        try {
            for (let i = 1; i <= 80; i++) {
                const angle = i * Math.PI * 4 / 80;
                await page.mouse.move(700 + Math.round(Math.sin(angle) * 115),
                    420 + Math.round(Math.sin(angle * 2) * 22));
                await delay(25);
            }
            await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
            moving = await page.evaluate(() => __pixelAtlasPerf.finish());
            after = metrics(await cdp.send("Performance.getMetrics"));
        } finally {
            // Let the last pointer delta age out so the final camera has no inertial drift.
            await page.waitForTimeout(160);
            await page.mouse.up({ button });
        }
        await page.waitForTimeout(1200);
        const settled = await settledFrame(page, view.name + "-" + gesture);
        await writeFile(path.join(output, view.name + "-" + gesture + ".png"), settled.bytes);
        const result = { name: view.name + "-" + gesture, gesture, mapBounds: map, inputMoves: 80,
            inputDelayMs: 25, ...summarizeProbe(moving, before, after), blocked: opened.blocked,
            restPixelSize: 2, settledMapHash: settled.mapHash };
        console.log("Measured", result.name, JSON.stringify({ raf: result.raf,
            quality: result.mapTextureUploadsByPixelSize, cpu: result.cpu }));
        await writeFile(path.join(output, result.name + ".samples.json"), JSON.stringify(moving));
        return result;
    } finally { await opened.context.close(); }
}

async function compare(baseline, candidate) {
    const original = JSON.parse(await readFile(path.join(baseline, "visual.json"), "utf8"));
    const current = JSON.parse(await readFile(path.join(candidate, "visual.json"), "utf8"));
    assert.deepEqual(current.viewport, original.viewport, "Identical browser viewport");
    assert.equal(current.browser, original.browser, "Identical browser version");
    assert.equal(current.deviceScaleFactor, original.deviceScaleFactor, "Identical browser pixel density");
    assert.deepEqual(current.cases.map(item => item.name), original.cases.map(item => item.name), "Identical visual matrix");
    const results = [];
    for (const view of original.cases) {
        const candidateView = current.cases.find(item => item.name === view.name);
        const a = PNG.sync.read(await readFile(path.join(baseline, view.name + ".map.png")));
        const b = PNG.sync.read(await readFile(path.join(candidate, view.name + ".map.png")));
        assert.equal(a.width, b.width);
        assert.equal(a.height, b.height);
        let different = 0, maxChannelDelta = 0;
        for (let i = 0; i < a.data.length; i += 4) {
            let changed = false;
            for (let channel = 0; channel < 4; channel++) {
                const delta = Math.abs(a.data[i + channel] - b.data[i + channel]);
                changed ||= delta !== 0;
                maxChannelDelta = Math.max(maxChannelDelta, delta);
            }
            if (changed) different++;
        }
        results.push({ name: view.name, differentMapPixels: different, totalMapPixels: a.width * a.height,
            maxChannelDelta, exactCanvas: view.canvasHash === candidateView.canvasHash });
    }
    await writeFile(path.join(candidate, "comparison.json"), JSON.stringify(results, null, 2) + "\n");
    console.table(results);
    assert.equal(results.filter(result => result.differentMapPixels !== 0).length, 0, "Exact browser map pixels");
}

async function main() {
    if (process.argv[2] === "--compare") return compare(process.argv[3], process.argv[4]);
    const base = process.argv[2], output = process.argv[3];
    assert.ok(base && output, "Provide a site URL and output directory");
    assert.ok(!expectCpuFallback || requestGpu, "--expect-cpu-fallback requires --gpu and a software-canvas test environment");
    const measureMotion = process.argv.includes("--motion");
    const filter = process.argv.find(arg => arg.startsWith("--case="))?.slice(7).split(",");
    const cases = views.filter(view => filter ? filter.includes(view.name) : !measureMotion ||
        ["map-day-iso55", "earth-day-iso55", "map-day-perspective65", "earth-day-perspective65"].includes(view.name));
    assert.ok(cases.length > 0, "The selected case exists");
    await mkdir(output, { recursive: true });
    const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined, args: browserArgs });
    const errors = [];
    const report = { browser: browser.version(), viewport, deviceScaleFactor: 1, browserArgs,
        platform: os.platform(), architecture: os.arch(), cpu: os.cpus()[0]?.model,
        base, revision: process.env.PIXELMAP_REVISION || null, mode: measureMotion ? "motion" : "visual",
        requestedGpuRendering: requestGpu,
        expectedGpuBackend: requestGpu ? expectCpuFallback ? "CPU fallback" : "GPU active" : null,
        performanceTimeDomain: "threadTicks",
        adaptiveQuality: "Production policy remains active. Resting views use pixel size 2. Motion sizes are inferred from exact WebGL upload dimensions. rAF measures browser scheduling. CPU metrics include the app and observational probe.",
        cases: [] };
    const destination = path.join(output, measureMotion ? "motion.json" : "visual.json");
    try {
        for (const view of cases) {
            if (measureMotion) {
                for (const gesture of ["pan", "orbit"]) report.cases.push(await motion(browser, base, view, gesture, output, errors));
            } else report.cases.push(await capture(browser, base, view, output, errors));
            report.runtime = runtimeMetadata;
            await writeFile(destination, JSON.stringify(report, null, 2) + "\n");
        }
        assert.deepEqual(errors, [], "Browser runtime errors");
    } finally {
        report.errors = errors;
        report.runtime = runtimeMetadata;
        await writeFile(destination, JSON.stringify(report, null, 2) + "\n");
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
