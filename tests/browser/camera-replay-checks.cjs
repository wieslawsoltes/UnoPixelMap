const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const {installCameraReplayProbe} = require("./camera-replay-probe.cjs");
const {sha256, checkStep, compareReplays} = require("./camera-replay-contract.cjs");
const {makeTrace, scenarios} = require("./camera-replay.cjs");

function observerFixture() {
    let time = 0;
    const listeners = {}, calls = [], frames = [];
    class WebGLRenderingContext {
        constructor(id) {
            this.canvas = {id}; this.ARRAY_BUFFER = 34962;
            this.ALREADY_SIGNALED = 37146; this.CONDITION_SATISFIED = 37148;
        }
    }
    for (const name of ["shaderSource", "attachShader", "getUniformLocation", "useProgram", "viewport",
        "uniform1f", "uniform1i", "uniform2f", "uniform2i", "uniform3f", "uniform4f", "bufferSubData",
        "drawArrays", "drawElements", "drawArraysInstanced", "drawElementsInstanced", "fenceSync",
        "clientWaitSync", "deleteSync"])
        WebGLRenderingContext.prototype[name] = function (...args) {
            calls.push({gl: this, name, args});
            if (name === "getUniformLocation") return args[1] === "missing" ? null : {owner: args[0], name: args[1]};
            if (name === "fenceSync") return {};
            if (name === "clientWaitSync") return args[1];
        };
    class WebGL2RenderingContext extends WebGLRenderingContext {}
    const sandbox = {WeakMap, Set, Map, Array, Object, ArrayBuffer, DataView, Error,
        performance: {now: () => ++time}, WebGLRenderingContext, WebGL2RenderingContext,
        requestAnimationFrame: callback => frames.push(callback),
        addEventListener: (name, callback) => { listeners[name] = callback; }};
    vm.runInNewContext("(" + installCameraReplayProbe.toString() + ")()", sandbox);
    const gl = new WebGL2RenderingContext("uno-canvas");
    const state = sandbox.__pixelAtlasCameraReplay;
    const program = (kind, owner = gl) => {
        const shader = {}, result = {};
        owner.shaderSource(shader, kind === "resolve" ? "uniform bool uArtworkOnly; uniform float uPhotoOpacity;"
            : "uniform vec4 uScreenX; uniform vec3 uGround;" + (kind === "terrain" ? " in vec4 aScreenX;" : ""));
        owner.attachShader(result, shader);
        owner.useProgram(result);
        return result;
    };
    const uniform = (program, name, data, owner = gl) =>
        owner["uniform" + data.length + "f"](owner.getUniformLocation(program, name), ...data);
    const wheel = input => listeners.wheel({isTrusted: true, deltaMode: 0, clientX: input.x, clientY: input.y,
        deltaX: input.deltaX, deltaY: input.deltaY, altKey: Boolean(input.modifiers & 1),
        ctrlKey: Boolean(input.modifiers & 2), metaKey: Boolean(input.modifiers & 4), shiftKey: Boolean(input.modifiers & 8)});
    return {gl, state, program, uniform, wheel, calls, WebGL2RenderingContext,
        advance: milliseconds => { time += milliseconds; },
        animationFrame: () => { for (const callback of frames.splice(0)) callback(); }};
}

const fixture = observerFixture();
const {gl, state, program, uniform, wheel, calls} = fixture;
const terrain = program("terrain");
const geometry = program("geometry");
const resolve = program("resolve");
const ignored = {};
for (const object of [terrain, geometry]) {
    uniform(object, "uArtSize", [600, 332]);
    uniform(object, "uGround", [15.25, 1, 100]);
    uniform(object, "uDepth", [1, -.16]);
    uniform(object, "uPerspective", [1]);
}
for (const name of ["uScreenX", "uScreenY", "uScreenW"]) uniform(geometry, name, [1, 2, 3, 4]);
uniform(resolve, "uArtSize", [600, 332]);
uniform(resolve, "uPixelSize", [2]);
uniform(resolve, "uLogicalSize", [1200, 664]);

// An unrelated context uses exactly the same uniform names with different data.
// Its state must not contaminate the map context, even when programs are cached.
const other = new fixture.WebGL2RenderingContext("other-canvas");
const otherProgram = program("geometry", other);
uniform(otherProgram, "uGround", [99, 88, 77], other);
gl.useProgram(ignored);
gl.uniform1f(null, 42);
const input = {x: 600, y: 400, deltaX: -10, deltaY: 0, modifiers: 8};
state.begin();
assert.throws(() => state.begin(), /already active/);
wheel(input);
gl.viewport(0, 0, 600, 332);
gl.useProgram(terrain);
const values = Float32Array.from({length: 35}, (_, index) => index + .25);
const sourceCopy = values.slice();
gl.bufferSubData(gl.ARRAY_BUFFER, 0, values, 3, 28);
const priorDrawCalls = calls.filter(call => call.name === "drawElementsInstanced").length;
gl.drawElementsInstanced(4, 1536, 5123, 0, 2);
assert.equal(calls.filter(call => call.name === "drawElementsInstanced").length - priorDrawCalls, 1,
    "Inherited WebGL2 entry points forward each draw exactly once");
values.fill(999);
gl.useProgram(geometry);
gl.drawElements(4, 120, 5125, 0);
gl.useProgram(resolve);
gl.drawArrays(4, 0, 3);
const fence = gl.fenceSync(37143, 0);
assert.equal(state.ready(), false, "A submission alone is not a completion");
gl.clientWaitSync({}, gl.ALREADY_SIGNALED, 0);
assert.equal(state.ready(), false, "An unrelated fence cannot complete this map frame");
gl.clientWaitSync(fence, 37147, 0);
assert.equal(state.ready(), false, "A timed-out fence cannot complete this map frame");
gl.clientWaitSync(fence, gl.CONDITION_SATISFIED, 0);
assert.equal(state.ready(), false, "The tile-refresh quiet barrier follows a fast fence");
fixture.advance(300);
assert.equal(state.ready(), true);
assert.equal(state.settled(), false, "The settled predicate returns a Boolean, never a truthy Promise");
fixture.animationFrame();
assert.equal(state.settled(), false, "One animation frame does not complete the compositor barrier");
fixture.animationFrame();
assert.equal(state.settled(), true);
const first = JSON.parse(JSON.stringify(state.finish()));
assert.throws(() => state.finish(), /No camera replay/);
assert.deepEqual(first.resolves[0].draws[0].tileProjections,
    [Array.from(sourceCopy.slice(3, 15)), Array.from(sourceCopy.slice(17, 29))],
    "Terrain transforms honor source offsets, copy only the submitted range and omit cache slots");
assert.deepEqual(first.resolves[0].draws[1].uniforms.uGround, [15.25, 1, 100],
    "Uniform values persist across inactive frames without leaking between contexts");
const step = checkStep(JSON.parse(JSON.stringify(first)), input, 0);
assert.ok(step.inputToFirstSubmissionMs > 0 && step.inputToSettledFenceSignalMs > step.inputToFirstSubmissionMs);

// Uniform caches may omit every setter on the next frame. The observer must
// retain those values, but must never retain previous-frame draws or fences.
state.begin();
wheel(input);
gl.useProgram(geometry);
gl.drawElements(4, 120, 5125, 0);
gl.useProgram(resolve);
gl.drawArrays(4, 0, 3);
assert.equal(state.ready(), false);
gl.clientWaitSync(fence, gl.ALREADY_SIGNALED, 0);
assert.equal(state.ready(), false, "An earlier frame's fence cannot satisfy the next step");
const secondFence = gl.fenceSync(37143, 0);
gl.clientWaitSync(secondFence, gl.ALREADY_SIGNALED, 0);
fixture.advance(300);
assert.equal(state.settled(), false);
fixture.animationFrame();
fixture.animationFrame();
assert.equal(state.settled(), true);
const second = JSON.parse(JSON.stringify(state.finish()));
assert.equal(second.resolves[0].draws.length, 1, "Draws cannot leak between frames");
assert.deepEqual(second.resolves[0].draws[0].uniforms, step.projection[0].draws[1].uniforms);

// A timer may reorder resident tiles after the first camera frame. Both map
// submissions are retained, and the first (or an unrelated) signaled fence
// cannot satisfy the second frame. No sorting or frame deduplication is allowed.
state.begin();
wheel(input);
const submit = count => {
    gl.useProgram(geometry);
    gl.drawElements(4, count, 5125, 0);
    gl.useProgram(resolve);
    gl.drawArrays(4, 0, 3);
    return gl.fenceSync(37143, 0);
};
const firstOfPair = submit(120);
gl.clientWaitSync(firstOfPair, gl.ALREADY_SIGNALED, 0);
fixture.advance(300);
assert.equal(state.settled(), false);
fixture.animationFrame();
const secondOfPair = submit(240);
fixture.animationFrame();
assert.equal(state.settled(), false, "A late second resolve invalidates an in-progress animation barrier");
assert.equal(state.ready(), false, "Every submitted frame needs its own signal");
gl.clientWaitSync(secondFence, gl.ALREADY_SIGNALED, 0);
assert.equal(state.ready(), false, "An earlier step's fence cannot finish an interleaved sequence");
gl.clientWaitSync(secondOfPair, gl.ALREADY_SIGNALED, 0);
fixture.advance(300);
assert.equal(state.ready(), true);
assert.equal(state.settled(), false);
fixture.animationFrame();
fixture.animationFrame();
assert.equal(state.settled(), true);
const pair = JSON.parse(JSON.stringify(state.finish()));
const pairStep = checkStep(pair, input, 0);
assert.deepEqual(pairStep.projection.map(frame => frame.draws[0].count), [120, 240]);
assert.ok(pairStep.inputToSettledFenceSignalMs > pairStep.inputToFirstFenceSignalMs,
    "First-frame and settled-last-fence latency remain distinct");

const clone = value => JSON.parse(JSON.stringify(value));
for (const mutate of [
    raw => { raw.inputs = []; }, raw => { raw.inputs.push(clone(raw.inputs[0])); },
    raw => { raw.inputs[0].trusted = false; }, raw => { raw.inputs[0].deltaX++; },
    raw => { raw.resolves = []; }, raw => { raw.resolves = Array(33).fill(clone(raw.resolves[0])); },
    raw => { raw.resolves[0].signaledAt = null; }, raw => { raw.resolves[0].compositorContext = false; },
    raw => { raw.quietForMs = 249; }, raw => { raw.lastResolveAt++; },
    raw => { raw.compositorBarrierResolves = 0; },
    raw => { raw.resolves[0].draws[0].tileProjections.pop(); },
    raw => { raw.resolves[0].draws[1].uniforms.uScreenX = [1, 2]; }
]) {
    const changed = clone(first);
    mutate(changed);
    assert.throws(() => checkStep(changed, input, 0), "Incomplete or mismatched render steps fail closed");
}

const report = {schema: 2, protocol: "native-wheel-resolve-fence-v2", viewport: {width: 1200, height: 780},
    deviceScaleFactor: 1, browser: "test-browser", browserArgs: [], cpu: "Fixture CPU", availableParallelism: 3,
    harness: {observer: "same"}, revision: "baseline",
    cases: [{name: "map", scenario: {}, map: {width: 1200, height: 664}, trace: [input], traceSha256: sha256([input]),
        steps: [step], summary: {steps: 1}}]};
const candidate = clone(report);
candidate.revision = "candidate";
assert.equal(compareReplays(report, candidate).comparisons[0].exactProjectionSequence, true);
const pairReport = clone(report);
pairReport.cases[0].steps = [pairStep];
assert.equal(compareReplays(pairReport, clone(pairReport)).comparisons[0].submissions, 2);
for (const mutate of [
    frames => frames.pop(), frames => frames.push(clone(frames[0])), frames => frames.reverse()
]) {
    const changed = clone(pairReport);
    const changedStep = changed.cases[0].steps[0];
    mutate(changedStep.projection);
    changedStep.projectionSha256 = sha256(changedStep.projection);
    assert.throws(() => compareReplays(pairReport, changed),
        "Missing, extra or reordered frames within an input fail exact comparison");
}
for (const mutate of [
    value => { value.cases = []; },
    value => { value.cases[0].trace = []; value.cases[0].steps = []; },
    value => { value.cases[0].trace = Array(4097).fill(input); value.cases[0].steps = Array(4097).fill(step); },
    value => { value.cases[0].steps[0].projection = []; },
    value => { value.cases[0].steps[0].projection = Array(33).fill(step.projection[0]); },
    value => { delete value.cpu; }, value => { value.availableParallelism = 0; }
]) {
    const invalid = clone(report);
    mutate(invalid);
    assert.throws(() => compareReplays(invalid, clone(invalid)),
        "Two identically incomplete or unbounded reports cannot pass comparison");
}
for (const mutate of [
    value => { value.cases[0].steps = []; }, value => { value.cases[0].steps.push(clone(step)); },
    value => { value.harness.observer = "different"; }, value => { value.deviceScaleFactor = 2; },
    value => { value.cpu = "Different CPU"; }, value => { value.availableParallelism = 2; },
    value => { value.cases[0].steps[0].projection[0].draws[0].tileProjections[0][0] += 1e-8;
        value.cases[0].steps[0].projectionSha256 = sha256(value.cases[0].steps[0].projection); },
    value => { value.cases[0].steps[0].projectionSha256 = "forged"; },
    value => { value.cases[0].trace[0].deltaX++; }
]) {
    const changed = clone(candidate);
    mutate(changed);
    assert.throws(() => compareReplays(report, changed), "Comparison rejects missing/extra frames, any pose drift and protocol differences");
}
for (const scenario of scenarios) {
    const trace = makeTrace(scenario, {x: 0, y: 80, width: 1200, height: 664});
    assert.equal(trace.length, 32);
    assert.equal(sha256(trace), sha256(makeTrace(scenario, {x: 0, y: 80, width: 1200, height: 664})));
    assert.ok(trace.every(event => event.x === 600 && event.y === 412 && (event.deltaX !== 0 || event.deltaY !== 0)));
    const fractional = makeTrace(scenario, {x: .25, y: 59, width: 1200, height: 665});
    assert.ok(fractional.every(event => event.x === 600 && event.y === 391),
        "Odd or fractional map geometry still declares the integral native wheel coordinates");
}

// Execute the actual production adapter. Native CDP input can carry a legacy
// detent even for tiny pixel deltas; the fence barrier also makes each burst
// fresh. A diagonal trace must still take the immediate pan/look route.
{
    const listeners = new Map();
    class Element { closest() { return null; } }
    const canvas = new Element();
    const document = {addEventListener: (name, callback) => listeners.set(name, callback),
        documentElement: {clientWidth: 1200, clientHeight: 780}};
    const adapterContext = {document, window: {addEventListener() {}}, Element, AbortController,
        WheelEvent: {DOM_DELTA_PIXEL: 0, DOM_DELTA_LINE: 1, DOM_DELTA_PAGE: 2}};
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../../src/PixelMap.Controls/Browser/map-input.js"), "utf8"), adapterContext);
    const adapter = adapterContext.PixelAtlasInput;
    adapter.attach(1, () => {}, () => {});
    let timestamp = 0;
    const classify = input => {
        const event = {target: canvas, eventPhase: 1, deltaMode: 0, deltaX: input.deltaX, deltaY: input.deltaY,
            clientX: input.x, clientY: input.y, timeStamp: timestamp += 1000, wheelDeltaY: 120,
            altKey: Boolean(input.modifiers & 1), ctrlKey: Boolean(input.modifiers & 2),
            metaKey: Boolean(input.modifiers & 4), shiftKey: Boolean(input.modifiers & 8), preventDefault() {}};
        listeners.get("wheel")(event);
        const sample = [...adapter.takeWheel(1)];
        event.eventPhase = 0;
        return sample;
    };
    assert.equal(classify({x: 600, y: 391, deltaX: 0, deltaY: 12, modifiers: 0})[0], 1,
        "The fixture reproduces the observed CDP pure-vertical detent");
    for (const scenario of scenarios)
        for (const input of makeTrace(scenario, {x: 0, y: 60, width: 1200, height: 663}))
            assert.deepEqual(classify(input), [0, input.deltaX, input.deltaY,
                input.modifiers & 8 ? 1 : 0, input.modifiers & 1 ? 1 : 0],
                "Every barrier-paced replay event reaches the immediate trackpad route");
}

async function focusChecks() {
    // Execute the actual CommonJS module and use its exported helpers. Browser
    // and image dependencies are unused here; keep these Node checks runnable
    // before npm/browser installation in the production build job.
    const uiModule = {exports: {}};
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, "explore.cjs"), "utf8"), {
        module: uiModule, URL, Promise, setTimeout, clearTimeout,
        process: {argv: [], env: {}},
        require: name => ["playwright", "pngjs", "./gpu.cjs"].includes(name) ? {} : require(name)
    }, {filename: "explore.cjs"});
    const {action, waitForSettledFocus} = uiModule.exports;
    let acquired = 0, disposed = 0, frames = 0, stableFrames = 0, losses = 0, reacquisitions = 0, keys = 0;
    const target = {isConnected: true, dispose: async () => { disposed++; }};
    const document = {activeElement: null};
    const sandbox = vm.createContext({document, Promise,
        requestAnimationFrame: callback => setImmediate(() => {
            // A closing popup reclaims focus during the first attempted barrier.
            if (++frames === 1) { document.activeElement = null; losses++; }
            stableFrames = document.activeElement === target ? stableFrames + 1 : 0;
            callback();
        })});
    const invoke = (callback, argument) => vm.runInContext("(" + callback.toString() + ")", sandbox)(argument);
    const locator = {or() { return this; }, waitFor: async () => {},
        focus: async options => {
            if (options) assert.ok(options.timeout > 0 && options.timeout <= 25000);
            if (losses && document.activeElement !== target) reacquisitions++;
            document.activeElement = target;
        },
        elementHandle: async () => { acquired++; return target; },
        getAttribute: async () => "button"};
    const page = {
        getByRole: () => locator,
        evaluate: async (callback, argument) => invoke(callback, argument),
        keyboard: {press: async key => {
            keys++;
            assert.equal(key, "Enter");
            assert.equal(document.activeElement, target);
            assert.ok(stableFrames >= 2, "Keyboard activation waits for two actual stable frames after focus is reacquired");
        }}
    };
    await action(page, "Fixture button");
    assert.equal(losses, 1, "The closing popup actually steals focus during the animation barrier");
    assert.ok(reacquisitions > 0, "The real helper reacquires focus; the fixture does not restore it on the helper's behalf");
    assert.equal(keys, 1, "The exported action issues exactly one native keyboard activation after recovery");
    assert.equal(disposed, acquired, "Every acquired handle is released after focus recovery");

    const failure = new Error("The browser detached the keyboard target");
    const acquiredBeforeFailure = acquired;
    const failing = {...page, evaluate: async () => { throw failure; }};
    await assert.rejects(waitForSettledFocus(failing, locator, 50), error => error === failure);
    assert.ok(acquired > acquiredBeforeFailure, "The failed browser evaluation occurs with an acquired target");
    assert.equal(disposed, acquired, "The target handle is disposed when browser evaluation fails");

    // A hidden or stalled page might never produce another animation frame.
    // The overall deadline must still release the handle and reject the wait.
    const acquiredBeforeStall = acquired;
    const stalled = {...page, evaluate: async () => new Promise(() => {})};
    await assert.rejects(waitForSettledFocus(stalled, locator, 20), /did not keep focus/);
    assert.ok(acquired > acquiredBeforeStall, "The stalled animation barrier owns a target handle");
    assert.equal(disposed, acquired, "The target handle is disposed when the animation barrier times out");
    assert.equal(keys, 1, "Failed focus waits cannot issue another keyboard activation");
}

focusChecks().then(() => {
    console.log("Camera replay checks passed: native input contract, projection state, terrain-instance ranges, context isolation, complete fence/animation barriers, exact sequence comparison and focus-loss recovery.");
}).catch(error => { console.error(error); process.exitCode = 1; });
