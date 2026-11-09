// node performance.cjs <site URL> <output directory> [--visual|--motion] [--gpu] [--terrain] [--case=<name>]
// node performance.cjs --compare <baseline directory> <candidate directory>
// Run baseline and candidate serially on the same machine, browser and viewport.
// PIXELMAP_BROWSER_ARGS accepts a JSON array of additional Chromium flags, recorded in each report.
const assert = require("node:assert/strict");
const { createHash } = require("node:crypto");
const { mkdir, readFile, writeFile } = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const { chromium } = require("playwright");
const { PNG } = require("pngjs");
const { setGpuRendering } = require("./gpu.cjs");

const viewport = { width: 1200, height: 780 };
const browserArgs = ["--disable-dev-shm-usage", "--use-angle=swiftshader", "--enable-unsafe-swiftshader",
    ...JSON.parse(process.env.PIXELMAP_BROWSER_ARGS || "[]")];
const requestGpu = process.argv.includes("--gpu");
const expectCpuFallback = process.argv.includes("--expect-cpu-fallback");
const requireCompositorGpu = process.argv.includes("--require-compositor-gpu");
const forceDetachedGpu = process.argv.includes("--force-detached-gpu");
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

const terrainViews = [];
for (const layer of ["Map", "Earth", "Elevation"])
for (const style of ["Day", "Night"])
for (const [angle, pitch, lens] of [["overhead", 0, "Orthographic"], ["iso55", 55, "Orthographic"],
    ["perspective58", 58, "Perspective"]]) {
    terrainViews.push({ name: `terrain-${layer.toLowerCase()}-${style.toLowerCase()}-${angle}`,
        fragment: `v4/45.875000/10.850000/12.300/-25.00/${pitch}/${style}/${layer}/${lens}/0.0/Relief/1.00`,
        journey: false, terrain: true });
}

const presentationViews = [
    ...["Pixel", "Normal", "Minecraft", "Roblox"].map(style => ({
        name: "presentation-" + style.toLowerCase(), journey: false, terrain: true, renderStyle: style,
        fragment: `v5/45.875/10.85/12.3/-25/58/Day/Map/Perspective/0/Relief/1.00/${style}/Orbit/0/0/0`
    })),
    {name: "presentation-earth-overhead", journey: false, terrain: true,
        fragment: "v4/45.875/10.85/12.3/-25/0/Day/Earth/Orthographic/0/Relief/1.00"},
    {name: "presentation-elevation", journey: false, terrain: true,
        fragment: "v4/45.875/10.85/12.3/-25/55/Night/Elevation/Orthographic/0/Relief/1.00"}
];

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
function installProbe({ observeDraws = false, requireDepthQuality = false, observeTransfers = false } = {}) {
    const state = globalThis.__pixelAtlasPerf = { active: false, frames: [], uploads: [], bootUploads: [], bootAllocations: [],
        bootDraws: [], draws: [], readbacks: [], transfers: [], syncCalls: [], bufferReads: [],
        bootResolves: [], resolves: [], bootReadbacks: [],
        pendingPixelPacks: 0, maximumPendingPixelPacks: 0, overwrittenPixelPacks: 0,
        abandonedPixelPacks: 0, releasedPixelPacks: 0,
        pendingDirectFrames: 0, maximumPendingDirectFrames: 0, releasedDirectFrames: 0,
        framebufferAttachments: 0, longTasks: [],
        width: 0, height: 0, quality: null, start: 0 };
    const channels = { 6402: 1, 6403: 1, 6406: 1, 6407: 3, 6408: 4, 6409: 1, 6410: 2,
        32993: 4, 33319: 2, 33320: 2, 36244: 1, 36248: 3, 36249: 4 };
    const scalarBytes = { 5120: 1, 5121: 1, 5122: 2, 5123: 2, 5124: 4, 5125: 4, 5126: 4, 5131: 2, 36193: 2 };
    const packedBytes = { 32819: 2, 32820: 2, 33635: 2, 33640: 4, 34042: 4, 35899: 4, 35902: 4, 36269: 8 };
    const pixelBytes = (width, height, depth, format, type) => {
        const stride = packedBytes[type] || channels[format] * scalarBytes[type];
        return Number.isFinite(stride) ? width * height * depth * stride : null;
    };
    const transfer = (name, durationMs, width, height, depth, bytes, allocation = false) => {
        if (durationMs === null) return;
        state.transfers.push({ method: name, width, height, depth, bytes, allocation,
            durationMs });
    };
    const quality = (width, height) => {
        for (const pixels of [1, 2, 3, 4])
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
        if (pixels !== null && !requireDepthQuality) state.quality = pixels;
        if (state.active) state.uploads.push([performance.now(), width, height, pixels]);
        else if (state.bootUploads.length < 2000) state.bootUploads.push([width, height]);
    };
    for (const type of [globalThis.WebGLRenderingContext, globalThis.WebGL2RenderingContext]) {
        if (!type) continue;
        for (const name of ["texImage2D", "texSubImage2D", "texStorage2D"]) {
            const original = type.prototype[name];
            if (typeof original !== "function" || original.__pixelAtlasObserved) continue;
            const observed = function (...args) {
                const started = observeTransfers && state.active ? performance.now() : null;
                const result = Reflect.apply(original, this, args);
                const duration = started === null ? null : performance.now() - started;
                if (name === "texStorage2D") {
                    record(args[3], args[4], true);
                    transfer(name, duration, args[3], args[4], 1, null, true);
                } else if (args.length >= 9) {
                    const index = name === "texImage2D" ? 3 : 4;
                    record(args[index], args[index + 1], observeDraws && args[8] === null);
                    transfer(name, duration, args[index], args[index + 1], 1,
                        pixelBytes(args[index], args[index + 1], 1, args[6], args[7]), args[8] === null);
                }
                return result;
            };
            observed.__pixelAtlasObserved = true;
            type.prototype[name] = observed;
        }
    }
    if (observeTransfers) {
        for (const type of [globalThis.WebGLRenderingContext, globalThis.WebGL2RenderingContext]) {
            if (!type) continue;
            for (const name of ["texImage3D", "texSubImage3D", "texStorage3D", "bufferData", "bufferSubData"]) {
                const original = type.prototype[name];
                if (typeof original !== "function" || original.__pixelAtlasObserved) continue;
                const observed = function (...args) {
                    const started = state.active ? performance.now() : null;
                    const result = Reflect.apply(original, this, args);
                    if (started === null) return result;
                    const duration = performance.now() - started;
                    if (name === "texImage3D" || name === "texSubImage3D") {
                        const index = name === "texImage3D" ? 3 : 5;
                        const formatIndex = name === "texImage3D" ? 7 : 8;
                        transfer(name, duration, args[index], args[index + 1], args[index + 2],
                            pixelBytes(args[index], args[index + 1], args[index + 2], args[formatIndex], args[formatIndex + 1]),
                            args[formatIndex + 2] === null);
                    } else if (name === "texStorage3D") {
                        // Storage allocation is separate from transferred pixels. Internal
                        // formats and mip levels can change the driver's actual allocation.
                        transfer(name, duration, args[3], args[4], args[5], null, true);
                    } else {
                        const index = name === "bufferData" ? 1 : 2;
                        const source = args[index];
                        const scalarSize = source?.BYTES_PER_ELEMENT || 1;
                        const offset = args[3] || 0;
                        const length = args[4] || 0;
                        const bytes = typeof source === "number" ? source : length > 0 ? length * scalarSize :
                            Math.max(0, (source?.byteLength || 0) - offset * scalarSize);
                        transfer(name, duration, null, null, null, bytes, typeof source === "number");
                    }
                    return result;
                };
                observed.__pixelAtlasObserved = true;
                type.prototype[name] = observed;
            }
        }
    }
    if (observeDraws) {
        const contexts = new WeakMap(), attached = new WeakSet();
        const resolveShaders = new WeakSet(), resolvePrograms = new WeakSet();
        const bufferIds = new WeakMap(), fences = new WeakMap(), pendingReads = new Map();
        const pendingDirectFrames = new Set();
        const syncMethods = new Set(["fenceSync", "clientWaitSync", "waitSync", "deleteSync", "flush", "finish"]);
        let nextBufferId = 1, nextReadbackId = 1, nextFenceId = 1, nextResolveId = 1;
        const updatePending = () => {
            state.pendingPixelPacks = pendingReads.size;
            if (state.active) state.maximumPendingPixelPacks = Math.max(state.maximumPendingPixelPacks, pendingReads.size);
        };
        const updateDirectPending = () => {
            state.pendingDirectFrames = pendingDirectFrames.size;
            if (state.active) state.maximumPendingDirectFrames =
                Math.max(state.maximumPendingDirectFrames, pendingDirectFrames.size);
        };
        for (const type of [globalThis.WebGLRenderingContext, globalThis.WebGL2RenderingContext]) {
            if (!type) continue;
            for (const name of ["bindFramebuffer", "framebufferTexture2D", "viewport", "enable", "disable", "readPixels",
                "shaderSource", "attachShader", "useProgram",
                "bindBuffer", "deleteBuffer", "getBufferSubData", ...syncMethods,
                "drawArrays", "drawElements", "drawArraysInstanced", "drawElementsInstanced"]) {
                const original = type.prototype[name];
                if (typeof original !== "function" || original.__pixelAtlasObserved) continue;
                const observed = function (...args) {
                    const timed = state.active && (name === "readPixels" ||
                        observeTransfers && (name === "getBufferSubData" || syncMethods.has(name)));
                    const started = timed ? performance.now() : 0;
                    const result = Reflect.apply(original, this, args);
                    const duration = timed ? performance.now() - started : 0;
                    let context = contexts.get(this);
                    if (!context) {
                        context = { framebuffer: null, width: 0, height: 0, depth: false, pixelPack: null,
                            lastReadback: null, lastResolve: null, program: null };
                        contexts.set(this, context);
                    }
                    if (name === "shaderSource") {
                        // Identify the production map resolve program through its existing
                        // uniforms. No application timing hooks or extra GL queries are needed.
                        if (args[1].includes("uniform bool uArtworkOnly;") &&
                            args[1].includes("uniform float uPhotoOpacity;")) resolveShaders.add(args[0]);
                    } else if (name === "attachShader") {
                        if (resolveShaders.has(args[1])) resolvePrograms.add(args[0]);
                    } else if (name === "useProgram") {
                        context.program = args[0];
                    } else if (name === "bindFramebuffer") {
                        if (args[0] === this.FRAMEBUFFER || args[0] === this.DRAW_FRAMEBUFFER) context.framebuffer = args[1];
                    } else if (name === "framebufferTexture2D") {
                        if (context.framebuffer && args[3]) { attached.add(context.framebuffer); state.framebufferAttachments++; }
                    } else if (name === "viewport") {
                        context.width = args[2]; context.height = args[3];
                    } else if (name === "enable" || name === "disable") {
                        if (args[0] === this.DEPTH_TEST) context.depth = name === "enable";
                    } else if (name === "bindBuffer") {
                        if (args[0] === this.PIXEL_PACK_BUFFER) {
                            context.pixelPack = args[1];
                            if (args[1] && !bufferIds.has(args[1])) bufferIds.set(args[1], nextBufferId++);
                        }
                    } else if (name === "deleteBuffer") {
                        const id = args[0] && bufferIds.get(args[0]);
                        if (context.pixelPack === args[0]) context.pixelPack = null;
                        if (pendingReads.delete(id)) {
                            if (state.active) state.abandonedPixelPacks++;
                            updatePending();
                        }
                    } else if (name === "readPixels") {
                        const mode = typeof args[6] === "number" ? "pixelPack" : "client";
                        const bufferId = context.pixelPack && bufferIds.get(context.pixelPack);
                        const readback = { id: nextReadbackId++, started: timed ? started : performance.now(),
                            bytes: pixelBytes(args[2], args[3], 1, args[4], args[5]), copiedBytes: 0, bufferId };
                        if (mode === "pixelPack") {
                            context.lastReadback = readback;
                            if (bufferId) {
                                if (state.active && pendingReads.has(bufferId)) state.overwrittenPixelPacks++;
                                pendingReads.set(bufferId, readback);
                                updatePending();
                            }
                        }
                        if (state.active) state.readbacks.push([started, duration, args[2], args[3],
                            quality(args[2], args[3]), mode, readback.id, bufferId]);
                        else if (state.bootReadbacks.length < 2000)
                            state.bootReadbacks.push({width: args[2], height: args[3], mode});
                    } else if (name === "getBufferSubData") {
                        const bufferId = args[0] === this.PIXEL_PACK_BUFFER && context.pixelPack
                            ? bufferIds.get(context.pixelPack) : null;
                        const readback = pendingReads.get(bufferId);
                        const scalarSize = args[2]?.BYTES_PER_ELEMENT || 1;
                        const bytes = args[4] > 0 ? args[4] * scalarSize :
                            Math.max(0, (args[2]?.byteLength || 0) - (args[3] || 0) * scalarSize);
                        if (timed) state.bufferReads.push({ time: started, durationMs: duration, bytes,
                            pixelPack: args[0] === this.PIXEL_PACK_BUFFER,
                            bufferId, readbackId: readback?.id ?? null,
                            submittedToCopyMs: readback ? started + duration - readback.started : null });
                        if (readback) {
                            readback.copiedBytes += bytes;
                            if (readback.copiedBytes >= readback.bytes) pendingReads.delete(bufferId);
                            updatePending();
                        }
                    } else if (syncMethods.has(name)) {
                        let fence = args[0] && typeof args[0] === "object" ? fences.get(args[0]) : null;
                        let firstSignalLatencyMs = null, resolveSignalLatencyMs = null;
                        if (name === "fenceSync" && result) {
                            fence = { id: nextFenceId++, readback: context.lastReadback,
                                resolve: context.lastResolve, signaled: false };
                            context.lastResolve = null;
                            fences.set(result, fence);
                            if (fence.resolve?.compositorContext) {
                                pendingDirectFrames.add(fence.id);
                                updateDirectPending();
                            }
                        } else if (name === "clientWaitSync" && fence && !fence.signaled &&
                            (result === this.ALREADY_SIGNALED || result === this.CONDITION_SATISFIED)) {
                            fence.signaled = true;
                            if (pendingDirectFrames.delete(fence.id)) updateDirectPending();
                            if (fence.readback) firstSignalLatencyMs =
                                (timed ? started + duration : performance.now()) - fence.readback.started;
                            if (fence.resolve) resolveSignalLatencyMs =
                                (timed ? started + duration : performance.now()) - fence.resolve.time;
                        } else if (name === "deleteSync" && args[0]) {
                            fences.delete(args[0]);
                            if (pendingDirectFrames.delete(fence?.id)) {
                                if (state.active) state.releasedDirectFrames++;
                                updateDirectPending();
                            }
                            const readback = fence?.readback;
                            if (readback && pendingReads.get(readback.bufferId) === readback) {
                                pendingReads.delete(readback.bufferId);
                                if (state.active) state.releasedPixelPacks++;
                                updatePending();
                            }
                        }
                        if (timed) state.syncCalls.push({ method: name, time: started, durationMs: duration,
                            fenceId: fence?.id ?? null, readbackId: fence?.readback?.id ?? null,
                            resolveId: fence?.resolve?.id ?? null, resolveSignalLatencyMs,
                            status: name === "clientWaitSync" ? result : null,
                            flags: name === "clientWaitSync" || name === "waitSync" ? args[1] : null,
                            timeoutNs: name === "clientWaitSync" || name === "waitSync" ? Number(args[2]) : null,
                            firstSignalLatencyMs });
                    } else if (name.startsWith("draw") && context.framebuffer && attached.has(context.framebuffer)) {
                        const count = name.startsWith("drawElements") ? args[1] : args[2];
                        if (state.bootDraws.length < 10000)
                            state.bootDraws.push([context.width, context.height, context.depth, count]);
                        if (state.active) {
                            const pixels = quality(context.width, context.height);
                            if (pixels !== null && (!requireDepthQuality || context.depth)) state.quality = pixels;
                            state.draws.push([context.width, context.height, pixels, context.depth, count]);
                        }
                        if (resolvePrograms.has(context.program)) {
                            const resolve = { id: nextResolveId++, time: performance.now(), width: context.width, height: context.height,
                                pixelSize: quality(context.width, context.height),
                                compositorContext: this.canvas?.id === "uno-canvas" };
                            context.lastResolve = resolve;
                            if (state.active) state.resolves.push(resolve);
                            else if (state.bootResolves.length < 2000) state.bootResolves.push(resolve);
                        }
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
        const draws = {}, depthDraws = {}, uploads = {}, allocations = {}, viewports = {};
        for (const [w, h, depth] of state.bootDraws) {
            viewports[`${w}x${h}`] = (viewports[`${w}x${h}`] || 0) + 1;
            const size = quality(w, h);
            if (size !== null) {
                draws[size] = (draws[size] || 0) + 1;
                if (depth) depthDraws[size] = (depthDraws[size] || 0) + 1;
            }
        }
        for (const [w, h] of requireDepthQuality ? [] : state.bootUploads) {
            const size = quality(w, h);
            if (size !== null) uploads[size] = (uploads[size] || 0) + 1;
        }
        for (const [w, h] of state.bootAllocations) {
            const size = quality(w, h);
            if (size !== null) allocations[size] = (allocations[size] || 0) + 1;
        }
        return { textureFramebufferAttachments: state.framebufferAttachments,
            artSizedOffscreenDrawsByPixelSize: draws, artSizedCpuUploadsByPixelSize: uploads,
            artSizedDepthTestedDrawsByPixelSize: depthDraws,
            artSizedTextureAllocationsByPixelSize: allocations,
            allOffscreenViewportDraws: viewports,
            mapResolves: state.bootResolves.map(item => ({...item, pixelSize: quality(item.width, item.height)})),
            readbacks: state.bootReadbacks.slice() };
    };
    state.resetGpuEvidence = () => {
        state.bootUploads = [];
        state.bootAllocations = [];
        state.bootDraws = [];
        state.bootResolves = [];
        state.bootReadbacks = [];
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
        for (const [w, h, depth] of state.bootDraws) {
            const pixels = quality(w, h);
            if (pixels !== null && (!requireDepthQuality || depth)) state.quality = pixels;
        }
        state.frames = [];
        state.uploads = [];
        state.draws = [];
        state.readbacks = [];
        state.transfers = [];
        state.syncCalls = [];
        state.bufferReads = [];
        state.resolves = [];
        state.pixelPacksAtStart = state.pendingPixelPacks;
        state.maximumPendingPixelPacks = state.pendingPixelPacks;
        state.overwrittenPixelPacks = 0;
        state.abandonedPixelPacks = 0;
        state.releasedPixelPacks = 0;
        state.directFramesAtStart = state.pendingDirectFrames;
        state.maximumPendingDirectFrames = state.pendingDirectFrames;
        state.releasedDirectFrames = 0;
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
            uploads: state.uploads, draws: state.draws, readbacks: state.readbacks, transfers: state.transfers,
            syncCalls: state.syncCalls, bufferReads: state.bufferReads, resolves: state.resolves,
            pixelPacks: { atStart: state.pixelPacksAtStart, atEnd: state.pendingPixelPacks,
                maximumPending: state.maximumPendingPixelPacks, overwritten: state.overwrittenPixelPacks,
                abandoned: state.abandonedPixelPacks, releasedWithoutCopy: state.releasedPixelPacks },
            directFrames: { atStart: state.directFramesAtStart, atEnd: state.pendingDirectFrames,
                maximumPending: state.maximumPendingDirectFrames, releasedBeforeSignal: state.releasedDirectFrames },
            longTasks: state.longTasks, finalPixelSize: state.quality };
    };
}

async function open(browser, base, view, errors, observe, output) {
    const context = await browser.newContext({ viewport, deviceScaleFactor: 1 });
    const page = await context.newPage();
    const blocked = {};
    page.on("pageerror", error => errors.push(view.name + " " + error.message));
    page.on("console", message => {
        if (message.text().startsWith("Pixel Atlas") && message.text().includes("GPU"))
            console.log("Map GPU", view.name, message.text());
        if (message.type() === "error" && !message.text().includes("ERR_FAILED"))
            console.error("Browser console", view.name, message.text().slice(0, 2000));
    });
    if (observe || requestGpu) await page.addInitScript(installProbe, { observeDraws: true,
        requireDepthQuality: requestGpu && view.terrain && !expectCpuFallback, observeTransfers: requestGpu });
    if (forceDetachedGpu) await page.addInitScript(() => {
        // Reject the map renderer's array-texture requirement only on Uno's window
        // context. The compositor and detached GLCanvas keep their real capabilities.
        const original = WebGL2RenderingContext.prototype.getParameter;
        WebGL2RenderingContext.prototype.getParameter = function (parameter) {
            if (this.canvas?.id === "uno-canvas" && parameter === this.MAX_ARRAY_TEXTURE_LAYERS) return 128;
            return Reflect.apply(original, this, [parameter]);
        };
    });
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
    if (view.terrain) {
        const deadline = Date.now() + 60000;
        let snapshot;
        do {
            snapshot = await page.locator("body").ariaSnapshot();
            if (/61 m elevation/.test(snapshot)) break;
            await page.waitForTimeout(200);
        } while (Date.now() < deadline);
        assert.match(snapshot, /61 m elevation/, "The bundled Garda elevation fixture is loaded");
    }
    await page.waitForTimeout(1800);
    if (!runtimeMetadata) runtimeMetadata = await readRuntimeMetadata(page);
    let actualBackend = null;
    if (requestGpu) try {
        await setGpuRendering(page, false);
        await page.evaluate(() => __pixelAtlasPerf.resetGpuEvidence());
        actualBackend = await setGpuRendering(page, true, expectCpuFallback ? "CPU fallback" : "GPU active");
        await page.waitForTimeout(500);
    } catch (error) {
        await page.screenshot({ path: path.join(output, view.name + "-gpu-setup-failure.png") }).catch(() => {});
        const snapshot = await page.locator("body").ariaSnapshot().catch(() => "Accessibility snapshot unavailable");
        await writeFile(path.join(output, view.name + "-gpu-setup-failure.txt"), snapshot);
        await context.close();
        throw error;
    }
    else await setGpuRendering(page, false);
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
        if (requestGpu && view.terrain && !expectCpuFallback)
            assert.ok(Object.values(gpuEvidence.artSizedDepthTestedDrawsByPixelSize).some(count => count > 0),
                "GPU terrain used hardware depth at the art resolution");
        if (requireCompositorGpu) {
            assert.ok(gpuEvidence.mapResolves.length > 0, "The map resolve shader submitted an image");
            assert.ok(gpuEvidence.mapResolves.every(item => item.compositorContext),
                "Map images render in Uno's compositor context");
            assert.equal(gpuEvidence.readbacks.length, 0, "Direct GPU presentation performs zero framebuffer readbacks");
        }
        if (forceDetachedGpu) {
            assert.ok(gpuEvidence.mapResolves.length > 0, "The fallback resolve shader submitted an image");
            assert.ok(gpuEvidence.mapResolves.every(item => !item.compositorContext),
                "Unavailable compositor GPU rendering selects the detached GLCanvas fallback");
            assert.ok(gpuEvidence.readbacks.some(item => item.mode === "pixelPack"),
                "The fallback retains asynchronous pixel-pack presentation");
        }
        await writeFile(path.join(output, view.name + ".png"), image.bytes);
        await writeFile(path.join(output, view.name + ".map.png"), PNG.sync.write(image.map));
        await writeFile(path.join(output, view.name + ".txt"), accessibility);
        console.log("Captured", view.name, image.mapHash, gpuEvidence ? JSON.stringify(gpuEvidence) : "");
        return { name: view.name, fragment: view.fragment, mapBounds: opened.map, crop: image.area,
            mapHash: image.mapHash, canvasHash: image.canvasHash,
            expectedRestPixelSize: ["Normal", "Roblox"].includes(view.renderStyle) ? 1 : 2,
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
    const drawsByPixelSize = {}, depthDrawsByPixelSize = {};
    for (const [, , size, depth] of probe.draws) {
        if (size === null) continue;
        drawsByPixelSize[size] = (drawsByPixelSize[size] || 0) + 1;
        if (depth) depthDrawsByPixelSize[size] = (depthDrawsByPixelSize[size] || 0) + 1;
    }
    const readbacksByPixelSize = {}, readbackDimensions = {}, readbacksByDestination = {};
    const transfersByMethod = {}, transferDimensions = {}, syncByMethod = {}, pollStatuses = {};
    for (const [, milliseconds, width, height, size] of probe.readbacks) {
        const key = `${width}x${height}`;
        readbackDimensions[key] = (readbackDimensions[key] || 0) + 1;
        if (size !== null && size !== undefined) (readbacksByPixelSize[size] ??= []).push(milliseconds);
    }
    for (const item of probe.readbacks)
        (readbacksByDestination[item[5] || "client"] ??= []).push(item[1]);
    for (const item of probe.transfers || []) {
        (transfersByMethod[item.method] ??= []).push(item);
        if (item.width === null) continue;
        const key = `${item.width}x${item.height}x${item.depth}`;
        const dimensions = transferDimensions[item.method] ??= {};
        const group = dimensions[key] ??= { calls: 0, uploadedBytes: 0, allocationCalls: 0, totalCallMs: 0 };
        group.calls++;
        group.uploadedBytes += item.allocation ? 0 : item.bytes || 0;
        group.allocationCalls += item.allocation ? 1 : 0;
        group.totalCallMs += item.durationMs;
    }
    for (const item of probe.syncCalls || []) {
        (syncByMethod[item.method] ??= []).push(item);
        if (item.method === "clientWaitSync") {
            const status = ({ 37146: "alreadySignaled", 37147: "timeoutExpired",
                37148: "conditionSatisfied", 37149: "waitFailed" })[item.status] || String(item.status);
            pollStatuses[status] = (pollStatuses[status] || 0) + 1;
        }
    }
    const bufferReads = (probe.bufferReads || []).filter(item => item.pixelPack);
    const signalLatencies = (probe.syncCalls || []).filter(item => item.firstSignalLatencyMs !== null);
    const positiveTimeoutPolls = (syncByMethod.clientWaitSync || []).filter(item => item.timeoutNs > 0).length;
    const resolves = probe.resolves || [];
    const resolveIntervals = resolves.slice(1).map((item, index) => item.time - resolves[index].time);
    const resolveSignals = (probe.syncCalls || []).filter(item => item.resolveSignalLatencyMs != null);
    const metricWindowMs = 1000 * (after.Timestamp - before.Timestamp);
    return { durationMs: probe.durationMs, raf: distribution(intervals),
        metricWindowMs: Number.isFinite(metricWindowMs) ? metricWindowMs : null,
        mapResolves: { submissions: resolves.length,
            submissionsPerSecond: resolves.length * 1000 / probe.durationMs,
            compositorSubmissions: resolves.filter(item => item.compositorContext).length,
            detachedSubmissions: resolves.filter(item => !item.compositorContext).length,
            intervals: distribution(resolveIntervals),
            signaledCompletions: resolveSignals.length,
            submitToSignal: distribution(resolveSignals.map(item => item.resolveSignalLatencyMs)),
            byPixelSize: Object.fromEntries([...new Set(resolves.map(item => item.pixelSize))]
                .map(size => [size, resolves.filter(item => item.pixelSize === size).length])) },
        directGpuFrames: probe.directFrames || null,
        rafByObservedPixelSize: Object.fromEntries(Object.entries(byQuality).map(([key, values]) => [key, distribution(values)])),
        mapTextureUploadsByPixelSize: uploads, allTextureUploadDimensions: dimensions,
        artSizedOffscreenDrawsByPixelSize: drawsByPixelSize,
        artSizedDepthTestedDrawsByPixelSize: depthDrawsByPixelSize,
        readPixels: { ...distribution(probe.readbacks.map(item => item[1])),
            totalPixels: probe.readbacks.reduce((sum, item) => sum + item[2] * item[3], 0),
            totalMs: probe.readbacks.reduce((sum, item) => sum + item[1], 0),
            dimensions: readbackDimensions,
            byDestination: Object.fromEntries(Object.entries(readbacksByDestination)
                .map(([destination, times]) => [destination, distribution(times)])),
            byPixelSize: Object.fromEntries(Object.entries(readbacksByPixelSize).map(([size, times]) => [size, distribution(times)])) },
        transfersByMethod: Object.fromEntries(Object.entries(transfersByMethod).map(([method, items]) => [method, {
            ...distribution(items.map(item => item.durationMs)),
            totalCallMs: items.reduce((sum, item) => sum + item.durationMs, 0),
            uploadedBytes: items.reduce((sum, item) => sum + (item.allocation ? 0 : item.bytes || 0), 0),
            allocationCalls: items.filter(item => item.allocation).length,
            unknownByteCounts: items.filter(item => item.bytes === null).length,
            dimensions: transferDimensions[method] || {}
        }])),
        pixelPackReadback: { ...probe.pixelPacks, submissions: readbacksByDestination.pixelPack?.length || 0,
            syncByMethod: Object.fromEntries(Object.entries(syncByMethod).map(([method, calls]) => [method, {
                ...distribution(calls.map(item => item.durationMs)),
                totalCallMs: calls.reduce((sum, item) => sum + item.durationMs, 0)
            }])),
            pollStatuses, positiveTimeoutPolls,
            submitToSignal: distribution(signalLatencies.map(item => item.firstSignalLatencyMs)),
            getBufferSubData: { ...distribution(bufferReads.map(item => item.durationMs)),
                totalCallMs: bufferReads.reduce((sum, item) => sum + item.durationMs, 0),
                copiedBytes: bufferReads.reduce((sum, item) => sum + item.bytes, 0),
                correlatedCopies: bufferReads.filter(item => item.readbackId !== null).length,
                submitToCopy: distribution(bufferReads.filter(item => item.submittedToCopyMs !== null)
                    .map(item => item.submittedToCopyMs)) } },
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
            restPixelSize: 2, actualBackend: opened.actualBackend, settledMapHash: settled.mapHash };
        if (requestGpu && view.terrain && !expectCpuFallback)
            assert.ok(Object.values(result.artSizedDepthTestedDrawsByPixelSize).some(count => count > 0),
                "The moving terrain frame used hardware depth at the art resolution");
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
    assert.ok(!(requireCompositorGpu && forceDetachedGpu), "Select one GPU presentation path");
    assert.ok(!(requireCompositorGpu || forceDetachedGpu) || requestGpu, "GPU presentation checks require --gpu");
    const measureMotion = process.argv.includes("--motion");
    const measureTerrain = process.argv.includes("--terrain");
    const filter = process.argv.find(arg => arg.startsWith("--case="))?.slice(7).split(",");
    const cases = (process.argv.includes("--presentation") ? presentationViews : measureTerrain ? terrainViews : views)
        .filter(view => filter ? filter.includes(view.name) : !measureMotion ||
        (measureTerrain ? /-day-perspective58$/.test(view.name) :
            ["map-day-iso55", "earth-day-iso55", "map-day-perspective65", "earth-day-perspective65"].includes(view.name)));
    assert.ok(cases.length > 0, "The selected case exists");
    await mkdir(output, { recursive: true });
    const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined, args: browserArgs });
    const errors = [];
    const report = { browser: browser.version(), viewport, deviceScaleFactor: 1, browserArgs,
        harnessSha256: digest(await readFile(__filename)), capturedAtUtc: new Date().toISOString(),
        platform: os.platform(), architecture: os.arch(), cpu: os.cpus()[0]?.model,
        availableParallelism: os.availableParallelism(), logicalCpuCount: os.cpus().length,
        base, revision: process.env.PIXELMAP_REVISION || null, mode: measureMotion ? "motion" : "visual",
        requestedGpuRendering: requestGpu,
        requiredCompositorGpu: requireCompositorGpu, forcedDetachedGpu: forceDetachedGpu,
        expectedGpuBackend: requestGpu ? expectCpuFallback ? "CPU fallback" : "GPU active" : null,
        performanceTimeDomain: "threadTicks",
        adaptiveQuality: "Production policy remains active. Resting views use pixel size 2. GPU terrain quality uses depth-tested art viewport dimensions. Shared 3D texture arrays never influence quality inference. CPU and legacy Skia modes use exact 2D upload/viewport dimensions. rAF measures browser scheduling. CPU metrics include the app and observational probe. readPixels records WebGL call duration including synchronization, grouped by art pixel size, dimensions, and client memory or pixel-pack buffer destination. GPU runs also time texture and buffer transfer calls and report uncompressed input bytes. Pixel-pack probes correlate read submissions, fence polling and buffer copies, including queue depth and completion age. These transfer timings cover the page's GL contexts and exclude final display composition.",
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

module.exports = {installProbe, summarizeProbe, metrics, distribution};

if (require.main === module)
    main().catch(error => { console.error(error); process.exitCode = 1; });
