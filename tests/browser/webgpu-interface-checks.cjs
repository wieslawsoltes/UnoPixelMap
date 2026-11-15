// Contract checks execute the real integration helpers without Playwright,
// Chromium, PNG dependencies, or a running application.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "webgpu.cjs"), "utf8");
const moduleUnderTest = {exports: {}}, loggedFailures = [];
const unavailable = () => assert.fail("Importing integration helpers must not launch a browser or execute a suite");
const dependencies = {
    playwright: {chromium: {launch: unavailable}}, pngjs: {},
    "./webgpu-rendergraph-probe.cjs": {installRenderGraphProbe: unavailable},
    "./webgpu-shader-fixtures.cjs": {runShaderFixtures: unavailable},
    "./webgpu-pixel-art-fixtures.cjs": {runPixelArtFixtures: unavailable},
    "./native-menu.cjs": {menu: unavailable},
    "./explore.cjs": {waitForSettledFocus: async (_page, locator) => locator.focus()}
};
const requireStub = name => name.startsWith("node:") ? require(name) :
    Object.hasOwn(dependencies, name) ? dependencies[name] : assert.fail("Unexpected integration dependency: " + name);
requireStub.main = {};
const integrationContext = vm.createContext({module: moduleUnderTest, require: requireStub,
    process: {argv: ["node", "webgpu.cjs", "--preflight-only"], env: {}},
    console: {log() {}, error: (...values) => loggedFailures.push(values)},
    URL, Date, Promise, setTimeout, clearTimeout});
vm.runInContext(source, integrationContext, {filename: "webgpu.cjs"});
const {waitForSheetTop, verifyLayerPointerDismissal, selectLayerKeyboard, runIndependentSuites,
    captureInterface, assertMobileGameClearance, verifyCameraHomeRoundTrip} = moduleUnderTest.exports;

function peer(name, id, rect, connected = true, attributes = {}) {
    const group = name === "Search and directions";
    return {id, tagName: group ? "DIV" : "BUTTON", isConnected: connected,
        getAttribute: key => attributes[key] ?? (key === "aria-label" ? name : key === "role" ? group ? "group" : "button" : null),
        getBoundingClientRect: () => rect};
}
const sheet = (y, id = "sheet") => peer("Search and directions", id, {x: 0, y, width: 390, height: 844 - y});
const handle = (y, id = "handle", connected = true) =>
    peer(y < 200 ? "Collapse search sheet" : "Expand search sheet", id, {x: 135, y, width: 120, height: 44}, connected);
const expandedRanges = {handle: [0, 167], sheet: [0, 167]};

async function sheetChecks() {
    let reads = 0, queries = 0;
    const frames = [
        [sheet(708, "old-sheet"), handle(710, "old-handle")],
        [sheet(76, "new-sheet"), handle(710, "old-handle", false)],
        [sheet(76, "new-sheet"), handle(78, "new-handle")]
    ];
    const page = {getByRole: () => {
        queries++;
        return {or: () => ({evaluateAll: async fn => fn(frames[Math.min(reads++, frames.length - 1)])})};
    }};
    const actual = await waitForSheetTop(page, expandedRanges, "Expanded replacement", {timeout: 1000, pollInterval: 1});
    assert.equal(actual.handle.id, "new-handle");
    assert.equal(actual.sheet.id, "new-sheet");
    assert.equal(reads, 3, "A disconnected predecessor cannot satisfy the geometry contract");
    assert.equal(queries, reads * 2, "Each poll resolves the sheet and handle locators afresh");

    for (const [name, peers] of [
        ["stale handle", [sheet(76), handle(710)]],
        ["stale sheet", [sheet(708), handle(78)]],
        ["missing sheet", [handle(78)]]
    ]) {
        const stalled = {getByRole: () => ({or: () => ({evaluateAll: async fn => fn(peers)})})};
        const started = Date.now();
        await assert.rejects(waitForSheetTop(stalled, expandedRanges, name, {timeout: 20, pollInterval: 1}), error => {
            assert.match(error.message, /did not reach their required bounds within 20 ms/);
            assert.ok(error.interfaceEvidence.samples.length > 0);
            assert.equal(error.interfaceEvidence.lastPeers.length, peers.length);
            assert.deepEqual(Array.from(error.interfaceEvidence.lastPeers, value => value.rect.y), peers.map(value => value.getBoundingClientRect().y));
            assert.equal(error.interfaceEvidence.timeout, 20);
            return true;
        });
        assert.ok(Date.now() - started < 5000, "A failed geometry check retains its bounded deadline");
    }
    const unresponsive = {getByRole: () => ({or: () => ({evaluateAll: () => new Promise(() => {})})})};
    await assert.rejects(waitForSheetTop(unresponsive, expandedRanges, "Unresponsive page", {timeout: 20}), error => {
        assert.match(error.interfaceEvidence.pendingFailure, /Timed out reading live sheet geometry/);
        return true;
    });

    const middleRanges = {handle: [430.44, 432.44], sheet: [429.44, 431.44]};
    let settlingReads = 0;
    const releaseFrames = [
        [sheet(408), handle(409)],
        [sheet(430, "first-sheet"), handle(431, "first-handle")],
        [sheet(408), handle(409)],
        [sheet(430, "settled-sheet"), handle(431, "settled-handle")]
    ];
    const settling = {getByRole: () => ({or: () => ({evaluateAll: async fn =>
        fn(releaseFrames[Math.min(settlingReads++, releaseFrames.length - 1)])})})};
    const settled = await waitForSheetTop(settling, middleRanges, "Touch release snaps", {timeout: 1000, pollInterval: 1, stableForMs: 12});
    assert.equal(settled.handle.id, "settled-handle", "A transient in-range peer cannot satisfy the settled detent contract");
    assert.equal(settled.sheet.id, "settled-sheet");
    assert.ok(settled.observedStableMs >= 12, "The same connected peers and bounds remain stable before capture");
    assert.ok(settlingReads > releaseFrames.length);

    const retainedDrag = {getByRole: () => ({or: () => ({evaluateAll: async fn => fn([sheet(408), handle(409)])})})};
    await assert.rejects(waitForSheetTop(retainedDrag, middleRanges, "Retained drag", {timeout: 25, pollInterval: 1, stableForMs: 12}),
        error => error.interfaceEvidence.lastPeers[0].rect.y === 408 && error.interfaceEvidence.stableForMs === 12);
    let movingPeer = 0;
    const unsettled = {getByRole: () => ({or: () => ({evaluateAll: async fn => {
        const offset = movingPeer++ % 2;
        return fn([sheet(430 + offset), handle(431 + offset)]);
    }})})};
    await assert.rejects(waitForSheetTop(unsettled, middleRanges, "Unsettled detent", {timeout: 25, pollInterval: 1, stableForMs: 12}),
        error => error.interfaceEvidence.stableForMs === 12 && error.interfaceEvidence.samples.length > 1);
}

function layerFixture({pointerWorks = true, keyboardWorks = true, initiallyOpen = true} = {}) {
    let open = initiallyOpen, focused = null;
    const clicks = [], keys = [], focus = [], waits = [];
    const card = peer("Map layer", "layer-map", {x: 880, y: 100, width: 80, height: 72});
    const choices = peer("Map layer choices", "layer-choices", {x: 880, y: 100, width: 272, height: 72}, true,
        {role: "group", elementtype: "Grid"});
    choices.tagName = "DIV";
    choices.style = {left: "17px", top: "60px"};
    const presenter = peer("Layers", "layer-presenter", {x: 863, y: 40, width: 306, height: 150}, true,
        {role: "group", elementtype: "FlyoutPresenter"});
    presenter.tagName = "DIV";
    presenter.style = {left: "863px", top: "40px"};
    card.parentElement = choices;
    choices.parentElement = presenter;
    const chooser = peer("Choose map layers", "layer-chooser", {x: 1130, y: 16, width: 44, height: 44});
    const canvas = {id: "uno-canvas", tagName: "CANVAS", getAttribute: () => null,
        getBoundingClientRect: () => ({x: 0, y: 0, width: 1200, height: 800})};
    const context = vm.createContext({document: {elementFromPoint: () => canvas},
        requestAnimationFrame: callback => setImmediate(callback), Promise});
    const invoke = (fn, ...args) => vm.runInContext("(" + fn.toString() + ")", context)(...args);
    const controls = new Map([card, chooser].map(element => {
        const name = element.getAttribute("aria-label");
        const visible = () => name === "Choose map layers" || open;
        const control = {
            or() { return this; },
            evaluateAll: async (fn, argument) => invoke(fn, visible() ? [element] : [], argument),
            isVisible: async () => visible(),
            focus: async () => { focused = name; focus.push(name); },
            waitFor: async ({state, timeout}) => {
                waits.push({state, timeout});
                if (visible() !== (state === "visible")) throw new Error("The layer peer did not become " + state);
            }
        };
        return [name, control];
    }));
    const page = {
        getByRole: (_role, {name}) => controls.get(name),
        viewportSize: () => ({width: 1200, height: 800}),
        evaluate: async (fn, argument) => invoke(fn, argument),
        mouse: {click: async (x, y) => { clicks.push({x, y}); if (pointerWorks) open = false; }},
        keyboard: {press: async key => {
            keys.push(key);
            if (focused === "Choose map layers") open = true;
            else if (focused === "Map layer" && keyboardWorks) open = false;
        }}
    };
    return {page, clicks, keys, focus, waits};
}

async function layerChecks() {
    const success = layerFixture();
    const receipt = await verifyLayerPointerDismissal(success.page, "Map layer");
    assert.equal(receipt.pointer.passed, true);
    assert.equal(receipt.keyboard.attempted, false);
    assert.deepEqual(success.clicks, [{x: 920, y: 136}]);
    assert.deepEqual(success.keys, [], "A successful native click does not send an extra activation key");
    assert.equal(receipt.pointer.before.peers[0].id, "layer-map");
    const ancestors = receipt.pointer.before.peers[0].ancestors;
    assert.deepEqual(Array.from(ancestors, value => value.id), ["layer-choices", "layer-presenter"]);
    assert.equal(ancestors[0].name, "Map layer choices");
    assert.equal(ancestors[0].tag, "DIV");
    assert.equal(ancestors[0].role, "group");
    assert.equal(ancestors[0].elementtype, "Grid");
    assert.equal(ancestors[0].inline.left, "17px");
    assert.equal(ancestors[0].inline.top, "60px");
    assert.equal(ancestors[0].rect.x, 880);
    assert.equal(ancestors[1].rect.y, 40);
    assert.equal(receipt.pointer.before.elementFromPoint.id, "uno-canvas");
    assert.equal(receipt.pointer.before.clickPoint.y, 136);
    assert.equal(receipt.pointer.after.peers.length, 0);

    for (const keyboardWorks of [true, false]) {
        const failure = layerFixture({pointerWorks: false, keyboardWorks});
        await assert.rejects(verifyLayerPointerDismissal(failure.page, "Map layer"), error => {
            assert.match(error.message, /native pointer activation did not dismiss the layer picker/);
            assert.equal(error.interfaceEvidence.pointer.passed, false);
            assert.equal(error.interfaceEvidence.pointer.attempted, true);
            assert.equal(error.interfaceEvidence.keyboard.attempted, true);
            assert.equal(error.interfaceEvidence.keyboard.passed, keyboardWorks);
            assert.equal(error.interfaceEvidence.pointer.after.peers[0].id, "layer-map");
            assert.match(error.interfaceEvidence.pointer.failure, /did not become hidden/);
            return true;
        });
        assert.equal(failure.clicks.length, 1, "A missed pointer hit is never retried or hidden");
        assert.deepEqual(failure.keys, ["Space"], "The independent settled keyboard diagnostic activates once");
        assert.deepEqual(failure.focus, ["Map layer"]);
        assert.ok(failure.waits.some(wait => wait.timeout === 30000));
        assert.ok(failure.waits.some(wait => wait.timeout === 10000));
    }

    const keyboard = layerFixture({initiallyOpen: false});
    await selectLayerKeyboard(keyboard.page, "Map layer");
    assert.deepEqual(keyboard.focus, ["Choose map layers", "Map layer"]);
    assert.deepEqual(keyboard.keys, ["Space", "Space"]);
    assert.equal(keyboard.clicks.length, 0, "Rendering layer setup is independent of the pointer-dismissal UI gate");
}

async function independenceChecks() {
    const order = [], browser = {};
    const suite = (name, fail = false) => [name, async received => {
        assert.equal(received, browser); order.push(name);
        if (fail) throw new Error(name + " failed");
    }];
    await assert.rejects(runIndependentSuites(browser, [suite("desktop-interface", true), suite("desktop-rendering"),
        suite("mobile-interface", true), suite("mobile-pinch"), suite("device-loss")]), error => {
        assert.equal(error.errors.length, 2);
        assert.match(error.message, /desktop-interface, mobile-interface browser suites failed/);
        return true;
    });
    assert.deepEqual(order, ["desktop-interface", "desktop-rendering", "mobile-interface", "mobile-pinch", "device-loss"]);
    assert.equal(loggedFailures.length, 2, "Every failed UI suite remains visible in the final failure report");
}

function gameChromeChecks() {
    const viewport = {width: 390, height: 844};
    const visible = (name, rect) => ({name, id: name, connected: true, rect});
    const fixture = () => ({
        chrome: [
            visible("Map layers and location", {x: 328, y: 213.5, width: 50, height: 95}),
            visible("Free roam settings", {x: 12, y: 12, width: 360, height: 189.5}),
            visible("Map attribution", {x: 12, y: 786.5, width: 265, height: 51.5})
        ],
        controls: {
            pad: [visible("Free roam touch controls", {x: 12, y: 640, width: 366, height: 128})],
            movement: [visible("Free roam movement stick", {x: 12, y: 656, width: 112, height: 112})],
            look: [visible("Free roam look stick", {x: 266, y: 656, width: 112, height: 112})],
            jump: [visible("Free roam jump or brake", {x: 171, y: 657, width: 48.5, height: 48})],
            boost: [visible("Free roam boost", {x: 171, y: 710, width: 48.5, height: 48})]
        }
    });
    const clear = fixture();
    assert.equal(assertMobileGameClearance(viewport, clear.chrome, clear.controls).visibleTouchPeers, 5);
    const expanded = fixture();
    expanded.chrome[0].rect.y = 12;
    expanded.chrome[1].rect = {x: 15, y: 263, width: 360, height: 503};
    assert.equal(assertMobileGameClearance(viewport, expanded.chrome, {}).passed, true);

    // These are the two observed e010a465 mobile failures: the original rail
    // behind the top card, and attribution covering the lower stick/pad area.
    const coveredRail = fixture(); coveredRail.chrome[0].rect.y = 12;
    assert.throws(() => assertMobileGameClearance(viewport, coveredRail.chrome, coveredRail.controls),
        /map rail does not overlap free-roam settings/);
    const coveredPad = fixture(); coveredPad.controls.pad[0].rect.y = 680;
    assert.throws(() => assertMobileGameClearance(viewport, coveredPad.chrome, coveredPad.controls), error => {
        assert.match(error.message, /Map attribution does not overlap Free roam touch controls/);
        assert.equal(error.interfaceEvidence.gameChromeClearance.touchControls.pad[0].rect.y, 680);
        return true;
    });
    const coveredSettings = fixture(); coveredSettings.chrome[1].rect = {x: 15, y: 329, width: 360, height: 503};
    assert.throws(() => assertMobileGameClearance(viewport, coveredSettings.chrome, {}),
        /Map attribution does not overlap free-roam settings/);
    const missingAttribution = fixture(); missingAttribution.chrome.pop();
    assert.throws(() => assertMobileGameClearance(viewport, missingAttribution.chrome, missingAttribution.controls),
        /Map attribution: one live mobile chrome peer exists/);
    const clippedRail = fixture(); clippedRail.chrome[0].rect.x = 350;
    assert.throws(() => assertMobileGameClearance(viewport, clippedRail.chrome, clippedRail.controls),
        /Map layers and location: visible mobile chrome remains inside the viewport/);
}

async function interfacePresentationChecks() {
    const originalVerify = integrationContext.verifyGpu, originalHealth = integrationContext.assertGraphicsHealthy;
    const order = [];
    let completed = false, geometryRead = false;
    try {
        integrationContext.assertGraphicsHealthy = async (_page, name) => order.push(name.endsWith("before screenshot") ? "health-before" : "health-after");
        integrationContext.verifyGpu = async () => {
            order.push("gpu-start");
            await Promise.resolve();
            completed = true;
            order.push("gpu-completed-and-presented");
        };
        const page = {screenshot: async () => {
            assert.ok(completed && geometryRead, "The PNG follows GPU completion/presentation and fresh geometry");
            order.push("screenshot");
        }};
        await captureInterface(page, "presented-interface-contract", async () => {
            assert.equal(completed, true);
            geometryRead = true;
            order.push("fresh-geometry");
            return {finalSheetTop: 430.5};
        });
        assert.deepEqual(order, ["health-before", "gpu-start", "gpu-completed-and-presented", "fresh-geometry", "screenshot", "health-after"]);
        const recorded = vm.runInContext("report.scenarios.at(-1)", integrationContext);
        assert.equal(recorded.name, "presented-interface-contract");
        assert.equal(recorded.finalSheetTop, 430.5, "The report retains geometry read after the presentation fence");

        integrationContext.verifyGpu = async () => { throw new Error("GPU presentation did not complete"); };
        await assert.rejects(captureInterface({screenshot: unavailable}, "failed-interface-contract", unavailable),
            /GPU presentation did not complete/);
    } finally {
        integrationContext.verifyGpu = originalVerify;
        integrationContext.assertGraphicsHealthy = originalHealth;
    }
}

async function cameraHomeChecks() {
    const home = ["Open route planner", "Share current view", "Browse city guide", "Open free roam game",
        "Explore Lower Manhattan", "Explore Paris", "Explore Tokyo"];
    function fixture({restoreHome = true, cameraRemains = false} = {}) {
        let open = true;
        const clicks = [];
        const visible = name => home.includes(name) ? !open && restoreHome :
            name === "Close panel" ? open : open || cameraRemains;
        const element = name => peer(name, name, name === "Close panel"
            ? {x: 315, y: 135, width: 44, height: 44} : {x: 32, y: 200, width: 150, height: 44});
        const locator = names => ({
            names,
            or(other) { return locator([...new Set([...names, ...other.names])]); },
            waitFor: async ({state}) => {
                assert.equal(names.length, 1);
                if (visible(names[0]) !== (state === "visible")) throw new Error(names[0] + " did not become " + state);
            },
            boundingBox: async () => element(names[0]).getBoundingClientRect(),
            evaluateAll: async (fn, argument) => fn(names.filter(visible).map(element), argument)
        });
        const context = vm.createContext({requestAnimationFrame: callback => setImmediate(callback), Promise});
        return {clicks, page: {
            getByRole: (_role, {name}) => locator([name]),
            getByLabel: name => locator([name]),
            evaluate: async fn => vm.runInContext("(" + fn.toString() + ")", context)(),
            mouse: {click: async (x, y) => { clicks.push({x, y}); open = false; }},
            keyboard: {press: unavailable}, touchscreen: {tap: unavailable}
        }};
    }
    const success = fixture();
    const receipt = await verifyCameraHomeRoundTrip(success.page);
    assert.equal(receipt.passed, true);
    assert.deepEqual(success.clicks, [{x: 337, y: 157}], "The roundtrip retains exactly one native Close-panel pointer input");
    assert.equal(receipt.pointer.point.y, 157);
    assert.deepEqual(Array.from(receipt.homePeers, value => value.name), home);
    assert.equal(receipt.cameraPeers.length, 0);

    const missingHome = fixture({restoreHome: false});
    await assert.rejects(verifyCameraHomeRoundTrip(missingHome.page, {timeout: 25}), error => {
        assert.match(error.interfaceEvidence.cameraHomeRoundTrip.failure, /Open route planner did not become visible/);
        assert.equal(error.interfaceEvidence.cameraHomeRoundTrip.homePeers.length, 0);
        assert.equal(error.interfaceEvidence.cameraHomeRoundTrip.pointer.input, "mouse");
        return true;
    });
    assert.equal(missingHome.clicks.length, 1, "Missing Home semantics cannot trigger a pointer or keyboard fallback");
    const staleCamera = fixture({cameraRemains: true});
    await assert.rejects(verifyCameraHomeRoundTrip(staleCamera.page), error => {
        assert.match(error.interfaceEvidence.cameraHomeRoundTrip.failure, /North-up plan did not become hidden/);
        assert.equal(error.interfaceEvidence.cameraHomeRoundTrip.cameraPeers.length, 9);
        return true;
    });
    assert.equal(staleCamera.clicks.length, 1);
}

(async () => {
    await sheetChecks();
    await layerChecks();
    gameChromeChecks();
    await interfacePresentationChecks();
    await cameraHomeChecks();
    await independenceChecks();
    console.log("WebGPU interface contracts passed: settled sheet geometry, completed GPU presentation, unobscured mobile game controls, native Camera/Home roundtrips, pointer receipts and independent rendering suites.");
})().catch(error => { console.error(error); process.exitCode = 1; });
