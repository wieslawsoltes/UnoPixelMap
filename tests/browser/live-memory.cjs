// Diagnostic only: run the unchanged 9171e95 AOT application with live providers.
// Observation hooks forward native calls unchanged and retain no Wasm heap buffers.
const assert = require("node:assert/strict");
const {createHash} = require("node:crypto");
const {createWriteStream} = require("node:fs");
const {mkdir, writeFile} = require("node:fs/promises");
const {join, resolve} = require("node:path");
const {performance} = require("node:perf_hooks");
const {chromium} = require("playwright");
const {action, choose, see, telemetry, waitForSettledFocus} = require("./explore.cjs");

const base = new URL(process.argv[2] || "http://127.0.0.1:8100/UnoPixelMap/");
const output = resolve(process.env.PIXELMAP_LIVE_OUTPUT || process.argv[3] || "artifacts/live-memory");
const fragment = "v4/40.7087/-74.0104/16.5/-15/55/Night/Map/Perspective/0/Relief/1.00";
const source = "9171e95efab06db930a97dde212d754b8da48f60";
const duration = Math.min(660, Math.max(60, Number(process.env.PIXELMAP_LIVE_SECONDS) || 540));
const corpusLimit = Math.min(512, Math.max(1, Number(process.env.PIXELMAP_CORPUS_MIB) || 256)) * 1048576;
const responseLimit = 16 * 1048576;
const started = performance.now();
const elapsed = () => Math.round((performance.now() - started) * 1000) / 1000;
let events, stage = "initializing", eventCount = 0, droppedEvents = 0, fatal, fatalKind, resolveFatal;
const fatalSignal = new Promise(resolve => { resolveFatal = resolve; });
const requests = [], requestLookup = new WeakMap(), pendingBodies = new Set(), bodies = new Set();
let responseOrder = 0, storedBytes = 0, corpusTruncated = false;
const checkpoints = [];

function record(kind, data = {}) {
    if (events?.writableEnded || events?.destroyed) return;
    if (++eventCount <= 30000) events?.write(JSON.stringify({order: eventCount, timeMs: elapsed(),
        utc: new Date().toISOString(), stage, kind, ...data}) + "\n");
    else droppedEvents++;
}

function fail(error, kind = "runtime-failure") {
    if (fatal) return;
    fatal = error instanceof Error ? error : new Error(String(error));
    fatalKind = kind;
    record(kind, {message: fatal.stack || String(fatal)});
    resolveFatal(fatal);
}

async function limit(promise, milliseconds, name, observeFailure = true) {
    if (observeFailure && fatal) throw fatal;
    let timer;
    const candidates = [promise, new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(`${name} timed out after ${milliseconds}ms`)), milliseconds);
    })];
    if (observeFailure) candidates.push(fatalSignal.then(error => { throw error; }));
    try { return await Promise.race(candidates); }
    finally { clearTimeout(timer); }
}

async function step(name, operation, milliseconds = 30000) {
    if (fatal) throw fatal;
    stage = name;
    record("step-start");
    const result = await limit(Promise.resolve().then(operation), milliseconds, name);
    record("step-complete");
    return result;
}

function provider(url) {
    const value = new URL(url);
    if (value.hostname === "tiles.openfreemap.org") return value.pathname === "/planet" ? "vector-catalog" : "vector";
    if ((value.hostname === "s3.amazonaws.com" && value.pathname.startsWith("/elevation-tiles-prod/")) ||
        value.hostname === "elevation-tiles-prod.s3.amazonaws.com") return "elevation";
    if (value.hostname === "tiles.maps.eox.at") return "imagery";
    if (value.hostname === "routing.openstreetmap.de") return "routing";
    return null;
}

function watchProviders(page) {
    page.on("request", request => {
        const kind = provider(request.url());
        if (!kind) return;
        if (requests.length >= 4096) { corpusTruncated = true; return; }
        const entry = {id: requests.length + 1, kind, url: request.url(), method: request.method(),
            requestedMs: elapsed(), requestedStage: stage};
        requests.push(entry);
        requestLookup.set(request, entry);
        record("provider-request", entry);
    });
    page.on("requestfinished", request => {
        const entry = requestLookup.get(request);
        if (!entry) return;
        entry.finishedMs = elapsed();
        entry.timing = request.timing();
        record("provider-finished", {id: entry.id, timing: entry.timing});
    });
    page.on("requestfailed", request => {
        const entry = requestLookup.get(request);
        if (!entry) return;
        entry.failedMs = elapsed();
        entry.failure = request.failure()?.errorText;
        record("provider-failed", {id: entry.id, failure: entry.failure});
    });
    page.on("response", response => {
        const entry = requestLookup.get(response.request());
        if (!entry) return;
        entry.responseOrder = ++responseOrder;
        entry.responseMs = elapsed();
        entry.responseStage = stage;
        entry.status = response.status();
        entry.headers = Object.fromEntries(Object.entries(response.headers()).filter(([name]) =>
            ["content-type", "content-length", "content-encoding", "etag", "last-modified",
                "cache-control", "access-control-allow-origin"].includes(name)));
        record("provider-response", {id: entry.id, responseOrder: entry.responseOrder,
            status: entry.status, headers: entry.headers});
        const task = (async () => {
            if (Number(entry.headers["content-length"]) > responseLimit || storedBytes >= corpusLimit) {
                entry.capture = "size-limit";
                corpusTruncated = true;
                return;
            }
            // Playwright returns the decoded HTTP body, not content-encoded wire bytes.
            const body = await limit(response.body(), 30000, `response body ${entry.id}`, false);
            entry.bodyBytes = body.length;
            if (body.length > responseLimit) { entry.capture = "response-limit"; corpusTruncated = true; return; }
            const hash = createHash("sha256").update(body).digest("hex");
            entry.sha256 = hash;
            if (!bodies.has(hash)) {
                if (storedBytes + body.length > corpusLimit) { entry.capture = "corpus-limit"; corpusTruncated = true; return; }
                bodies.add(hash);
                storedBytes += body.length;
                await writeFile(join(output, "corpus", hash + ".bin"), body);
            }
            entry.body = "corpus/" + hash + ".bin";
            entry.capture = "saved";
            entry.capturedMs = elapsed();
            record("provider-body", {id: entry.id, sha256: hash, bytes: body.length, storedBytes});
        })().catch(error => { entry.capture = "error"; entry.captureError = String(error); record("capture-error", {id: entry.id, error: String(error)}); });
        pendingBodies.add(task);
        task.finally(() => pendingBodies.delete(task));
    });
}

// Runs before the application. All referenced memory/instance objects are weak or local;
// only scalar lengths and diagnostic records cross the Playwright binding.
function installObservation() {
    Error.stackTraceLimit = 100;
    const emit = (kind, data) => { void globalThis.pixelLiveEvent({kind, ...data}).catch(() => {}); };
    const memories = [], known = new WeakSet(), wrappedResize = new WeakMap();
    const NativeMemory = WebAssembly.Memory, NativeInstance = WebAssembly.Instance;
    const sizes = () => memories.map(entry => {
        const memory = entry.reference.deref();
        return {id: entry.id, source: entry.source, initial: entry.initial, maximum: entry.maximum,
            byteLength: memory ? memory.buffer.byteLength : null};
    });
    const track = (memory, source, descriptor = {}) => {
        if (!(memory instanceof NativeMemory) || known.has(memory)) return;
        known.add(memory);
        const entry = {id: memories.length + 1, source, initial: descriptor.initial,
            maximum: descriptor.maximum, reference: new WeakRef(memory)};
        memories.push(entry);
        emit("wasm-memory", {memories: sizes()});
    };
    WebAssembly.Memory = new Proxy(NativeMemory, {construct(target, args, newTarget) {
        const memory = Reflect.construct(target, args, newTarget);
        track(memory, "constructor", args[0]);
        return memory;
    }});
    const originalGrow = NativeMemory.prototype.grow;
    NativeMemory.prototype.grow = new Proxy(originalGrow, {apply(target, memory, args) {
        track(memory, "grow");
        const before = memory.buffer.byteLength;
        try {
            const result = Reflect.apply(target, memory, args);
            emit("wasm-grow", {pages: args[0], before, after: memory.buffer.byteLength, result});
            return result;
        } catch (error) {
            emit("wasm-grow-failed", {pages: args[0], before, after: memory.buffer.byteLength, error: String(error)});
            throw error;
        }
    }});
    const inspectImports = imports => {
        if (!imports || typeof imports !== "object") return;
        for (const [moduleName, moduleDescriptor] of Object.entries(Object.getOwnPropertyDescriptors(imports))) {
            const module = moduleDescriptor.value;
            if (!module || typeof module !== "object") continue;
            for (const [name, descriptor] of Object.entries(Object.getOwnPropertyDescriptors(module))) {
                const value = descriptor.value;
                if (value instanceof NativeMemory) track(value, `import:${moduleName}.${name}`);
                if (name !== "emscripten_resize_heap" || typeof value !== "function") continue;
                let wrapped = wrappedResize.get(value);
                if (!wrapped) {
                    wrapped = new Proxy(value, {apply(target, receiver, args) {
                        emit("wasm-resize-request", {requestedBytes: args[0], memories: sizes()});
                        try {
                            const result = Reflect.apply(target, receiver, args);
                            emit("wasm-resize-result", {requestedBytes: args[0], result, memories: sizes()});
                            return result;
                        } catch (error) {
                            emit("wasm-resize-failed", {requestedBytes: args[0], error: String(error), memories: sizes()});
                            throw error;
                        }
                    }});
                    wrappedResize.set(value, wrapped);
                    wrappedResize.set(wrapped, wrapped);
                }
                try {
                    Object.defineProperty(module, name, {...descriptor, value: wrapped});
                    emit("wasm-resize-hook", {moduleName, name});
                }
                catch (error) { emit("observation-unavailable", {name, error: String(error)}); }
            }
        }
    };
    const inspectResult = result => {
        const instance = result instanceof NativeInstance ? result : result?.instance;
        if (instance) for (const [name, value] of Object.entries(instance.exports)) track(value, "export:" + name);
        return result;
    };
    WebAssembly.Instance = new Proxy(NativeInstance, {construct(target, args, newTarget) {
        inspectImports(args[1]);
        return inspectResult(Reflect.construct(target, args, newTarget));
    }});
    for (const method of ["instantiate", "instantiateStreaming"]) {
        const original = WebAssembly[method];
        if (!original) continue;
        WebAssembly[method] = new Proxy(original, {apply(target, receiver, args) {
            inspectImports(args[1]);
            return Reflect.apply(target, receiver, args).then(inspectResult);
        }});
    }
    let wheels = 0, wheelX = 0, wheelY = 0, depthDraws = 0, draws = 0;
    const viewports = {};
    document.addEventListener("wheel", event => {
        wheels++; wheelX += event.deltaX; wheelY += event.deltaY;
        const data = {eventTime: event.timeStamp, trusted: event.isTrusted, deltaX: event.deltaX,
            deltaY: event.deltaY, deltaMode: event.deltaMode, wheelDelta: event.wheelDelta,
            wheelDeltaX: event.wheelDeltaX, wheelDeltaY: event.wheelDeltaY,
            ctrl: event.ctrlKey, meta: event.metaKey, alt: event.altKey, shift: event.shiftKey,
            clientX: event.clientX, clientY: event.clientY, targetId: event.target?.id,
            targetRole: event.target?.getAttribute?.("role")};
        queueMicrotask(() => emit("wheel", {...data, prevented: event.defaultPrevented}));
    }, {capture: true, passive: true});
    const gl = WebGL2RenderingContext.prototype;
    for (const name of ["drawElements", "drawElementsInstanced", "drawArrays", "drawArraysInstanced"]) {
        const original = gl[name];
        gl[name] = function (...args) { draws++; if (this.isEnabled(this.DEPTH_TEST)) depthDraws++; return Reflect.apply(original, this, args); };
    }
    const viewport = gl.viewport;
    gl.viewport = function (x, y, width, height) {
        const key = width + "x" + height;
        if (key in viewports || Object.keys(viewports).length < 64) viewports[key] = (viewports[key] || 0) + 1;
        return Reflect.apply(viewport, this, [x, y, width, height]);
    };
    globalThis.pixelLiveObservation = () => ({memories: sizes(), wheels, wheelX, wheelY, draws, depthDraws, viewports: {...viewports}});
    emit("observation-installed", {stackTraceLimit: Error.stackTraceLimit});
}

function publicState() {
    const labels = Array.from(document.querySelectorAll("[aria-label]"), element => element.getAttribute("aria-label"));
    const map = document.querySelector('[aria-label="Interactive world map"]')?.getBoundingClientRect();
    const canvas = document.getElementById("uno-canvas");
    const describe = element => element ? {id: element.id, role: element.getAttribute("role"),
        name: element.getAttribute("aria-label"), expanded: element.getAttribute("aria-expanded"),
        controls: element.getAttribute("aria-controls"), activeDescendant: element.getAttribute("aria-activedescendant"),
        selected: element.getAttribute("aria-selected"), type: element.getAttribute("elementtype")} : null;
    return {status: labels.filter(label => /GPU active|GPU fallback|Loading map|Map ready|Loading terrain|Terrain ready|unavailable/.test(label)),
        telemetry: labels.find(label => label.startsWith("Free roam telemetry")),
        paused: labels.includes("Resume free roam"), active: labels.includes("Pause free roam"),
        geometry: {width: innerWidth, height: innerHeight, dpr: devicePixelRatio, canvasWidth: canvas?.width, canvasHeight: canvas?.height},
        map: map ? {x: map.x, y: map.y, width: map.width, height: map.height} : null,
        focus: describe(document.activeElement),
        controls: Array.from(document.querySelectorAll('[role="combobox"], [role="listbox"], [role="option"]'), describe),
        observation: globalThis.pixelLiveObservation?.()};
}

async function run() {
    await mkdir(join(output, "corpus"), {recursive: true});
    events = createWriteStream(join(output, "events.ndjson"));
    record("protocol", {source, fragment, input: "native CDP Input.synthesizeScrollGesture; no synthetic Pan fallback",
        viewport: {width: 1800, height: 924, deviceScaleFactor: 2}, durationSeconds: duration,
        corpusLimit, responseLimit, bodyFormat: "decoded HTTP response bytes; remove content-encoding/content-length when replaying",
        observation: "Weak Wasm-memory references, forwarded grow/resize calls, passive wheel listener, WebGL draw/viewport counters; no managed app-state access"});
    const deadline = setTimeout(() => fail(new Error("The bounded diagnostic deadline expired"), "deadline"), duration * 1000);
    let browser, page, context, cdp, poller, polling = false, closing = false, error;
    async function sample(name) {
        if (polling) return null;
        polling = true;
        try {
            const results = await Promise.allSettled([
                limit(page.evaluate(publicState), 4000, "DOM/memory sample", false),
                limit(cdp.send("Performance.getMetrics"), 4000, "performance metrics", false),
                limit(cdp.send("Memory.getDOMCounters"), 4000, "DOM counters", false),
                limit(cdp.send("Runtime.getHeapUsage"), 4000, "JS heap usage", false)
            ]);
            const state = {name, values: results.map(result => result.status === "fulfilled" ? result.value : {error: String(result.reason)})};
            record("sample", state);
            return state;
        } finally { polling = false; }
    }
    async function checkpoint(name, screenshots = true, timeout = 10000) {
        const data = await limit(page.evaluate(publicState), timeout, name + " state");
        checkpoints.push({name, timeMs: elapsed(), ...data});
        await writeFile(join(output, "checkpoints.json"), JSON.stringify(checkpoints, null, 2));
        record("checkpoint", {name, state: data});
        if (screenshots) await Promise.allSettled([
            limit(page.screenshot({path: join(output, name + ".png"), timeout: 10000}), 11000, "screenshot", false),
            limit(page.locator("body").ariaSnapshot({timeout: 10000}), 11000, "accessibility", false)
                .then(snapshot => writeFile(join(output, name + ".txt"), snapshot))
        ]);
        return data;
    }
    async function ready(name) {
        await step(name, () => page.waitForFunction(() => Array.from(document.querySelectorAll("[aria-label]"))
            .some(element => { const label = element.getAttribute("aria-label");
                return /Map ready/.test(label) && /Terrain ready/.test(label) && /GPU active/.test(label); }), undefined, {timeout: 120000}), 125000);
    }
    async function nativeScroll(y, name) {
        stage = name;
        const parameters = {x: 1100, y: 480, yDistance: -y, speed: 800,
            gestureSourceType: "mouse", preventFling: true};
        record("native-scroll-start", {requestedWheelY: y, parameters});
        const command = cdp.send("Input.synthesizeScrollGesture", parameters);
        command.then(() => record("native-scroll-ack", {requestedWheelY: y}),
            failure => record("native-scroll-error", {requestedWheelY: y, error: String(failure)}));
        try { await limit(command, 10000, name); }
        catch (failure) {
            if (fatal) throw fatal;
            if (!String(failure).includes("timed out after 10000ms")) throw failure;
            record("native-scroll-timeout", {error: String(failure), note: "No further gesture until public-control liveness is acknowledged"});
        }
        await step(name + "-settling", () => page.waitForTimeout(3500), 5000);
        await checkpoint(name, true, 45000);
    }
    try {
        browser = await chromium.launch({executablePath: process.env.CHROMIUM_PATH || undefined,
            args: ["--disable-dev-shm-usage", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"]});
        browser.on("disconnected", () => { if (!closing) fail(new Error("Chromium disconnected"), "browser-disconnected"); });
        context = await browser.newContext({viewport: {width: 1800, height: 924}, deviceScaleFactor: 2});
        page = await context.newPage();
        page.setDefaultTimeout(25000);
        page.on("crash", () => fail(new Error("The renderer process crashed"), "page-crash"));
        page.on("pageerror", failure => fail(failure, "page-error"));
        page.on("console", message => {
            const text = message.text();
            record("console", {level: message.type(), text: text.slice(0, fatal ? 4096 : 65536), location: message.location()});
            if (/Unhandled (?:managed |dispatcher )?exception|MONO_WASM: Assert failed|\.NET runtime already exited|RuntimeError:|^ExitStatus\b|memory access out of bounds|Aborted\(/.test(text)) fail(new Error(text));
        });
        await page.exposeFunction("pixelLiveEvent", event => { const {kind, ...data} = event; record(kind, data); });
        await page.addInitScript(installObservation);
        watchProviders(page);
        cdp = await context.newCDPSession(page);
        await cdp.send("Performance.enable");
        poller = setInterval(() => { void sample("periodic").catch(failure => record("sample-error", {error: String(failure)})); }, 2000);
        const address = new URL(base); address.hash = fragment;
        await step("open-frozen-application", () => page.goto(address.href, {waitUntil: "domcontentloaded", timeout: 120000}), 125000);
        await step("wait-native-canvas", () => page.waitForFunction(() => document.getElementById("uno-canvas")?.width > 0, undefined, {timeout: 100000}), 105000);
        const accessibility = page.getByRole("button", {name: "Enable accessibility", exact: true});
        if (await accessibility.count()) await step("enable-accessibility", () => accessibility.evaluate(element => element.click()));
        await step("GPU-ready", () => see(page, /GPU active/, 90000), 95000);
        await checkpoint("01-initial-map");
        await step("open-game", () => action(page, "Open free roam game"));
        const relief = page.getByRole("switch", {name: "Game terrain relief", exact: true});
        if (await relief.getAttribute("aria-checked") !== "true") await step("enable-relief", () => action(page, "Game terrain relief"));
        await step("select-north-up", () => choose(page, "Free roam camera", 4));
        await step("start-walking", () => action(page, "Start free roam at map center"));
        await step("north-up-started", () => see(page, /rendering.+?·\s*z24\.0\s*·\s*0°/));
        await ready("north-up-live-data-ready");
        const north = await checkpoint("02-north-up-live-ready");
        assert.deepEqual(north.geometry, {width: 1800, height: 924, dpr: 2, canvasWidth: 3600, canvasHeight: 1848});
        const physical = await step("read-live-ground", () => telemetry(page));
        record("live-data-coverage", {physical, remoteVectors: requests.filter(r => r.kind === "vector" && r.status === 200).length,
            remoteElevation: requests.filter(r => r.kind === "elevation" && r.status === 200).length});
        assert.ok(requests.some(r => r.kind === "vector" && r.status === 200) &&
            requests.some(r => r.kind === "elevation" && r.status === 200), "The diagnostic must load real remote vectors and DEMs");
        assert.ok(Math.abs(physical.altitude - 10.8) < 2, "Detailed Manhattan ground must replace the 44.2m world fallback");
        await nativeScroll(480, "03-native-close-orbit");
        // The live run paused while new data arrived. Reproduce that state boundary
        // explicitly if focus changes did not already pause this fresh browser.
        if (await page.getByRole("button", {name: "Pause free roam", exact: true}).count())
            await step("pause-after-native-scroll", () => action(page, "Pause free roam"));
        await step("pause-acknowledged", () => see(page, /Resume free roam/));
        await ready("close-orbit-live-data-ready");
        await checkpoint("04-paused-close-ready");
        await step("resume-close-orbit", () => action(page, "Resume free roam"));
        await step("resume-acknowledged", () => see(page, /Pause free roam/));
        const expand = page.getByRole("button", {name: "Expand game settings", exact: true});
        if (await expand.count()) await step("expand-game-settings", () => action(page, "Expand game settings"));
        const camera = page.getByRole("combobox", {name: "Free roam camera", exact: true});
        await step("focus-camera-picker", () => camera.focus());
        await step("picker-focus-settled", () => waitForSettledFocus(page, camera));
        await step("open-camera-picker", () => page.keyboard.press("Enter"));
        await step("observe-orbit-selection", () => page.waitForFunction(() => {
            const head = document.querySelector('[role="combobox"][aria-label="Free roam camera"]');
            const list = document.getElementById(head?.getAttribute("aria-controls") ?? "");
            const active = document.getElementById(head?.getAttribute("aria-activedescendant") ?? "");
            return head?.getAttribute("aria-expanded") === "true" && list?.getAttribute("role") === "listbox" &&
                active?.getAttribute("role") === "option" && active.getAttribute("aria-label") === "Orbit" && list.contains(active);
        }));
        await checkpoint("05-orbit-picker");
        await step("dismiss-camera-picker", () => page.keyboard.press("Escape"));
        await step("picker-closed", () => page.waitForFunction(() =>
            document.querySelector('[role="combobox"][aria-label="Free roam camera"]')?.getAttribute("aria-expanded") === "false"));
        await step("picker-focus-restored", () => waitForSettledFocus(page, camera));
        await step("collapse-game-settings", () => action(page, "Collapse game settings"));
        await step("active-before-horizon", () => see(page, /Pause free roam/));
        await nativeScroll(-240, "06-native-horizon");
        await step("horizon-pause-command", () => action(page, "Pause free roam"));
        await step("horizon-pause-acknowledged", () => see(page, /Resume free roam/));
        await ready("horizon-live-data-ready");
        await checkpoint("07-horizon-paused-ready");
        await step("horizon-resume-command", () => action(page, "Resume free roam"));
        await step("horizon-resume-acknowledged", () => see(page, /Pause free roam/));
        await step("horizon-dwell", () => page.waitForTimeout(15000), 17000);
        await checkpoint("08-horizon-responsive");
        await step("exit-free-roam", () => action(page, "Exit free roam"));
        await step("map-restored", () => page.getByRole("textbox", {name: "Search places or latitude and longitude", exact: true}).waitFor({state: "visible"}));
        await checkpoint("09-returned-to-map");
        record("diagnostic-complete", {result: "No abort reproduced in this captured native-input/live-provider run"});
    } catch (failure) {
        error = failure;
        record("diagnostic-failed", {error: failure.stack || String(failure)});
        console.error(failure);
    } finally {
        clearTimeout(deadline);
        clearInterval(poller);
        if (page && !page.isClosed()) {
            await Promise.allSettled([
                limit(page.evaluate(publicState), 7000, "final DOM", false).then(state => writeFile(join(output, "final-dom.json"), JSON.stringify(state, null, 2))),
                limit(page.locator("body").ariaSnapshot({timeout: 7000}), 8000, "final AX", false).then(state => writeFile(join(output, "final-ax.txt"), state)),
                limit(page.screenshot({path: join(output, "final.png"), timeout: 7000}), 8000, "final screenshot", false)
            ]);
        }
        await limit(Promise.allSettled([...pendingBodies]), 10000, "provider capture drain", false).catch(failure => record("capture-drain-timeout", {error: String(failure)}));
        closing = true;
        if (browser) await limit(browser.close(), 10000, "browser close", false).catch(failure => record("close-error", {error: String(failure)}));
        const summary = {source, fragment, inputVariant: "native-synthesized-scroll", elapsedMs: elapsed(),
            outcome: fatal ? fatalKind : error ? "diagnostic-step-failure" : "not-reproduced",
            error: error?.stack || null, fatal: fatal?.stack || null, checkpoints: checkpoints.length,
            requestCount: requests.length, responseCount: responseOrder, storedBytes, uniqueBodies: bodies.size(),
            corpusLimit, corpusTruncated, eventCount, droppedEvents,
            limitations: ["Public DOM/request/API observations only; no decoded geometry, managed heap or retained-frame internals.",
                "Wasm byteLength is allocated linear memory, not live native/managed allocation or process RSS.",
                "Body files are decoded HTTP bytes; response order/timing are preserved separately. Browser/provider state is live.",
                "No synthetic input fallback is silently substituted for the native gesture."]};
        await writeFile(join(output, "summary.json"), JSON.stringify(summary, null, 2));
        await writeFile(join(output, "corpus-manifest.json"), JSON.stringify({source, corpusLimit, storedBytes, corpusTruncated,
            bodyFormat: "decoded HTTP response bytes", requests}, null, 2));
        await new Promise(resolve => events.end(resolve));
    }
    if (error || fatal) process.exitCode = 1;
}

run().catch(error => { console.error(error); process.exitCode = 1; });
