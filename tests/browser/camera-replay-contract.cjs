const assert = require("node:assert/strict");
const {createHash} = require("node:crypto");

const sha256 = value => createHash("sha256").update(JSON.stringify(value)).digest("hex");

function checkStep(raw, expected, index) {
    assert.equal(raw.inputs.length, 1, `Step ${index} receives exactly one native wheel event`);
    const {time, trusted, deltaMode, ...input} = raw.inputs[0];
    assert.equal(trusted, true, `Step ${index} uses trusted browser input`);
    assert.equal(deltaMode, 0, `Step ${index} uses pixel deltas`);
    assert.deepEqual(input, expected, `Step ${index} receives its exact ordered input`);
    assert.ok(Array.isArray(raw.resolves) && raw.resolves.length > 0 && raw.resolves.length <= 32,
        `Step ${index} produces a bounded nonempty map submission sequence`);
    assert.ok(Number.isFinite(raw.beganAt) && raw.beganAt <= time &&
        Number.isFinite(raw.finishedAt) && raw.lastResolveAt === raw.resolves.at(-1).time &&
        raw.quietForMs === raw.finishedAt - raw.lastResolveAt && raw.quietForMs >= 250,
        `Step ${index} retains the complete sequence through its quiet barrier`);
    assert.equal(raw.compositorBarrierResolves, raw.resolves.length,
        `Step ${index} completes its animation-frame barrier after the final submission`);
    let previousSubmission = time;
    const projection = raw.resolves.map((resolve, frame) => {
        const {time: submittedAt, signaledAt, ...projection} = resolve;
        assert.equal(projection.compositorContext, true, `Step ${index} uses the compositor context`);
        assert.ok(Number.isFinite(time) && Number.isFinite(submittedAt) && Number.isFinite(signaledAt) &&
            submittedAt >= previousSubmission && signaledAt >= submittedAt && signaledAt <= raw.finishedAt,
            `Step ${index} frame ${frame} observes its own ordered submission and signaled fence`);
        previousSubmission = submittedAt;
        assert.ok(projection.width > 0 && projection.height > 0 && projection.draws.length > 0,
            `Step ${index} submits nonempty map geometry`);
        const checkNumbers = value => {
            if (typeof value === "number") assert.ok(Number.isFinite(value), "Projection values are finite");
            else if (value && typeof value === "object") Object.values(value).forEach(checkNumbers);
        };
        checkNumbers(projection);
        for (const draw of projection.draws) {
            assert.ok(draw.uniforms.uArtSize?.length === 2 && draw.uniforms.uGround?.length === 3 &&
                draw.uniforms.uDepth?.length === 2 && draw.uniforms.uPerspective?.length === 1,
                `Step ${index} retains the submitted lens, datum and art dimensions`);
            assert.deepEqual(draw.uniforms.uArtSize, [projection.width, projection.height]);
            if (draw.kind === "terrain") {
                assert.equal(draw.tileProjections?.length, draw.instances,
                    `Step ${index} retains every terrain instance transform`);
                assert.ok(draw.tileProjections.every(transform => transform.length === 12));
            } else {
                assert.ok(["uScreenX", "uScreenY", "uScreenW"].every(name => draw.uniforms[name]?.length === 4),
                    `Step ${index} retains every geometry transform`);
            }
        }
        return projection;
    });
    return {index, input, inputToFirstSubmissionMs: raw.resolves[0].time - time,
        inputToFirstFenceSignalMs: raw.resolves[0].signaledAt - time,
        inputToSettledFenceSignalMs: Math.max(...raw.resolves.map(resolve => resolve.signaledAt)) - time,
        projectionSha256: sha256(projection), projection};
}

function compareReplays(baseline, candidate) {
    for (const report of [baseline, candidate]) {
        assert.equal(report.schema, 2, "Replay report schema is supported");
        assert.equal(report.protocol, "native-wheel-resolve-fence-v2", "Replay protocol is supported");
        assert.ok(typeof report.cpu === "string" && report.cpu.length > 0 &&
            Number.isInteger(report.availableParallelism) && report.availableParallelism > 0,
            "Replay runner identity is recorded");
        assert.ok(Array.isArray(report.cases) && report.cases.length > 0 && report.cases.length <= 16,
            "Replay contains a bounded nonempty scenario set");
        for (const item of report.cases) {
            assert.ok(Array.isArray(item.trace) && item.trace.length > 0 && item.trace.length <= 4096 &&
                Array.isArray(item.steps) && item.steps.length === item.trace.length,
                "Replay contains a bounded nonempty input sequence and all results");
            for (const step of item.steps)
                assert.ok(Array.isArray(step.projection) && step.projection.length > 0 && step.projection.length <= 32,
                    "Replay contains every bounded nonempty per-input submission sequence");
        }
    }
    for (const field of ["schema", "protocol", "viewport", "deviceScaleFactor", "browser", "browserArgs",
        "cpu", "availableParallelism", "harness"])
        assert.deepEqual(candidate[field], baseline[field], `Replay ${field} is identical`);
    assert.deepEqual(candidate.cases.map(item => item.name), baseline.cases.map(item => item.name),
        "Replay scenarios and their order are identical");
    const comparisons = [];
    for (let index = 0; index < baseline.cases.length; index++) {
        const before = baseline.cases[index], after = candidate.cases[index];
        assert.equal(before.traceSha256, sha256(before.trace), `${before.name}: baseline input trace hash is valid`);
        assert.equal(after.traceSha256, sha256(after.trace), `${before.name}: candidate input trace hash is valid`);
        for (const field of ["scenario", "map", "trace", "traceSha256"])
            assert.deepEqual(after[field], before[field], `${before.name}: ${field} is identical`);
        assert.equal(before.steps.length, before.trace.length, `${before.name}: baseline has every input result`);
        assert.equal(after.steps.length, before.steps.length, `${before.name}: candidate has every input result`);
        for (let sample = 0; sample < before.steps.length; sample++) {
            const a = before.steps[sample], b = after.steps[sample];
            assert.equal(a.index, sample);
            assert.equal(b.index, sample);
            assert.deepEqual(a.input, before.trace[sample]);
            assert.deepEqual(b.input, before.trace[sample]);
            assert.equal(a.projectionSha256, sha256(a.projection), `${before.name}: baseline projection hash is valid`);
            assert.equal(b.projectionSha256, sha256(b.projection), `${before.name}: candidate projection hash is valid`);
            assert.deepEqual(b.projection, a.projection,
                `${before.name}: step ${sample} submits identical projection inputs and draw dimensions`);
        }
        comparisons.push({name: before.name, exactProjectionSequence: true, steps: before.steps.length,
            submissions: before.steps.reduce((total, step) => total + step.projection.length, 0),
            traceSha256: before.traceSha256, projectionSequenceSha256: sha256(before.steps.map(step => step.projection)),
            baseline: before.summary, candidate: after.summary});
    }
    return {schema: 2, baseline: baseline.revision, candidate: candidate.revision,
        notes: "Exact ordered native inputs and every submitted projection float match. These are controlled " +
            "camera/render workloads with a fence barrier between inputs, not free-roam simulation or physical display FPS.",
        comparisons};
}

module.exports = {sha256, checkStep, compareReplays};
