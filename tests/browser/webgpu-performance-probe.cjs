// Low-overhead WebGPU timing observation. This intentionally does not wrap draws,
// render passes, bind groups or shader execution. The correctness suite owns the
// full render-graph audit; its instrumentation must not contaminate throughput.
function installPerformanceProbe() {
    const state = globalThis.__pixelAtlasWebGpuPerformance = {
        devices: [], adapters: [], compilation: [], errors: [], losses: [], active: null,
        mapSubmits: 0, lastMapSubmitAt: -Infinity, lastDiagnosticAt: -Infinity, lastDiagnostic: null,
        sceneCapture: null, sceneBufferCounts: {uniform: 0, terrain: 0}
    };
    const encoders = new WeakSet(), commands = new WeakSet(), buffers = new WeakSet(), textures = new WeakMap();
    const sceneBuffers = new WeakMap();
    const uniformUsage = 0x40, vertexUsage = 0x20;
    const sceneUniformBytes = 513 * 512, terrainInstanceBytes = 64 * 80;
    const map = label => /^PixelMap[ /]/.test(label || "");
    const now = () => performance.now();
    const queuePrefix = () => {
        const startedAt = now();
        return Promise.all(state.devices.map(device => device.queue.onSubmittedWorkDone()))
            .then(() => now() - startedAt);
    };
    const bounded = (array, value) => {
        if (array.length >= 20000) throw new Error("Performance observation exceeded its bounded sample count");
        array.push(value);
    };
    function wrap(prototype, name, action) {
        if (!prototype?.[name]) return;
        const original = prototype[name];
        prototype[name] = function (...args) { return action.call(this, original, args); };
    }
    wrap(globalThis.GPUAdapter?.prototype, "requestDevice", async function (original, args) {
        const device = await Reflect.apply(original, this, args);
        const info = this.info;
        state.devices.push(device);
        state.adapters.push({vendor: info?.vendor, architecture: info?.architecture, device: info?.device,
            description: info?.description, isFallbackAdapter: info?.isFallbackAdapter, features: [...device.features]});
        device.addEventListener("uncapturederror", event => state.errors.push(event.error.message));
        device.lost.then(info => state.losses.push({reason: info.reason, message: info.message}));
        return device;
    });
    wrap(globalThis.GPUDevice?.prototype, "createShaderModule", function (original, args) {
        const result = Reflect.apply(original, this, args);
        if (map(args[0]?.label)) state.compilation.push(result.getCompilationInfo().then(info => {
            for (const item of info.messages) if (item.type === "error") state.errors.push(item.message);
        }).catch(error => state.errors.push(String(error))));
        return result;
    });
    wrap(globalThis.GPUDevice?.prototype, "createCommandEncoder", function (original, args) {
        const result = Reflect.apply(original, this, args);
        if (args[0]?.label === "PixelMap frame") encoders.add(result);
        return result;
    });
    wrap(globalThis.GPUCommandEncoder?.prototype, "finish", function (original, args) {
        const result = Reflect.apply(original, this, args);
        if (encoders.has(this)) commands.add(result);
        return result;
    });
    wrap(globalThis.GPUDevice?.prototype, "createBuffer", function (original, args) {
        const result = Reflect.apply(original, this, args);
        if (map(args[0]?.label)) {
            buffers.add(result);
            if (state.active) state.active.bufferAllocations++;
            // The frozen baseline/candidate ABI uses 513 512-byte uniform slots
            // and 64 80-byte terrain instances. Fail closed if that ABI changes.
            // No buffer content is copied unless an explicit untimed capture is active.
            const descriptor = args[0];
            const kind = descriptor.size === sceneUniformBytes && (descriptor.usage & uniformUsage) ? "uniform" :
                descriptor.size === terrainInstanceBytes && (descriptor.usage & vertexUsage) ? "terrain" : null;
            if (kind) { sceneBuffers.set(result, kind); state.sceneBufferCounts[kind]++; }
        }
        return result;
    });
    wrap(globalThis.GPUDevice?.prototype, "createTexture", function (original, args) {
        const result = Reflect.apply(original, this, args);
        if (map(args[0]?.label)) {
            textures.set(result, args[0]?.format);
            if (state.active) state.active.textureAllocations++;
        }
        return result;
    });
    wrap(globalThis.GPUQueue?.prototype, "writeBuffer", function (original, args) {
        if (state.active && buffers.has(args[0])) {
            const source = args[2], unit = ArrayBuffer.isView(source) ? source.BYTES_PER_ELEMENT || 1 : 1;
            state.active.bufferUploadBytes += args[4] === undefined
                ? Math.max(0, source.byteLength - (args[3] || 0) * unit) : args[4] * unit;
        }
        if (state.sceneCapture && sceneBuffers.has(args[0])) {
            if (state.active) throw new Error("Camera payload capture cannot overlap measurement");
            const kind = sceneBuffers.get(args[0]), source = args[2];
            const unit = ArrayBuffer.isView(source) ? source.BYTES_PER_ELEMENT || 1 : 1;
            const offset = (args[3] || 0) * unit;
            const length = args[4] === undefined ? source.byteLength - offset : args[4] * unit;
            const stride = kind === "uniform" ? 512 : 80;
            if (args[1] !== 0 || length <= 0 || length % stride)
                throw new Error("Unexpected scene projection upload layout");
            const words = new Uint32Array(ArrayBuffer.isView(source) ? source.buffer : source,
                (ArrayBuffer.isView(source) ? source.byteOffset : 0) + offset, length / 4);
            const records = [];
            for (let start = 0; start < words.length; start += stride / 4) {
                // Projection rows/view/ground/lens/world, or terrain rows/world.
                // Exclude texture/height cache-layer slots, lighting and shadows.
                records.push(kind === "uniform" ? Array.from(words.subarray(start, start + 28)) :
                    [...words.subarray(start, start + 12), ...words.subarray(start + 16, start + 19)]);
            }
            state.sceneCapture[kind] = records;
            if (kind === "uniform") state.sceneCapture.eye = Array.from(words.subarray(52, 55));
        }
        return Reflect.apply(original, this, args);
    });
    wrap(globalThis.GPUQueue?.prototype, "writeTexture", function (original, args) {
        if (state.active && textures.has(args[0]?.texture)) {
            const extent = args[3], width = extent.width ?? extent[0], height = extent.height ?? extent[1] ?? 1,
                layers = extent.depthOrArrayLayers ?? extent[2] ?? 1;
            // Count texel payload, excluding staging row padding. All production
            // writeTexture formats are the listed uncompressed formats.
            const format = textures.get(args[0].texture);
            const stride = {r8unorm: 1, rg8unorm: 2, rgba8unorm: 4, "rgba8unorm-srgb": 4,
                bgra8unorm: 4, rg32float: 8, r32float: 4, rgba16float: 8}[format];
            if (!stride) state.errors.push("Unknown observed texture upload format: " + format);
            else state.active.textureUploadBytes += width * height * layers * stride;
        }
        return Reflect.apply(original, this, args);
    });
    wrap(globalThis.GPUQueue?.prototype, "submit", function (original, args) {
        const submitted = Array.from(args[0] || []);
        const isMap = submitted.some(command => commands.has(command));
        const started = now();
        // Materialize an arbitrary iterable once, preserving generator inputs.
        const result = Reflect.apply(original, this, [submitted]);
        if (isMap) {
            state.mapSubmits++;
            state.lastMapSubmitAt = started;
            const active = state.active;
            if (active) {
                const sample = {submittedAt: started, submitCallMilliseconds: now() - started};
                bounded(active.submissions, sample);
                // Fence each serialized input sample; continuous throughput must
                // not install per-frame queue callbacks or artificial backpressure.
                if (active.mode === "step") {
                    active.pending++;
                    this.onSubmittedWorkDone().then(() => { sample.queueCompletedAt = now(); })
                        .catch(error => state.errors.push("Queue completion: " + error))
                        .finally(() => { active.pending--; });
                }
            }
        }
        return result;
    });
    // Install before the adapter script initializes. Generated .NET imports cache
    // the function reference, so replacing this method after startup is too late.
    Object.defineProperty(globalThis, "PixelAtlasInput", {configurable: true, enumerable: true,
        set(value) {
            const report = value.reportWebGpu;
            if (typeof report !== "function") throw new Error("WebGPU diagnostics import is missing");
            value.reportWebGpu = function (...args) {
                const result = Reflect.apply(report, this, args);
                state.lastDiagnosticAt = now();
                state.lastDiagnostic = {...globalThis.PixelAtlasWebGpu};
                if (state.active) bounded(state.active.frames, {at: state.lastDiagnosticAt, ...state.lastDiagnostic});
                return result;
            };
            Object.defineProperty(globalThis, "PixelAtlasInput", {value, configurable: true, writable: true, enumerable: true});
        }
    });
    addEventListener("wheel", event => {
        if (!state.active || !event.isTrusted) return;
        bounded(state.active.inputs, {at: now(), type: "wheel", x: event.clientX, y: event.clientY,
            deltaX: event.deltaX, deltaY: event.deltaY, deltaMode: event.deltaMode,
            modifiers: (event.altKey ? 1 : 0) | (event.ctrlKey ? 2 : 0) | (event.metaKey ? 4 : 0) | (event.shiftKey ? 8 : 0)});
    }, {capture: true, passive: true});
    let observer;
    if (globalThis.PerformanceObserver) {
        observer = new PerformanceObserver(list => {
            if (state.active) for (const item of list.getEntries())
                if (item.startTime >= state.active.startedAt) bounded(state.active.longTasks,
                    {at: item.startTime, milliseconds: item.duration});
        });
        try { observer.observe({type: "longtask", buffered: false}); } catch { observer = null; }
    }
    state.begin = mode => {
        if (state.active) throw new Error("A performance window is already active");
        if (state.sceneCapture) throw new Error("Camera payload capture cannot overlap measurement");
        if (!["step", "throughput"].includes(mode)) throw new Error("Unknown performance window mode");
        state.active = {mode, startedAt: now(), startMapSubmits: state.mapSubmits, inputs: [], submissions: [],
            frames: [], raf: [], longTasks: [], pending: 0, bufferUploadBytes: 0, textureUploadBytes: 0,
            bufferAllocations: 0, textureAllocations: 0};
        if (mode === "throughput") state.boundaries = {start: queuePrefix(), end: null};
        const capture = state.active;
        function frame(at) {
            if (state.active !== capture) return;
            bounded(capture.raf, at);
            requestAnimationFrame(frame);
        }
        requestAnimationFrame(frame);
    };
    state.ready = (quiet = 350) => {
        const capture = state.active;
        return Boolean(capture && capture.inputs.length === 1 && capture.submissions.length && !capture.pending &&
            now() - state.lastMapSubmitAt >= quiet && state.lastDiagnosticAt >= state.lastMapSubmitAt);
    };
    state.finish = () => {
        if (!state.active) throw new Error("No active performance window");
        if (state.active.pending) throw new Error("Queue completion is still pending");
        const result = state.active;
        result.finishedAt = now();
        result.durationMilliseconds = result.finishedAt - result.startedAt;
        result.mapSubmits = state.mapSubmits - result.startMapSubmits;
        if (result.mode === "throughput") state.boundaries.end = queuePrefix();
        delete result.pending;
        state.active = null;
        return result;
    };
    state.throughputBoundaries = async () => {
        if (!state.boundaries?.end) throw new Error("The throughput window has not finished");
        const [startQueuePrefixMilliseconds, endQueueDrainMilliseconds] =
            await Promise.all([state.boundaries.start, state.boundaries.end]);
        return {startQueuePrefixMilliseconds, endQueueDrainMilliseconds};
    };
    state.beginSceneCapture = () => {
        if (state.active || state.sceneCapture) throw new Error("Camera payload capture must run outside measurement");
        if (state.sceneBufferCounts.uniform !== 1 || state.sceneBufferCounts.terrain !== 1)
            throw new Error("Expected exactly one map camera uniform and terrain instance buffer");
        state.sceneCapture = {uniform: null, terrain: null, eye: null};
    };
    state.finishSceneCapture = () => {
        if (state.active || !state.sceneCapture) throw new Error("No untimed camera capture is active");
        const captured = state.sceneCapture;
        state.sceneCapture = null;
        if (!captured.uniform?.length || !captured.terrain?.length || captured.eye?.length !== 3)
            throw new Error("The untimed scene did not upload its camera and terrain projection");
        // Tile arrival can reorder independent draw records. Compare the actual
        // transform multiset; pixel/geometry gates separately verify scene work.
        captured.uniform.sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)));
        captured.terrain.sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)));
        return captured;
    };
    state.fence = async () => {
        await Promise.all(state.compilation);
        await Promise.all(state.devices.map(device => device.queue.onSubmittedWorkDone()));
    };
    state.snapshot = () => ({adapters: state.adapters, errors: [...state.errors], losses: [...state.losses],
        deviceCount: state.devices.length, mapSubmits: state.mapSubmits, diagnostic: state.lastDiagnostic});
}

module.exports = {installPerformanceProbe};
