// node webgpu-performance.cjs <baseline URL> <candidate URL> <output directory>
// Paired Release AOT benchmark. One browser/runner, ABBA order, one frozen harness.
// PERF_CASES limits named cases; PERF_STEPS_PER_SEGMENT defaults to 2.
// PERF_REPETITIONS=1 is a quick diagnostic (AB only), not the default ABBA comparison.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const {createHash} = require("node:crypto");
const {installPerformanceProbe} = require("./webgpu-performance-probe.cjs");

const viewport = {width: 1200, height: 800};
const camera = style => `v5/40.706846/-74.008817/17/-51/55/Day/Map/Perspective/0/Relief/1.00/${style}/Orbit/0/0/0`;
const cases = [
    {name: "pixel-pan", style: "Pixel", motion: "pan"},
    {name: "normal-pan", style: "Normal", motion: "pan"},
    {name: "normal-orbit", style: "Normal", motion: "orbit"},
    {name: "normal-walk-look", style: "Normal", motion: "look", walk: true}
];
const browserArgs = ["--disable-dev-shm-usage", "--disable-gpu-watchdog",
    "--enable-features=CDPScreenshotNewSurface,Vulkan", "--use-angle=vulkan", "--use-vulkan=swiftshader",
    "--use-webgpu-adapter=swiftshader", "--disable-vulkan-surface", "--enable-unsafe-webgpu", "--enable-unsafe-swiftshader"];
const digest = value => createHash("sha256").update(value).digest("hex");
const hashObject = value => digest(JSON.stringify(value));
const sum = (items, select) => items.reduce((total, item) => total + (select(item) || 0), 0);

function distribution(values) {
    const numbers = values.filter(Number.isFinite).sort((a, b) => a - b);
    if (!numbers.length) return {samples: 0};
    return {samples: numbers.length, minimum: numbers[0], median: numbers[Math.floor(numbers.length / 2)],
        p95: numbers[Math.min(numbers.length - 1, Math.ceil(numbers.length * .95) - 1)], maximum: numbers.at(-1),
        mean: sum(numbers, value => value) / numbers.length};
}
function makeTrace(scenario, bounds, count) {
    assert.ok(Number.isSafeInteger(count) && count > 0 && count <= 20, "Step count is bounded");
    const point = {x: Math.floor(bounds.x + bounds.width / 2), y: Math.floor(bounds.y + bounds.height / 2)};
    const segments = scenario.motion === "pan" ? [[12, 1, 0], [1, 12, 0], [-12, -1, 0], [-1, -12, 0]] :
        scenario.motion === "orbit" ? [[-10, 0, 8], [1, -5, 1], [10, 0, 8], [-1, 5, 1]] :
            [[-10, 0, 0], [1, -5, 0], [10, 0, 0], [-1, 5, 0]];
    return segments.flatMap(([deltaX, deltaY, modifiers]) => Array.from({length: count}, () =>
        ({...point, deltaX, deltaY, modifiers})));
}
function checkStep(raw, input) {
    assert.equal(raw.mode, "step");
    assert.equal(raw.inputs.length, 1, "Exactly one trusted native wheel input belongs to each step");
    const delivered = raw.inputs[0];
    for (const key of ["x", "y", "deltaX", "deltaY", "modifiers"]) assert.equal(delivered[key], input[key], "Native input " + key);
    assert.equal(delivered.deltaMode, 0, "Native trackpad deltas remain pixels");
    assert.ok(raw.submissions.length > 0 && raw.frames.length > 0, "The input produces actual map commands and a managed frame");
    assert.equal(raw.mapSubmits, raw.submissions.length, "Every observed map submission belongs to this step");
    for (const submission of raw.submissions) {
        assert.ok(submission.submittedAt >= delivered.at, "No unrelated earlier submission is attributed to the input");
        assert.ok(Number.isFinite(submission.queueCompletedAt) && submission.queueCompletedAt >= submission.submittedAt,
            "Each submitted map command has an observed queue-completion callback");
    }
    for (const frame of raw.frames)
        for (const key of ["frameMilliseconds", "submissionMilliseconds", "uploadBytes"])
            assert.ok(Number.isFinite(frame[key]) && frame[key] >= 0, "The frame reports a finite nonnegative " + key);
    const phases = raw.frames.every(frame => Number.isFinite(frame.preparationMilliseconds) && Number.isFinite(frame.overlayMilliseconds));
    const first = raw.submissions[0], last = raw.submissions.at(-1);
    return {mapSubmits: raw.mapSubmits, managedFrames: raw.frames.length,
        inputToFirstSubmitMilliseconds: first.submittedAt - delivered.at,
        inputToFirstQueueCompletionMilliseconds: first.queueCompletedAt - delivered.at,
        inputToLastQueueCompletionMilliseconds: last.queueCompletedAt - delivered.at,
        renderCpuMilliseconds: sum(raw.frames, frame => frame.frameMilliseconds),
        preparationMilliseconds: phases ? sum(raw.frames, frame => frame.preparationMilliseconds) : null,
        overlayMilliseconds: phases ? sum(raw.frames, frame => frame.overlayMilliseconds) : null,
        submissionCpuMilliseconds: sum(raw.frames, frame => frame.submissionMilliseconds),
        phaseTimingsAvailable: phases,
        reportedUploadBytes: sum(raw.frames, frame => frame.uploadBytes), bufferUploadBytes: raw.bufferUploadBytes,
        textureUploadBytes: raw.textureUploadBytes,
        bufferAllocations: raw.bufferAllocations, textureAllocations: raw.textureAllocations,
        longTaskMilliseconds: sum(raw.longTasks, task => task.milliseconds),
        lastDiagnostic: raw.frames.at(-1)};
}
function imageDifference(left, right) {
    assert.equal(left.width, right.width, "Compared captures have the same width");
    assert.equal(left.height, right.height, "Compared captures have the same height");
    assert.ok(left.width > 0 && left.height > 0, "Compared captures are not empty");
    assert.equal(left.data.length, left.width * left.height * 4);
    assert.equal(right.data.length, right.width * right.height * 4);
    let changed = 0, significant = 0, total = 0, squared = 0, maximum = 0;
    for (let index = 0; index < left.data.length; index += 4) {
        let pixelMaximum = 0;
        for (let channel = 0; channel < 3; channel++) {
            const delta = Math.abs(left.data[index + channel] - right.data[index + channel]);
            total += delta; squared += delta * delta; pixelMaximum = Math.max(pixelMaximum, delta);
        }
        changed += pixelMaximum > 0 ? 1 : 0;
        significant += pixelMaximum > 2 ? 1 : 0;
        maximum = Math.max(maximum, pixelMaximum);
    }
    const pixels = left.width * left.height;
    return {pixels, changedPixelFraction: changed / pixels, significantPixelFraction: significant / pixels,
        meanAbsoluteChannelError: total / (pixels * 3), rootMeanSquareChannelError: Math.sqrt(squared / (pixels * 3)),
        maximumChannelError: maximum};
}
function metrics(reply) { return Object.fromEntries(reply.metrics.map(item => [item.name, item.value])); }
function summarizeSteps(steps) {
    const fields = ["inputToFirstSubmitMilliseconds", "inputToFirstQueueCompletionMilliseconds", "inputToLastQueueCompletionMilliseconds",
        "renderCpuMilliseconds", "submissionCpuMilliseconds", "reportedUploadBytes", "bufferUploadBytes", "textureUploadBytes"];
    const summary = Object.fromEntries(fields.map(field => [field, distribution(steps.map(step => step[field]))]));
    summary.steps = steps.length;
    summary.warmSteps = steps.filter(step => step.textureUploadBytes === 0 &&
        step.bufferAllocations === 0 && step.textureAllocations === 0).length;
    summary.streamingSteps = steps.length - summary.warmSteps;
    summary.mapSubmits = sum(steps, step => step.mapSubmits);
    if (steps.every(step => step.phaseTimingsAvailable)) {
        summary.preparationMilliseconds = distribution(steps.map(step => step.preparationMilliseconds));
        summary.overlayMilliseconds = distribution(steps.map(step => step.overlayMilliseconds));
    }
    return summary;
}
async function click(page, name) {
    const target = page.getByRole("button", {name, exact: true, disabled: false})
        .or(page.getByRole("checkbox", {name, exact: true, disabled: false}));
    await target.waitFor({state: "visible"});
    const bounds = await target.boundingBox();
    assert.ok(bounds?.width > 0 && bounds?.height > 0, name + " has a native input target");
    await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
}
async function menu(page, name, submenu) {
    await click(page, "Map appearance");
    const item = text => page.getByRole("menuitem", {name: text, exact: true})
        .or(page.getByRole("menuitemcheckbox", {name: text, exact: true}));
    if (submenu) { await item(submenu).focus(); await page.keyboard.press("ArrowRight"); }
    await item(name).waitFor({state: "visible"});
    await item(name).focus();
    await page.keyboard.press("Enter");
    await item(name).waitFor({state: "hidden"});
    await page.mouse.move(viewport.width / 2, 20);
}
function checkHealth(state) {
    assert.equal(state.deviceCount, 1, "The application uses Uno's one shared device");
    assert.deepEqual(state.errors, [], "No WebGPU validation or shader errors");
    assert.deepEqual(state.losses, [], "No device losses during performance measurement");
    // Failures can stop rendering without publishing another successful frame.
    // The last copied diagnostic cannot establish the renderer's current health.
    assert.equal(state.liveFailure, null, "The live managed renderer remains healthy");
    return state;
}
async function healthy(page) {
    return checkHealth(await page.evaluate(() => __pixelAtlasWebGpuPerformance.snapshot()));
}
async function settle(page) {
    // This barrier is outside measured CPU/render latency. It prevents a prior
    // queue backlog from being mistaken for this input's GPU execution time.
    await page.waitForFunction(() => __pixelAtlasWebGpuPerformance.lastDiagnostic?.patches > 0, undefined, {timeout: 120000});
    for (let attempt = 0; attempt < 30; attempt++) {
        const version = await page.evaluate(async () => {
            const state = __pixelAtlasWebGpuPerformance, version = state.mapSubmits;
            let timer;
            try {
                await Promise.race([state.fence(), new Promise((_, reject) => {
                    timer = setTimeout(() => reject(new Error("WebGPU queue did not settle within 120 seconds")), 120000);
                })]);
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
            } finally { clearTimeout(timer); }
            return version;
        });
        await page.waitForTimeout(350);
        // A submission just after the fence can already look 300 ms old here
        // while its GPU work is still pending. Require the fenced version too.
        const quiet = await page.evaluate(version => __pixelAtlasWebGpuPerformance.unchangedAndQuietSince(version), version);
        if (quiet) { await healthy(page); return; }
    }
    throw new Error("Map did not become quiescent before a deterministic camera measurement");
}
async function sharePose(page) {
    await menu(page, "Share this view");
    const field = page.getByRole("textbox", {name: "Shareable map link", exact: true});
    await field.waitFor({state: "visible"});
    const hash = new URL(await field.inputValue()).hash;
    assert.ok(hash.startsWith("#v5/"), "Public share state exposes the complete physical camera");
    await click(page, "Close panel");
    await settle(page);
    return hash;
}
async function sceneProjection(page) {
    // Share opens a drawer that intentionally exits free roam. A public quality
    // change instead re-uploads the same physical camera without stopping play.
    // Finish at the measured High setting, and copy projection words only here.
    await menu(page, "Balanced quality", "Rendering quality");
    await page.waitForFunction(() => globalThis.PixelAtlasWebGpu?.quality === "Balanced");
    await settle(page);
    await page.evaluate(() => __pixelAtlasWebGpuPerformance.beginSceneCapture());
    await menu(page, "High quality", "Rendering quality");
    await page.waitForFunction(() => globalThis.PixelAtlasWebGpu?.quality === "High");
    await settle(page);
    return page.evaluate(() => __pixelAtlasWebGpuPerformance.finishSceneCapture());
}
async function step(page, cdp, input) {
    await page.evaluate(() => __pixelAtlasWebGpuPerformance.begin("step"));
    await cdp.send("Input.dispatchMouseEvent", {type: "mouseWheel", ...input});
    const capture = await page.waitForFunction(() => {
        const state = __pixelAtlasWebGpuPerformance;
        if (state.errors.length || state.losses.length || globalThis.PixelAtlasWebGpu?.failure)
            throw new Error("WebGPU failed during camera replay");
        return state.finishStepIfReady();
    }, undefined, {timeout: 120000});
    try {
        const raw = await capture.jsonValue();
        return {raw, result: checkStep(raw, input)};
    } finally { await capture.dispose(); }
}
async function captureMap(page, output, name, bounds) {
    await settle(page);
    const filename = name + ".png";
    const bytes = await page.screenshot({path: path.join(output, filename), clip: {
        x: Math.floor(bounds.x), y: Math.floor(bounds.y), width: Math.floor(bounds.width), height: Math.floor(bounds.height)
    }, timeout: 30000});
    return {filename, sha256: digest(bytes), bytes: bytes.length};
}
async function measure(browser, variant, repetition, scenario, base, output, stepsPerSegment, movingMs) {
    const context = await browser.newContext({viewport, deviceScaleFactor: 1, serviceWorkers: "block"});
    const page = await context.newPage();
    page.setDefaultTimeout(30000);
    const errors = [], blocked = {};
    page.on("pageerror", error => errors.push(String(error)));
    await page.addInitScript(installPerformanceProbe);
    const origin = new URL(base).origin;
    await page.route("**/*", route => {
        const address = new URL(route.request().url());
        if (address.origin === origin || address.protocol === "data:") return route.continue();
        blocked[address.hostname || address.protocol] = (blocked[address.hostname || address.protocol] || 0) + 1;
        return route.abort();
    });
    const prefix = `${scenario.name}-${variant}-${repetition}`;
    const result = {variant, repetition, scenario, trace: [], rawSteps: [], steps: [], blockedProviders: blocked};
    try {
        const address = new URL(base); address.hash = camera(scenario.style);
        await page.goto(address.href, {waitUntil: "domcontentloaded", timeout: 120000});
        await page.waitForFunction(() => globalThis.PixelAtlasWebGpu?.patches > 0, undefined, {timeout: 120000});
        const accessibility = page.getByRole("button", {name: "Enable accessibility", exact: true});
        if (await accessibility.count()) await accessibility.evaluate(element => element.click());
        await page.getByRole("button", {name: "Map appearance", exact: true}).waitFor({state: "visible"});
        await menu(page, "High quality", "Rendering quality");
        const ui = require("./explore.cjs");
        if (scenario.walk) {
            await click(page, "Open free roam game");
            await ui.choose(page, "Free roam camera", 1);
            await click(page, "Start free roam at map center");
            await page.getByRole("button", {name: "Pause free roam", exact: true}).waitFor({state: "visible"});
            await page.waitForTimeout(1500);
            await click(page, "Pause free roam");
            await page.getByRole("button", {name: "Resume free roam", exact: true}).waitFor({state: "visible"});
        }
        await settle(page);
        const map = page.getByRole("group", {name: "Interactive world map", exact: true});
        const bounds = await map.boundingBox();
        assert.ok(bounds?.width > 500 && bounds?.height > 300, "Map viewport is established");
        result.bounds = bounds;
        await map.focus(); await ui.waitForSettledFocus(page, map);
        const cdp = await context.newCDPSession(page);
        await cdp.send("Performance.enable", {timeDomain: "threadTicks"});
        result.trace = makeTrace(scenario, bounds, stepsPerSegment);
        result.traceSha256 = hashObject(result.trace);
        // One closed warm-up traversal visits every measured camera segment.
        for (const input of result.trace) await step(page, cdp, input);
        await settle(page);
        result.beforePose = scenario.walk ? null : await sharePose(page);
        result.beforeSceneProjection = await sceneProjection(page);
        result.beforeHud = scenario.walk ? await ui.telemetry(page) : null;
        result.beforeDiagnostic = (await healthy(page)).diagnostic;
        assert.equal(result.beforeDiagnostic.style, scenario.style);
        assert.equal(result.beforeDiagnostic.quality, "High");
        result.beforeImage = await captureMap(page, output, prefix + "-before", bounds);
        await map.focus(); await ui.waitForSettledFocus(page, map);
        await page.mouse.move(result.trace[0].x, result.trace[0].y);
        await settle(page);
        const before = metrics(await cdp.send("Performance.getMetrics"));
        for (const input of result.trace) {
            const captured = await step(page, cdp, input);
            result.rawSteps.push(captured.raw); result.steps.push(captured.result);
        }
        const after = metrics(await cdp.send("Performance.getMetrics"));
        result.summary = summarizeSteps(result.steps);
        result.summary.mainThreadTaskMilliseconds = (after.TaskDuration - before.TaskDuration) * 1000;
        result.summary.cdpMetricTimestampIntervalMilliseconds = (after.Timestamp - before.Timestamp) * 1000;
        result.afterPose = scenario.walk ? null : await sharePose(page);
        result.afterSceneProjection = await sceneProjection(page);
        result.afterHud = scenario.walk ? await ui.telemetry(page) : null;
        if (scenario.walk) assert.deepEqual(result.afterHud, result.beforeHud, "Paused camera replay preserves physical simulation state");
        result.afterDiagnostic = (await healthy(page)).diagnostic;
        result.afterImage = await captureMap(page, output, prefix + "-after", bounds);
        if (scenario.walk && movingMs > 0) {
            // Separate fixed-duration workload. Trajectories are recorded and may
            // differ with scheduling; it is not used for pixel/pose parity claims.
            await click(page, "Resume free roam");
            await map.focus(); await ui.waitForSettledFocus(page, map);
            await page.keyboard.down("w");
            try {
                await page.waitForTimeout(1000);
                const beforeHud = await ui.telemetry(page);
                await page.evaluate(() => __pixelAtlasWebGpuPerformance.begin("throughput"));
                await page.waitForTimeout(movingMs);
                const raw = await page.evaluate(() => __pixelAtlasWebGpuPerformance.finish());
                const afterHud = await ui.telemetry(page);
                const intervals = raw.submissions.slice(1).map((value, index) => value.submittedAt - raw.submissions[index].submittedAt);
                const raf = raw.raf.slice(1).map((value, index) => value - raw.raf[index]);
                result.moving = {beforeHud, afterHud, raw, submittedMapFramesPerSecond: raw.mapSubmits * 1000 / raw.durationMilliseconds,
                    submittedFrameIntervalsMilliseconds: distribution(intervals), rafIntervalsMilliseconds: distribution(raf),
                    renderCpuMilliseconds: distribution(raw.frames.map(frame => frame.frameMilliseconds)),
                    textureUploadBytes: raw.textureUploadBytes, bufferUploadBytes: raw.bufferUploadBytes};
                assert.ok(raw.mapSubmits > 0, "The moving workload submits real map frames");
                assert.ok(afterHud.distance > beforeHud.distance, "Native held-key walking advances during the timed workload");
            } finally { await page.keyboard.up("w"); }
            await click(page, "Pause free roam");
            result.moving.queueBoundaries = await page.evaluate(async () => {
                let timer;
                try { return await Promise.race([__pixelAtlasWebGpuPerformance.throughputBoundaries(),
                    new Promise((_, reject) => { timer = setTimeout(() => reject(new Error("Throughput queue drain timed out")), 120000); })]); }
                finally { clearTimeout(timer); }
            });
            await settle(page);
        }
        result.gpu = await healthy(page);
        assert.deepEqual(errors, [], "No page exceptions during benchmark");
        fs.writeFileSync(path.join(output, prefix + "-raw.json"), JSON.stringify(result, null, 2) + "\n");
        const {rawSteps, ...compact} = result;
        if (compact.moving) compact.moving = {...compact.moving, raw: undefined};
        console.log("WEBGPU_PERF", JSON.stringify({case: scenario.name, variant, repetition, ...result.summary,
            movingSubmissionsPerSecond: result.moving?.submittedMapFramesPerSecond}));
        return compact;
    } catch (error) {
        result.failure = error.stack || String(error);
        result.gpu = await page.evaluate(() => globalThis.__pixelAtlasWebGpuPerformance?.snapshot()).catch(() => null);
        result.pending = await page.evaluate(() => globalThis.__pixelAtlasWebGpuPerformance?.active).catch(() => null);
        result.lastCompleted = await page.evaluate(() => globalThis.__pixelAtlasWebGpuPerformance?.lastCompleted).catch(() => null);
        fs.writeFileSync(path.join(output, prefix + "-failure.json"), JSON.stringify(result, null, 2) + "\n");
        await page.screenshot({path: path.join(output, prefix + "-failure.png"), timeout: 15000}).catch(() => {});
        fs.writeFileSync(path.join(output, prefix + "-failure.txt"), await page.locator("body").ariaSnapshot().catch(() => ""));
        throw error;
    } finally { await context.close(); }
}
function compare(report, output, PNG) {
    const groups = [];
    for (const scenario of report.scenarios) {
        const samples = report.runs.filter(run => run.scenario.name === scenario.name);
        const baseline = samples.filter(run => run.variant === "baseline"), candidate = samples.filter(run => run.variant === "candidate");
        assert.equal(baseline.length, report.repetitions); assert.equal(candidate.length, report.repetitions);
        const visual = [];
        for (let repeat = 1; repeat <= report.repetitions; repeat++) {
            const a = baseline.find(run => run.repetition === repeat), b = candidate.find(run => run.repetition === repeat);
            assert.equal(a.traceSha256, b.traceSha256, scenario.name + ": native input traces match");
            assert.deepEqual(a.bounds, b.bounds, scenario.name + ": logical viewport matches");
            assert.equal(a.beforePose, b.beforePose, scenario.name + ": initial public physical camera matches");
            assert.equal(a.afterPose, b.afterPose, scenario.name + ": final public physical camera matches");
            for (const phase of ["before", "after"]) {
                assert.deepEqual(a[phase + "SceneProjection"], b[phase + "SceneProjection"],
                    `${scenario.name}: ${phase} submitted camera/terrain projection matches`);
                assert.deepEqual(a[phase + "Hud"], b[phase + "Hud"],
                    `${scenario.name}: ${phase} physical simulation telemetry matches`);
                const left = a[phase + "Diagnostic"], right = b[phase + "Diagnostic"];
                for (const key of ["style", "quality", "width", "height", "physicalWidth", "physicalHeight", "pixelSize", "renderWidth", "renderHeight", "patches", "triangles", "imageryCount", "relief", "elevation", "zoom"])
                    assert.equal(left[key], right[key], `${scenario.name}: ${phase} scene ${key} matches`);
                const ai = a[phase + "Image"], bi = b[phase + "Image"];
                const difference = imageDifference(PNG.sync.read(fs.readFileSync(path.join(output, ai.filename))),
                    PNG.sync.read(fs.readFileSync(path.join(output, bi.filename))));
                visual.push({repetition: repeat, phase, baseline: ai, candidate: bi, ...difference});
                // Small shader-rounding edges are quantified, not silently called
                // exact. Larger changes require investigation before acceptance.
                assert.ok(difference.significantPixelFraction <= .01 && difference.meanAbsoluteChannelError <= .25,
                    scenario.name + ": visual parity tolerance exceeded: " + JSON.stringify(difference));
            }
        }
        const baselineSummary = summarizeSteps(baseline.flatMap(run => run.steps));
        const candidateSummary = summarizeSteps(candidate.flatMap(run => run.steps));
        const ratios = {};
        for (const key of ["renderCpuMilliseconds", "submissionCpuMilliseconds", "inputToFirstSubmitMilliseconds", "inputToFirstQueueCompletionMilliseconds", "inputToLastQueueCompletionMilliseconds"])
            ratios[key] = baselineSummary[key].median > 0 ? candidateSummary[key].median / baselineSummary[key].median : null;
        groups.push({scenario: scenario.name, baseline: baselineSummary, candidate: candidateSummary,
            candidateOverBaselineMedian: ratios, visual,
            warmClassification: {baselineStepsWithoutTextureUploadOrResourceAllocation: baselineSummary.warmSteps,
                candidateStepsWithoutTextureUploadOrResourceAllocation: candidateSummary.warmSteps,
                allStepsWarm: baselineSummary.streamingSteps === 0 && candidateSummary.streamingSteps === 0}});
    }
    return groups;
}
async function main() {
    const [baselineUrl, candidateUrl, output] = process.argv.slice(2);
    assert.ok(baselineUrl && candidateUrl && output, "Provide baseline URL, candidate URL and output directory");
    const steps = Number(process.env.PERF_STEPS_PER_SEGMENT || 2), repetitions = Number(process.env.PERF_REPETITIONS || 2);
    assert.ok(repetitions === 1 || repetitions === 2, "Use AB or ABBA order");
    const movingMs = Number(process.env.PERF_MOVING_MS || 6000);
    assert.ok(Number.isFinite(movingMs) && movingMs >= 0 && movingMs <= 60000, "Moving duration is bounded");
    const names = process.env.PERF_CASES?.split(",");
    const selected = names ? cases.filter(item => names.includes(item.name)) : cases;
    assert.ok(selected.length && (!names || names.every(name => selected.some(item => item.name === name))), "Every selected case exists");
    fs.mkdirSync(output, {recursive: true});
    const report = {schema: 1, protocol: "webgpu-native-input-abba-v1", capturedAtUtc: new Date().toISOString(),
        revisions: {baseline: process.env.PERF_BASELINE_REVISION || null, candidate: process.env.PERF_CANDIDATE_REVISION || null},
        dotnetSdk: {baseline: process.env.PERF_BASELINE_SDK_VERSION || null, candidate: process.env.PERF_CANDIDATE_SDK_VERSION || null},
        viewport, deviceScaleFactor: 1, repetitions, stepsPerSegment: steps, movingMilliseconds: movingMs,
        cpu: os.cpus()[0]?.model, availableParallelism: os.availableParallelism(), browserArgs,
        renderer: "Uno shared WebGPU / SwiftShader Vulkan software adapter", scenarios: selected,
        harness: Object.fromEntries(["webgpu-performance.cjs", "webgpu-performance-probe.cjs", "explore.cjs", "gpu.cjs"]
            .map(name => [name, digest(fs.readFileSync(path.join(__dirname, name)))])),
        notes: ["The identical head harness observes both Release AOT packages on one runner and browser in ABBA order.",
            "Native wheel inputs use the same closed trajectory, bundled data and fixed quality. Free-roam camera replay is paused.",
            "Input-to-submit measures event/CPU latency. Queue-completion callback latency includes prior queue work and callback scheduling; it is not a GPU timestamp or display time.",
            "CPU frame/submission diagnostics exclude GPU execution. Phase timings exist only in the candidate and are never invented for the baseline.",
            "Serialized steps measure latency, not sustained FPS. The separate held-key phase reports submissions/s, rAF and start/end queue-prefix completion latency, with trajectory and upload data; it does not assert identical world positions.",
            "No full graph, per-draw wrapper, screenshot or GPU image readback runs in the timing windows. Lightweight queue/upload/diagnostic hooks remain and are identical for both variants.",
            "Untimed Balanced-to-High transitions upload the unchanged camera for projection/terrain-transform comparison; no projection payload is copied during measurement. Walking retains its paused session and compares physical HUD telemetry instead of opening Share, which exits free roam.",
            "Screenshot parity is checked outside timing at the same submitted physical projection, extent, style, quality and scene geometry counts. Map cases also compare public v5 links. Different scene coverage fails comparison.",
            "Shader-rounding tolerance is explicit: <=1% pixels differ by more than 2/255 and mean RGB error <=0.25/255. Original PNGs and numerical differences are retained.",
            "SwiftShader results establish software-adapter behavior, not physical-GPU FPS or hardware speedup."], runs: []};
    const save = () => fs.writeFileSync(path.join(output, "report.json"), JSON.stringify(report, null, 2) + "\n");
    const {chromium} = require("playwright"), {PNG} = require("pngjs");
    const executablePath = process.env.CHROMIUM_PATH || undefined;
    const browser = await chromium.launch({headless: false, executablePath, channel: executablePath ? undefined : "chromium",
        ignoreDefaultArgs: ["--enable-features=CDPScreenshotNewSurface"], args: browserArgs});
    report.browser = browser.version();
    try {
        const order = repetitions === 2 ? [["baseline", 1], ["candidate", 1], ["candidate", 2], ["baseline", 2]] : [["baseline", 1], ["candidate", 1]];
        report.order = order;
        report.comparisons = [];
        for (const scenario of selected) {
            for (const [variant, repetition] of order) {
                report.runs.push(await measure(browser, variant, repetition, scenario, variant === "baseline" ? baselineUrl : candidateUrl, output, steps, movingMs));
                save();
            }
            report.comparisons.push(...compare({...report, scenarios: [scenario]}, output, PNG));
            save();
        }
        report.passed = true;
        console.log("WEBGPU_PERF_COMPARISON", JSON.stringify(report.comparisons.map(({visual, ...rest}) => rest)));
    } catch (error) { report.passed = false; report.failure = error.stack || String(error); throw error; }
    finally { save(); await browser.close(); }
}
if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });
module.exports = {distribution, makeTrace, checkStep, checkHealth, imageDifference, summarizeSteps, compare};
