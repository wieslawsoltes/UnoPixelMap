// Offline verification of the archived raw observations. No browser or npm
// dependency is required, and no current production/harness file is imported.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const {createHash} = require("node:crypto");
const {gunzipSync} = require("node:zlib");

const root = path.resolve(process.argv[2] || __dirname);
const bytes = name => fs.readFileSync(path.join(root, name));
const hash = value => createHash("sha256").update(value).digest("hex");
const objectHash = value => hash(JSON.stringify(value));
const decoded = name => name.endsWith(".gz") ? gunzipSync(bytes(name)) : bytes(name);
const json = name => JSON.parse(decoded(name).toString("utf8"));
const files = (directory = root, prefix = "") => fs.readdirSync(directory, {withFileTypes: true})
    .flatMap(entry => entry.isDirectory() ? files(path.join(directory, entry.name), prefix + entry.name + "/")
        : [prefix + entry.name]).sort();
const safeName = name => typeof name === "string" && name.length > 0 &&
    !path.isAbsolute(name) && !name.split(/[\\/]/).some(part => part === ".." || part === "." || part === "");

const manifest = json("manifest.json");
assert.equal(manifest.schema, 1);
const actual = files();
assert.deepEqual(manifest.files.map(item => item.path).sort(),
    actual.filter(name => !["manifest.json", "SHA256SUMS"].includes(name)), "Every retained file is manifested");
for (const item of manifest.files) {
    assert.ok(safeName(item.path), "Manifest paths stay inside the archive");
    const stored = bytes(item.path);
    assert.equal(stored.length, item.bytes, item.path + ": stored length");
    assert.equal(hash(stored), item.sha256, item.path + ": stored SHA256");
    if (item.path.endsWith(".gz")) {
        const original = gunzipSync(stored);
        assert.equal(original.length, item.decodedBytes, item.path + ": decoded length");
        assert.equal(hash(original), item.decodedSha256, item.path + ": decoded SHA256");
    }
}
const sums = bytes("SHA256SUMS").toString("utf8").trimEnd().split("\n").map(line => {
    const match = line.match(/^([a-f0-9]{64})  (.+)$/);
    assert.ok(match && safeName(match[2]), "SHA256SUMS has a valid relative file entry");
    assert.equal(hash(bytes(match[2])), match[1], match[2] + ": SHA256SUMS identity");
    return match[2];
});
assert.deepEqual(sums.sort(), actual.filter(name => name !== "SHA256SUMS"),
    "SHA256SUMS also covers the JSON manifest");

const provenance = json("provenance.json");
assert.equal(provenance.preflight.conclusion, "success");
assert.equal(provenance.job.conclusion, "success");
assert.equal(hash(bytes("reference-contract.cjs")), provenance.harness_sha256["camera-replay-contract.cjs"],
    "The frozen contract is exactly the one used by the CI captures");
const {checkStep, compareReplays} = require(path.join(root, "reference-contract.cjs"));
const reports = [1, 2].map(repetition => json(`repetition-${repetition}/report.json.gz`));
assert.deepEqual(compareReplays(...reports), json("comparison.json"),
    "Recomputed complete comparison equals the archived CI comparison");

function distribution(values) {
    const sorted = [...values].sort((a, b) => a - b);
    return {samples: sorted.length, medianMs: sorted[Math.floor(sorted.length / 2)],
        p95Ms: sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * .95))],
        maxMs: sorted.at(-1), meanMs: sorted.reduce((total, value) => total + value, 0) / sorted.length};
}

let totalInputs = 0, totalSubmissions = 0, minimumQuietMs = Infinity;
const repetitions = reports.map((report, repetition) => {
    assert.equal(report.revision, provenance.application.head_sha);
    assert.equal(report.schema, provenance.report_schema);
    assert.equal(report.protocol, provenance.protocol);
    assert.deepEqual(report.harness, provenance.harness_sha256);
    for (const [key, value] of Object.entries(provenance.environment)) assert.deepEqual(report[key], value);
    return {repetition: repetition + 1, cases: report.cases.map((scenario, caseIndex) => {
        const source = json(`repetition-${repetition + 1}/${scenario.name}-raw.json.gz`);
        assert.equal(source.rawSteps.length, scenario.trace.length);
        assert.equal(objectHash(scenario.trace), scenario.traceSha256);
        const rebuilt = source.rawSteps.map((raw, index) => checkStep(raw, scenario.trace[index], index));
        assert.deepEqual(rebuilt, scenario.steps, scenario.name + ": every step is rebuilt from raw observations");
        const projections = source.rawSteps.map(raw => raw.resolves.map(({time, signaledAt, ...projection}) => projection));
        const expected = provenance.cases[caseIndex];
        assert.equal(scenario.name, expected.name);
        assert.equal(objectHash(projections), expected.projectionSequenceSha256);
        assert.equal(scenario.traceSha256, expected.traceSha256);
        assert.equal(scenario.steps.length, expected.steps);
        const flattened = source.rawSteps.flatMap(raw => raw.resolves);
        assert.equal(flattened.length, expected.submissions);
        assert.equal(source.raw.resolves.length, flattened.length, "The general probe retains every scoped resolve");
        assert.equal(scenario.summary.submissions, flattened.length);
        assert.equal(scenario.summary.capture.mapResolves.submissions, flattened.length);
        source.raw.resolves.forEach((resolve, index) => {
            assert.equal(resolve.width, flattened[index].width);
            assert.equal(resolve.height, flattened[index].height);
            assert.equal(resolve.compositorContext, true);
            assert.equal(resolve.pixelSize, flattened[index].uniforms.uPixelSize[0]);
        });
        if (scenario.beforeHud !== null) assert.deepEqual(scenario.afterHud, scenario.beforeHud);

        const timings = {
            inputToFirstSubmission: distribution(source.rawSteps.map(raw => raw.resolves[0].time - raw.inputs[0].time)),
            inputToFirstFenceSignal: distribution(source.rawSteps.map(raw => raw.resolves[0].signaledAt - raw.inputs[0].time)),
            inputToSettledFenceSignal: distribution(source.rawSteps.map(raw =>
                Math.max(...raw.resolves.map(resolve => resolve.signaledAt)) - raw.inputs[0].time))
        };
        for (const [key, value] of Object.entries(timings)) assert.deepEqual(scenario.summary[key], value);
        const taskCpuMs = 1000 * (source.after.TaskDuration - source.before.TaskDuration);
        assert.equal(scenario.summary.taskCpuMs, taskCpuMs);
        assert.equal(scenario.summary.taskCpuPerStepMs, taskCpuMs / source.rawSteps.length);
        assert.equal(scenario.summary.capture.cpu.TaskDurationMs, taskCpuMs);
        const counts = {};
        for (const raw of source.rawSteps) {
            counts[raw.resolves.length] = (counts[raw.resolves.length] || 0) + 1;
            minimumQuietMs = Math.min(minimumQuietMs, raw.quietForMs);
        }
        totalInputs += source.rawSteps.length;
        totalSubmissions += flattened.length;
        return {name: scenario.name, inputs: source.rawSteps.length, submissions: flattened.length,
            submissionsPerInput: counts, multiSubmissionSteps: source.rawSteps.flatMap((raw, index) => raw.resolves.length > 1 ? [index] : []),
            traceSha256: scenario.traceSha256, projectionSequenceSha256: objectHash(projections),
            taskCpuMs, taskCpuPerStepMs: taskCpuMs / source.rawSteps.length, ...timings};
    })};
});
const verification = {schema: 1, status: "passed", application: provenance.application.head_sha,
    preflightRunId: provenance.preflight.id, exactProjectionSequence: true,
    totalInputs, totalSubmissions, minimumQuietMs, repetitions};
if (fs.existsSync(path.join(root, "verification.json"))) assert.deepEqual(verification, json("verification.json"),
    "Independent raw recomputation reproduces the retained verification result");
process.stdout.write(JSON.stringify(verification, null, 2) + "\n");
