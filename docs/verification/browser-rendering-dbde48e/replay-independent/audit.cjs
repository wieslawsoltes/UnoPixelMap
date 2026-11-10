// Offline audit of the fixed 2026-11-10 camera replay artifact. No npm or browser required.
// Usage: node audit.cjs [evidence-root [original-performance.zip]] > audit.json
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const {createHash} = require('node:crypto');
const {gunzipSync} = require('node:zlib');

const evidence = path.resolve(process.argv[2] || path.join(__dirname, '..'));
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const manifestBytes = fs.readFileSync(path.join(__dirname, 'input-manifest.json'));
const manifest = JSON.parse(manifestBytes);
assert.equal(manifest.schema, 1);
assert.equal(Object.keys(manifest.files).length, 35);
if (process.argv[3]) {
    const archive = fs.readFileSync(path.resolve(process.argv[3]));
    assert.equal(archive.length, manifest.artifact.bytes, 'Original artifact byte count');
    assert.equal(digest(archive), manifest.artifact.sha256, 'Original artifact SHA256');
    process.stderr.write('Original performance ZIP size and SHA256 verified.\n');
}
const consumed = new Set();
const read = relative => {
    const expected = manifest.files[relative];
    assert.ok(expected, relative + ': input is in the fixed acquisition manifest');
    const file = path.join(evidence, relative);
    const bytes = fs.existsSync(file) ? fs.readFileSync(file) :
        gunzipSync(fs.readFileSync(file + '.gz'), {maxOutputLength: expected.bytes});
    assert.equal(bytes.length, expected.bytes, relative + ': decoded byte count');
    assert.equal(digest(bytes), expected.sha256, relative + ': original content identity');
    consumed.add(relative);
    return JSON.parse(bytes);
};
const contractFile = path.join(__dirname, 'reference-contract.cjs');
assert.equal(digest(fs.readFileSync(contractFile)), manifest.harness['camera-replay-contract.cjs']);
const {checkStep, compareReplays, sha256} = require(contractFile);
const provenance = read('provenance.json'), environment = read('environment.json');
const expectedSources = manifest.source, referenceCases = manifest.preflight.cases;
for (const [variant, sha] of Object.entries(expectedSources)) assert.equal(provenance[variant].source_sha, sha);
const expectedOrder = referenceCases.flatMap(c => [['baseline', 1], ['candidate', 1], ['candidate', 2], ['baseline', 2]]
    .map(([variant, repetition]) => [c.name, variant, repetition]));
assert.deepEqual(provenance.camera_replay_execution.map(e => [e.scenario, e.variant, e.repetition]), expectedOrder);
const browser = environment.browser.match(/\b\d+\.\d+\.\d+\.\d+\b/)?.[0];
const cpu = environment.cpu_description.match(/^Model name:\s*(.+)$/m)?.[1].trim();
assert.ok(browser && cpu, 'The complete Chromium version and CPU model are present');
const timingTypes = ['inputToFirstSubmission', 'inputToFirstFenceSignal', 'inputToSettledFenceSignal'];
const distribution = values => {
    assert.ok(values.length > 0 && values.every(value => Number.isFinite(value) && value >= 0));
    const sorted = [...values].sort((a, b) => a - b);
    return {samples: sorted.length, medianMs: sorted[Math.floor(sorted.length / 2)],
        p95Ms: sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * .95))],
        maxMs: sorted.at(-1), meanMs: sorted.reduce((sum, value) => sum + value, 0) / sorted.length};
};

let previousEnd = 0, minimumQuietMs = Infinity, inputs = 0, submissions = 0;
const anchors = {}, comparisons = [], rows = [], captures = [];
for (const [index, entry] of provenance.camera_replay_execution.entries()) {
    assert.equal(entry.order, index + 1);
    assert.equal(entry.exit_code, 0);
    assert.equal(entry.source_sha, expectedSources[entry.variant]);
    assert.equal(entry.output, `camera-replay/${entry.variant}/${entry.scenario}/repetition-${entry.repetition}`);
    const start = Date.parse(entry.started_utc), end = Date.parse(entry.completed_utc);
    assert.ok(Number.isFinite(start) && Number.isFinite(end) && start >= previousEnd && end >= start,
        'The complete ABBA processes run serially');
    previousEnd = end;
    const report = read(entry.output + '/report.json');
    assert.equal(report.error, undefined);
    assert.equal(report.revision, entry.source_sha);
    assert.equal(report.cases.length, 1);
    assert.equal(report.base, `http://127.0.0.1:8100/${entry.variant}/UnoPixelMap/`);
    assert.ok(Date.parse(report.capturedAtUtc) >= start && Date.parse(report.capturedAtUtc) <= end);
    assert.deepEqual(report.harness, manifest.harness);
    for (const [name, sha] of Object.entries(report.harness)) assert.equal(environment[name + '_sha256'], sha);
    assert.deepEqual(report.viewport, {width: 1200, height: 780});
    assert.equal(report.deviceScaleFactor, 1);
    assert.equal(report.browser, browser);
    assert.equal(report.cpu, cpu);
    assert.equal(report.availableParallelism, environment.cpu_affinity.split(',').length);
    assert.deepEqual(report.browserArgs, ['--disable-dev-shm-usage', '--use-angle=swiftshader',
        '--enable-unsafe-swiftshader', ...environment.extra_browser_args]);
    compareReplays(report, report);
    const c = report.cases[0], expected = referenceCases.find(item => item.name === c.name);
    assert.equal(c.name, entry.scenario);
    assert.equal(c.trace.length, 32);
    assert.equal(c.traceSha256, expected.traceSha256);
    const data = read(entry.output + '/' + c.name + '-raw.json');
    assert.equal(data.rawSteps.length, 32);
    const rebuilt = data.rawSteps.map((raw, input) => checkStep(raw, c.trace[input], input));
    assert.deepEqual(rebuilt, c.steps, c.name + ': every step reconstructed from native observations');
    for (let step = 1; step < data.rawSteps.length; step++)
        assert.ok(data.rawSteps[step].beganAt >= data.rawSteps[step - 1].finishedAt,
            'The next input begins only after the preceding complete barrier');
    const frames = data.rawSteps.flatMap(step => step.resolves);
    assert.equal(frames.length, expected.submissions);
    assert.equal(frames.length, c.summary.submissions);
    assert.equal(frames.length, c.summary.capture.mapResolves.submissions);
    assert.equal(frames.length, c.summary.capture.mapResolves.signaledCompletions);
    assert.equal(frames.length, data.raw.resolves.length);
    assert.deepEqual(data.raw.resolves.map(r => [r.width, r.height, r.compositorContext, r.pixelSize]),
        frames.map(r => [r.width, r.height, r.compositorContext, r.uniforms.uPixelSize[0]]));
    const signals = data.raw.syncCalls.filter(call => call.resolveSignalLatencyMs != null);
    assert.equal(signals.length, frames.length, 'The independent performance probe also observes every signal');
    assert.equal(new Set(data.raw.resolves.map(resolve => resolve.id)).size, frames.length);
    assert.equal(new Set(signals.map(signal => signal.resolveId)).size, frames.length);
    for (const [frame, resolved] of data.raw.resolves.entries()) {
        const signal = signals.find(call => call.resolveId === resolved.id);
        const fence = data.raw.syncCalls.filter(call => call.method === 'fenceSync' && call.resolveId === resolved.id);
        assert.ok(signal && fence.length === 1 && signal.fenceId === fence[0].fenceId,
            'Each resolve has its own matched created and signaled fence in the full probe');
        assert.equal(signal.method, 'clientWaitSync');
        assert.ok([37146, 37148].includes(signal.status), 'The matched fence reports a signaled WebGL status');
        // Both probes timestamp the same nested call at different points. Check their ordering,
        // rather than imposing a tolerance or pretending those observation times must be identical.
        assert.ok(resolved.time <= frames[frame].time && frames[frame].time <= signal.time &&
            signal.time + signal.durationMs <= frames[frame].signaledAt);
    }
    if (c.beforeHud !== null) assert.deepEqual(c.afterHud, c.beforeHud);
    const taskCpuMs = 1000 * (data.after.TaskDuration - data.before.TaskDuration);
    assert.ok(Number.isFinite(taskCpuMs) && taskCpuMs >= 0);
    assert.equal(c.summary.taskCpuMs, taskCpuMs);
    assert.equal(c.summary.taskCpuPerStepMs, taskCpuMs / 32);
    assert.equal(c.summary.capture.cpu.TaskDurationMs, taskCpuMs);
    const timing = {
        inputToFirstSubmission: distribution(data.rawSteps.map(r => r.resolves[0].time - r.inputs[0].time)),
        inputToFirstFenceSignal: distribution(data.rawSteps.map(r => r.resolves[0].signaledAt - r.inputs[0].time)),
        inputToSettledFenceSignal: distribution(data.rawSteps.map(r => Math.max(...r.resolves.map(f => f.signaledAt)) - r.inputs[0].time))
    };
    for (const [name, value] of Object.entries(timing)) assert.deepEqual(c.summary[name], value);
    const sequenceHash = sha256(rebuilt.map(s => s.projection));
    assert.equal(sequenceHash, expected.projectionSequenceSha256,
        'The complete ordered projection sequence also matches the independently archived preflight');
    if (!anchors[c.name]) {
        assert.equal(entry.variant, 'baseline');
        assert.equal(entry.repetition, 1);
        anchors[c.name] = report;
    } else {
        assert.equal(entry.comparison_exit_code, 0);
        const compared = compareReplays(anchors[c.name], report);
        assert.deepEqual(compared, read(entry.output + '/comparison-with-baseline-first.json'),
            'The independent exact comparison reproduces the complete CI comparison');
        comparisons.push({scenario: c.name, variant: entry.variant, repetition: entry.repetition,
            exactProjectionSequence: compared.comparisons[0].exactProjectionSequence});
    }
    for (const step of data.rawSteps) minimumQuietMs = Math.min(minimumQuietMs, step.quietForMs);
    inputs += rebuilt.length;
    submissions += frames.length;
    const row = {scenario: c.name, variant: entry.variant, repetition: entry.repetition, order: entry.order,
        inputs: rebuilt.length, submissions: frames.length, traceSha256: c.traceSha256,
        projectionSequenceSha256: sequenceHash, sameProjectionAsPreflight: true,
        taskCpuMs, taskCpuPerInputMs: taskCpuMs / 32, ...timing};
    rows.push(row);
    captures.push({row, timings: rebuilt.map(({index, inputToFirstSubmissionMs, inputToFirstFenceSignalMs,
        inputToSettledFenceSignalMs}) => ({index, inputToFirstSubmissionMs, inputToFirstFenceSignalMs,
        inputToSettledFenceSignalMs}))});
}

const mean = values => values.reduce((a, b) => a + b, 0) / values.length;
const pct = (a, b) => (b / a - 1) * 100;
const cases = referenceCases.map(({name}) => {
    const pair = variant => captures.filter(c => c.row.scenario === name && c.row.variant === variant)
        .sort((a, b) => a.row.repetition - b.row.repetition);
    const a = pair('baseline'), b = pair('candidate');
    assert.equal(a.length, 2); assert.equal(b.length, 2);
    const metrics = {taskCpuPerInputMs: r => r.taskCpuPerInputMs};
    for (const type of timingTypes)
        for (const stat of ['medianMs', 'p95Ms', 'maxMs', 'meanMs']) metrics[type + '_' + stat] = r => r[type][stat];
    const perInput = Object.fromEntries(timingTypes.map(type => {
        const field = type + 'Ms';
        const observations = a.flatMap((capture, process) => capture.timings.map((step, index) => {
            const baselineMs = step[field], candidateMs = b[process].timings[index][field];
            return {repetition: capture.row.repetition, index, baselineMs, candidateMs, deltaMs: candidateMs - baselineMs};
        }));
        const slower = observations.filter(value => value.deltaMs > 0);
        const worst = observations.reduce((prior, value) => value.deltaMs > prior.deltaMs ? value : prior);
        return [field, {samples: observations.length, slower: slower.length,
            faster: observations.filter(value => value.deltaMs < 0).length,
            equal: observations.filter(value => value.deltaMs === 0).length,
            positiveDeltasBelowPointZeroOneMs: slower.filter(value => value.deltaMs < .01).length,
            worstIncrease: worst.deltaMs > 0 ? worst : null, observations}];
    }));
    return {name, exactProjectionSequence: true, submissionsPerCapture: a[0].row.submissions,
        metrics: Object.fromEntries(Object.entries(metrics).map(([name, select]) => {
            const baseline = a.map(c => select(c.row)), candidate = b.map(c => select(c.row));
            const baselineMean = mean(baseline), candidateMean = mean(candidate);
            return [name, {baseline, candidate, baselineProcessMean: baselineMean, candidateProcessMean: candidateMean,
                changePercent: pct(baselineMean, candidateMean),
                pairedChangePercent: baseline.map((value, i) => pct(value, candidate[i]))}];
        })), perInput};
});
assert.equal(consumed.size, Object.keys(manifest.files).length, 'Every pinned input was consumed');
assert.equal(inputs, 384); assert.equal(submissions, 408); assert.equal(comparisons.length, 9);
const output = {schema: 1, status: 'passed', source: expectedSources, artifact: manifest.artifact,
    integrity: {decodedFiles: consumed.size,
        decodedBytes: Object.values(manifest.files).reduce((sum, file) => sum + file.bytes, 0),
        inputManifestSha256: digest(manifestBytes),
        originalArchive: 'The original ZIP SHA256 was verified at acquisition. This script rehashes it only when passed as its second argument.'},
    browser: environment.browser, browserSha256: environment.browser_sha256,
    cpuAffinity: environment.cpu_affinity, cpu, node: environment.node,
    inputs, submissions, minimumQuietMs, exactComparisons: comparisons.length,
    interpretation: 'Two fresh processes per variant, in serial ABBA order for each case. CPU/input is total task CPU divided by 32 inputs. Latency aggregates are arithmetic means of the two process statistics, not pooled percentiles. Per-input delta is candidate minus baseline with no threshold, rounding or exclusions. Fence signal is observed completion of the map submission, not browser presentation. These are controlled camera/render costs, not simulation or physical display FPS.',
    comparisons, cases, rows};
process.stdout.write(JSON.stringify(output, null, 2) + '\n');
