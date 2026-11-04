// node play-performance.cjs <site URL> <output directory>
// Run baseline and candidate serially with the same browser, CPU affinity and viewport.
// PROFILE_CASES selects comma-separated names. PROFILE_DURATION_MS defaults to 8000.
// CPU_PROFILE_CASES selects separate CPU profiling passes, or "none" to omit them.
// PIXELMAP_REVISION records the deployed artifact revision, independently of this harness.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const {createHash} = require("node:crypto");
const {chromium} = require("playwright");
const {installProbe, summarizeProbe, metrics, distribution} = require("./performance.cjs");

const city = "v4/40.706846/-74.008817/17/-51/55/Day/Map/Perspective/0/Relief/1.00";
const harbor = "v4/40.695/-74.0285/17/-51/55/Day/Map/Perspective/0/Relief/1.00";
const scenarios = [
    {name: "map-idle-default", hash: ""},
    {name: "map-idle-relief", hash: city},
    {name: "walk-stationary-firstperson", hash: city, vehicle: 0, camera: 1, title: "Walking"},
    {name: "walk-moving-firstperson", hash: city, vehicle: 0, camera: 1, title: "Walking", key: "w"},
    {name: "walk-moving-chase", hash: city, vehicle: 0, camera: 0, title: "Walking", key: "w"},
    {name: "car-stationary-chase", hash: city, vehicle: 1, camera: 0, title: "Driving"},
    {name: "car-moving-chase", hash: city, vehicle: 1, camera: 0, title: "Driving", key: "w"},
    {name: "car-moving-firstperson", hash: city, vehicle: 1, camera: 1, title: "Driving", key: "w"},
    {name: "freeflight-stationary", hash: harbor, vehicle: 6, title: "Free flight"},
    {name: "freeflight-moving", hash: harbor, vehicle: 6, title: "Free flight", key: "w"}
];
const digest = file => createHash("sha256").update(fs.readFileSync(file)).digest("hex");

function summarizeProfile(profile) {
    const nodes = new Map(profile.nodes.map(node => [node.id, node]));
    const parents = new Map();
    for (const node of profile.nodes)
        for (const child of node.children || []) parents.set(child, node.id);
    const self = new Map();
    const inclusive = new Map();
    for (let index = 0; index < (profile.samples || []).length; index++) {
        let id = profile.samples[index];
        const time = profile.timeDeltas?.[index] || 1000;
        self.set(id, (self.get(id) || 0) + time);
        while (id) {
            inclusive.set(id, (inclusive.get(id) || 0) + time);
            id = parents.get(id);
        }
    }
    const table = values => [...values].sort((a, b) => b[1] - a[1]).slice(0, 45).map(([id, time]) => ({
        id, milliseconds: time / 1000, ...nodes.get(id).callFrame,
        parent: parents.has(id) ? nodes.get(parents.get(id)).callFrame.functionName : null
    }));
    return {
        sampleCount: profile.samples?.length || 0,
        durationMs: (profile.endTime - profile.startTime) / 1000,
        self: table(self), inclusive: table(inclusive)
    };
}

async function prepare(browser, scenario, ui, output) {
    const opened = await ui.open(browser, scenario.hash);
    const {page} = opened;
    if (scenario.vehicle !== undefined) {
        await ui.action(page, "Open free roam game");
        if (scenario.vehicle !== 0) await ui.choose(page, "Free roam vehicle", scenario.vehicle);
        if (scenario.camera !== undefined) await ui.choose(page, "Free roam camera", scenario.camera);
        await ui.action(page, "Start free roam at map center");
        await ui.see(page, new RegExp(scenario.title));
        await page.getByRole("button", {name: "Pause free roam", exact: true}).waitFor({state: "visible"});
        const map = page.getByRole("group", {name: "Interactive world map", exact: true});
        await map.focus();
        await ui.waitForSettledFocus(page, map);
    }
    await page.waitForTimeout(2000);
    await page.screenshot({path: path.join(output, scenario.name + "-before.png")});
    return opened;
}

async function captureProfile(page, cdp, scenario, duration) {
    if (scenario.vehicle !== undefined) await page.keyboard.press("r");
    await page.waitForTimeout(1000);
    await cdp.send("Profiler.enable");
    await cdp.send("Profiler.setSamplingInterval", {interval: 1000});
    if (scenario.key) await page.keyboard.down(scenario.key);
    try {
        await cdp.send("Profiler.start");
        await page.waitForTimeout(duration);
        return (await cdp.send("Profiler.stop")).profile;
    } finally {
        if (scenario.key) await page.keyboard.up(scenario.key);
    }
}

async function measure(browser, scenario, ui, report, output, profileCases, save) {
    const {context, page} = await prepare(browser, scenario, ui, output);
    const cdp = await context.newCDPSession(page);
    try {
        await cdp.send("Performance.enable", {timeDomain: "threadTicks"});
        const map = await page.getByRole("group", {name: "Interactive world map", exact: true}).boundingBox();
        const beforeHud = scenario.vehicle === undefined ? null : await ui.telemetry(page);
        if (scenario.key) await page.keyboard.down(scenario.key);
        let raw, before, after;
        try {
            before = metrics(await cdp.send("Performance.getMetrics"));
            await page.evaluate(({width, height}) => __pixelAtlasPerf.begin(width, height), map);
            await page.waitForTimeout(report.durationRequestedMs);
            raw = await page.evaluate(() => __pixelAtlasPerf.finish());
            after = metrics(await cdp.send("Performance.getMetrics"));
        } finally {
            if (scenario.key) await page.keyboard.up(scenario.key);
        }
        const afterHud = scenario.vehicle === undefined ? null : await ui.telemetry(page);
        const result = {name: scenario.name, scenario, beforeHud, afterHud, ...summarizeProbe(raw, before, after)};
        const copyTimes = raw.bufferReads.filter(item => item.pixelPack).map(item => item.time);
        const copyIntervals = copyTimes.slice(1).map((time, index) => time - copyTimes[index]);
        result.completedArtFrames = copyTimes.length;
        result.completedArtFramesPerSecond = copyTimes.length * 1000 / raw.durationMs;
        result.completedArtFrameIntervals = distribution(copyIntervals);
        result.rafPerSecond = raw.frames.length * 1000 / raw.durationMs;
        result.cpuTaskPercent = result.cpu.TaskDurationMs / raw.durationMs * 100;
        fs.writeFileSync(path.join(output, scenario.name + "-raw.json"), JSON.stringify({raw, before, after}, null, 2));
        await page.screenshot({path: path.join(output, scenario.name + "-after.png")});
        console.log("PLAY", JSON.stringify({
            name: result.name, wallMs: result.durationMs, cpuMs: result.cpu.TaskDurationMs,
            cpuPercent: result.cpuTaskPercent, raf: result.rafPerSecond,
            rafMedian: result.raf.medianMs, rafP95: result.raf.p95Ms, rafMax: result.raf.maxMs,
            artFps: result.completedArtFramesPerSecond, artPixelSizes: result.readPixels.byPixelSize,
            maximumPending: result.pixelPackReadback.maximumPending,
            copyMs: result.pixelPackReadback.getBufferSubData.totalCallMs,
            copyP95: result.pixelPackReadback.getBufferSubData.p95Ms, beforeHud, afterHud
        }));
        report.cases.push(result);
        save();
        if (scenario.key)
            assert.ok(afterHud.distance > beforeHud.distance, "The held native key advances " + scenario.name);
        if (profileCases.has(scenario.name)) {
            const profile = await captureProfile(page, cdp, scenario, report.durationRequestedMs);
            fs.writeFileSync(path.join(output, scenario.name + ".cpuprofile"), JSON.stringify(profile));
            result.profile = summarizeProfile(profile);
            fs.writeFileSync(path.join(output, scenario.name + "-stacks.json"), JSON.stringify(result.profile, null, 2));
            console.log("PROFILE", scenario.name, JSON.stringify(result.profile.self.slice(0, 12)));
            save();
        }
    } catch (error) {
        await page.screenshot({path: path.join(output, scenario.name + "-failure.png")}).catch(() => {});
        fs.writeFileSync(path.join(output, scenario.name + "-failure.txt"),
            await page.locator("body").ariaSnapshot().catch(() => ""));
        throw error;
    } finally {
        await context.close();
    }
}

async function main() {
    const base = process.argv[2], output = process.argv[3];
    assert.ok(base && output, "Provide a site URL and output directory");
    const duration = Number(process.env.PROFILE_DURATION_MS || 8000);
    assert.ok(Number.isFinite(duration) && duration > 0, "The measurement duration is positive and finite");
    const filter = process.env.PROFILE_CASES?.split(",");
    const selected = filter ? scenarios.filter(item => filter.includes(item.name)) : scenarios;
    assert.ok(selected.length > 0, "The selected play case exists");
    if (filter) assert.ok(filter.every(name => scenarios.some(item => item.name === name)), "Every selected play case exists");
    const profileCases = new Set((process.env.CPU_PROFILE_CASES ||
        "walk-stationary-firstperson,car-moving-chase,freeflight-moving").split(","));
    fs.mkdirSync(output, {recursive: true});
    process.env.PIXELMAP_SHOTS = output;
    // Load after setting the evidence directory. Public activation and source
    // isolation are shared with the end-to-end exploration checks.
    const ui = require("./explore.cjs");
    const browserArgs = ["--disable-dev-shm-usage", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"];
    const report = {
        revision: process.env.PIXELMAP_REVISION || null, base, capturedAtUtc: new Date().toISOString(),
        viewport: {width: 1200, height: 780}, deviceScaleFactor: 1,
        cpu: os.cpus()[0]?.model, availableParallelism: os.availableParallelism(),
        backend: "GPU active, SwiftShader", browserArgs,
        durationRequestedMs: duration, warmupMs: 2000, metricsTimeDomain: "threadTicks",
        harnessSha256: digest(__filename),
        explorationHarnessSha256: digest(path.join(__dirname, "explore.cjs")),
        productionPerfHarnessSha256: digest(path.join(__dirname, "performance.cjs")),
        originalHarnessSha256: "a0b48ce1fe0bb44ee5ceccbdd9651f71fab20b688e1591e6276cee629a6b3940",
        originalBaselineRevision: "11188d0c265c9688ca8a169200e2f1573ec9fbeb",
        notes: "rAF measures scheduling cadence. Pixel-pack copies and art uploads count pipeline work. " +
            "Final display presentation is outside these probes. Task CPU uses the main thread's threadTicks. " +
            "External map providers are blocked and bundled data supplies the scene. " +
            "GPU transfer timings include synchronization. CPU profiling runs separately after each selected timing case. " +
            "Moving cases use identical native held-key workloads. Faster simulation can travel farther, " +
            "so the before/after public HUD poses and distances are included. Stationary cases keep the same camera. " +
            "The software GPU is recorded explicitly and results must be compared under the same driver.",
        cases: []
    };
    const save = () => fs.writeFileSync(path.join(output, "report.json"), JSON.stringify(report, null, 2));
    const browser = await chromium.launch({executablePath: process.env.CHROMIUM_PATH || undefined, args: browserArgs});
    report.browser = browser.version();
    const createContext = browser.newContext.bind(browser);
    browser.newContext = async options => {
        const context = await createContext({...options, viewport: report.viewport});
        await context.addInitScript(installProbe, {observeDraws: true, requireDepthQuality: true, observeTransfers: true});
        return context;
    };
    try {
        for (const scenario of selected) await measure(browser, scenario, ui, report, output, profileCases, save);
        assert.deepEqual(ui.failures, [], "No managed or browser errors during the play benchmark");
    } catch (error) {
        report.error = error.stack;
        throw error;
    } finally {
        save();
        await browser.close();
    }
}

if (require.main === module)
    main().catch(error => { console.error(error); process.exitCode = 1; });
