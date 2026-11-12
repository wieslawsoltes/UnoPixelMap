// Independent observation of the application's real resources and submitted work.
// installRenderGraphProbe is serialized into each browser page after installProbe.
// No production diagnostics, source labels alone, or allocated-but-unused textures
// are sufficient to prove a mip chain or bloom pass reached the GPU queue.
function installRenderGraphProbe() {
    const state = globalThis.__pixelAtlasGpuProbe.renderGraph = {
        textures: [], samplers: [], frames: [], submissions: 0, errors: []
    };
    const textures = new WeakMap(), views = new WeakMap(), groups = new WeakMap(), samplers = new WeakMap();
    const smallStorage = new WeakMap(), dirtyTables = new WeakMap();
    const encoders = new WeakMap(), passes = new WeakMap(), commands = new WeakMap();
    const map = label => /^PixelMap[ /]/.test(label || "");
    function wrap(prototype, name, action) {
        if (!prototype?.[name]) return;
        const original = prototype[name];
        prototype[name] = function (...args) { return action.call(this, original, args); };
    }
    function viewRecord(texture, d = {}) {
        const dimension = d.dimension ?? (texture.layers > 1 ? "2d-array" : "2d");
        const mip = d.baseMipLevel ?? 0, layer = d.baseArrayLayer ?? 0;
        return { textureId: texture.id, label: texture.label, format: d.format ?? texture.format,
            mip, mips: d.mipLevelCount ?? texture.mips - mip,
            layer, layers: d.arrayLayerCount ?? (dimension === "2d" ? 1 : texture.layers - layer) };
    }
    function boundViews(pass) {
        const result = [];
        for (const [index, entries] of pass.bound)
            for (const entry of entries) if (entry.textureId) result.push({ group: index, ...entry });
        return result;
    }
    function vector(value, names, defaults) {
        return names.map((name, index) => Number((Array.isArray(value) || ArrayBuffer.isView(value) ? value[index] : value?.[name]) ?? defaults[index]));
    }
    function validateTerrainSubmission(submitted, submission) {
        const consumed = new Set();
        for (let index = 0; index < submitted.length; index++) {
            const pass = submitted[index];
            if (!/^PixelMap terrain mip /.test(pass.label)) continue;
            let valid = true;
            const check = (condition, message) => {
                if (condition) return;
                valid = false;
                state.errors.push(`Submission ${submission}, ${pass.label}: ${message}`);
            };
            const level = Number(pass.label.match(/^PixelMap terrain mip ([1-8])$/)?.[1]);
            const source = pass.inputs?.find(view => view.group === 0 && view.binding === 0);
            const destination = pass.inputs?.find(view => view.group === 0 && view.binding === 1);
            const artwork = source && state.textures[source.textureId - 1];
            const scratch = destination && state.textures[destination.textureId - 1];
            const dispatch = pass.dispatches?.[0];
            check(pass.kind === "compute" && pass.dispatches.length === 1, "must execute exactly one compute dispatch");
            check(Number.isInteger(level), "must identify a destination mip from one through eight");
            check(pass.pipeline === "PixelMap terrain linear mip reduction", "must use the terrain reduction pipeline");
            check(!!artwork && !!scratch && !!dispatch, "source, scratch and dispatch must be observed");
            if (!artwork || !scratch || !dispatch || !Number.isInteger(level)) continue;
            check(artwork.label === "PixelMap terrain artwork" && !artwork.destroyed &&
                artwork.width === 256 && artwork.height === 256 && artwork.mips === 9 && artwork.samples === 1,
                "source must be the live, complete 256-to-one artwork array");
            check(source.format === "rgba8unorm-srgb" && source.mip === level - 1 && source.mips === 1 &&
                source.layer === 0 && source.layers === artwork.layers,
                "source view must address only the previous sRGB mip and all artwork layers");
            check(scratch.label === "PixelMap terrain mip scratch" && !scratch.destroyed &&
                scratch.id !== artwork.id && scratch.width === 128 && scratch.height === 128 &&
                scratch.mips === 1 && scratch.samples === 1 && scratch.layers === artwork.layers,
                "scratch must be a distinct, live 128-square single-mip array");
            check(destination.format === "rgba8unorm" && destination.mip === 0 && destination.mips === 1 &&
                destination.layer === 0 && destination.layers === scratch.layers,
                "storage view must address scratch mip zero and all layers");
            const width = Math.max(1, artwork.width >> level), height = Math.max(1, artwork.height >> level);
            check(dispatch[0] === Math.ceil(width / 8) && dispatch[1] === Math.ceil(height / 8) &&
                Number.isInteger(dispatch[2]) && dispatch[2] > 0 && dispatch[2] <= artwork.layers,
                "dispatch must cover the exact reduced rectangle and a bounded dirty-layer count");

            // Observe the actual bounded CPU upload bound at group zero/binding
            // two. This is not a GPU readback. Resolve it at submit time because
            // queue writes between encoding and submit also precede this work.
            const binding = dirtyTables.get(pass), count = dispatch[2], dirty = [];
            check(!!binding && Number.isInteger(count) && count > 0 && count <= artwork.layers,
                "dirty-layer storage binding must be observed");
            if (binding && Number.isInteger(count) && count > 0 && count <= artwork.layers) {
                const { buffer, offset, size } = binding;
                check(offset % 4 === 0 && size >= count * 4 && offset + count * 4 <= buffer.bytes.length,
                    "dirty-layer binding must contain every dispatched index");
                check(buffer.lastWrite?.offset === offset && buffer.lastWrite?.length === count * 4,
                    "dispatch count must equal the latest bounded dirty-layer upload, not spare capacity");
                if (offset % 4 === 0 && size >= count * 4 && offset + count * 4 <= buffer.bytes.length) {
                    const bytes = new DataView(buffer.bytes.buffer);
                    for (let i = 0; i < count; i++) {
                        const first = offset + i * 4;
                        check(buffer.written.subarray(first, first + 4).every(Boolean), "every dispatched dirty-layer index must have been uploaded");
                        const layer = bytes.getUint32(first, true);
                        dirty.push(layer);
                        check(layer < artwork.layers, "dirty-layer index exceeds the artwork array");
                    }
                    check(new Set(dirty).size === dirty.length, "dirty-layer indices must be unique");
                }
            }
            pass.dirtyLayers = dirty;
            const copies = [];
            for (let next = index + 1; next < submitted.length && submitted[next].kind === "copy"; next++) {
                copies.push(submitted[next]); consumed.add(next);
            }
            check(copies.length === count && dirty.length === count, "each dirty layer must have exactly one immediately following copy");
            const versions = [];
            for (let i = 0; i < copies.length; i++) {
                const copy = copies[i], layer = dirty[i];
                check(copy.source?.textureId === scratch.id && copy.destination?.textureId === artwork.id,
                    "copy must move this reduction's scratch into its artwork array");
                check(copy.source?.mip === 0 && copy.destination?.mip === level,
                    "copy must read scratch mip zero and write this reduction's destination mip");
                check(copy.source?.layer === layer && copy.destination?.layer === layer,
                    "copy source/destination layers must follow the uploaded dirty table exactly");
                check(copy.source?.origin?.[0] === 0 && copy.source?.origin?.[1] === 0 &&
                    copy.destination?.origin?.[0] === 0 && copy.destination?.origin?.[1] === 0,
                    "copy must begin at the origin of both mip rectangles");
                check(copy.extent?.[0] === width && copy.extent?.[1] === height && copy.extent?.[2] === 1,
                    "copy must cover the full destination mip and exactly one layer");
                const version = artwork.uploads[`0:${layer}`];
                check(Number.isInteger(version) && version > 0, "source layer must have an observed base upload");
                check(level === 1 || artwork.copiedVersions[`${level - 1}:${layer}`] === version,
                    "previous mip must already contain the latest base upload before reduction");
                versions.push(version);
            }
            // An incomplete, stale or incorrectly ordered batch cannot make the
            // resident mip chain appear current in a later retained frame.
            if (valid) for (let i = 0; i < dirty.length; i++)
                artwork.copiedVersions[`${level}:${dirty[i]}`] = versions[i];
        }
        for (let index = 0; index < submitted.length; index++) {
            const pass = submitted[index];
            if (pass.kind === "copy" && pass.destination?.label === "PixelMap terrain artwork" && !consumed.has(index))
                state.errors.push(`Submission ${submission}: artwork copy has no immediately preceding terrain reduction`);
        }
    }
    wrap(globalThis.GPUDevice?.prototype, "createTexture", function (fn, args) {
        const result = Reflect.apply(fn, this, args), d = args[0];
        if (map(d?.label)) {
            const record = { id: state.textures.length + 1, label: d.label,
                width: result.width, height: result.height, layers: result.depthOrArrayLayers,
                mips: result.mipLevelCount, samples: result.sampleCount,
                format: result.format, usage: result.usage, viewFormats: [...(d.viewFormats || [])],
                uploads: {}, dispatched: {}, copiedVersions: {}, rendered: {}, destroyed: false };
            textures.set(result, record);
            state.textures.push(record);
        }
        return result;
    });
    wrap(globalThis.GPUTexture?.prototype, "createView", function (fn, args) {
        const result = Reflect.apply(fn, this, args), texture = textures.get(this);
        if (texture) views.set(result, viewRecord(texture, args[0]));
        return result;
    });
    wrap(globalThis.GPUTexture?.prototype, "destroy", function (fn, args) {
        const texture = textures.get(this);
        if (texture) texture.destroyed = true;
        return Reflect.apply(fn, this, args);
    });
    wrap(globalThis.GPUDevice?.prototype, "createSampler", function (fn, args) {
        const result = Reflect.apply(fn, this, args), d = args[0] || {};
        if (map(d.label)) {
            const record = { ...d };
            state.samplers.push(record); samplers.set(result, record);
        }
        return result;
    });
    wrap(globalThis.GPUDevice?.prototype, "createBuffer", function (fn, args) {
        const result = Reflect.apply(fn, this, args), d = args[0] || {};
        // The layer table is at most 384 bytes. Limit observed upload shadows
        // to small storage buffers instead of copying resident mesh streams.
        if (map(d.label) && (d.usage & GPUBufferUsage.STORAGE) && d.size <= 4096)
            smallStorage.set(result, { bytes: new Uint8Array(d.size), written: new Uint8Array(d.size) });
        return result;
    });
    wrap(globalThis.GPUDevice?.prototype, "createBindGroup", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        const entries = (args[0]?.entries || []).flatMap(entry => {
            const view = views.get(entry.resource);
            if (view) return [{ binding: entry.binding, ...view }];
            const sampler = samplers.get(entry.resource);
            if (sampler) return [{ binding: entry.binding, sampler }];
            const buffer = smallStorage.get(entry.resource?.buffer);
            if (buffer) return [{ binding: entry.binding, buffer, offset: entry.resource.offset ?? 0,
                size: entry.resource.size ?? buffer.bytes.length - (entry.resource.offset ?? 0) }];
            return [];
        });
        if (entries.length) groups.set(result, entries);
        return result;
    });
    wrap(globalThis.GPUDevice?.prototype, "createCommandEncoder", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        if (map(args[0]?.label)) encoders.set(result, []);
        return result;
    });
    for (const [name, kind] of [["beginRenderPass", "render"], ["beginComputePass", "compute"]])
        wrap(globalThis.GPUCommandEncoder?.prototype, name, function (fn, args) {
            const result = Reflect.apply(fn, this, args), encoder = encoders.get(this), d = args[0] || {};
            if (encoder) {
                const record = { kind, label: d.label || "", pipeline: "", draws: 0, dispatches: [], inputs: [], terrainInputs: [], colors: [] };
                for (const color of d.colorAttachments || []) if (color) record.colors.push({
                    view: views.get(color.view) || null, resolve: views.get(color.resolveTarget) || null,
                    load: color.loadOp, store: color.storeOp
                });
                if (d.depthStencilAttachment) record.depth = {
                    view: views.get(d.depthStencilAttachment.view) || null,
                    load: d.depthStencilAttachment.depthLoadOp, store: d.depthStencilAttachment.depthStoreOp
                };
                passes.set(result, { record, bound: new Map() });
                encoder.push(record);
            }
            return result;
        });
    for (const prototype of [globalThis.GPURenderPassEncoder?.prototype, globalThis.GPUComputePassEncoder?.prototype]) {
        wrap(prototype, "setBindGroup", function (fn, args) {
            const pass = passes.get(this);
            if (pass) pass.bound.set(args[0], groups.get(args[1]) || []);
            return Reflect.apply(fn, this, args);
        });
        wrap(prototype, "setPipeline", function (fn, args) {
            const pass = passes.get(this);
            if (pass) pass.record.pipeline = args[0]?.label || "";
            return Reflect.apply(fn, this, args);
        });
    }
    for (const name of ["draw", "drawIndexed", "drawIndirect", "drawIndexedIndirect"])
        wrap(globalThis.GPURenderPassEncoder?.prototype, name, function (fn, args) {
            const pass = passes.get(this);
            if (pass) {
                pass.record.draws++;
                // Each postprocess pass uses a single pipeline and bind group.
                // Record its actual inputs without copying every building draw.
                if (pass.record.draws === 1) pass.record.inputs = boundViews(pass);
                if (!pass.record.terrainInputs.length && /^PixelMap terrain \(/.test(pass.record.pipeline))
                    pass.record.terrainInputs = (pass.bound.get(1) || [])
                        .filter(entry => entry.textureId || entry.sampler).map(entry => ({ group: 1, ...entry }));
            }
            return Reflect.apply(fn, this, args);
        });
    wrap(globalThis.GPUComputePassEncoder?.prototype, "dispatchWorkgroups", function (fn, args) {
        const pass = passes.get(this);
        if (pass) {
            pass.record.dispatches.push([args[0], args[1] ?? 1, args[2] ?? 1]);
            pass.record.inputs = boundViews(pass);
            const table = (pass.bound.get(0) || []).find(entry => entry.binding === 2 && entry.buffer);
            if (table) dirtyTables.set(pass.record, table);
        }
        return Reflect.apply(fn, this, args);
    });
    wrap(globalThis.GPUCommandEncoder?.prototype, "finish", function (fn, args) {
        const result = Reflect.apply(fn, this, args), encoder = encoders.get(this);
        if (encoder) commands.set(result, encoder);
        return result;
    });
    wrap(globalThis.GPUCommandEncoder?.prototype, "copyTextureToTexture", function (fn, args) {
        const result = Reflect.apply(fn, this, args), encoder = encoders.get(this);
        const describe = value => {
            const texture = textures.get(value?.texture);
            if (!texture) return null;
            const origin = vector(value.origin, ["x", "y", "z"], [0, 0, 0]);
            return { ...viewRecord(texture, { baseMipLevel: value.mipLevel ?? 0, mipLevelCount: 1,
                baseArrayLayer: origin[2], arrayLayerCount: 1, dimension: "2d" }), origin };
        };
        if (encoder) encoder.push({ kind: "copy", label: "PixelMap texture copy",
            source: describe(args[0]), destination: describe(args[1]),
            extent: vector(args[2], ["width", "height", "depthOrArrayLayers"], [0, 1, 1]), colors: [] });
        return result;
    });
    wrap(globalThis.GPUQueue?.prototype, "writeTexture", function (fn, args) {
        const texture = textures.get(args[0]?.texture);
        if (texture) {
            const d = args[0], layer = vector(d.origin, ["x", "y", "z"], [0, 0, 0])[2];
            const key = `${d.mipLevel ?? 0}:${layer}`;
            texture.uploads[key] = (texture.uploads[key] || 0) + 1;
        }
        return Reflect.apply(fn, this, args);
    });
    wrap(globalThis.GPUQueue?.prototype, "writeBuffer", function (fn, args) {
        const result = Reflect.apply(fn, this, args), shadow = smallStorage.get(args[0]);
        if (shadow) {
            const data = args[2], typed = ArrayBuffer.isView(data), unit = typed ? data.BYTES_PER_ELEMENT || 1 : 1;
            const first = (args[3] ?? 0) * unit, length = args[4] === undefined ? data.byteLength - first : args[4] * unit;
            try {
                const bytes = new Uint8Array(typed ? data.buffer : data, (typed ? data.byteOffset : 0) + first, length);
                shadow.bytes.set(bytes, args[1]); shadow.written.fill(1, args[1], args[1] + length);
                shadow.lastWrite = { offset: args[1], length };
            } catch (error) { state.errors.push(`Could not observe bounded storage upload: ${error.message}`); }
        }
        return result;
    });
    wrap(globalThis.GPUQueue?.prototype, "submit", function (fn, args) {
        const result = Reflect.apply(fn, this, args);
        for (const command of args[0] || []) {
            const submitted = commands.get(command);
            if (!submitted) continue;
            state.submissions++;
            state.frames.push({ submission: state.submissions, passes: submitted });
            if (state.frames.length > 3) state.frames.shift();
            validateTerrainSubmission(submitted, state.submissions);
            for (const pass of submitted) {
                for (const color of pass.colors) for (const view of [color.view, color.resolve]) if (view) {
                    const texture = state.textures[view.textureId - 1], key = `${view.mip}:${view.layer}`;
                    texture.rendered[key] = (texture.rendered[key] || 0) + pass.draws;
                }
                if (/^PixelMap terrain mip /.test(pass.label)) {
                    const destination = pass.inputs.find(view => view.binding === 1);
                    if (destination) {
                        const texture = state.textures[destination.textureId - 1];
                        texture.dispatched[destination.mip] = (texture.dispatched[destination.mip] || 0) + pass.dispatches.length;
                    }
                }
            }
        }
        return result;
    });
}

module.exports = { installRenderGraphProbe };
