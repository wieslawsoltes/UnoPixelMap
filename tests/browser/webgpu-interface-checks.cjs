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
vm.runInNewContext(source, {module: moduleUnderTest, require: requireStub,
    process: {argv: ["node", "webgpu.cjs", "--preflight-only"], env: {}},
    console: {log() {}, error: (...values) => loggedFailures.push(values)},
    URL, Date, Promise, setTimeout, clearTimeout}, {filename: "webgpu.cjs"});
const {waitForSheetTop, verifyLayerPointerDismissal, selectLayerKeyboard, runIndependentSuites} = moduleUnderTest.exports;

function peer(name, id, rect, connected = true) {
    const group = name === "Search and directions";
    return {id, tagName: group ? "DIV" : "BUTTON", isConnected: connected,
        getAttribute: key => key === "aria-label" ? name : key === "role" ? group ? "group" : "button" : null,
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
}

function layerFixture({pointerWorks = true, keyboardWorks = true, initiallyOpen = true} = {}) {
    let open = initiallyOpen, focused = null;
    const clicks = [], keys = [], focus = [], waits = [];
    const card = peer("Map layer", "layer-map", {x: 880, y: 100, width: 80, height: 72});
    const chooser = peer("Choose map layers", "layer-chooser", {x: 1130, y: 16, width: 44, height: 44});
    const canvas = {id: "uno-canvas", tagName: "CANVAS", getAttribute: () => null,
        getBoundingClientRect: () => ({x: 0, y: 0, width: 1200, height: 800})};
    const context = vm.createContext({document: {elementFromPoint: () => canvas},
        requestAnimationFrame: callback => setImmediate(callback), Promise});
    const invoke = (fn, argument) => vm.runInContext("(" + fn.toString() + ")", context)(argument);
    const controls = new Map([card, chooser].map(element => {
        const name = element.getAttribute("aria-label");
        const visible = () => name === "Choose map layers" || open;
        const control = {
            or() { return this; },
            evaluateAll: async fn => invoke(fn, visible() ? [element] : []),
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

(async () => {
    await sheetChecks();
    await layerChecks();
    await independenceChecks();
    console.log("WebGPU interface contracts passed: replaced sheet peers, strict handle/group geometry, bounded failures, pointer receipts, independent keyboard diagnostics and rendering-suite continuation.");
})().catch(error => { console.error(error); process.exitCode = 1; });
