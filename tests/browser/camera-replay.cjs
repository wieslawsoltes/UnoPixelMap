// node camera-replay.cjs <site URL> <output directory>
// node camera-replay.cjs --compare <baseline report.json> <candidate report.json> [output.json]
// CAMERA_REPLAY_CASES selects comma-separated cases. No production hooks or
// synthetic DOM events are used: each native wheel input awaits its map fence.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const {createHash} = require("node:crypto");
const {installCameraReplayProbe} = require("./camera-replay-probe.cjs");
const {sha256, checkStep, compareReplays} = require("./camera-replay-contract.cjs");

const city = "v4/40.706846/-74.008817/17/-51/55/Day/Map/Perspective/0/Relief/1.00";
const scenarios = [
    {name: "map-pan-relief", hash: city, motion: "pan"},
    {name: "map-orbit-relief", hash: city, motion: "orbit"},
    {name: "walk-look-firstperson", hash: city, motion: "look", vehicle: 0, camera: 1}
];
const digest = file => createHash("sha256").update(fs.readFileSync(file)).digest("hex");

function makeTrace(scenario, map) {
    // Chromium exposes native WheelEvent client coordinates as integral CSS
    // pixels even when CDP receives a fractional point (odd map dimensions).
    // Choose an integral point up front so the declared trace equals the input.
    const center = {x: Math.floor(map.x + map.width * .5), y: Math.floor(map.y + map.height * .5)};
    // A barrier-paced CDP vertical wheel can expose a legacy 120-unit detent.
    // Keep a horizontal component in each event so the production adapter
    // unambiguously selects its immediate two-axis trackpad path, even after a
    // slow GPU fence. Alt/Shift still select tilt/orbit through the public route.
    const segments = scenario.motion === "pan"
        ? [[12, 1, 0], [1, 12, 0], [-12, -1, 0], [-1, -12, 0]]
        : scenario.motion === "orbit"
            ? [[-10, 0, 8], [1, -5, 1], [10, 0, 8], [-1, 5, 1]]
            : [[-10, 0, 0], [1, -5, 0], [10, 0, 0], [-1, 5, 0]];
    return segments.flatMap(([deltaX, deltaY, modifiers]) => Array.from({length: 8}, () =>
        ({...center, deltaX, deltaY, modifiers})));
}

async function measure(browser, scenario, ui, output, report, performanceProbe, save) {
    const {context, page} = await ui.open(browser, scenario.hash);
    const diagnostics = {scenario, trace: [], rawSteps: []};
    try {
        if (scenario.vehicle !== undefined) {
            await ui.action(page, "Open free roam game");
            await ui.choose(page, "Free roam camera", scenario.camera);
            await ui.action(page, "Start free roam at map center");
            await page.getByRole("button", {name: "Pause free roam", exact: true}).waitFor({state: "visible"});
            await page.waitForTimeout(1200);
            await ui.action(page, "Pause free roam");
            await page.getByRole("button", {name: "Resume free roam", exact: true}).waitFor({state: "visible"});
        }
        await page.waitForTimeout(2000);
        const mapElement = page.getByRole("group", {name: "Interactive world map", exact: true});
        const map = await mapElement.boundingBox();
        assert.ok(map?.width > 500 && map?.height > 300, "The map viewport has settled");
        await mapElement.focus();
        await ui.waitForSettledFocus(page, mapElement);
        await page.mouse.move(map.x + map.width * .5, map.y + map.height * .5);
        await page.waitForFunction(() => performance.now() - __pixelAtlasCameraReplay.lastResolveAt >= 300);
        const beforeHud = scenario.vehicle === undefined ? null : await ui.telemetry(page);
        await page.screenshot({path: path.join(output, scenario.name + "-before.png")});
        const trace = makeTrace(scenario, map);
        diagnostics.trace = trace;
        const cdp = await context.newCDPSession(page);
        await cdp.send("Performance.enable", {timeDomain: "threadTicks"});
        const before = performanceProbe.metrics(await cdp.send("Performance.getMetrics"));
        await page.evaluate(({width, height}) => __pixelAtlasPerf.begin(width, height), map);
        const rawSteps = diagnostics.rawSteps, steps = [];
        for (let index = 0; index < trace.length; index++) {
            await page.evaluate(() => __pixelAtlasCameraReplay.begin());
            await cdp.send("Input.dispatchMouseEvent", {type: "mouseWheel", ...trace[index]});
            await page.waitForFunction(async () => {
                if (!__pixelAtlasCameraReplay.ready()) return false;
                // Let the signaled image enter the compositor before the next
                // pose. A newly submitted frame must pass the full barrier too.
                await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                return __pixelAtlasCameraReplay.ready();
            }, undefined, {timeout: 20000});
            const raw = await page.evaluate(() => __pixelAtlasCameraReplay.finish());
            rawSteps.push(raw);
            steps.push(checkStep(raw, trace[index], index));
        }
        const raw = await page.evaluate(() => __pixelAtlasPerf.finish());
        const after = performanceProbe.metrics(await cdp.send("Performance.getMetrics"));
        const captured = performanceProbe.summarizeProbe(raw, before, after);
        const submissions = steps.reduce((total, step) => total + step.projection.length, 0);
        assert.equal(captured.mapResolves.submissions, submissions,
            "Every map submission in the full capture belongs to an input step");
        const afterHud = scenario.vehicle === undefined ? null : await ui.telemetry(page);
        if (beforeHud) assert.deepEqual(afterHud, beforeHud, "A paused look replay retains the exact public physical telemetry");
        const result = {name: scenario.name, scenario, map, trace, traceSha256: sha256(trace), beforeHud, afterHud, steps,
            summary: {steps: steps.length, submissions, taskCpuMs: captured.cpu.TaskDurationMs,
                taskCpuPerStepMs: captured.cpu.TaskDurationMs / steps.length,
                inputToFirstSubmission: performanceProbe.distribution(steps.map(step => step.inputToFirstSubmissionMs)),
                inputToFirstFenceSignal: performanceProbe.distribution(steps.map(step => step.inputToFirstFenceSignalMs)),
                inputToSettledFenceSignal: performanceProbe.distribution(steps.map(step => step.inputToSettledFenceSignalMs)),
                capture: captured}};
        fs.writeFileSync(path.join(output, scenario.name + "-raw.json"), JSON.stringify({rawSteps, raw, before, after}, null, 2));
        await page.screenshot({path: path.join(output, scenario.name + "-after.png")});
        report.cases.push(result);
        save();
        console.log("CAMERA_REPLAY", JSON.stringify({name: result.name, traceSha256: result.traceSha256,
            projectionSequenceSha256: sha256(steps.map(step => step.projection)), ...result.summary}));
    } catch (error) {
        diagnostics.pending = await page.evaluate(() => __pixelAtlasCameraReplay.step).catch(() => null);
        diagnostics.lastResolveAt = await page.evaluate(() => __pixelAtlasCameraReplay.lastResolveAt).catch(() => null);
        fs.writeFileSync(path.join(output, scenario.name + "-failure-probe.json"), JSON.stringify(diagnostics, null, 2));
        await page.screenshot({path: path.join(output, scenario.name + "-failure.png")}).catch(() => {});
        fs.writeFileSync(path.join(output, scenario.name + "-failure.txt"),
            await page.locator("body").ariaSnapshot().catch(() => ""));
        throw error;
    } finally { await context.close(); }
}

async function main() {
    if (process.argv[2] === "--compare") {
        const baseline = JSON.parse(fs.readFileSync(process.argv[3], "utf8"));
        const candidate = JSON.parse(fs.readFileSync(process.argv[4], "utf8"));
        const comparison = compareReplays(baseline, candidate);
        if (process.argv[5]) fs.writeFileSync(process.argv[5], JSON.stringify(comparison, null, 2) + "\n");
        console.log(JSON.stringify(comparison, null, 2));
        return;
    }
    const base = process.argv[2], output = process.argv[3];
    assert.ok(base && output, "Provide a site URL and output directory");
    const filter = process.env.CAMERA_REPLAY_CASES?.split(",");
    const selected = filter ? scenarios.filter(item => filter.includes(item.name)) : scenarios;
    assert.ok(selected.length && (!filter || filter.every(name => selected.some(item => item.name === name))),
        "Every selected replay case exists");
    fs.mkdirSync(output, {recursive: true});
    process.env.PIXELMAP_SHOTS = output;
    const {chromium} = require("playwright");
    const ui = require("./explore.cjs");
    const performanceProbe = require("./performance.cjs");
    const browserArgs = ["--disable-dev-shm-usage", "--use-angle=swiftshader", "--enable-unsafe-swiftshader",
        ...JSON.parse(process.env.PIXELMAP_BROWSER_ARGS || "[]")];
    const browser = await chromium.launch({executablePath: process.env.CHROMIUM_PATH || undefined, args: browserArgs});
    const report = {schema: 2, protocol: "native-wheel-resolve-fence-v2", revision: process.env.PIXELMAP_REVISION || null,
        base, capturedAtUtc: new Date().toISOString(), viewport: {width: 1200, height: 780}, deviceScaleFactor: 1,
        browser: browser.version(), browserArgs, cpu: os.cpus()[0]?.model, availableParallelism: os.availableParallelism(),
        harness: Object.fromEntries(["camera-replay.cjs", "camera-replay-probe.cjs", "camera-replay-contract.cjs",
            "performance.cjs", "explore.cjs"].map(name => [name, digest(path.join(__dirname, name))])),
        notes: "Native wheel inputs traverse the public input adapter and immediate camera update path. " +
            "Each input retains every map resolve in order and waits for all corresponding signaled GPU fences plus a quiet barrier. " +
            "All map projection uniforms and terrain instance transform floats are retained for exact cross-artifact comparison. " +
            "Cache-layer indices are excluded from pose identity. External providers are blocked; bundled tiles supply the scene. " +
            "The walking case is paused: it measures look/render work, not simulation. " +
            "Task CPU includes probe and automation overhead; input latency starts at the trusted native wheel event. " +
            "Barrier-paced submissions are not throughput or physical display FPS. This is a software GPU measurement.", cases: []};
    const save = () => fs.writeFileSync(path.join(output, "report.json"), JSON.stringify(report, null, 2) + "\n");
    const createContext = browser.newContext.bind(browser);
    browser.newContext = async options => {
        const context = await createContext({...options, viewport: report.viewport, deviceScaleFactor: 1});
        await context.addInitScript(performanceProbe.installProbe,
            {observeDraws: true, requireDepthQuality: true, observeTransfers: true});
        await context.addInitScript(installCameraReplayProbe);
        return context;
    };
    try {
        for (const scenario of selected) await measure(browser, scenario, ui, output, report, performanceProbe, save);
        assert.deepEqual(ui.failures, [], "No managed or browser errors during camera replay");
    } catch (error) { report.error = error.stack; throw error; }
    finally { save(); await browser.close(); }
}

if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });
module.exports = {makeTrace, scenarios};
