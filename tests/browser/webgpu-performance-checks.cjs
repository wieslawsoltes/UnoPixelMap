const assert = require("node:assert/strict");
const vm = require("node:vm");
const {installPerformanceProbe} = require("./webgpu-performance-probe.cjs");
const {distribution, makeTrace, checkStep, imageDifference, summarizeSteps} = require("./webgpu-performance.cjs");

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
    sandbox.PixelAtlasInput = {reportWebGpu: frame => Object.assign(sandbox.PixelAtlasWebGpu, frame)};
    const state = sandbox.__pixelAtlasWebGpuPerformance;
    return {sandbox, state, events, frames, calls, queueWaiters,
        tick: value => { clock = value; }, adapter: new GPUAdapter(),
        wheel: (input, trusted = true) => events.wheel({isTrusted: trusted, clientX: input.x, clientY: input.y,
            deltaX: input.deltaX, deltaY: input.deltaY, deltaMode: 0,
            altKey: Boolean(input.modifiers & 1), ctrlKey: Boolean(input.modifiers & 2),
            metaKey: Boolean(input.modifiers & 4), shiftKey: Boolean(input.modifiers & 8)})};
}
async function main() {
    const f = fixture(), device = await f.adapter.requestDevice(), state = f.state;
    const mapEncoder = device.createCommandEncoder({label: "PixelMap frame"});
    const command = mapEncoder.finish();
    const other = device.createCommandEncoder({label: "Uno UI"}).finish();
    const buffer = device.createBuffer({label: "PixelMap buffer"});
    const unrelated = device.createBuffer({label: "Uno UI"});
    const texture = device.createTexture({label: "PixelMap heights", format: "rg32float"});
    const uniform = device.createBuffer({label: "PixelMap buffer Uniform, CopyDst", size: 513 * 512, usage: 64 | 8});
    const terrain = device.createBuffer({label: "PixelMap buffer Vertex, CopyDst", size: 64 * 80, usage: 32 | 8});
    state.beginSceneCapture();
    assert.throws(() => state.begin("step"), /cannot overlap/);
    const cameraData = Float32Array.from({length: 132}, (_, index) => index / 10);
    const terrainData = Float32Array.from({length: 20}, (_, index) => index / 5);
    device.queue.writeBuffer(uniform, 0, cameraData, 2, 128);
    device.queue.writeBuffer(terrain, 0, terrainData);
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
