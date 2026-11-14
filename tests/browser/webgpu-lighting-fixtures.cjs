// Compare the current production lighting with the frozen pre-optimization
// function on the same GPU. Test-only counters observe comparison-sample calls;
// they are work counts, not a claim about target-hardware GPU execution time.
const assert = require("node:assert/strict");
const { createHash } = require("node:crypto");

const referenceCommit = "a9a284263da76a201114381467bf45aa4823a2d6";
const referenceSha256 = "e91c58f9d99f336af9cc837f2deae1282fdc83097adffc3b38d779a85b1046d5";

function buildLightingFixture(common, reference) {
    assert.equal(createHash("sha256").update(reference).digest("hex"), referenceSha256,
        "Lighting reference remains the unmodified function from the performance baseline");
    assert.ok(common?.includes("fn lightingLinear("), "Actual production lighting WGSL is exported by the native assembly");
    const uniform = "@group(0) @binding(0) var<uniform> f: Frame;";
    const comparison = "sum+=textureSampleCompareLevel(";
    assert.equal(common.split(uniform).length, 2, "The fixture replaces exactly one frame binding with per-invocation state");
    assert.equal(common.split(comparison).length, 2, "The fixture instruments exactly the production PCF sample expression");
    const production = common.replace(uniform, "var<private> f: Frame;")
        .replace(comparison, "fixtureComparisons+=1u; sum+=textureSampleCompareLevel(");
    const original = reference.replace("fn lightingLinear(", "fn lightingLinearReference(");
    const inputs = [];
    const normalize = v => { const length = Math.hypot(...v); return v.map(x => x / length); };
    const roughnesses = [0, .045, .16, .18, .32, .38, .82, 1];
    const worlds = [[0, 0, -30], [8, -5, -26], [-12, 10, -2], [0, 0, 40],
        [5, 8, 48], [-20, 12, 130], [0, 0, 148], [0, 0, 170], [250, 0, -30]];
    let index = 0;
    for (let style = 0; style < 4; style++) for (let palette = 0; palette < 3; palette++) {
        const sun = normalize([-.45, -.55, palette === 1 ? .35 : .82]);
        const tangent = normalize([sun[1], -sun[0], 0]);
        const normals = [[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1],
            sun, sun.map(x => -x), tangent,
            ...[-1e-4, -1e-7, 1e-7, 1e-4].map(epsilon => normalize(tangent.map((x, axis) => x + epsilon * sun[axis])))];
        for (const cascades of [1, 2, 4]) for (const roughness of roughnesses)
            for (const metallic of [0, .28, 1]) for (const normal of normals) {
                const world = worlds[index % worlds.length], lit = style === 1 || style === 3;
                const albedo = index % 5 === 0 ? [0, 0, 0] : index % 7 === 0 ? [1, 1, 1] : [.18, .42, .67];
                const emission = palette === 2 ? [2.5, .7, .08] : [0, 0, 0];
                inputs.push(...albedo, roughness, ...world, metallic, ...normal, lit ? 1 : 0,
                    45, 55, 250, cascades, ...sun, palette, ...emission, style);
                index++;
            }
    }
    const wgsl = "var<private> fixtureComparisons: u32;\n" + production + original + `
        struct LightingCase {
            albedoRoughness: vec4f, worldMetallic: vec4f, normalEnable: vec4f,
            eyeCount: vec4f, sunPalette: vec4f, emissionStyle: vec4f,
        }
        @group(1) @binding(0) var<storage, read> cases: array<LightingCase>;
        @group(1) @binding(1) var<storage, read_write> results: array<vec4f>;
        @compute @workgroup_size(64) fn verifyLighting(@builtin(global_invocation_id) id: vec3u) {
            if(id.x>=arrayLength(&cases)){return;}
            let c=cases[id.x]; let night=c.sunPalette.w>1.5; let dusk=c.sunPalette.w>0.5&&!night;
            f.ground=vec4f(0.0,1.0,1.0,c.emissionStyle.w);
            f.sun=vec4f(c.sunPalette.xyz,c.normalEnable.w);
            f.sunColor=vec4f(1.0,select(0.94,0.68,dusk),select(0.82,0.4,dusk),5.0);
            f.eye=vec4f(c.eyeCount.xyz,select(0.0,c.eyeCount.w,c.normalEnable.w>0.5));
            f.quality=vec4f(1.05,0.00012,select(select(3.7,2.5,dusk),0.42,night),select(1.1,0.6,night));
            f.horizon=vec4f(select(select(vec3f(198,214,205),vec3f(210,170,161),dusk),vec3f(64,82,100),night)/255.0,0.0);
            f.shadowView=vec4f(0.0,0.0,1.0,50.0);f.shadowSplits=vec4f(25,50,100,200);
            f.shadowTexels=vec4f(0.02,0.1,0.5,2.0);f.shadowDepths=vec4f(0.001);
            f.shadowRow0=vec4f(0.02,0,0,0);f.shadowRow1=vec4f(0,0.02,0,0);f.shadowRow2=vec4f(0,0,0.001,0.45);
            f.shadowRow3=vec4f(0.01,0,0,0);f.shadowRow4=vec4f(0,0.01,0,0);f.shadowRow5=vec4f(0,0,0.001,0.45);
            f.shadowRow6=vec4f(0.005,0,0,0);f.shadowRow7=vec4f(0,0.005,0,0);f.shadowRow8=vec4f(0,0,0.001,0.45);
            f.shadowRow9=vec4f(0.0025,0,0,0);f.shadowRow10=vec4f(0,0.0025,0,0);f.shadowRow11=vec4f(0,0,0.001,0.45);
            fixtureComparisons=0u;
            let before=lightingLinearReference(c.albedoRoughness.xyz,c.worldMetallic.xyz,c.normalEnable.xyz,
                c.albedoRoughness.w,c.worldMetallic.w,c.emissionStyle.xyz);
            let originalCalls=fixtureComparisons;fixtureComparisons=0u;
            let after=lightingLinear(c.albedoRoughness.xyz,c.worldMetallic.xyz,c.normalEnable.xyz,
                c.albedoRoughness.w,c.worldMetallic.w,c.emissionStyle.xyz);
            let optimizedCalls=fixtureComparisons;
            let ndl=max(dot(normalize(c.normalEnable.xyz),normalize(c.sunPalette.xyz)),0.0);
            results[id.x*3u]=vec4f(before,f32(originalCalls));
            results[id.x*3u+1u]=vec4f(after,f32(optimizedCalls));
            results[id.x*3u+2u]=vec4f(ndl,c.emissionStyle.w,c.sunPalette.w,c.eyeCount.w);
        }`;
    return { wgsl, inputs, count: index, strideFloats: 24, outputStrideFloats: 12,
        referenceCommit, referenceSha256, styles: 4, palettes: 3, roughnesses, cascades: [1, 2, 4] };
}

// This function is serialized into the isolated browser fixture page. All GPU
// objects belong to the caller's test device/resource list, never the map device.
async function executeLightingFixture(device, texture, buffer, module, fixture) {
    const input = buffer(fixture.inputs.length * 4, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST);
    device.queue.writeBuffer(input, 0, new Float32Array(fixture.inputs));
    const bytes = fixture.count * fixture.outputStrideFloats * 4;
    const output = buffer(bytes, GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC);
    const readback = buffer(bytes, GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ);
    const shadow = texture(4, 4, "depth32float", { size: [4, 4, 4],
        usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING });
    const comparison = device.createSampler({ magFilter: "linear", minFilter: "linear", compare: "less-equal" });
    const shader = await module(fixture.wgsl);
    const pipeline = device.createComputePipeline({ layout: "auto", compute: { module: shader, entryPoint: "verifyLighting" } });
    const shadows = device.createBindGroup({ layout: pipeline.getBindGroupLayout(0), entries: [
        { binding: 4, resource: shadow.createView({ dimension: "2d-array" }) }, { binding: 5, resource: comparison }
    ] });
    const data = device.createBindGroup({ layout: pipeline.getBindGroupLayout(1), entries: [
        { binding: 0, resource: { buffer: input } }, { binding: 1, resource: { buffer: output } }
    ] });
    const encoder = device.createCommandEncoder();
    for (let layer = 0; layer < 4; layer++) {
        const pass = encoder.beginRenderPass({ colorAttachments: [], depthStencilAttachment: {
            view: shadow.createView({ dimension: "2d", baseArrayLayer: layer, arrayLayerCount: 1 }),
            depthLoadOp: "clear", depthStoreOp: "store", depthClearValue: [.42, .5, .58, .66][layer]
        } });
        pass.end();
    }
    const pass = encoder.beginComputePass();
    pass.setPipeline(pipeline);pass.setBindGroup(0, shadows);pass.setBindGroup(1, data);
    pass.dispatchWorkgroups(Math.ceil(fixture.count / 64));pass.end();
    encoder.copyBufferToBuffer(output, 0, readback, 0, bytes);
    device.queue.submit([encoder.finish()]);
    await readback.mapAsync(GPUMapMode.READ);
    const values = Array.from(new Float32Array(readback.getMappedRange().slice(0)));
    readback.unmap();
    return { values };
}

function verifyLighting(actual, fixture) {
    assert.equal(actual.values.length, fixture.count * 12, "Every lighting fixture has both results and work counters");
    let maximumError = 0, originalComparisons = 0, optimizedComparisons = 0, backlitCases = 0, omittedShadowCases = 0;
    const styles = new Set(), palettes = new Set(), cascades = new Set();
    for (let i = 0; i < fixture.count; i++) {
        const result = actual.values.slice(i * 12, (i + 1) * 12);
        assert.ok(result.every(Number.isFinite), "Lighting results and counters are finite at case " + i);
        for (let channel = 0; channel < 3; channel++) {
            const error = Math.abs(result[channel] - result[channel + 4]);
            maximumError = Math.max(maximumError, error);
            assert.ok(error <= 2e-6 * Math.max(1, Math.abs(result[channel])),
                `Lighting case ${i} channel ${channel} differs from the frozen original by ${error}`);
        }
        const before = result[3], after = result[7], ndl = result[8];
        assert.ok([0, 9, 18].includes(before) && [0, 9, 18].includes(after), "PCF comparison counts match one or two complete nine-tap cascades");
        if (ndl <= 0) {
            backlitCases++;
            assert.equal(after, 0, "A backlit fragment performs no shadow comparison samples");
            if (before > 0) omittedShadowCases++;
        } else assert.equal(after, before, "Front-lit and positive-grazing fragments retain every original shadow comparison");
        originalComparisons += before;optimizedComparisons += after;
        styles.add(result[9]);palettes.add(result[10]);cascades.add(result[11]);
    }
    assert.equal(styles.size, 4, "All render-style parameter sets are exercised");
    assert.equal(palettes.size, 3, "Day, dusk and night parameter sets are exercised");
    assert.deepEqual([...cascades].sort((a, b) => a - b), [1, 2, 4], "Every shadow-cascade quality is exercised");
    assert.ok(omittedShadowCases > 0 && optimizedComparisons < originalComparisons, "Backlit PCF work is actually eliminated");
    return { cases: fixture.count, comparedChannels: fixture.count * 3, maximumAbsoluteError: maximumError,
        backlitCases, omittedShadowCases, originalComparisons, optimizedComparisons,
        comparisonReduction: 1 - optimizedComparisons / originalComparisons,
        referenceCommit: fixture.referenceCommit, referenceSha256: fixture.referenceSha256,
        description: "Production lighting versus the frozen original on the same device; counters instrument comparison calls only. They do not measure GPU duration or full-frame speedup." };
}

module.exports = { buildLightingFixture, executeLightingFixture, verifyLighting };
