const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const {installPerformanceProbe} = require("./webgpu-performance-probe.cjs");
const {distribution, makeTrace, checkStep, checkHealth, imageDifference, summarizeSteps} = require("./webgpu-performance.cjs");

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
    // Run the real menu and focus helpers without installing or launching a
    // browser. Only explore's unused browser/image imports are substituted.
    const uiModule = {exports: {}};
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, "explore.cjs"), "utf8"), {
        module: uiModule, URL, Promise, setTimeout, clearTimeout, process: {argv: [], env: {}},
        require: name => ["playwright", "pngjs", "./gpu.cjs"].includes(name) ? {} : require(name)
    }, {filename: "explore.cjs"});
    const performanceModule = {exports: {}};
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, "webgpu-performance.cjs"), "utf8"), {
        module: performanceModule,
        require: name => name === "./explore.cjs" ? uiModule.exports : require(name)
    }, {filename: "webgpu-performance.cjs"});
    let frames = 0, focusedAt = 0, disposed = 0, rootOpen = false, submenuOpen = false;
    const keys = [], document = {activeElement: null};
    const sandbox = vm.createContext({document, Promise,
        requestAnimationFrame: callback => setImmediate(() => { frames++; callback(); })});
    const invoke = (callback, argument) => vm.runInContext("(" + callback.toString() + ")", sandbox)(argument);
    const targets = new Map();
    for (const name of ["Map appearance", "Rendering quality", "High quality"]) {
        const element = {name, isConnected: true, dispose: async () => { disposed++; }};
        targets.set(name, {or() { return this; },
            waitFor: async ({state}) => {
                const visible = name === "Map appearance" || (name === "Rendering quality" ? rootOpen : submenuOpen);
                assert.equal(visible, state === "visible", name + " has the requested popup state");
            },
            boundingBox: async () => ({x: 10, y: 10, width: 80, height: 30}),
            focus: async () => { document.activeElement = element; focusedAt = frames; },
            elementHandle: async () => element});
    }
    const page = {
        getByRole: (_, {name}) => targets.get(name),
        mouse: {click: async () => { rootOpen = true; }, move: async () => {}},
        waitForFunction: async (callback, argument) => assert.equal(invoke(callback, argument), true),
        evaluate: async (callback, argument) => invoke(callback, argument),
        keyboard: {press: async key => {
            assert.ok(frames - focusedAt >= 2, "Native menu input cannot precede managed focus settling");
            keys.push(key);
            assert.equal(document.activeElement.name, key === "ArrowRight" ? "Rendering quality" : "High quality");
            if (key === "ArrowRight") submenuOpen = true;
            else { assert.equal(key, "Enter"); rootOpen = submenuOpen = false; }
        }}
    };
    await performanceModule.exports.menu(page, "High quality", "Rendering quality");
    assert.deepEqual(keys, ["ArrowRight", "Enter"], "One native activation follows each completed focus barrier");
    assert.equal(disposed, 2, "Both focus barriers release their target handles");
}
async function main() {
    await menuFocusChecks();
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
