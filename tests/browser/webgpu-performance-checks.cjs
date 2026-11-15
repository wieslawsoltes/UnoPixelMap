const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const {installPerformanceProbe} = require("./webgpu-performance-probe.cjs");
const {distribution, makeTrace, mapRelativeTrace, intentionalPixelChange, fitMapExtent,
    checkStep, checkHealth, imageDifference, summarizeSteps,
    summarizeMovingWindow, movingContinuation} = require("./webgpu-performance.cjs");

function fixture() {
    let clock = 0;
    const events = {}, frames = [], calls = [], queueWaiters = [];
    class GPUQueue {
        submit(sequence) { calls.push({method: "submit", sequence: Array.from(sequence)}); return "submitted"; }
        writeBuffer(...args) { calls.push({method: "writeBuffer", args}); }
        writeTexture(...args) { calls.push({method: "writeTexture", args}); }
        onSubmittedWorkDone() { return new Promise(resolve => queueWaiters.push(resolve)); }
    }
    class GPUCommandEncoder { finish() { return {}; } }
    class GPUDevice {
        constructor() { this.queue = new GPUQueue(); this.features = new Set(); this.lost = new Promise(() => {}); }
        addEventListener() {}
        createShaderModule() { return {getCompilationInfo: async () => ({messages: []})}; }
        createCommandEncoder() { return new GPUCommandEncoder(); }
        createBuffer() { return {}; }
        createTexture() { return {}; }
    }
    class GPUAdapter {
        constructor() { this.info = {description: "contract fixture"}; }
        async requestDevice() { return new GPUDevice(); }
    }
    const sandbox = {GPUQueue, GPUCommandEncoder, GPUDevice, GPUAdapter,
        ArrayBuffer, Set, WeakSet, WeakMap, performance: {now: () => clock},
        requestAnimationFrame: callback => frames.push(callback), addEventListener: (name, callback) => { events[name] = callback; }};
    vm.runInNewContext("(" + installPerformanceProbe.toString() + ")()", sandbox);
    sandbox.PixelAtlasWebGpu = {};
    sandbox.PixelAtlasInput = {
        reportWebGpu: frame => Object.assign(sandbox.PixelAtlasWebGpu, frame, {failure: null}),
        failWebGpu: message => { sandbox.PixelAtlasWebGpu.failure = String(message); }
    };
    const state = sandbox.__pixelAtlasWebGpuPerformance;
    return {sandbox, state, events, frames, calls, queueWaiters,
        tick: value => { clock = value; }, adapter: new GPUAdapter(),
        wheel: (input, trusted = true) => events.wheel({isTrusted: trusted, clientX: input.x, clientY: input.y,
            deltaX: input.deltaX, deltaY: input.deltaY, deltaMode: 0,
            altKey: Boolean(input.modifiers & 1), ctrlKey: Boolean(input.modifiers & 2),
            metaKey: Boolean(input.modifiers & 4), shiftKey: Boolean(input.modifiers & 8)})};
}
async function menuFocusChecks() {
    const {menu} = require("./webgpu-performance.cjs");
    const run = async ({name = "High quality", submenu = "Rendering quality", initialQuality = "Balanced",
        activate = true, offscreen = false} = {}) => {
        let rootOpen = false, submenuOpen = false, shareOpen = false, frames = 0, pendingQuality = null;
        const clicks = [], reads = [], keys = [], previousFocus = {name: "Previous popup owner"};
        const document = {activeElement: previousFocus};
        const status = {quality: initialQuality};
        const sandbox = vm.createContext({document, Promise, PixelAtlasWebGpu: status,
            requestAnimationFrame: callback => setImmediate(() => {
                frames++;
                // Repeated delayed managed focus restoration must not redirect
                // a native pointer command or require a keyboard focus barrier.
                document.activeElement = previousFocus;
                if (pendingQuality && frames >= pendingQuality.frame) {
                    status.quality = pendingQuality.quality;
                    pendingQuality = null;
                }
                callback();
            })});
        const invoke = (callback, argument) => vm.runInContext("(" + callback.toString() + ")", sandbox)(argument);
        const geometry = new Map([
            ["Map appearance", {x: 1100, y: 12, width: 80, height: 36}],
            ["Rendering quality", {x: 960, y: 88, width: 220, height: 32}],
            ["High quality", {x: 740, y: 120, width: 220, height: 32}],
            ["Balanced quality", {x: 740, y: 88, width: 220, height: 32}],
            ["Share this view", {x: 960, y: 494, width: 220, height: 32}]
        ]);
        if (offscreen) geometry.get(name).x = 1300;
        const visible = target => target === "Map appearance" ||
            (target === "Shareable map link" ? shareOpen :
                ["High quality", "Balanced quality"].includes(target) ? submenuOpen : rootOpen);
        const targets = new Map([...geometry.keys(), "Shareable map link"].map(target => [target, {
            or() { return this; },
            waitFor: async ({state}) => assert.equal(visible(target), state === "visible", target + " has the requested popup state"),
            boundingBox: async () => { reads.push({target, frames}); return geometry.get(target); },
            focus: async () => assert.fail("Menu setup must use native pointer input, independent of transient DOM focus")
        }]));
        const page = {
            getByRole: (_, {name}) => targets.get(name),
            viewportSize: () => ({width: 1200, height: 800}),
            mouse: {
                click: async (x, y) => {
                    const hit = [...geometry].find(([target, r]) => visible(target) &&
                        x >= r.x && x < r.x + r.width && y >= r.y && y < r.y + r.height)?.[0];
                    assert.ok(hit, "Native input hits a visible painted command");
                    clicks.push(hit);
                    if (hit === "Map appearance") rootOpen = true;
                    else if (hit === "Rendering quality") submenuOpen = true;
                    else {
                        rootOpen = submenuOpen = false;
                        // Closure can happen even when activation fails; the
                        // public resulting quality/panel must be checked too.
                        if (activate && hit === "Share this view") shareOpen = true;
                        else if (activate) pendingQuality = {quality: hit.split(" ")[0], frame: frames + 3};
                    }
                    document.activeElement = previousFocus;
                },
                move: async () => {}
            },
            evaluate: async (callback, argument) => invoke(callback, argument),
            waitForFunction: async (callback, argument) => {
                for (let attempt = 0; attempt < 8; attempt++) {
                    if (invoke(callback, argument)) return;
                    await invoke(() => new Promise(requestAnimationFrame));
                }
                throw new Error("The command's public result did not appear");
            },
            keyboard: {press: async key => { keys.push(key); assert.fail("Untimed menu commands do not send keyboard input"); }}
        };
        const result = menu(page, name, submenu);
        if (!activate || offscreen) await assert.rejects(result, offscreen ? /intersects the viewport/ : /public result|requested popup state/);
        else await result;
        assert.deepEqual(keys, [], "Delayed popup focus cannot capture an unintended menu key");
        assert.deepEqual(clicks, offscreen ? ["Map appearance", "Rendering quality"] :
            submenu ? ["Map appearance", submenu, name] : ["Map appearance", name],
            "Each command is dispatched once without blind activation retries");
        assert.ok(reads.every((read, index) => read.frames >= (index + 1) * 2),
            "Popup input coordinates are sampled after the native layout barrier");
        if (activate && !offscreen && submenu) assert.equal(status.quality, name.split(" ")[0]);
    };
    await run();
    await run({initialQuality: "High"});
    await run({name: "Balanced quality", initialQuality: "High"});
    await run({name: "Share this view", submenu: null});
    await run({activate: false});
    await run({name: "Share this view", submenu: null, activate: false});
    await run({offscreen: true});
}
async function menuGroupingChecks() {
    const {menu} = require("./native-menu.cjs");
    // One frozen harness must activate both the baseline's flat commands and
    // the candidate's compact groups through their actual visible menu rows.
    for (const grouped of [false, true]) {
        for (const [name, group] of [["Normal rendering", "Rendering style"],
            ["After dark", "Lighting"], ["Perspective lens", "Camera"]]) {
            let rootOpen = false, groupOpen = false, activated = false;
            const clicks = [];
            const bounds = new Map([
                ["Map appearance", {x: 1100, y: 12, width: 44, height: 44}],
                [group, {x: 940, y: 68, width: 220, height: 44}],
                [name, {x: grouped ? 720 : 940, y: 112, width: 220, height: 44}]
            ]);
            const visible = target => target === "Map appearance" ||
                (target === group ? grouped && rootOpen : rootOpen && (!grouped || groupOpen));
            const target = texts => ({
                texts,
                or(other) { return target([...new Set([...texts, ...other.texts])]); },
                first() { return target([texts.find(visible) ?? texts[0]]); },
                isVisible: async () => texts.some(visible),
                waitFor: async ({state}) => assert.equal(texts.some(visible), state === "visible", texts.join(" or ") + " is reachable"),
                boundingBox: async () => bounds.get(texts.find(visible) ?? texts[0])
            });
            const scope = vm.createContext({Promise, requestAnimationFrame: callback => setImmediate(callback)});
            const page = {
                getByRole: (_, {name: text}) => target([text]),
                viewportSize: () => ({width: 1200, height: 700}),
                evaluate: async callback => vm.runInContext("(" + callback.toString() + ")", scope)(),
                mouse: {
                    click: async (x, y) => {
                        const hit = [...bounds].find(([text, r]) => visible(text) &&
                            x >= r.x && x < r.x + r.width && y >= r.y && y < r.y + r.height)?.[0];
                        assert.ok(hit, "A native pointer reaches a visible menu row");
                        clicks.push(hit);
                        if (hit === "Map appearance") rootOpen = true;
                        else if (hit === group) groupOpen = true;
                        else { activated = true; rootOpen = groupOpen = false; }
                    },
                    move: async () => {}
                }
            };
            await menu(page, name);
            assert.equal(activated, true, name + " executes through the visible menu");
            assert.deepEqual(clicks, grouped ? ["Map appearance", group, name] : ["Map appearance", name],
                "Public submenu discovery preserves both application revisions without retries");
        }
    }
}
async function focusReacquisitionChecks() {
    // Execute the real helper without browser/image dependencies. A fake clock
    // and animation queue reproduce delayed focus theft and replaced DOM peers.
    const run = async alwaysLose => {
        let clock = 0, frames = 0, focuses = 0, disposed = 0, generation = 0;
        const document = {activeElement: null}, owner = {};
        const peer = () => ({generation: generation++, isConnected: true, dispose: async () => { disposed++; }});
        let target = peer();
        const pageScope = vm.createContext({document, Promise,
            requestAnimationFrame: callback => setImmediate(() => {
                frames++; clock += 5;
                if (alwaysLose || [1, 3, 5].includes(frames)) document.activeElement = owner;
                if (frames === 3) { target.isConnected = false; target = peer(); }
                callback();
            })});
        const uiModule = {exports: {}};
        vm.runInNewContext(fs.readFileSync(path.join(__dirname, "explore.cjs"), "utf8"), {
            module: uiModule, URL, Promise, setTimeout, clearTimeout, Date: {now: () => clock}, process: {argv: [], env: {}},
            require: name => ["playwright", "pngjs", "./gpu.cjs"].includes(name) ? {} : require(name)
        }, {filename: "explore.cjs"});
        const locator = {
            focus: async () => { focuses++; document.activeElement = target; },
            elementHandle: async () => target
        };
        const page = {evaluate: async (callback, argument) =>
            vm.runInContext("(" + callback.toString() + ")", pageScope)(argument)};
        const result = uiModule.exports.waitForSettledFocus(page, locator, 80);
        if (alwaysLose) await assert.rejects(result, /did not keep focus for two animation frames/);
        else {
            await result;
            assert.equal(document.activeElement, target, "The current replacement peer owns stable keyboard focus");
            assert.equal(frames, 7, "Every delayed restoration is followed by two actual stable animation frames");
            assert.equal(focuses, 4, "Three focus losses cause bounded reacquisition before keyboard input");
        }
        assert.equal(disposed, focuses, "Every acquired peer is released on success and timeout");
    };
    await run(false);
    await run(true);
}

async function movingWindowChecks() {
    const f = fixture(), device = await f.adapter.requestDevice(), state = f.state;
    const command = device.createCommandEncoder({label: "PixelMap frame"}).finish();
    const other = device.createCommandEncoder({label: "Uno UI"}).finish();
    const buffer = device.createBuffer({label: "PixelMap moving buffer"});
    const texture = device.createTexture({label: "PixelMap moving texture", format: "r8unorm"});
    const clone = value => JSON.parse(JSON.stringify(value));
    const report = frames => f.sandbox.PixelAtlasInput.reportWebGpu({frames,
        frameMilliseconds: 2, submissionMilliseconds: 1, uploadBytes: 0});
    f.tick(10); device.queue.submit([command]); report(1);
    f.tick(100); state.begin("throughput");
    assert.throws(() => state.renderProgressAfter(1, 100), /outside measurement/);
    f.tick(6100);
    const raw = state.finish(), original = JSON.stringify(raw);
    const moving = summarizeMovingWindow(clone(raw), 6000, {distance: 1}, {distance: 10});
    assert.equal(moving.windowStatus, "zero-submission-window");
    assert.equal(moving.submittedMapFramesPerSecond, 0, "A slow six-second window remains an actual measured zero");
    assert.deepEqual(moving.renderCpuMilliseconds, {samples: 0}, "No CPU timing is invented for an absent frame");
    assert.deepEqual(moving.submittedFrameIntervalsMilliseconds, {samples: 0});
    assert.deepEqual(moving.rafIntervalsMilliseconds, {samples: 0});
    assert.equal(moving.raw.durationMilliseconds, 6000);
    assert.throws(() => summarizeMovingWindow(clone(raw), 6001, {distance: 1}, {distance: 10}), /not shortened/);
    assert.throws(() => summarizeMovingWindow(clone(raw), 6000, {distance: 1}, {distance: 1}), /walking advances/);
    const mismatched = clone(raw); mismatched.mapSubmits = 1;
    assert.throws(() => summarizeMovingWindow(mismatched, 6000, {distance: 1}, {distance: 10}), /timed map submission/);

    // Use the real probe and the real page predicate with a deterministic timer
    // scheduler. No rAF is delivered during the six-second measurement. A later
    // submit alone still cannot satisfy the successful-render diagnostic gate.
    const boundaries = state.boundaries, startBoundary = boundaries.start, endBoundary = boundaries.end;
    const queueWaiters = [...f.queueWaiters];
    let polls = 0, disposed = 0;
    const page = {
        waitForFunction: async (callback, argument, options) => {
            assert.deepEqual(options, {polling: 50, timeout: 500}, "Liveness uses bounded timer polling even when rAF is stalled");
            for (let elapsed = 0; elapsed <= options.timeout; elapsed += options.polling) {
                polls++;
                f.tick(raw.finishedAt + elapsed);
                if (elapsed === 50) device.queue.submit([other]);
                if (elapsed === 100) {
                    device.queue.submit([command]);
                    device.queue.writeBuffer(buffer, 0, new Uint8Array(8));
                    device.queue.writeTexture({texture}, new Uint8Array(4), {bytesPerRow: 4}, {width: 4, height: 1});
                    f.wheel({x: 1, y: 1, deltaX: 1, deltaY: 1, modifiers: 0});
                    for (const callback of f.frames.splice(0)) callback(raw.finishedAt + elapsed);
                }
                if (elapsed === 200) report(2);
                const value = vm.runInNewContext("(" + callback.toString() + ")", f.sandbox)(argument);
                if (value) return {jsonValue: async () => clone(value), dispose: async () => { disposed++; }};
            }
            throw new Error("Post-window renderer observation timed out");
        }
    };
    const continuation = await movingContinuation(page, clone(raw), 500);
    assert.equal(polls, 5, "Neither the old frame, unrelated compositor work nor a submit without diagnostics establishes liveness");
    assert.equal(disposed, 1);
    assert.equal(continuation.mapSubmitsAfterWindow, 1);
    assert.equal(continuation.lastMapSubmitAt, 6200);
    assert.equal(continuation.lastDiagnosticAt, 6300);
    assert.equal(continuation.observedAfterWindowMilliseconds, 200);
    assert.equal(JSON.stringify(raw), original,
        "Post-window submit, diagnostic, upload, wheel and rAF callbacks cannot modify any captured work or duration");
    assert.equal(state.active, null, "Continuation never reopens or retries the measurement");
    assert.equal(state.lastCompleted, raw);
    assert.equal(state.boundaries, boundaries);
    assert.equal(state.boundaries.start, startBoundary);
    assert.equal(state.boundaries.end, endBoundary, "Continuation cannot move either queue-prefix boundary");
    assert.deepEqual(f.queueWaiters, queueWaiters, "Continuation installs no completion wait or per-frame fence");
    f.tick(6500); f.queueWaiters.shift()();
    await startBoundary;
    f.tick(6600); f.queueWaiters.shift()();
    assert.deepEqual(clone(await state.throughputBoundaries()), {
        startQueuePrefixMilliseconds: 6400, endQueueDrainMilliseconds: 500
    }, "The retained queue observations remain anchored to begin and finish, before continuation");

    const stalled = fixture(), stalledDevice = await stalled.adapter.requestDevice();
    stalled.tick(100); stalled.state.begin("throughput");
    stalled.tick(6100); const stalledRaw = clone(stalled.state.finish());
    let stalledPolls = 0;
    const stalledPage = {
        waitForFunction: async (callback, argument, options) => {
            assert.deepEqual(options, {polling: 50, timeout: 250});
            for (let elapsed = 0; elapsed <= options.timeout; elapsed += options.polling) {
                stalledPolls++;
                stalled.tick(stalledRaw.finishedAt + elapsed);
                // Even fresh diagnostics without actual map submissions are not
                // evidence that the renderer is making forward progress.
                stalled.sandbox.PixelAtlasInput.reportWebGpu({frames: 0, frameMilliseconds: 2});
                const value = vm.runInNewContext("(" + callback.toString() + ")", stalled.sandbox)(argument);
                assert.equal(value, null);
            }
            throw new Error("Post-window renderer observation timed out");
        }
    };
    await assert.rejects(movingContinuation(stalledPage, stalledRaw, 250), /observation timed out/);
    assert.equal(stalledPolls, 6, "A permanently stalled renderer fails at the separate bounded deadline");
    assert.equal(stalled.state.active, null);
    assert.equal(stalled.state.mapSubmits, 0);
    assert.equal(stalled.queueWaiters.length, 2, "A timeout cannot extend or refence the closed sample");
    const unhealthyPage = {waitForFunction: async (callback, argument) => {
        const command = stalledDevice.createCommandEncoder({label: "PixelMap frame"}).finish();
        stalledDevice.queue.submit([command]);
        stalled.sandbox.PixelAtlasInput.reportWebGpu({frames: 1, frameMilliseconds: 2});
        stalled.sandbox.PixelAtlasInput.failWebGpu("Managed rendering failed after the last successful frame");
        return vm.runInNewContext("(" + callback.toString() + ")", stalled.sandbox)(argument);
    }};
    await assert.rejects(movingContinuation(unhealthyPage, stalledRaw), /WebGPU failed while checking post-window movement/,
        "A fresh frame cannot hide a subsequent live managed failure");
}

async function main() {
    await menuFocusChecks();
    await menuGroupingChecks();
    await focusReacquisitionChecks();
    await movingWindowChecks();
    const f = fixture(), device = await f.adapter.requestDevice(), state = f.state;
    const mapEncoder = device.createCommandEncoder({label: "PixelMap frame"});
    const command = mapEncoder.finish();
    const other = device.createCommandEncoder({label: "Uno UI"}).finish();
    const buffer = device.createBuffer({label: "PixelMap buffer"});
    const unrelated = device.createBuffer({label: "Uno UI"});
    const texture = device.createTexture({label: "PixelMap heights", format: "rg32float"});
    const uniform = device.createBuffer({label: "PixelMap buffer Uniform, CopyDst", size: 513 * 512, usage: 64 | 8});
    const terrain = device.createBuffer({label: "PixelMap buffer Vertex, CopyDst", size: 64 * 80, usage: 32 | 8});
    const equallySizedMesh = device.createBuffer({label: "PixelMap buffer Vertex, CopyDst", size: 64 * 80, usage: 32 | 8});
    assert.equal(state.sceneBufferCounts.terrain, 1, "A later equally sized mesh is not the terrain instance buffer");
    state.beginSceneCapture();
    assert.throws(() => state.begin("step"), /cannot overlap/);
    const cameraData = Float32Array.from({length: 132}, (_, index) => index / 10);
    const terrainData = Float32Array.from({length: 20}, (_, index) => index / 5);
    device.queue.writeBuffer(uniform, 0, cameraData, 2, 128);
    device.queue.writeBuffer(terrain, 0, terrainData);
    device.queue.writeBuffer(equallySizedMesh, 0, new Float32Array(20).fill(999));
    const scene = JSON.parse(JSON.stringify(state.finishSceneCapture()));
    const cameraWords = new Uint32Array(cameraData.buffer, 2 * 4, 128);
    const terrainWords = new Uint32Array(terrainData.buffer);
    assert.deepEqual(scene.uniform, [Array.from(cameraWords.subarray(0, 28))],
        "Camera identity contains only projection, view, ground, lens and world words");
    assert.deepEqual(scene.eye, Array.from(cameraWords.subarray(52, 55)),
        "Physical eye is retained without the following shadow count");
    assert.deepEqual(scene.terrain, [[...terrainWords.subarray(0, 12), ...terrainWords.subarray(16, 19)]],
        "Terrain identity excludes texture/height cache-layer indices");
    cameraData.fill(0); terrainData.fill(0);
    assert.ok(scene.uniform[0].some(value => value !== 0), "Untimed capture owns its copied values");
    state.beginSceneCapture();
    assert.throws(() => state.finishSceneCapture(), /did not upload/);
    const input = {x: 600, y: 400, deltaX: 12, deltaY: 1, modifiers: 0};
    f.tick(1); state.begin("step");
    assert.throws(() => state.begin("step"), /already active/);
    assert.throws(() => state.beginSceneCapture(), /outside measurement/);
    assert.equal(state.sceneCapture, null, "Payload copying is disabled throughout timing");
    f.tick(2); f.wheel(input, false);
    assert.equal(state.active.inputs.length, 0, "Synthetic DOM input is not accepted as native work");
    f.tick(3); f.wheel(input);
    device.queue.writeBuffer(buffer, 0, new Uint16Array(10), 2, 4);
    device.queue.writeBuffer(unrelated, 0, new Uint8Array(100));
    device.queue.writeTexture({texture}, new Uint8Array(256), {bytesPerRow: 256}, {width: 3, height: 2});
    assert.equal(state.active.bufferUploadBytes, 8, "Typed-array write offsets/lengths are elements");
    assert.equal(state.active.textureUploadBytes, 48, "Texture texel payload excludes row padding");
    f.tick(4); device.queue.submit([other]);
    assert.equal(state.mapSubmits, 0, "Uno's unrelated compositor submission is excluded");
    f.tick(5);
    function* sequence() { yield command; }
    assert.equal(device.queue.submit(sequence()), "submitted", "Native return values are preserved");
    assert.deepEqual(f.calls.at(-1).sequence, [command], "Generator submissions are forwarded exactly once");
    assert.equal(state.active.pending, 1);
    assert.equal(state.ready(), false, "Submission does not establish completion");
    f.tick(6); f.sandbox.PixelAtlasInput.reportWebGpu({frames: 1, frameMilliseconds: 2,
        submissionMilliseconds: 1, uploadBytes: 56, preparationMilliseconds: .75, overlayMilliseconds: .25});
    f.tick(20); f.queueWaiters.shift()();
    await Promise.resolve(); await Promise.resolve(); await Promise.resolve();
    assert.equal(state.ready(), false, "Queue completion still requires the quiet boundary");
    f.tick(400); assert.equal(state.ready(), true);
    const raw = JSON.parse(JSON.stringify(state.finish()));
    assert.equal(state.unchangedAndQuietSince(state.mapSubmits), true, "The fenced submission version is quiet");
    assert.equal(state.unchangedAndQuietSince(null), false, "A missing fence version cannot establish readiness");
    assert.equal(raw.submissions[0].queueCompletedAt, 20);
    const checked = checkStep(raw, input);
    assert.equal(checked.inputToFirstSubmitMilliseconds, 2);
    assert.equal(checked.inputToFirstQueueCompletionMilliseconds, 17);
    assert.equal(checked.renderCpuMilliseconds, 2);
    assert.equal(checked.preparationMilliseconds, .75);
    assert.equal(checked.phaseTimingsAvailable, true);
    assert.throws(() => state.finish(), /No active/);
    const clone = value => JSON.parse(JSON.stringify(value));
    for (const mutate of [
        value => { value.inputs[0].deltaX++; },
        value => { value.inputs[0].deltaMode = 1; },
        value => { delete value.submissions[0].queueCompletedAt; },
        value => { value.submissions[0].submittedAt = 0; },
        value => { value.mapSubmits++; },
        value => { value.frames[0].frameMilliseconds = null; }
    ]) { const broken = clone(raw); mutate(broken); assert.throws(() => checkStep(broken, input)); }
    const baseline = clone(raw);
    delete baseline.frames[0].preparationMilliseconds; delete baseline.frames[0].overlayMilliseconds;
    const legacy = checkStep(baseline, input);
    assert.equal(legacy.preparationMilliseconds, null, "Missing baseline phases are not invented as measured zero");
    assert.equal(legacy.phaseTimingsAvailable, false);
    assert.equal(summarizeSteps([legacy]).preparationMilliseconds, undefined);
    state.begin("throughput");
    const waits = f.queueWaiters.length;
    f.tick(500); device.queue.submit([command]);
    assert.equal(f.queueWaiters.length, waits, "Throughput adds no per-frame fence/backpressure");
    const throughput = state.finish();
    assert.equal(throughput.mapSubmits, 1);
    assert.equal(throughput.submissions[0].queueCompletedAt, undefined);
    assert.equal(f.queueWaiters.length, waits + 1, "Only the end boundary adds a second queue-prefix observation");
    f.tick(800);
    for (const resolve of f.queueWaiters.splice(0)) resolve();
    const boundaries = await state.throughputBoundaries();
    assert.equal(boundaries.startQueuePrefixMilliseconds, 400);
    assert.equal(boundaries.endQueueDrainMilliseconds, 300);
    assert.deepEqual(JSON.parse(JSON.stringify(state.snapshot().errors)), []);
    const fencedVersion = state.mapSubmits;
    f.tick(805); device.queue.submit([command]);
    f.tick(1150);
    assert.equal(state.unchangedAndQuietSince(fencedVersion), false,
        "A later submission aged 345 ms still invalidates the previous queue fence");
    assert.equal(state.unchangedAndQuietSince(state.mapSubmits), true,
        "The final recheck requires the current submission version");

    const deferred = fixture(), deferredDevice = await deferred.adapter.requestDevice();
    const deferredState = deferred.state;
    const deferredCommand = deferredDevice.createCommandEncoder({label: "PixelMap frame"}).finish();
    const reportFrame = frames => deferred.sandbox.PixelAtlasInput.reportWebGpu({frames,
        frameMilliseconds: 2, submissionMilliseconds: 1, uploadBytes: 0});
    const completeDeferred = async at => {
        deferred.tick(at);
        for (const resolve of deferred.queueWaiters.splice(0)) resolve();
        await Promise.resolve(); await Promise.resolve(); await Promise.resolve();
    };
    deferredState.begin("step");
    deferred.tick(1); deferred.wheel(input);
    deferred.tick(2); deferredDevice.queue.submit([deferredCommand]);
    deferred.tick(3); reportFrame(1);
    await completeDeferred(2000);
    assert.equal(deferredState.finishStepIfReady(), null,
        "Slow GPU work cannot consume the quiet interval before its completion callback");
    deferred.tick(2350); assert.equal(deferredState.ready(), true);
    deferred.tick(2351); deferredDevice.queue.submit([deferredCommand]);
    deferred.tick(2352); reportFrame(2);
    assert.equal(deferredState.finishStepIfReady(), null,
        "An intervening submission invalidates an older readiness observation instead of throwing at finish");
    await completeDeferred(2400);
    deferred.tick(2749); assert.equal(deferredState.finishStepIfReady(), null);
    deferred.tick(2750);
    const finished = JSON.parse(JSON.stringify(deferredState.finishStepIfReady()));
    assert.equal(checkStep(finished, input).mapSubmits, 2);
    assert.equal(deferredState.active, null, "Atomic readiness closes the timed window in the same task");
    deferred.tick(2751); deferredDevice.queue.submit([deferredCommand]);
    assert.equal(finished.mapSubmits, 2, "Later work cannot mutate the completed capture returned to automation");

    const invalidLayout = fixture(), invalidDevice = await invalidLayout.adapter.requestDevice();
    invalidDevice.createBuffer({label: "PixelMap buffer Uniform, CopyDst", size: 513 * 512, usage: 64 | 8});
    invalidDevice.createBuffer({label: "PixelMap buffer Vertex, CopyDst", size: 16, usage: 32 | 8});
    invalidDevice.createBuffer({label: "PixelMap buffer Vertex, CopyDst", size: 64 * 80, usage: 32 | 8});
    assert.equal(invalidLayout.state.sceneBufferCounts.terrain, 0,
        "An unexpected first allocation cannot be masked by a later matching mesh size");
    assert.match(invalidLayout.state.errors[0], /Unexpected first map Vertex allocation/);
    assert.throws(() => invalidLayout.state.beginSceneCapture(), /Expected exactly one/);

    checkHealth(JSON.parse(JSON.stringify(state.snapshot())));
    f.sandbox.PixelAtlasInput.failWebGpu("Managed throughput draw failed");
    const unhealthy = JSON.parse(JSON.stringify(state.snapshot()));
    assert.equal(unhealthy.diagnostic.failure, null, "The previous successful frame remains available for diagnosis");
    assert.equal(unhealthy.liveFailure, "Managed throughput draw failed", "A later managed failure is observed without a new frame");
    assert.throws(() => checkHealth(unhealthy), /live managed renderer remains healthy/,
        "Final health fails even when earlier submissions/telemetry would pass throughput checks");

    const trace = makeTrace({motion: "pan"}, {x: 0, y: 60, width: 1200, height: 699}, 2);
    assert.equal(trace.length, 8);
    assert.equal(trace.reduce((sum, item) => sum + item.deltaX, 0), 0);
    assert.equal(trace.reduce((sum, item) => sum + item.deltaY, 0), 0);
    assert.ok(trace.every(item => Number.isInteger(item.x) && Number.isInteger(item.y)), "Trusted wheel coordinates use integral CSS pixels");
    assert.throws(() => makeTrace({motion: "pan"}, {x: 0, y: 0, width: 1, height: 1}, 0));
    const oldBounds = {x: 0, y: 59, width: 1200, height: 700};
    const newBounds = {x: 0, y: 0, width: 1200, height: 700};
    assert.deepEqual(mapRelativeTrace(makeTrace({motion: "orbit"}, oldBounds, 2), oldBounds),
        mapRelativeTrace(makeTrace({motion: "orbit"}, newBounds, 2), newBounds),
        "Matching map-relative input preserves its anchor despite redesigned UI origins");
    const declared = {revisions: {baseline: "d014060af60b0b961b0b5e52f39359525da2435b"}};
    assert.equal(intentionalPixelChange(declared, {style: "Pixel"}), true);
    assert.equal(intentionalPixelChange(declared, {style: "Normal"}), false);
    assert.equal(intentionalPixelChange({revisions: {baseline: "0".repeat(40)}}, {style: "Pixel"}), false,
        "Future baselines retain strict Pixel image parity");
    for (const chromeHeight of [0, 117]) {
        let size = {width: 1200, height: 800}, resizes = 0;
        const map = {boundingBox: async () => ({x: 0, y: chromeHeight / 3, width: size.width, height: size.height - chromeHeight})};
        const page = {evaluate: async () => {}, viewportSize: () => size,
            setViewportSize: async next => { size = next; resizes++; }};
        const bounds = await fitMapExtent(page, map);
        assert.equal(bounds.width, 1200); assert.equal(bounds.height, 700);
        assert.equal(resizes, 1, "Canvas calibration uses the public viewport once");
        assert.equal(size.height, 700 + chromeHeight);
    }
    await assert.rejects(fitMapExtent({evaluate: async () => {}, viewportSize: () => ({width: 1200, height: 800}),
        setViewportSize: async () => {}}, {boundingBox: async () => ({x: 0, y: 0, width: 1200, height: 600})}),
        /could not reach/, "A canvas that ignores resize fails instead of comparing unlike extents");
    assert.deepEqual(distribution([4, 1, 3, 2]), {samples: 4, minimum: 1, median: 3, p95: 4, maximum: 4, mean: 2.5});
    const left = {width: 2, height: 1, data: Uint8Array.from([0, 0, 0, 255, 10, 10, 10, 255])};
    const right = {width: 2, height: 1, data: Uint8Array.from([0, 0, 0, 255, 14, 10, 10, 255])};
    const difference = imageDifference(left, right);
    assert.equal(difference.changedPixelFraction, .5);
    assert.equal(difference.significantPixelFraction, .5);
    assert.equal(difference.maximumChannelError, 4);
    assert.equal(difference.meanAbsoluteChannelError, 4 / 6);
    assert.throws(() => imageDifference(left, {...right, width: 1}));
    console.log("WebGPU performance observer/input/queue/phase/image contracts passed.");
}
main().catch(error => { console.error(error); process.exitCode = 1; });
