// Pixel quality fixtures have their own references. They do not replace the
// continuous-style lighting/facade oracles or relax the live app's health gates.
const assert = require("node:assert/strict");
const {readFile, writeFile} = require("node:fs/promises");
const {join} = require("node:path");

async function executePixelArtFixture(fixture) {
    const adapter = await navigator.gpu.requestAdapter();
    if (!adapter) throw new Error("No WebGPU adapter for pixel-art fixtures");
    const device = await adapter.requestDevice(), errors = [], resources = [];
    let lost = null;
    device.addEventListener("uncapturederror", event => errors.push(event.error.message));
    device.lost.then(info => { lost = {reason: info.reason, message: info.message}; });
    device.pushErrorScope("validation");
    const texture = (width, height, format, usage) => {
        const result = device.createTexture({size: [width, height], format, usage});
        resources.push(result); return result;
    };
    const buffer = (size, usage) => {
        const result = device.createBuffer({size, usage}); resources.push(result); return result;
    };
    async function module(code) {
        const result = device.createShaderModule({code});
        const messages = (await result.getCompilationInfo()).messages.filter(value => value.type === "error");
        if (messages.length) throw new Error(messages.map(value => `${value.lineNum}:${value.linePos} ${value.message}`).join("\n"));
        return result;
    }
    async function read(image, floating = false) {
        const stride = floating ? 16 : 4, bytesPerRow = Math.ceil(image.width * stride / 256) * 256;
        const staging = buffer(bytesPerRow * image.height, GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ);
        const encoder = device.createCommandEncoder();
        encoder.copyTextureToBuffer({texture: image}, {buffer: staging, bytesPerRow}, [image.width, image.height]);
        device.queue.submit([encoder.finish()]);
        await staging.mapAsync(GPUMapMode.READ);
        const mapped = new Uint8Array(staging.getMappedRange()), values = [];
        for (let y = 0; y < image.height; y++) {
            const bytes = mapped.slice(y * bytesPerRow, y * bytesPerRow + image.width * stride);
            values.push(...(floating ? new Float32Array(bytes.buffer) : bytes));
        }
        staging.unmap(); return values;
    }
    function draw(pipeline, group, target) {
        const encoder = device.createCommandEncoder();
        const pass = encoder.beginRenderPass({colorAttachments: [{view: target.createView(), loadOp: "clear", storeOp: "store"}]});
        pass.setPipeline(pipeline); pass.setBindGroup(0, group); pass.draw(3); pass.end();
        device.queue.submit([encoder.finish()]);
    }
    try {
        if (fixture.strideFloats !== 12 || fixture.inputs.length !== fixture.width * 12)
            throw new Error("Pixel fixture's shader storage layout is inconsistent");
        const inputs = buffer(fixture.inputs.length * 4, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST);
        device.queue.writeBuffer(inputs, 0, new Float32Array(fixture.inputs));
        const shader = await module(fixture.wgsl);
        const pipeline = device.createRenderPipeline({layout: "auto", vertex: {module: shader, entryPoint: "vs"},
            fragment: {module: shader, entryPoint: "fs", targets: [{format: "rgba32float"}]}});
        const group = device.createBindGroup({layout: pipeline.getBindGroupLayout(0), entries: [{binding: 0, resource: {buffer: inputs}}]});
        const target = texture(fixture.width, 1, "rgba32float", GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC);
        draw(pipeline, group, target);
        const probes = await read(target, true);

        // Exercise the production resolve, with an encoded UNORM scene. Its
        // shader explicitly decodes only when the borrowed target is sRGB; the
        // target then encodes once. This catches dark-value double conversions.
        const sourceWidth = fixture.colors.length / 4, scale = 3;
        const usage = GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST;
        const scene = texture(sourceWidth, 1, "rgba8unorm", usage), photo = texture(sourceWidth, 1, "rgba8unorm", usage);
        const overlay = texture(1, 1, "rgba8unorm", usage);
        device.queue.writeTexture({texture: scene}, Uint8Array.from(fixture.colors), {bytesPerRow: sourceWidth * 4}, [sourceWidth, 1]);
        const photograph = new Uint8Array(fixture.colors.length);
        for (let i = 0; i < sourceWidth; i++) {
            photograph.set(fixture.colors.slice(((i + 37) % sourceWidth) * 4, ((i + 37) % sourceWidth) * 4 + 4), i * 4);
            photograph[i * 4 + 3] = i % 3 === 0 ? 0 : 255;
        }
        device.queue.writeTexture({texture: photo}, photograph, {bytesPerRow: sourceWidth * 4}, [sourceWidth, 1]);
        const overlayBytes = [15, 23, 31, 64];
        device.queue.writeTexture({texture: overlay}, Uint8Array.from(overlayBytes), {bytesPerRow: 4}, [1, 1]);
        const sampler = device.createSampler({minFilter: "linear", magFilter: "linear"});
        const settings = buffer(64, GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST);
        const resolve = await module(fixture.resolveWgsl), pipelines = new Map();
        const resolves = [];
        for (const sample of [
            {name: "encoded byte identity", format: "rgba8unorm", photographOpacity: 0, overlay: false},
            {name: "sRGB borrowed target identity", format: "rgba8unorm-srgb", photographOpacity: 0, overlay: false},
            {name: "water and photograph preservation", format: "rgba8unorm", photographOpacity: 209 / 255, overlay: false},
            {name: "premultiplied overlay and photograph on sRGB", format: "rgba8unorm-srgb", photographOpacity: .42, overlay: true}
        ]) {
            let presentation = pipelines.get(sample.format);
            if (!presentation) {
                presentation = device.createRenderPipeline({layout: "auto", vertex: {module: resolve, entryPoint: "vs"},
                    fragment: {module: resolve, entryPoint: "fs", targets: [{format: sample.format}]}});
                pipelines.set(sample.format, presentation);
            }
            const width = sourceWidth * scale, height = scale;
            device.queue.writeBuffer(settings, 0, new Float32Array([sourceWidth, 1, 0, 0, width, height, width, height,
                0, scale, sample.photographOpacity, sample.overlay ? 1 : 0, 0, 0, 1, sample.format.endsWith("srgb") ? 1 : 0]));
            const bindings = device.createBindGroup({layout: presentation.getBindGroupLayout(0), entries: [
                {binding: 0, resource: {buffer: settings}}, {binding: 1, resource: scene.createView()},
                {binding: 2, resource: photo.createView()}, {binding: 3, resource: overlay.createView()},
                {binding: 4, resource: sampler}, {binding: 5, resource: scene.createView()}
            ]});
            const image = texture(width, height, sample.format, GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.COPY_SRC);
            draw(presentation, bindings, image);
            resolves.push({...sample, width, height, values: await read(image)});
        }
        const scoped = await device.popErrorScope();
        if (scoped) errors.push(scoped.message);
        await device.queue.onSubmittedWorkDone();
        if (errors.length || lost) throw new Error(JSON.stringify({errors, lost}));
        return {probes, resolves, photograph: [...photograph], overlayBytes, errors, lost};
    } finally {
        for (const resource of resources) resource.destroy();
        device.destroy();
    }
}

function verifyPixelArt(actual, fixture) {
    assert.deepEqual(actual.errors, []); assert.equal(actual.lost, null);
    assert.ok(Array.isArray(fixture.colors) && fixture.colors.length % 4 === 0);
    assert.equal(fixture.displayPalette.length, 17);
    assert.ok(fixture.displayPalette.every(color => Array.isArray(color) && color.length === 3 &&
        color.every(value => Number.isInteger(value) && value >= 0 && value <= 255)), "The fixture preserves explicit encoded RGB palette bytes");
    assert.equal(fixture.expected.length, fixture.width);
    assert.equal(actual.probes.length, fixture.width * 4);
    let maximumProbeError = 0;
    for (const [index, expected] of fixture.expected.entries()) {
        for (let channel = 0; channel < 4; channel++) {
            const observed = actual.probes[index * 4 + channel], wanted = expected.values[channel];
            assert.ok(Number.isFinite(observed), expected.name + ": finite shader output");
            const error = Math.abs(observed - wanted); maximumProbeError = Math.max(maximumProbeError, error);
            assert.ok(error <= expected.tolerance, `${expected.name}, channel ${channel}: ${observed} differs from ${wanted}`);
        }
    }
    let previous = null;
    const regions = [];
    for (const region of fixture.regions) {
        const mask = Array.from({length: region.width * region.height}, (_, i) => actual.probes[(region.start + i) * 4]);
        assert.ok(mask.includes(0) && mask.includes(1) && mask.includes(2), region.name + ": actual GPU retains masonry, windows and ledges");
        if (previous) assert.deepEqual(mask, previous, "Nested room groups preserve the connected projected motif");
        previous = mask;
        for (let y = 0; y < region.height; y++) for (let x = 0; x < region.width; x++) {
            if (mask[y * region.width + x] !== 1) continue;
            const across = x > 0 && mask[y * region.width + x - 1] === 1 || x + 1 < region.width && mask[y * region.width + x + 1] === 1;
            const vertical = y > 0 && mask[(y - 1) * region.width + x] === 1 || y + 1 < region.height && mask[(y + 1) * region.width + x] === 1;
            assert.ok(across && vertical, region.name + ": no isolated window pixels or stripes");
        }
        regions.push({name: region.name, windowPixels: mask.filter(value => value === 1).length,
            ledgePixels: mask.filter(value => value === 2).length, masonryPixels: mask.filter(value => value === 0).length});
    }
    assert.equal(actual.resolves.length, 4);
    const sourceWidth = fixture.colors.length / 4, resolves = [];
    for (const sample of actual.resolves) {
        assert.equal(sample.width, sourceWidth * 3); assert.equal(sample.height, 3);
        assert.equal(sample.values.length, sample.width * sample.height * 4);
        let maximumByteError = 0;
        for (let y = 0; y < 3; y++) for (let x = 0; x < sample.width; x++) {
            const source = Math.floor(x / 3) * 4, destination = (y * sample.width + x) * 4;
            for (let channel = 0; channel < 3; channel++) {
                let expected = fixture.colors[source + channel];
                if (sample.photographOpacity > 0 && actual.photograph[source + 3] > 127)
                    expected = Math.floor(expected * (1 - sample.photographOpacity) + actual.photograph[source + channel] * sample.photographOpacity);
                if (sample.overlay) expected = Math.round(actual.overlayBytes[2 - channel] + expected * (1 - actual.overlayBytes[3] / 255));
                const value = sample.values[destination + channel], error = Math.abs(value - expected);
                maximumByteError = Math.max(maximumByteError, error);
                assert.ok(error <= (sample.name === "encoded byte identity" ? 0 : 1), `${sample.name}: encoded byte ${source / 4}, channel ${channel} changed by ${error}`);
                assert.equal(value, sample.values[(Math.floor(x / 3) * 3) * 4 + channel], sample.name + ": every art cell resolves as one exact 3×3 block");
            }
            assert.equal(sample.values[destination + 3], 255, sample.name + ": output stays opaque");
        }
        resolves.push({name: sample.name, format: sample.format, encodedColors: sourceWidth, outputPixels: sample.width * sample.height,
            maximumByteError, exactNearestNeighbourBlocks: true});
    }
    return {probes: fixture.width, maximumProbeError, regions, resolves, work: fixture.work};
}

async function runPixelArtFixtures(browser, base, output) {
    const path = process.env.PIXELMAP_PIXEL_ART_FIXTURE || join(output, "pixel-art-fixture.json");
    const fixture = JSON.parse(await readFile(path, "utf8"));
    const context = await browser.newContext();
    try {
        const page = await context.newPage(), url = new URL("__pixel-art-fixtures.html", base).href;
        await page.route(url, route => route.fulfill({contentType: "text/html", body: "<!doctype html><title>Pixel-art quality fixtures</title>"}));
        await page.goto(url);
        let timer;
        const actual = await Promise.race([page.evaluate(executePixelArtFixture, fixture), new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error("Pixel-art WGSL/readback fixture exceeded 120 seconds")), 120000);
        })]).finally(() => clearTimeout(timer));
        const summary = verifyPixelArt(actual, fixture);
        await writeFile(join(output, "pixel-art-fixtures.json"), JSON.stringify({passed: true, ...summary}, null, 2));
        const {PNG} = require("pngjs"), scale = 8, gap = 8;
        const image = new PNG({width: fixture.regions.length * (24 * scale + gap) - gap, height: 24 * scale});
        for (let row = 0; row < image.height; row++) for (let column = 0; column < image.width; column++) {
            const index = (row * image.width + column) * 4;
            const group = Math.floor(column / (24 * scale + gap)), x = column % (24 * scale + gap);
            let color = [23, 43, 64];
            if (x < 24 * scale) {
                const region = fixture.regions[group], part = actual.probes[(region.start + Math.floor(row / scale) * region.width + Math.floor(x / scale)) * 4];
                color = fixture.displayPalette[part === 1 ? 5 : part === 2 ? 12 : 8];
            }
            image.data.set([...color, 255], index);
        }
        await writeFile(join(output, "pixel-art-connected-clusters.png"), PNG.sync.write(image));
        return {name: "pixel-art-quality", ...summary};
    } finally { await context.close(); }
}

module.exports = {runPixelArtFixtures, executePixelArtFixture, verifyPixelArt};
