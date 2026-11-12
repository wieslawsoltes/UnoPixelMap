// Numerical checks of the production WGSL in an isolated test device. Readback
// belongs only to this fixture; the application probe still requires zero reads.
const assert = require("node:assert/strict");
const { readFile, writeFile } = require("node:fs/promises");
const { join } = require("node:path");

function rawStrings(source) {
    return Object.fromEntries([...source.matchAll(/(?:const|readonly) string (\w+)\s*=\s*"""\r?\n([\s\S]*?)\r?\n\s*""";/g)]
        .map(match => [match[1], match[2]]));
}

async function runShaderFixtures(browser, base, output) {
    const rendering = join(__dirname, "../../src/PixelMap.Rendering");
    const b = rawStrings(await readFile(join(rendering, "WebGpuBloomShaders.cs"), "utf8"));
    for (const name of ["Common", "Input", "PhotographInput", "DownsampleKernel", "DownsampleFragment", "UpsampleKernel"])
        assert.ok(b[name], `The production bloom ${name} source is present`);
    const bloom = {
        prefilter: b.Common + b.PhotographInput + b.DownsampleKernel + b.DownsampleFragment,
        downsample: b.Common + b.Input + b.DownsampleKernel + b.DownsampleFragment,
        upsample: b.Common + b.UpsampleKernel
    };
    const mip = rawStrings(await readFile(join(rendering, "WebGpuTerrainMipShaders.cs"), "utf8")).Reduce;
    assert.ok(mip, "The production terrain reduction shader is present");
    const facadePath = process.env.PIXELMAP_FACADE_FIXTURE || join(output, "facade-fixture.json");
    const facade = JSON.parse(await readFile(facadePath, "utf8"));
    const context = await browser.newContext();
    try {
        const page = await context.newPage(), url = new URL("__shader-fixtures.html", base).href;
        await page.route(url, route => route.fulfill({ contentType: "text/html", body: "<!doctype html><title>WebGPU numerical shader fixtures</title>" }));
        await page.goto(url);
        const evaluation = page.evaluate(async ({ bloom, mip, facade }) => {
            const adapter = await navigator.gpu.requestAdapter();
            if (!adapter) throw new Error("No GPU adapter for numerical fixtures");
            const device = await adapter.requestDevice(), errors = [], resources = [];
            device.addEventListener("uncapturederror", event => errors.push(event.error.message));
            device.pushErrorScope("validation");
            const texture = (width, height, format = "rgba16float", extra = {}) => {
                const value = device.createTexture({ size: [width, height], format,
                    usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.COPY_SRC | GPUTextureUsage.RENDER_ATTACHMENT, ...extra });
                resources.push(value); return value;
            };
            const buffer = (size, usage) => {
                const value = device.createBuffer({ size, usage }); resources.push(value); return value;
            };
            const sampler = device.createSampler({ magFilter: "linear", minFilter: "linear", mipmapFilter: "linear" });
            function half(value) {
                const f = new Float32Array([value]), bits = new Uint32Array(f.buffer)[0];
                const sign = (bits >>> 16) & 0x8000, exponent = (bits >>> 23) & 255, mantissa = bits & 0x7fffff;
                if (exponent === 255) return sign | 0x7c00 | (mantissa ? 0x200 : 0);
                let e = exponent - 127 + 15;
                if (e >= 31) return sign | 0x7c00;
                if (e <= 0) {
                    if (e < -10) return sign;
                    const m = mantissa | 0x800000, shift = 14 - e;
                    return sign | ((m + (1 << (shift - 1)) - 1 + ((m >>> shift) & 1)) >>> shift);
                }
                const rounded = mantissa + 0xfff + ((mantissa >>> 13) & 1);
                if (rounded & 0x800000) { e++; if (e >= 31) return sign | 0x7c00; }
                return sign | (e << 10) | ((rounded >>> 13) & 0x3ff);
            }
            function fromHalf(bits) {
                const sign = bits & 0x8000 ? -1 : 1, e = (bits >>> 10) & 31, m = bits & 1023;
                return sign * (e === 0 ? m * 2 ** -24 : e === 31 ? (m ? NaN : Infinity) : (1 + m / 1024) * 2 ** (e - 15));
            }
            function upload(image, pixels) {
                device.queue.writeTexture({ texture: image }, Uint16Array.from(pixels, half),
                    { bytesPerRow: image.width * 8 }, [image.width, image.height]);
            }
            async function read(image, format = "half", layer = 0, mipLevel = 0) {
                const width = Math.max(1, image.width >> mipLevel), height = Math.max(1, image.height >> mipLevel);
                const stride = format === "float" ? 16 : format === "byte" ? 4 : 8;
                const bytesPerRow = Math.ceil(width * stride / 256) * 256;
                const staging = buffer(bytesPerRow * height, GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ);
                const encoder = device.createCommandEncoder();
                encoder.copyTextureToBuffer({ texture: image, mipLevel, origin: [0, 0, layer] },
                    { buffer: staging, bytesPerRow }, [width, height]);
                device.queue.submit([encoder.finish()]);
                await staging.mapAsync(GPUMapMode.READ);
                const bytes = new Uint8Array(staging.getMappedRange()), values = [];
                for (let y = 0; y < height; y++) {
                    const row = bytes.slice(y * bytesPerRow, y * bytesPerRow + width * stride);
                    values.push(...(format === "float" ? new Float32Array(row.buffer) :
                        format === "byte" ? row : Array.from(new Uint16Array(row.buffer), fromHalf)));
                }
                staging.unmap(); return { width, height, values };
            }
            async function module(code) {
                const value = device.createShaderModule({ code });
                const info = await value.getCompilationInfo();
                const failures = info.messages.filter(message => message.type === "error");
                if (failures.length) throw new Error(failures.map(message => `${message.lineNum}:${message.linePos} ${message.message}`).join("\n"));
                return value;
            }
            const bloomBindings = device.createBindGroupLayout({ entries: [
                { binding: 0, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: "float" } },
                { binding: 1, visibility: GPUShaderStage.FRAGMENT, texture: { sampleType: "float" } },
                { binding: 2, visibility: GPUShaderStage.FRAGMENT, sampler: { type: "filtering" } },
                { binding: 3, visibility: GPUShaderStage.FRAGMENT, buffer: { type: "uniform", minBindingSize: 64 } }
            ] });
            const bloomLayout = device.createPipelineLayout({ bindGroupLayouts: [bloomBindings] });
            const pipelines = {};
            for (const [name, code] of Object.entries(bloom)) {
                const shader = await module(code);
                pipelines[name] = device.createRenderPipeline({ layout: bloomLayout,
                    vertex: { module: shader, entryPoint: "vs" },
                    fragment: { module: shader, entryPoint: "fs", targets: [{ format: "rgba16float" }] } });
            }
            const uniform = buffer(64, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
            function draw(encoder, pipeline, source, detail, target) {
                const bindings = device.createBindGroup({ layout: pipeline.getBindGroupLayout(0), entries: [
                    { binding: 0, resource: source.createView() }, { binding: 1, resource: detail.createView() },
                    { binding: 2, resource: sampler }, { binding: 3, resource: { buffer: uniform } }
                ] });
                const pass = encoder.beginRenderPass({ colorAttachments: [{ view: target.createView(), loadOp: "clear", storeOp: "store" }] });
                pass.setPipeline(pipeline); pass.setBindGroup(0, bindings); pass.draw(3); pass.end();
            }
            async function bloomCase(name, width, height, count, color, impulse = false, photoColor = null) {
                const input = texture(width, height), photo = texture(width, height), pixels = new Float32Array(width * height * 4);
                for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
                    const index = (y * width + x) * 4;
                    const scale = !impulse || (x === Math.floor(width / 2) && y === Math.floor(height / 2)) ? 1 : 0;
                    pixels.set([color[0] * scale, color[1] * scale, color[2] * scale, 1], index);
                }
                upload(input, pixels);
                if (photoColor) { const p = new Float32Array(pixels.length); for (let i = 0; i < p.length; i += 4) p.set([...photoColor, 1], i); upload(photo, p); }
                const settings = new Float32Array(16); settings[10] = photoColor ? .35 : 0;
                device.queue.writeBuffer(uniform, 0, settings);
                const encoder = device.createCommandEncoder(), down = [];
                let source = input;
                for (let level = 0; level < count && (source.width > 1 || source.height > 1); level++) {
                    const target = texture(Math.ceil(source.width / 2), Math.ceil(source.height / 2));
                    draw(encoder, level === 0 ? pipelines.prefilter : pipelines.downsample, source, level === 0 ? photo : source, target);
                    down.push(target); source = target;
                }
                for (let level = down.length - 2; level >= 0; level--) {
                    const target = texture(down[level].width, down[level].height);
                    draw(encoder, pipelines.upsample, source, down[level], target); source = target;
                }
                device.queue.submit([encoder.finish()]);
                return { name, input: { width, height, color, impulse, photoColor }, levels: down.length, result: await read(source) };
            }
            async function facadeCases() {
                if (facade.strideFloats !== 20 || facade.inputs.length !== facade.width * 20)
                    throw new Error("Facade fixture SSBO dimensions do not match the production contract");
                const rooms = texture(facade.mips[0].width, facade.mips[0].height, "rg8unorm", {
                    mipLevelCount: facade.mips.length, usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST });
                for (const [index, level] of facade.mips.entries()) {
                    const pixels = Uint8Array.from(atob(level.base64), value => value.charCodeAt(0));
                    device.queue.writeTexture({ texture: rooms, mipLevel: index }, pixels,
                        { bytesPerRow: level.width * 2 }, [level.width, level.height]);
                }
                const input = buffer(facade.inputs.length * 4, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST);
                device.queue.writeBuffer(input, 0, new Float32Array(facade.inputs));
                const shader = await module(facade.wgsl);
                const pipeline = device.createRenderPipeline({ layout: "auto",
                    vertex: { module: shader, entryPoint: "vs" }, fragment: { module: shader, entryPoint: "fs",
                        targets: [{ format: "rgba32float" }, { format: "rgba32float" }] } });
                const roomSampler = device.createSampler({ addressModeU: "repeat", addressModeV: "repeat",
                    magFilter: "linear", minFilter: "linear", mipmapFilter: "linear", lodMaxClamp: facade.mips.length - 1 });
                const bindings = device.createBindGroup({ layout: pipeline.getBindGroupLayout(0), entries: [
                    { binding: 0, resource: rooms.createView() }, { binding: 1, resource: roomSampler },
                    { binding: 2, resource: { buffer: input } }
                ] });
                const outputs = [texture(facade.width, 1, "rgba32float"), texture(facade.width, 1, "rgba32float")];
                const encoder = device.createCommandEncoder();
                const pass = encoder.beginRenderPass({ colorAttachments: outputs.map(image => ({
                    view: image.createView(), loadOp: "clear", storeOp: "store" })) });
                pass.setPipeline(pipeline); pass.setBindGroup(0, bindings); pass.draw(3); pass.end();
                device.queue.submit([encoder.finish()]);
                return { albedoRoughness: (await read(outputs[0], "float")).values,
                    emissionMetallic: (await read(outputs[1], "float")).values };
            }
            async function terrainCases() {
                const artwork = texture(256, 256, "rgba8unorm", { size: [256, 256, 4], mipLevelCount: 9,
                    viewFormats: ["rgba8unorm-srgb"], usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.COPY_SRC });
                const scratch = texture(128, 128, "rgba8unorm", { size: [128, 128, 4],
                    usage: GPUTextureUsage.STORAGE_BINDING | GPUTextureUsage.COPY_SRC });
                const inputs = [];
                for (let layer = 0; layer < 4; layer++) {
                    const pixels = new Uint8Array(256 * 256 * 4);
                    for (let y = 0; y < 256; y++) for (let x = 0; x < 256; x++) {
                        const checker = (x + y) & 1;
                        const color = layer === 0 ? [checker * 255, checker * 255, checker * 255, checker * 255] :
                            layer === 1 ? [31, 63, 127, 223] :
                            layer === 2 ? [x < 127 ? 8 : 240, y < 129 ? 32 : 192, checker * 128, 255] :
                            [17 + x % 64, 43 + y % 128, 7 + (x * 13 + y * 7) % 249, (x * 3 + y * 5) % 256];
                        pixels.set(color, (y * 256 + x) * 4);
                    }
                    inputs.push(pixels);
                    device.queue.writeTexture({ texture: artwork, origin: [0, 0, layer] }, pixels, { bytesPerRow: 256 * 4 }, [256, 256]);
                }
                // A clean layer has sentinels in every lower mip. Nonsequential
                // dirty IDs expose shaders that accidentally address dispatch Z.
                for (let level = 1; level < 9; level++) {
                    const size = 256 >> level, sentinel = new Uint8Array(size * size * 4);
                    for (let i = 0; i < sentinel.length; i += 4) sentinel.set([31, 63, 127, 223], i);
                    device.queue.writeTexture({ texture: artwork, mipLevel: level, origin: [0, 0, 1] }, sentinel,
                        { bytesPerRow: size * 4 }, [size, size]);
                }
                const dirty = new Uint32Array([3, 0, 2]), table = buffer(16, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST);
                device.queue.writeBuffer(table, 0, new Uint32Array([3, 0, 2, 1]));
                const shader = await module(mip), pipeline = device.createComputePipeline({ layout: "auto", compute: { module: shader, entryPoint: "reduce" } });
                const encoder = device.createCommandEncoder();
                for (let level = 1; level < 9; level++) {
                    const size = 256 >> level;
                    const bindings = device.createBindGroup({ layout: pipeline.getBindGroupLayout(0), entries: [
                        { binding: 0, resource: artwork.createView({ format: "rgba8unorm-srgb", dimension: "2d-array", baseMipLevel: level - 1, mipLevelCount: 1 }) },
                        { binding: 1, resource: scratch.createView({ dimension: "2d-array" }) },
                        { binding: 2, resource: { buffer: table } }
                    ] });
                    const pass = encoder.beginComputePass();
                    pass.setPipeline(pipeline); pass.setBindGroup(0, bindings);
                    pass.dispatchWorkgroups(Math.ceil(size / 8), Math.ceil(size / 8), dirty.length); pass.end();
                    for (const layer of dirty) encoder.copyTextureToTexture(
                        { texture: scratch, origin: [0, 0, layer] }, { texture: artwork, mipLevel: level, origin: [0, 0, layer] }, [size, size, 1]);
                }
                device.queue.submit([encoder.finish()]);
                const results = [];
                for (let layer = 0; layer < 4; layer++) {
                    const base = await read(artwork, "byte", layer, 0);
                    if (!base.values.every((value, index) => value === inputs[layer][index]))
                        throw new Error(`Terrain mip generation modified original layer ${layer} bytes`);
                    for (const level of [1, 4, 8]) results.push({ layer, level, ...(await read(artwork, "byte", layer, level)) });
                }
                return { results, baseBytesPreserved: 4 * 256 * 256 * 4, dirtyLayers: [...dirty],
                    description: "Production compute shader, sRGB input views, reusable unorm storage scratch and ordered per-layer copies; extra dirty-buffer capacity is intentionally not dispatched." };
            }
            try {
                const bloomResults = [];
                bloomResults.push(await bloomCase("subthreshold constant", 65, 49, 5, [.03125, .0625, .125]));
                bloomResults.push(await bloomCase("HDR constant odd extent", 65, 49, 6, [16, 2, .25]));
                bloomResults.push(await bloomCase("photograph shares linear scattering", 31, 17, 5, [8, 1, .25], false, [.25, 2, 4]));
                bloomResults.push(await bloomCase("one-axis pyramid", 1, 33, 6, [4, 2, 1]));
                bloomResults.push(await bloomCase("HDR impulse High", 128, 96, 5, [64, 16, 4], true));
                bloomResults.push(await bloomCase("HDR impulse Ultra odd extent", 65, 49, 6, [64, 16, 4], true));
                // Terrain and facade fixtures use the same helpers/device but
                // are independent of the live application's no-readback path.
                const fixtureResult = { bloom: bloomResults, facade: await facadeCases(), terrain: await terrainCases() };
                const scopeError = await device.popErrorScope();
                if (scopeError) errors.push(scopeError.message);
                await device.queue.onSubmittedWorkDone();
                if (errors.length) throw new Error(errors.join("\n"));
                return fixtureResult;
            } finally {
                for (const resource of resources) resource.destroy();
                device.destroy();
            }
        }, { bloom, mip, facade });
        let timer;
        const result = await Promise.race([evaluation, new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error("Numerical WebGPU shader fixtures exceeded their 120-second GPU/readback deadline")), 120000);
        })]).finally(() => clearTimeout(timer));
        const summary = verifyBloom(result.bloom), facadeSummary = verifyFacade(result.facade, facade), terrainSummary = verifyTerrain(result.terrain);
        await writeFile(join(output, "shader-fixtures.json"), JSON.stringify({ passed: true, bloom: summary,
            facade: facadeSummary, terrain: terrainSummary }, null, 2));
        return { name: "numerical-webgpu-shaders", bloom: summary.length, facade: facadeSummary, terrain: terrainSummary };
    } finally { await context.close(); }
}

function verifyTerrain(actual) {
    const decode = value => value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4;
    const encode = value => value <= .0031308 ? value * 12.92 : 1.055 * value ** (1 / 2.4) - .055;
    assert.deepEqual(actual.dirtyLayers, [3, 0, 2], "Terrain fixture dispatches nonsequential physical layers");
    assert.equal(actual.results.length, 12, "Every dirty and clean layer has all requested readbacks");
    const keys = actual.results.map(value => `${value.layer}:${value.level}`).sort();
    assert.deepEqual(keys, [0, 1, 2, 3].flatMap(layer => [1, 4, 8].map(level => `${layer}:${level}`)).sort(),
        "Terrain readbacks have exactly the required unique layer/mip pairs");
    let comparedBytes = 0, maximumError = 0;
    const summaries = [];
    for (let layer = 0; layer < 4; layer++) {
        let size = 256, pixels = new Uint8Array(size * size * 4);
        for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
            const checker = (x + y) & 1;
            const color = layer === 0 ? [checker * 255, checker * 255, checker * 255, checker * 255] :
                layer === 1 ? [31, 63, 127, 223] :
                layer === 2 ? [x < 127 ? 8 : 240, y < 129 ? 32 : 192, checker * 128, 255] :
                [17 + x % 64, 43 + y % 128, 7 + (x * 13 + y * 7) % 249, (x * 3 + y * 5) % 256];
            pixels.set(color, (y * size + x) * 4);
        }
        for (let level = 1; level < 9; level++) {
            const nextSize = size / 2, next = new Uint8Array(nextSize * nextSize * 4);
            for (let y = 0; y < nextSize; y++) for (let x = 0; x < nextSize; x++) for (let c = 0; c < 4; c++) {
                let value = 0;
                for (let dy = 0; dy < 2; dy++) for (let dx = 0; dx < 2; dx++) {
                    const byte = pixels[((y * 2 + dy) * size + x * 2 + dx) * 4 + c] / 255;
                    value += (c === 3 ? byte : decode(byte)) / 4;
                }
                next[(y * nextSize + x) * 4 + c] = Math.round((c === 3 ? value : encode(value)) * 255);
            }
            pixels = next; size = nextSize;
            const measured = actual.results.find(value => value.layer === layer && value.level === level);
            if (!measured) continue;
            assert.deepEqual([measured.width, measured.height], [size, size]);
            assert.equal(measured.values.length, pixels.length, "Terrain readback preserves every channel");
            let error = 0;
            for (let i = 0; i < pixels.length; i++) {
                const delta = Math.abs(measured.values[i] - pixels[i]);
                error = Math.max(error, delta); comparedBytes++;
                // Allow at most two encoded-byte roundings across eight levels;
                // 127/128 gamma-space averages cannot satisfy this oracle.
                assert.ok(delta <= (layer === 1 ? 0 : 2), `Terrain layer ${layer} mip ${level} byte ${i}: expected ${pixels[i]}, got ${measured.values[i]}`);
            }
            maximumError = Math.max(maximumError, error);
            summaries.push({ layer, mip: level, comparedBytes: pixels.length, maximumError: error });
        }
    }
    assert.equal(actual.baseBytesPreserved, 1_048_576);
    assert.equal(comparedBytes, 266_256);
    return { comparedBytes, maximumError, baseBytesPreserved: actual.baseBytesPreserved,
        dirtyLayers: actual.dirtyLayers, levels: summaries, description: actual.description };
}

function verifyFacade(actual, fixture) {
    assert.equal(actual.albedoRoughness.length, fixture.width * 4);
    assert.equal(actual.emissionMetallic.length, fixture.width * 4);
    let maximumError = 0, maximumNormalizedError = 0, maximumContinuityDelta = 0;
    const errors = {};
    for (const [index, expected] of fixture.expected.entries()) {
        const values = actual.albedoRoughness.slice(index * 4, index * 4 + 4)
            .concat(actual.emissionMetallic.slice(index * 4, index * 4 + 4));
        const reference = [...expected.albedo, expected.roughness, ...expected.emission, expected.metallic];
        for (let channel = 0; channel < 8; channel++) {
            const error = Math.abs(values[channel] - reference[channel]);
            assert.ok(Number.isFinite(values[channel]) && error <= expected.tolerance,
                `${expected.name}: channel ${channel} expected ${reference[channel]}, got ${values[channel]} (limit ${expected.tolerance})`);
            maximumError = Math.max(maximumError, error);
            maximumNormalizedError = Math.max(maximumNormalizedError, error / expected.tolerance);
            const style = expected.name.split("-")[0];
            errors[style] = Math.max(errors[style] || 0, error);
        }
    }
    assert.ok(fixture.continuity?.length > 0, "Normal facade mip transition pairs are present");
    for (const pair of fixture.continuity) {
        assert.ok(pair.leftIndex >= fixture.expected.length && pair.leftIndex < fixture.width &&
            pair.rightIndex >= fixture.expected.length && pair.rightIndex < fixture.width && pair.leftIndex !== pair.rightIndex,
            pair.name + ": continuity indices refer to additional independent probes");
        for (const target of [actual.albedoRoughness, actual.emissionMetallic]) for (let channel = 0; channel < 4; channel++) {
            const delta = Math.abs(target[pair.leftIndex * 4 + channel] - target[pair.rightIndex * 4 + channel]);
            assert.ok(Number.isFinite(delta) && delta <= pair.tolerance,
                `${pair.name}: material channel ${channel} jumps by ${delta} (limit ${pair.tolerance})`);
            maximumContinuityDelta = Math.max(maximumContinuityDelta, delta);
        }
    }
    return { cases: fixture.expected.length, comparedChannels: fixture.expected.length * 8,
        maximumError, maximumNormalizedError, tolerances: [...new Set(fixture.expected.map(value => value.tolerance))],
        maximumErrorByStyle: errors, continuityPairs: fixture.continuity.length, maximumContinuityDelta,
        reference: fixture.reference };
}

// CPU reference integrates the documented filters independently of the WGSL.
// Compare every output texel, including clamped edges and odd-size reconstruction.
function verifyBloom(cases) {
    const taps = [[-2,-2,1], [0,-2,2], [2,-2,1], [-2,0,2], [0,0,4], [2,0,2], [-2,2,1], [0,2,2], [2,2,1], [-1,-1,4], [1,-1,4], [-1,1,4], [1,1,4]];
    const sample = (image, u, v, channel) => {
        const x = u * image.width - .5, y = v * image.height - .5, x0 = Math.floor(x), y0 = Math.floor(y);
        const read = (ix, iy) => image.values[(Math.max(0, Math.min(image.height - 1, iy)) * image.width + Math.max(0, Math.min(image.width - 1, ix))) * 4 + channel];
        const fx = x - x0, fy = y - y0;
        return (read(x0, y0) * (1 - fx) + read(x0 + 1, y0) * fx) * (1 - fy) +
            (read(x0, y0 + 1) * (1 - fx) + read(x0 + 1, y0 + 1) * fx) * fy;
    };
    return cases.map(test => {
        const { width, height, color, impulse, photoColor } = test.input;
        let source = { width, height, values: new Float64Array(width * height * 4) };
        for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) for (let channel = 0; channel < 3; channel++) {
            let value = !impulse || (x === Math.floor(width / 2) && y === Math.floor(height / 2)) ? color[channel] : 0;
            if (photoColor) value = value * .65 + photoColor[channel] * .35;
            source.values[(y * width + x) * 4 + channel] = value;
        }
        const down = [];
        for (let level = 0; level < test.levels; level++) {
            const target = { width: Math.ceil(source.width / 2), height: Math.ceil(source.height / 2) };
            target.values = new Float64Array(target.width * target.height * 4);
            for (let y = 0; y < target.height; y++) for (let x = 0; x < target.width; x++) for (let c = 0; c < 3; c++)
                target.values[(y * target.width + x) * 4 + c] = taps.reduce((sum, [dx, dy, weight]) => sum +
                    sample(source, (x + .5) / target.width + dx / source.width, (y + .5) / target.height + dy / source.height, c) * weight / 32, 0);
            down.push(target); source = target;
        }
        for (let level = down.length - 2; level >= 0; level--) {
            const detail = down[level], target = { width: detail.width, height: detail.height, values: new Float64Array(detail.values.length) };
            for (let y = 0; y < target.height; y++) for (let x = 0; x < target.width; x++) for (let c = 0; c < 3; c++) {
                const u = (x + .5) / target.width, v = (y + .5) / target.height;
                let wide = 0;
                for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++)
                    wide += sample(source, u + dx / source.width, v + dy / source.height, c) * (dx === 0 ? 2 : 1) * (dy === 0 ? 2 : 1) / 16;
                target.values[(y * target.width + x) * 4 + c] = sample(detail, u, v, c) * .35 + wide * .65;
            }
            source = target;
        }
        assert.deepEqual([test.result.width, test.result.height], [source.width, source.height], test.name + ": output extent");
        let maximumError = 0;
        for (let i = 0; i < source.values.length; i++) {
            const actual = test.result.values[i], expected = i % 4 === 3 ? 1 : source.values[i];
            assert.ok(Number.isFinite(actual) && actual >= 0, test.name + ": finite nonnegative HDR radiance");
            maximumError = Math.max(maximumError, Math.abs(actual - expected));
            assert.ok(Math.abs(actual - expected) <= .002 + Math.abs(expected) * .008,
                `${test.name}: texel channel ${i} expected ${expected}, received ${actual}`);
        }
        return { name: test.name, width, height, levels: test.levels, maximumError, comparedChannels: source.values.length };
    });
}

module.exports = { runShaderFixtures, rawStrings, verifyBloom, verifyFacade, verifyTerrain };
