// Deliberately independent of application exports: the same observer can inspect
// an already-published baseline and a candidate without changing either binary.
// Only existing WebGL calls are observed. No getParameter/readPixels/finish calls
// are added, and cached uniform values are tracked while measurement is inactive.
function installCameraReplayProbe() {
    const state = globalThis.__pixelAtlasCameraReplay = {step: null, lastResolveAt: 0};
    const contexts = new WeakMap(), shaders = new WeakMap(), programs = new WeakMap();
    const locations = new WeakMap(), fences = new WeakMap();
    const names = new Set(["uScreenX", "uScreenY", "uScreenW", "uArtSize", "uDepth",
        "uGround", "uLens", "uPerspective", "uRenderStyle", "uLogicalSize", "uPixelSize"]);
    const contextFor = gl => {
        let context = contexts.get(gl);
        if (!context) contexts.set(gl, context = {program: null, width: 0, height: 0,
            projectionDraws: [], terrainInstances: null, lastResolve: null});
        return context;
    };
    const values = program => Object.fromEntries(Object.keys(program.values).sort()
        .map(name => [name, program.values[name].slice()]));
    function projectionBytes(args) {
        const source = args[2];
        if (!ArrayBuffer.isView(source)) return null;
        const scalar = source.BYTES_PER_ELEMENT || 1;
        const offset = (args[3] || 0) * scalar;
        const size = args[4] === undefined || args[4] === 0
            ? source.byteLength - offset : args[4] * scalar;
        if (size < 0 || offset + size > source.byteLength || size % 4) return null;
        const view = new DataView(source.buffer, source.byteOffset + offset, size);
        return Array.from({length: size / 4}, (_, index) => view.getFloat32(index * 4, true));
    }
    for (const type of [globalThis.WebGLRenderingContext, globalThis.WebGL2RenderingContext]) {
        if (!type) continue;
        for (const name of ["shaderSource", "attachShader", "getUniformLocation", "useProgram", "viewport",
            "uniform1f", "uniform1i", "uniform2f", "uniform2i", "uniform3f", "uniform4f",
            "bufferSubData", "drawArrays", "drawElements", "drawArraysInstanced", "drawElementsInstanced",
            "fenceSync", "clientWaitSync", "deleteSync"]) {
            const original = type.prototype[name];
            if (typeof original !== "function" || original.__pixelAtlasCameraReplayObserved) continue;
            const observed = function (...args) {
                const result = Reflect.apply(original, this, args);
                const context = contextFor(this);
                if (name === "shaderSource") {
                    const source = args[1];
                    const kind = source.includes("uniform bool uArtworkOnly;") && source.includes("uniform float uPhotoOpacity;")
                        ? "resolve" : source.includes("uniform vec4 uScreenX;") && source.includes("uniform vec3 uGround;")
                            ? source.includes("in vec4 aScreenX;") ? "terrain" : "geometry" : null;
                    if (kind) shaders.set(args[0], kind);
                } else if (name === "attachShader") {
                    const kind = shaders.get(args[1]);
                    if (kind) programs.set(args[0], {kind, values: {}});
                } else if (name === "getUniformLocation") {
                    if (result && programs.has(args[0]) && names.has(args[1]))
                        locations.set(result, {program: programs.get(args[0]), name: args[1]});
                } else if (name === "useProgram") context.program = programs.get(args[0]) || null;
                else if (name === "viewport") { context.width = args[2]; context.height = args[3]; }
                else if (name.startsWith("uniform")) {
                    const location = args[0] && locations.get(args[0]);
                    if (location) location.program.values[location.name] = args.slice(1);
                } else if (name === "bufferSubData" && context.program?.kind === "terrain" &&
                    args[0] === this.ARRAY_BUFFER && args[1] === 0) {
                    context.terrainInstances = projectionBytes(args);
                } else if (name.startsWith("draw") && context.program) {
                    const program = context.program;
                    if (program.kind === "resolve") {
                        const time = performance.now();
                        state.lastResolveAt = time;
                        const resolve = {time, width: context.width, height: context.height,
                            compositorContext: this.canvas?.id === "uno-canvas",
                            uniforms: values(program), draws: context.projectionDraws, signaledAt: null};
                        context.projectionDraws = [];
                        context.lastResolve = resolve;
                        if (state.step) state.step.resolves.push(resolve);
                    } else if (state.step) {
                        const elements = name.startsWith("drawElements");
                        const instanced = name.endsWith("Instanced");
                        const instances = instanced ? args[elements ? 4 : 3] : 1;
                        const draw = {kind: program.kind, method: name, mode: args[0],
                            count: args[elements ? 1 : 2], instances,
                            width: context.width, height: context.height, uniforms: values(program)};
                        if (program.kind === "terrain") {
                            // Each production instance has three vec4 transforms followed
                            // by two cache-layer indices. Cache slots are resource identity,
                            // not pose; keep the 12 submitted projection floats per tile.
                            const data = context.terrainInstances;
                            if (!data || data.length < instances * 14)
                                throw new Error("The terrain projection instance upload is missing");
                            draw.tileProjections = Array.from({length: instances}, (_, index) =>
                                data.slice(index * 14, index * 14 + 12));
                        }
                        context.projectionDraws.push(draw);
                    }
                } else if (name === "fenceSync" && result && context.lastResolve) {
                    fences.set(result, context.lastResolve);
                    context.lastResolve = null;
                } else if (name === "clientWaitSync" &&
                    (result === this.ALREADY_SIGNALED || result === this.CONDITION_SATISFIED)) {
                    const resolve = args[0] && fences.get(args[0]);
                    if (resolve && resolve.signaledAt === null) resolve.signaledAt = performance.now();
                } else if (name === "deleteSync" && args[0]) fences.delete(args[0]);
                return result;
            };
            observed.__pixelAtlasCameraReplayObserved = true;
            type.prototype[name] = observed;
        }
    }
    globalThis.addEventListener("wheel", event => {
        if (state.step) state.step.inputs.push({time: performance.now(), trusted: event.isTrusted,
            x: event.clientX, y: event.clientY, deltaX: event.deltaX, deltaY: event.deltaY,
            deltaMode: event.deltaMode, modifiers: (event.altKey ? 1 : 0) | (event.ctrlKey ? 2 : 0) |
                (event.metaKey ? 4 : 0) | (event.shiftKey ? 8 : 0)});
    }, {capture: true, passive: true});
    state.begin = () => {
        if (state.step) throw new Error("A camera replay step is already active");
        state.step = {beganAt: performance.now(), inputs: [], resolves: []};
    };
    // The public tile refresh timer may submit a second frame after changing
    // resident-tile order. Retain it and await its own fence as well; completing
    // on the first signal would leak later map work into the next input.
    state.ready = () => state.step?.resolves.length > 0 &&
        state.step.resolves.every(resolve => resolve.signaledAt !== null) &&
        performance.now() - state.lastResolveAt >= 250;
    state.finish = () => {
        const step = state.step;
        if (!step) throw new Error("No camera replay step is active");
        step.finishedAt = performance.now();
        step.lastResolveAt = state.lastResolveAt;
        step.quietForMs = step.finishedAt - state.lastResolveAt;
        state.step = null;
        return step;
    };
}

module.exports = {installCameraReplayProbe};
