#!/usr/bin/env node
// Offline audit only. Executes the frozen, hashed pure summary/contract helpers;
// does not load Playwright, start Chromium, or change the supplied evidence.
"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");
const {createHash} = require("node:crypto");
const frozen = path.join(__dirname, "frozen-harness");
const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "source-provenance.json")));
const digest = bytes => createHash("sha256").update(bytes).digest("hex");
for (const name of ["performance.cjs", "play-performance.cjs", "camera-replay.cjs", "camera-replay-probe.cjs", "camera-replay-contract.cjs"])
    assert.equal(digest(fs.readFileSync(path.join(frozen, name))),
        manifest.harness["tests/browser/" + name].sha256, "Frozen helper hash matches its source manifest");
const {checkStep, compareReplays, sha256} = require("./frozen-harness/camera-replay-contract.cjs");
const {makeTrace, scenarios: replayScenarios} = require("./frozen-harness/camera-replay.cjs");
const playSource = fs.readFileSync(path.join(frozen, "play-performance.cjs"), "utf8");
const playScenarioStart = playSource.indexOf("const city = "), playScenarioEnd = playSource.indexOf("const digest = ");
assert.ok(playScenarioStart >= 0 && playScenarioEnd > playScenarioStart);
const playScenarios = Function(playSource.slice(playScenarioStart, playScenarioEnd) + "return scenarios;")();
function read(file) {
    if (!fs.existsSync(file) && fs.existsSync(file + ".gz")) file += ".gz";
    const bytes = fs.readFileSync(file);
    return JSON.parse(file.endsWith(".gz") ? zlib.gunzipSync(bytes) : bytes);
}
function pureFunction(file, signature, dependencies = {}) {
    const source = fs.readFileSync(path.join(frozen, file), "utf8");
    const start = source.indexOf(signature), end = source.indexOf("\n}\n", start);
    assert.ok(start >= 0 && end > start, "Frozen pure helper has its expected source boundary");
    // These are the verbatim trusted local helpers, not code from the artifact.
    return Function(...Object.keys(dependencies), "return (" + source.slice(start, end + 2) + ");")
        (...Object.values(dependencies));
}
const distribution = pureFunction("performance.cjs", "function distribution(values) {");
const summarizeProbe = pureFunction("performance.cjs", "function summarizeProbe(probe, before, after) {", {distribution});
const summarizeProfile = pureFunction("play-performance.cjs", "function summarizeProfile(profile) {");
function auditCapture(capture, data) {
    const derived = summarizeProbe(data.raw, data.before, data.after);
    for (const key of Object.keys(derived))
        assert.deepEqual(capture[key], derived[key], "Raw-derived capture field: " + key);
    assert.ok(Number.isFinite(capture.durationMs) && capture.durationMs > 0);
    assert.ok(Number.isFinite(capture.metricWindowMs) && capture.metricWindowMs > 0);
    assert.ok(Number.isFinite(capture.cpu.TaskDurationMs) && capture.cpu.TaskDurationMs >= 0);
    for (const values of [data.raw.frames.map(frame => frame[0]), data.raw.resolves.map(frame => frame.time)])
        assert.ok(values.every((time, i) => Number.isFinite(time) && (i === 0 || time >= values[i - 1])),
            "Raw callback/submission timestamps are finite and ordered");
    assert.ok(data.raw.resolves.every(frame => Number.isInteger(frame.id) && frame.id > 0));
    assert.equal(new Set(data.raw.resolves.map(frame => frame.id)).size, data.raw.resolves.length,
        "Submitted map IDs are unique within the process capture");
    for (const call of data.raw.syncCalls)
        if (call.resolveSignalLatencyMs != null)
            assert.ok(Number.isFinite(call.resolveSignalLatencyMs) && call.resolveSignalLatencyMs >= 0 &&
                Number.isInteger(call.resolveId) && call.resolveId > 0,
                "Observed signal latencies are finite and nonnegative");
}
function profileShape(profile) {
    const nodes = new Map(), parents = new Map();
    for (const node of profile.nodes) {
        assert.ok(Number.isInteger(node.id) && !nodes.has(node.id), "Unique integer profile node IDs");
        assert.ok(typeof node.callFrame?.functionName === "string", "Profile node has a call frame");
        nodes.set(node.id, node);
    }
    for (const node of profile.nodes) for (const child of node.children || []) {
        assert.ok(nodes.has(child) && !parents.has(child), "Profile children exist and have one parent");
        parents.set(child, node.id);
    }
    for (const node of profile.nodes) {
        const visited = new Set();
        for (let id = node.id; id !== undefined; id = parents.get(id)) {
            assert.ok(!visited.has(id), "Profile parent graph has no cycles");
            visited.add(id);
        }
    }
    assert.ok(Array.isArray(profile.samples) && profile.samples.length > 0);
    assert.equal(profile.timeDeltas?.length, profile.samples.length);
    assert.ok(profile.samples.every(id => nodes.has(id)), "All sampled nodes exist");
    assert.ok(profile.timeDeltas.every(t => Number.isFinite(t) && t >= 0), "Recorded profile deltas are nonnegative");
    assert.ok(Number.isFinite(profile.startTime) && Number.isFinite(profile.endTime) && profile.endTime > profile.startTime);
}
function play(reportPath, rawPath, profilePath) {
    const report = read(reportPath), data = read(rawPath);
    assert.equal(report.cases.length, 1);
    const c = report.cases[0], raw = data.raw;
    assert.deepEqual(c.scenario, playScenarios.find(item => item.name === c.name), "Published play scenario is unchanged");
    auditCapture(c, data);
    const times = raw.bufferReads.filter(item => item.pixelPack).map(item => item.time);
    assert.equal(c.completedArtFrames, times.length);
    assert.equal(c.completedArtFramesPerSecond, times.length * 1000 / raw.durationMs);
    assert.deepEqual(c.completedArtFrameIntervals, distribution(times.slice(1).map((t, i) => t - times[i])));
    assert.equal(c.rafPerSecond, raw.frames.length * 1000 / raw.durationMs);
    assert.equal(c.cpuTaskPercent, c.cpu.TaskDurationMs / c.metricWindowMs * 100);
    if (profilePath) {
        const profile = read(profilePath);
        profileShape(profile);
        assert.deepEqual(c.profile, summarizeProfile(profile), "All recorded top-45 profile fields rederive exactly");
    } else assert.equal(c.profile, undefined, "Unrequested processes do not select another profile");
    return {valid: true, captureFieldsRecomputed: Object.keys(summarizeProbe(raw, data.before, data.after)).length,
        profileRecomputed: Boolean(profilePath)};
}
function replay(reportPath) {
    const report = read(reportPath), cases = [];
    // Self-comparison independently checks schema, hashes, ordering and metadata.
    compareReplays(report, report);
    for (const c of report.cases) {
        assert.deepEqual(c.scenario, replayScenarios.find(item => item.name === c.name), "Published replay scenario is unchanged");
        assert.deepEqual(c.trace, makeTrace(c.scenario, c.map), "All 32 ordered native inputs equal the published trace");
        const data = read(path.join(path.dirname(reportPath), c.name + "-raw.json"));
        assert.equal(data.rawSteps.length, c.trace.length);
        assert.equal(c.trace.length, 32, "Published trace retains all 32 inputs");
        const steps = data.rawSteps.map((raw, index) => checkStep(raw, c.trace[index], index));
        assert.deepEqual(c.steps, steps, "Every reported step rederives exactly from raw native inputs and fences");
        const submissions = steps.reduce((sum, step) => sum + step.projection.length, 0);
        assert.equal(data.raw.resolves.length, submissions, "No full-capture submission is outside the step sequence");
        const stepSizes = steps.flatMap(step => step.projection.map(p => [p.width, p.height, p.compositorContext]));
        assert.deepEqual(data.raw.resolves.map(p => [p.width, p.height, p.compositorContext]), stepSizes,
            "The independent probes retain the same ordered submitted dimensions and contexts");
        auditCapture(c.summary.capture, data);
        const captured = summarizeProbe(data.raw, data.before, data.after);
        assert.deepEqual(c.summary, {steps: steps.length, submissions, taskCpuMs: captured.cpu.TaskDurationMs,
            taskCpuPerStepMs: captured.cpu.TaskDurationMs / steps.length,
            inputToFirstSubmission: distribution(steps.map(step => step.inputToFirstSubmissionMs)),
            inputToFirstFenceSignal: distribution(steps.map(step => step.inputToFirstFenceSignalMs)),
            inputToSettledFenceSignal: distribution(steps.map(step => step.inputToSettledFenceSignalMs)),
            capture: captured}, "Replay summary rederives exactly");
        if (c.beforeHud !== null) assert.deepEqual(c.afterHud, c.beforeHud, "Paused physical telemetry remains exact");
        cases.push({name: c.name, steps: steps.length, submissions, traceSha256: sha256(c.trace),
            projectionSequenceSha256: sha256(steps.map(step => step.projection))});
    }
    return {valid: true, cases};
}
try {
    const [action, ...args] = process.argv.slice(2);
    let result;
    if (action === "play") result = play(...args);
    else if (action === "replay") result = replay(...args);
    else if (action === "compare") {
        const comparison = compareReplays(read(args[0]), read(args[1]));
        result = {valid: true, ...comparison, comparisons: comparison.comparisons.map(c =>
            Object.fromEntries(Object.entries(c).filter(([key]) => !["baseline", "candidate"].includes(key))))};
    } else throw Error("Expected play, replay or compare action");
    process.stdout.write(JSON.stringify(result) + "\n");
} catch (error) {
    process.stdout.write(JSON.stringify({valid: false, error: error.message, stack: error.stack}) + "\n");
    process.exitCode = 1;
}
