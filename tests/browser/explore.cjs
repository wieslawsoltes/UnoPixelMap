const assert = require("node:assert/strict");
const {mkdir, writeFile} = require("node:fs/promises");
const {join} = require("node:path");
const {chromium} = require("playwright");
const {PNG} = require("pngjs");
const {activateMenuItem, setGpuRendering} = require("./gpu.cjs");

const base = new URL(process.argv[2] || "http://127.0.0.1:8000/");
const evidence = process.env.PIXELMAP_SHOTS;
const mountainView = "v4/45.875/10.85/12.3/-25/58/Day/Elevation/Perspective/0/Relief/1.00";
// The bundled Pine Street road center gives movement checks room before a wall.
const cityView = "v4/40.706846/-74.008817/17/-51/55/Day/Map/Perspective/0/Relief/1.00";
const failures = [];

async function see(page, expected, timeout = 25000) {
    const deadline = Date.now() + timeout;
    let snapshot;
    do {
        snapshot = await page.locator("body").ariaSnapshot();
        if (typeof expected === "function" ? expected(snapshot) : expected.test(snapshot)) return snapshot;
        await page.waitForTimeout(150);
    } while (Date.now() < deadline);
    if (evidence) {
        await page.screenshot({path: join(evidence, "explore-state-failure.png")});
        await writeFile(join(evidence, "explore-state-failure.txt"), snapshot);
    }
    throw new Error("Missing exploration state " + expected + "\n" + snapshot);
}

async function controlBounds(locator) {
    return locator.evaluate(element => {
        const {x, y, width, height} = element.getBoundingClientRect();
        const bounds = {x, y, width, height};
        const portal = element.closest('[elementtype="ScrollContentPresenter"]');
        const region = document.querySelector('[aria-label="Free roam settings content"]');
        if (!portal || portal.closest('[role="dialog"]') || !region) return bounds;
        // Sample the peer, portal and named region in one layout frame. Uno can
        // publish their absolute offsets in different frames after a resize.
        const native = portal.getBoundingClientRect();
        const visible = region.getBoundingClientRect();
        return {...bounds, x: x + visible.x - native.x, y: y + visible.y - native.y};
    });
}

async function tap(page, locator, touch = false) {
    await locator.waitFor({state: "visible"});
    await page.waitForTimeout(150);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const bounds = await controlBounds(locator);
    const viewport = page.viewportSize();
    assert.ok(bounds?.width > 0 && bounds?.height > 0, "The control has visible bounds");
    const left = Math.max(0, bounds.x), right = Math.min(viewport.width, bounds.x + bounds.width);
    const top = Math.max(0, bounds.y), bottom = Math.min(viewport.height, bounds.y + bounds.height);
    assert.ok(right > left && bottom > top, "The control intersects the viewport");
    const y = await locator.getAttribute("role") === "combobox" ? bottom - Math.min(16, (bottom - top) / 2) : (top + bottom) / 2;
    if (touch) await page.touchscreen.tap((left + right) / 2, y);
    else await page.mouse.click((left + right) / 2, y);
}

async function waitForSettledFocus(page, locator) {
    const target = await locator.elementHandle();
    assert.ok(target, "The keyboard target has an accessibility element");
    try {
        await page.waitForFunction(async element => {
            if (!element.isConnected || document.activeElement !== element) return false;
            for (let frame = 0; frame < 2; frame++) {
                await new Promise(requestAnimationFrame);
                if (document.activeElement !== element) return false;
            }
            return true;
        }, target);
    } finally {
        await target.dispose();
    }
}

async function action(page, name, touch = false) {
    const target = page.getByRole("button", {name, exact: true})
        .or(page.getByRole("checkbox", {name, exact: true}))
        .or(page.getByRole("switch", {name, exact: true}));
    if (touch) await tap(page, target, true);
    else {
        await target.waitFor({state: "visible"});
        await target.focus();
        await waitForSettledFocus(page, target);
        await page.keyboard.press(await target.getAttribute("role") === "switch" ? "Space" : "Enter");
    }
}

async function menu(page, name) {
    await action(page, "Map appearance");
    const item = page.getByRole("menuitem", {name, exact: true})
        .or(page.getByRole("menuitemcheckbox", {name, exact: true}));
    await activateMenuItem(page, item);
    await page.mouse.move(page.viewportSize().width / 2, 15);
}

async function choose(page, name, index, touch = false, selectedIndex = 0) {
    const selector = page.getByRole("combobox", {name, exact: true});
    const anchor = touch ? await controlBounds(selector) : null;
    const existingScrollers = await page.locator('[elementtype="ScrollContentPresenter"]')
        .evaluateAll(elements => elements.map(element => element.id));
    await tap(page, selector, touch);
    await page.waitForFunction(name => document.querySelector('[aria-label="' + name + '"]')?.getAttribute("aria-expanded") === "true", name);
    if (!touch) {
        // Leave focus inside the native popup. Focusing its external ComboBox
        // again races Uno's popup focus restoration and can discard the next key.
        await page.waitForFunction(() => document.activeElement?.getAttribute("elementtype") === "ScrollContentPresenter");
        await page.keyboard.press("Home");
        for (let item = 0; item < index; item++) {
            await page.waitForTimeout(75);
            await page.keyboard.press("ArrowDown");
        }
        await page.waitForTimeout(75);
        await page.keyboard.press("Enter");
        await page.waitForFunction(name => document.querySelector('[aria-label="' + name + '"]')?.getAttribute("aria-expanded") === "false", name);
        // Popup closure also restores ComboBox focus. Let that handoff finish
        // before a following command takes focus and receives its native key.
        await waitForSettledFocus(page, selector);
        return;
    }
    const options = page.getByRole("listbox", {name, exact: true}).getByRole("option");
    await options.nth(index).waitFor({state: "attached"});
    await page.waitForTimeout(200);
    // Uno exposes virtual options without rectangles. Use the native presenter
    // for row dimensions and its painted blue selection marker for row position.
    // The touch scenario opens the initial Walk selection at index zero.
    const popup = await page.locator('[elementtype="ScrollContentPresenter"]').evaluateAll((elements, existing) => {
        const element = elements.find(element => !existing.includes(element.id) && element.getBoundingClientRect().height > 0);
        if (!element) return null;
        const {x, y, width, height} = element.getBoundingClientRect();
        return {x, y, width, height};
    }, existingScrollers);
    assert.ok(popup?.width > 0 && popup?.height > 0, "The open picker exposes visible native popup bounds");
    const count = await options.count();
    const rowHeight = (popup.height - 8) / count;
    let selectedCenter = null;
    const deadline = Date.now() + 10000;
    do {
        selectedCenter = paintedSelection(await screen(page, "mobile-picker-painted"), anchor, popup.height);
        if (selectedCenter === null) await page.waitForTimeout(100);
    } while (selectedCenter === null && Date.now() < deadline);
    assert.notEqual(selectedCenter, null, "The touch picker paints its selected row before native selection");
    const x = anchor.x + anchor.width / 2;
    const y = selectedCenter + (index - selectedIndex) * rowHeight;
    await page.touchscreen.tap(x, y);
    await page.waitForFunction(name => document.querySelector('[aria-label="' + name + '"]')?.getAttribute("aria-expanded") === "false", name);
    await page.getByRole("listbox", {name, exact: true}).waitFor({state: "hidden"});
}

async function open(browser, hash, phone = false) {
    const context = await browser.newContext({
        viewport: phone ? {width: 390, height: 844} : {width: 1200, height: 800},
        deviceScaleFactor: 1, isMobile: phone, hasTouch: phone
    });
    const page = await context.newPage();
    await page.addInitScript(() => {
        window.pixelExploreGpu = {depthDraws: 0};
        document.addEventListener("pointerdown", event => {
            if (event.target === document.getElementById("uno-canvas")) window.pixelExplorePointer = event.pointerId;
        }, {capture: true});
        const prototype = WebGL2RenderingContext.prototype;
        for (const name of ["drawElements", "drawElementsInstanced", "drawArrays", "drawArraysInstanced"]) {
            const original = prototype[name];
            prototype[name] = function (...args) {
                if (this.isEnabled(this.DEPTH_TEST)) window.pixelExploreGpu.depthDraws++;
                return Reflect.apply(original, this, args);
            };
        }
    });
    page.setDefaultTimeout(25000);
    page.on("pageerror", error => { failures.push(error.message); console.error(error.message); });
    page.on("console", message => {
        if (message.text().includes("Pixel Atlas") && /GPU|readback|input adapter/.test(message.text()))
            console.log(message.text());
        if (message.type() === "error" && /Unhandled (?:managed )?exception|RuntimeError:|System\.[\w.]*Exception/.test(message.text()))
            failures.push(message.text());
    });
    for (const host of ["tiles.openfreemap.org", "tiles.maps.eox.at", "s3.amazonaws.com/elevation-tiles-prod",
        "elevation-tiles-prod.s3.amazonaws.com", "routing.openstreetmap.de"])
        await page.route("https://" + host + "/**", route => route.abort());
    await page.route("https://raw.githubusercontent.com/notofonts/**", route => {
        failures.push("An exploration view requested an external fallback font");
        return route.abort();
    });
    const address = new URL(base);
    address.hash = hash;
    await page.goto(address.href, {waitUntil: "domcontentloaded", timeout: 120000});
    await page.waitForFunction(() => document.getElementById("uno-canvas")?.width > 0, undefined, {timeout: 100000});
    await page.waitForLoadState("networkidle", {timeout: 45000});
    await page.screenshot();
    const accessibility = page.getByRole("button", {name: "Enable accessibility", exact: true});
    if (await accessibility.count()) await accessibility.evaluate(element => element.click());
    await see(page, /GPU active/);
    await page.getByRole("button", {name: "Open free roam game", exact: true}).waitFor({state: "visible"});
    return {context, page};
}

async function screen(page, name) {
    const bytes = await page.screenshot({timeout: 20000});
    if (evidence) await writeFile(join(evidence, name + ".png"), bytes);
    return PNG.sync.read(bytes);
}

function imageDifference(before, after) {
    assert.equal(before.width, after.width);
    assert.equal(before.height, after.height);
    let count = 0, changed = 0;
    for (let y = Math.floor(before.height * .25); y < before.height * .7; y += 2) {
        for (let x = Math.floor(before.width * .36); x < before.width * .86; x += 2) {
            const offset = (y * before.width + x) * 4;
            if (before.data[offset] !== after.data[offset] || before.data[offset + 1] !== after.data[offset + 1] ||
                before.data[offset + 2] !== after.data[offset + 2]) changed++;
            count++;
        }
    }
    return changed / count;
}

function paintedSelection(frame, anchor, popupHeight) {
    const left = Math.ceil(anchor.x + 3), right = Math.floor(anchor.x + 12);
    const top = Math.max(0, Math.floor(anchor.y - popupHeight));
    const bottom = Math.min(frame.height, Math.ceil(anchor.y + popupHeight));
    let first = -1;
    for (let y = top; y < bottom; y++) {
        let selected = false;
        for (let x = left; x < right; x++) {
            const pixel = (y * frame.width + x) * 4;
            if (frame.data[pixel] <= 1 && Math.abs(frame.data[pixel + 1] - 90) <= 1 &&
                Math.abs(frame.data[pixel + 2] - 158) <= 1) selected = true;
        }
        if (selected) {
            if (first < 0) first = y;
        } else if (first >= 0) {
            if (y - first >= 12 && y - first <= 20) return (first + y) / 2;
            first = -1;
        }
    }
    return null;
}

function paintedStartButton(frame, viewport) {
    const left = Math.ceil(viewport.x + 8), right = Math.floor(viewport.x + viewport.width - 8);
    const top = Math.ceil(viewport.y), bottom = Math.floor(viewport.y + viewport.height);
    let band = -1;
    for (let y = top; y <= bottom; y++) {
        let green = 0, samples = 0;
        for (let x = left; x < right; x += 2) {
            const pixel = (y * frame.width + x) * 4;
            // The public Start button uses the opaque #245D4D action fill.
            if (Math.abs(frame.data[pixel] - 36) <= 1 && Math.abs(frame.data[pixel + 1] - 93) <= 1 &&
                Math.abs(frame.data[pixel + 2] - 77) <= 1) green++;
            samples++;
        }
        if (green > samples * .6) {
            if (band < 0) band = y;
        } else if (band >= 0) {
            // A 38px button paints at least 37 complete fill rows. Inspect its
            // final edge even when that edge coincides with the viewport boundary.
            if (y - band >= 37 && band > top && y <= bottom)
                return {x: viewport.x + viewport.width / 2, y: (band + y) / 2};
            band = -1;
        }
    }
    return null;
}

async function pageGeometry(page) {
    return page.evaluate(() => {
        const canvas = document.getElementById("uno-canvas").getBoundingClientRect();
        return {scale: visualViewport.scale, width: innerWidth, height: innerHeight, ratio: devicePixelRatio,
            canvas: [canvas.x, canvas.y, canvas.width, canvas.height]};
    });
}

async function readCamera(page) {
    const snapshot = await see(page, /rendering.+z[\d.]+/);
    const match = snapshot.match(/rendering\s*·\s*(-?[\d.]+)°,[ ]*(-?[\d.]+)°\s*·\s*z([\d.]+)\s*·\s*(-?[\d.]+)°/);
    assert.ok(match, "The public camera readout exposes location, zoom and bearing");
    return {latitude: Number(match[1]), longitude: Number(match[2]), zoom: Number(match[3]), bearing: Number(match[4])};
}

async function desktopInput(page) {
    const geometry = await pageGeometry(page);
    const initial = await readCamera(page);
    await page.mouse.move(760, 410);
    await page.keyboard.down("Control");
    await page.mouse.wheel(0, -90);
    await page.keyboard.up("Control");
    await see(page, snapshot => {
        const zoom = snapshot.match(/rendering.+?·\s*z([\d.]+)/);
        return zoom && Number(zoom[1]) > initial.zoom;
    });
    assert.deepEqual(await pageGeometry(page), geometry, "Trackpad pinch zooms the map while page and UI geometry stay fixed");
    const zoomed = await readCamera(page);
    await page.waitForTimeout(300);
    await page.mouse.wheel(70, 45);
    await page.waitForTimeout(400);
    const panned = await readCamera(page);
    assert.equal(panned.zoom, zoomed.zoom, "Diagonal continuous trackpad movement preserves zoom");
    assert.ok(panned.latitude !== zoomed.latitude || panned.longitude !== zoomed.longitude, "Diagonal trackpad input pans the map");
    await page.keyboard.down("Shift");
    await page.mouse.wheel(60, 20);
    await page.keyboard.up("Shift");
    await page.waitForTimeout(300);
    assert.notEqual((await readCamera(page)).bearing, panned.bearing, "Shift-trackpad orbits the map");
    assert.deepEqual(await pageGeometry(page), geometry, "Map gestures preserve browser zoom and the chrome layout");
    const beforeGesture = await readCamera(page);
    await page.evaluate(() => {
        const canvas = document.getElementById("uno-canvas");
        for (const [type, scale, rotation] of [["gesturestart", 1, 0], ["gesturechange", 2, 15], ["gestureend", 2, 15]]) {
            const gesture = new Event(type, {bubbles: true, cancelable: true});
            Object.defineProperties(gesture, {scale: {value: scale}, rotation: {value: rotation},
                clientX: {value: 760}, clientY: {value: 410}});
            canvas.dispatchEvent(gesture);
        }
    });
    await page.waitForTimeout(350);
    const afterGesture = await readCamera(page);
    assert.ok(Math.abs(afterGesture.zoom - beforeGesture.zoom - 1) < .11, "WebKit gesture translation applies its scale exactly once through Uno");
    assert.ok(Math.abs(Math.abs(afterGesture.bearing - beforeGesture.bearing) - 15) <= 1, "WebKit gesture translation preserves twist");
    assert.deepEqual(await pageGeometry(page), geometry, "Translated WebKit gestures preserve page geometry");
    await screen(page, "desktop-trackpad-camera");
}

async function renderingStyles(page) {
    const names = ["Pixel art rendering", "Normal rendering", "Minecraft rendering", "Roblox rendering"];
    const styles = ["Pixel", "Normal", "Minecraft", "Roblox"];
    const frames = [];
    for (let index = 0; index < styles.length; index++) {
        await menu(page, names[index]);
        await see(page, new RegExp("GPU active.+" + styles[index] + " rendering"));
        await page.waitForTimeout(1000);
        frames.push(await screen(page, "terrain-" + styles[index].toLowerCase()));
    }
    for (let first = 0; first < frames.length; first++)
        for (let second = first + 1; second < frames.length; second++)
            assert.ok(imageDifference(frames[first], frames[second]) > .02, styles[first] + " and " + styles[second] + " render distinct terrain geometry or material");
    await menu(page, names[0]);
    await see(page, /GPU active.+Pixel rendering/);
    await page.waitForTimeout(1000);
    const restored = await screen(page, "terrain-pixel-restored");
    assert.ok(imageDifference(frames[0], restored) < .01, "Returning to Pixel preserves its existing appearance");
    await setGpuRendering(page, false);
    await menu(page, names[1]);
    await see(page, /Normal rendering/);
    await screen(page, "terrain-normal-cpu");
    await setGpuRendering(page, true);
    await see(page, /GPU active/);
    await menu(page, names[0]);
}

async function defaultView(browser) {
    const {context, page} = await open(browser, "");
    await see(page, /GPU active.+Pixel rendering/);
    await screen(page, "default-pixel-gpu");
    await action(page, "Map appearance");
    for (const name of ["Pixel art rendering", "GPU rendering"])
        await page.getByRole("menuitem", {name, exact: true})
            .or(page.getByRole("menuitemcheckbox", {name, exact: true})).waitFor({state: "visible"});
    await screen(page, "default-appearance-selections");
    if (evidence) await writeFile(join(evidence, "default-appearance-selections.txt"), await page.locator("body").ariaSnapshot());
    await page.keyboard.press("Escape");
    await context.close();
    console.log("Default view opened with GPU-active Pixel rendering.");
}

async function telemetry(page) {
    const snapshot = await see(page, /Free roam telemetry/);
    const match = snapshot.match(/Free roam telemetry[^\n]*?([\d.]+) km\/h · (-?[\d.]+) m elevation · (-?[\d.]+) m clearance\s+(-?[\d.]+)°, (-?[\d.]+)° · ([\d.]+) m travelled/);
    assert.ok(match, "The game exposes physical telemetry through its visible HUD");
    return {speed: Number(match[1]), altitude: Number(match[2]), clearance: Number(match[3]),
        latitude: Number(match[4]), longitude: Number(match[5]), distance: Number(match[6])};
}

async function holdKey(page, key, milliseconds) {
    await page.keyboard.down(key);
    await page.waitForTimeout(milliseconds);
    await page.keyboard.up(key);
    await page.waitForTimeout(200);
}

async function waitTelemetry(page, predicate, message, timeout = 10000) {
    const deadline = Date.now() + timeout;
    let state;
    do {
        state = await telemetry(page);
        if (predicate(state)) return state;
        await page.waitForTimeout(100);
    } while (Date.now() < deadline);
    throw new Error(message + " " + JSON.stringify(state));
}

async function expandSettings(page, touch = false) {
    const expand = page.getByRole("button", {name: "Expand game settings", exact: true});
    if (await expand.count()) await action(page, "Expand game settings", touch);
}

async function assertSettledGpu(page) {
    let count = await page.evaluate(() => window.pixelExploreGpu.depthDraws);
    let stableSince = Date.now();
    const deadline = stableSince + 10000;
    while (Date.now() < deadline) {
        await page.waitForTimeout(200);
        const next = await page.evaluate(() => window.pixelExploreGpu.depthDraws);
        if (next !== count) { count = next; stableSince = Date.now(); }
        if (Date.now() - stableSince >= 1000) return;
    }
    throw new Error("Paused terrain and camera did not settle into retained GPU artwork");
}

async function pausedRendering(page, paused) {
    await expandSettings(page);
    for (const [index, style] of [[2, "minecraft"], [3, "roblox"], [1, "normal"], [0, "pixel"]]) {
        await choose(page, "Game rendering style", index);
        await see(page, new RegExp("GPU active.+" + style + " rendering", "i"));
        await assertSettledGpu(page);
        assert.deepEqual(await telemetry(page), paused, "A paused " + style + " style change preserves the physical pose");
        await screen(page, "game-paused-" + style);
    }
    for (const relief of [false, true]) {
        await action(page, "Game terrain relief");
        await assertSettledGpu(page);
        assert.deepEqual(await telemetry(page), paused, "Changing relief retains paused terrain physics");
        const state = await page.getByRole("switch", {name: "Game terrain relief", exact: true}).getAttribute("aria-checked");
        assert.equal(state, String(relief), "The visual relief switch updates independently of physics");
    }
    await action(page, "Collapse game settings");
    console.log("Paused style and relief changes retain the pose and settle GPU rendering.");
}

async function startMode(page, index, title, touch = false) {
    await expandSettings(page, touch);
    await choose(page, "Free roam vehicle", index, touch);
    await action(page, "Start free roam at map center", touch);
    const resume = page.getByRole("button", {name: "Resume free roam", exact: true});
    if (await resume.count()) await action(page, "Resume free roam", touch);
    await see(page, new RegExp(title));
    return telemetry(page);
}

async function freeRoam(page) {
    await tap(page, page.getByRole("button", {name: "Open free roam game", exact: true}));
    await action(page, "Start free roam at map center");
    await see(page, /Walking/);
    const walkedFrom = await telemetry(page);
    await page.evaluate(() => {
        const element = document.querySelector('[aria-label^="Free roam telemetry"]');
        const updates = {count: 0, intermediateNames: []};
        const observer = new MutationObserver(records => {
            for (const record of records) {
                updates.count++;
                if (!record.oldValue?.startsWith("Free roam telemetry")) updates.intermediateNames.push(record.oldValue);
            }
        });
        observer.observe(element, {attributes: true, attributeFilter: ["aria-label"], attributeOldValue: true});
        window.finishTelemetryObservation = () => { observer.disconnect(); return updates; };
    });
    await page.keyboard.down("w");
    try {
        await waitTelemetry(page, state => state.distance > walkedFrom.distance, "Walking advances without a planned route");
    } finally { await page.keyboard.up("w"); }
    const telemetryUpdates = await page.evaluate(() => window.finishTelemetryObservation());
    assert.ok(telemetryUpdates.count > 0, "Movement publishes fresh accessible telemetry");
    assert.deepEqual(telemetryUpdates.intermediateNames, [], "Each HUD update publishes its complete telemetry name once");
    await page.keyboard.down("Space");
    await waitTelemetry(page, state => state.clearance > .1, "Walking jumps above the elevation surface");
    await waitTelemetry(page, state => state.clearance < .1, "Gravity returns the walker to terrain");
    await page.waitForTimeout(800);
    assert.ok((await telemetry(page)).clearance < .1, "Holding jump through landing does not start another jump");
    await page.keyboard.up("Space");
    await screen(page, "game-walking-first-person");
    console.log("Walking, terrain contact and held-jump behavior passed.");

    const beforeDrag = await readCamera(page);
    await page.mouse.move(800, 400);
    await page.mouse.down({button: "right"});
    await page.waitForFunction(() => document.getElementById("uno-canvas")?.hasPointerCapture(window.pixelExplorePointer));
    await page.mouse.move(850, 420, {steps: 5});
    await page.mouse.up({button: "right"});
    await page.waitForTimeout(250);
    assert.notEqual((await readCamera(page)).bearing, beforeDrag.bearing, "Native mouse dragging rotates the first-person view");
    assert.equal(await page.evaluate(() => document.pointerLockElement), null, "Right dragging keeps ordinary pointer input");

    // Acquire the actual browser lock through a real click. CDP absolute mouse
    // movement under headless Pointer Lock produces opposing cursor-warp deltas,
    // so explicit standard relative MouseEvents exercise the live JS→C# callback.
    await page.mouse.click(800, 400);
    await page.waitForFunction(() => document.pointerLockElement !== null);
    await page.waitForTimeout(300);
    const beforeLook = await readCamera(page);
    await page.evaluate(() => {
        const canvas = document.getElementById("uno-canvas");
        for (let index = 0; index < 2; index++) canvas.dispatchEvent(new MouseEvent("mousemove", {
            bubbles: true, cancelable: true, clientX: 800, clientY: 400, movementX: 30, movementY: -15
        }));
    });
    let relativeTurn = 0;
    const lookDeadline = Date.now() + 10000;
    do {
        await page.waitForTimeout(100);
        relativeTurn = ((await readCamera(page)).bearing - beforeLook.bearing + 540) % 360 - 180;
    } while (Math.abs(relativeTurn - 10.8) > 1 && Date.now() < lookDeadline);
    assert.ok(Math.abs(relativeTurn - 10.8) <= 1,
        "Coalesced relative mouse input reaches the first-person camera exactly once, observed " + relativeTurn + " degrees");
    await page.keyboard.press("Escape");
    await page.waitForFunction(() => document.pointerLockElement === null);
    await see(page, /Resume free roam/);
    const paused = await telemetry(page);
    await holdKey(page, "w", 300);
    assert.equal((await telemetry(page)).distance, paused.distance, "Paused free roam releases movement input");
    const pausedCamera = await readCamera(page);
    for (const key of ["ArrowLeft", "ArrowUp", "ArrowRight", "ArrowDown", "q", "e", "PageUp", "PageDown", "Escape"])
        await page.keyboard.press(key);
    await page.waitForTimeout(400);
    await see(page, /Resume free roam/);
    assert.deepEqual(await readCamera(page), pausedCamera,
        "Paused movement keys retain game ownership and Escape keeps the session paused");
    await pausedRendering(page, paused);
    console.log("Native drag, browser Pointer Lock, relative look and pause passed.");
    await action(page, "Resume free roam");
    await page.keyboard.press("Home");
    await see(page, /rendering.+?·\s*z16\.5\s*·/);
    await page.waitForTimeout(500);
    assert.equal((await readCamera(page)).zoom, 16.5,
        "A manual map flight detaches free-roam follow and remains at its destination");
    await page.mouse.move(800, 400);
    const detached = await readCamera(page);
    await page.mouse.wheel(70, 45);
    await page.waitForTimeout(350);
    const detachedPan = await readCamera(page);
    assert.equal(detachedPan.zoom, detached.zoom, "A detached game's trackpad pans the map without restoring follow");
    assert.ok(detachedPan.latitude !== detached.latitude || detachedPan.longitude !== detached.longitude,
        "Detached map panning changes geographic position");
    await page.keyboard.down("Control");
    await page.mouse.wheel(0, -50);
    await page.keyboard.up("Control");
    await see(page, /rendering.+?·\s*z17\.0\s*·/);
    await page.waitForTimeout(350);
    assert.equal((await readCamera(page)).zoom, 17,
        "A detached game's pinch changes map zoom without returning to the physical rig");

    const carFrom = await startMode(page, 1, "Driving");
    await page.keyboard.down("w");
    try {
        await waitTelemetry(page, state => state.distance > carFrom.distance, "The car drives freely through the streamed world");
    } finally { await page.keyboard.up("w"); }
    await screen(page, "game-car-chase");
    await expandSettings(page);
    for (const camera of [1, 2, 3, 4, 0]) {
        await choose(page, "Free roam camera", camera);
        if (camera !== 4) continue;
        const northUp = page.getByRole("combobox", {name: "Free roam camera", exact: true});
        const selected = await northUp.ariaSnapshot();
        await page.evaluate(() => {
            for (const [type, scale] of [["gesturestart", 1], ["gesturechange", 1.5], ["gestureend", 1.5]]) {
                const gesture = new Event(type, {bubbles: true, cancelable: true});
                Object.defineProperties(gesture, {scale: {value: scale}, rotation: {value: 0},
                    clientX: {value: 900}, clientY: {value: 420}});
                document.getElementById("uno-canvas").dispatchEvent(gesture);
            }
        });
        await page.waitForTimeout(250);
        assert.equal(await northUp.ariaSnapshot(), selected,
            "A pure Safari pinch retains the north-up camera instead of switching to orbit");
        assert.equal((await readCamera(page)).bearing, 0, "North-up pinch preserves its compass orientation");
    }
    await action(page, "Collapse game settings");
    console.log("Car driving and all five camera rigs passed.");

    await startMode(page, 3, "Sailing");
    await see(page, /waiting for navigable water/);
    const dryBoat = await telemetry(page);
    await holdKey(page, "w", 300);
    assert.equal((await telemetry(page)).distance, dryBoat.distance, "A boat waits for water when selected on dry land");

    // Isolate flight from the car's eventual position beneath real overhangs.
    // This seeded harbour point is mapped water, 770m from the nearest building.
    await action(page, "Exit free roam");
    const search = page.getByRole("textbox", {name: "Search places or latitude and longitude", exact: true});
    await tap(page, search);
    await page.keyboard.press("Control+A");
    await page.keyboard.type("40.695, -74.0285");
    await page.keyboard.press("Enter");
    await see(page, /rendering\s*·\s*40\.6950°,\s*-74\.0285°/);
    await tap(page, page.getByRole("button", {name: "Open free roam game", exact: true}));
    const launch = await startMode(page, 6, "Free flight");
    await page.keyboard.down("Shift");
    await page.keyboard.down("PageUp");
    try {
        await waitTelemetry(page, state => state.altitude > launch.altitude + 250 && state.clearance > 650,
            "Boosted free flight gains altitude above the seeded buildings", 15000);
    } finally {
        await page.keyboard.up("PageUp");
        await page.keyboard.up("Shift");
    }
    const flyingFrom = await startMode(page, 4, "Flying");
    await page.keyboard.down("w");
    await page.keyboard.down("PageUp");
    try {
        await waitTelemetry(page, state => state.altitude > flyingFrom.altitude + .5, "The aircraft climbs over real terrain");
    } finally {
        await page.keyboard.up("PageUp");
        await page.keyboard.up("w");
    }
    const flyingTo = await telemetry(page);
    assert.ok(flyingTo.altitude > flyingFrom.altitude, "The aircraft climbs over real terrain");
    await screen(page, "game-aircraft");

    const freeFrom = await startMode(page, 6, "Free flight");
    // The moving aircraft can reach another block while its mode picker is open.
    // Seeded towers reach 541m, so establish clear air with real vertical input
    // before checking unrestricted horizontal flight through this physical world.
    await page.keyboard.down("Shift");
    await page.keyboard.down("PageUp");
    try {
        await waitTelemetry(page, state => state.altitude > freeFrom.altitude + .5 && state.clearance > 650,
            "Independent free flight moves vertically above the seeded buildings", 15000);
    } finally {
        await page.keyboard.up("PageUp");
        await page.keyboard.up("Shift");
    }
    await page.keyboard.down("w");
    try {
        await waitTelemetry(page, state => state.distance > freeFrom.distance &&
            (state.latitude !== freeFrom.latitude || state.longitude !== freeFrom.longitude),
            "Independent free flight moves horizontally");
    } finally { await page.keyboard.up("w"); }
    await action(page, "Respawn free roam vehicle");
    assert.ok((await telemetry(page)).distance < 1, "Respawn resets free-roam progress");
    await screen(page, "game-free-flight");
    const returnedFrom = await telemetry(page);
    await action(page, "Exit free roam");
    await page.getByRole("textbox", {name: "Search places or latitude and longitude", exact: true}).waitFor({state: "visible"});
    await page.getByRole("button", {name: "Switch to 2D overhead", exact: true}).waitFor({state: "visible"});
    const returnedCamera = await readCamera(page);
    assert.ok(Math.abs(returnedCamera.latitude - returnedFrom.latitude) < .00015 &&
        Math.abs(returnedCamera.longitude - returnedFrom.longitude) < .00015,
        "Leaving Play refreshes the map readout at the current physical position");
    await action(page, "Open route planner");
    await page.getByRole("button", {name: "Calculate route", exact: true}).waitFor({state: "visible"});
    await action(page, "Close journeys");
}

async function cameraOwnershipInput(cdp, page) {
    const viewport = page.viewportSize();
    const points = (spread, rotation = 0) => [
        {id: 1, x: 800 - spread, y: 400 - rotation},
        {id: 2, x: 800 + spread, y: 400 + rotation}
    ];
    // The compact phone layout hides the public camera readout. Exercise this
    // touchscreen-and-keyboard sequence at desktop width, then restore portrait.
    await page.setViewportSize({width: 1200, height: 800});
    try {
        await page.waitForTimeout(500);
        await see(page, /rendering.+?·\s*z24\.0\s*·/);
        await cdp.send("Input.dispatchTouchEvent", {type: "touchStart", touchPoints: points(40)});
        try {
            // A hardware Home key can replace the camera while touchscreen contacts
            // remain down. Their old free-look baseline must stop owning the view.
            await page.keyboard.press("Home");
            await see(page, /rendering.+?·\s*z16\.5\s*·/);
            await page.waitForTimeout(500);
            const home = await readCamera(page);
            await cdp.send("Input.dispatchTouchEvent", {type: "touchMove", touchPoints: points(70, 15)});
            await page.waitForTimeout(350);
            assert.deepEqual(await readCamera(page), home,
                "Contacts held across Home cannot restore their previous camera baseline");
        } finally { await cdp.send("Input.dispatchTouchEvent", {type: "touchEnd", touchPoints: []}); }

        await page.keyboard.press("c");
        await see(page, /rendering.+?·\s*z24\.0\s*·/);
        assert.equal((await readCamera(page)).zoom, 24,
            "The game camera shortcut restores follow after a manual Home flight");

        for (const kind of ["pan", "pinch"]) {
            await page.keyboard.press("Home");
            await see(page, /rendering.+?·\s*z16\.5\s*·/);
            await page.waitForTimeout(500);
            await cdp.send("Input.dispatchTouchEvent", {type: "touchStart", touchPoints: points(40)});
            try {
                await cdp.send("Input.dispatchTouchEvent", {type: "touchMove", touchPoints: points(65, 12)});
                await page.waitForTimeout(200);
                const touched = await readCamera(page);
                await page.mouse.move(800, 400);
                if (kind === "pinch") await page.keyboard.down("Control");
                try {
                    await page.mouse.wheel(kind === "pan" ? 70 : 0, kind === "pan" ? 45 : -90);
                } finally {
                    if (kind === "pinch") await page.keyboard.up("Control");
                }
                await page.waitForTimeout(300);
                const immediate = await readCamera(page);
                assert.notDeepEqual(immediate, touched, `Trackpad ${kind} takes over from held touch contacts`);
                await cdp.send("Input.dispatchTouchEvent", {type: "touchMove", touchPoints: points(85, 28)});
                await page.waitForTimeout(250);
                assert.deepEqual(await readCamera(page), immediate,
                    `Contacts held across immediate trackpad ${kind} cannot restore their old pinch baseline`);
            } finally { await cdp.send("Input.dispatchTouchEvent", {type: "touchEnd", touchPoints: []}); }
        }

        await page.keyboard.press("Home");
        await see(page, /rendering.+?·\s*z16\.5\s*·/);
        await page.waitForTimeout(500);
        await cdp.send("Input.dispatchTouchEvent", {type: "touchStart", touchPoints: points(40)});
        try {
            await cdp.send("Input.dispatchTouchEvent", {type: "touchMove", touchPoints: points(65, 12)});
            await page.waitForTimeout(200);
            assert.ok((await readCamera(page)).zoom > 16.5,
                "Fresh contacts can pinch the detached map after cancellation");
            await page.keyboard.press("c");
            await see(page, /rendering.+?·\s*z24\.0\s*·/);
            await page.waitForTimeout(350);
            const followed = await readCamera(page);
            await cdp.send("Input.dispatchTouchEvent", {type: "touchMove", touchPoints: points(85, 28)});
            await page.waitForTimeout(350);
            assert.deepEqual(await readCamera(page), followed,
                "Reattaching the game camera cancels an in-progress detached pinch and twist");
        } finally { await cdp.send("Input.dispatchTouchEvent", {type: "touchEnd", touchPoints: []}); }
    } finally {
        await page.setViewportSize(viewport);
        await page.waitForTimeout(500);
    }
    console.log("Held-touch flights, mixed trackpad takeover, camera reattachment and pinch cancellation passed.");
}

async function mobileInput(context, page) {
    const cdp = await context.newCDPSession(page);
    const geometry = await pageGeometry(page);
    const before = await screen(page, "mobile-map-before-pinch");
    const points = (spread, rotation = 0) => [
        {id: 1, x: 195 - spread, y: 410 - rotation},
        {id: 2, x: 195 + spread, y: 410 + rotation}
    ];
    await cdp.send("Input.dispatchTouchEvent", {type: "touchStart", touchPoints: points(40)});
    for (let step = 1; step <= 8; step++) {
        await cdp.send("Input.dispatchTouchEvent", {type: "touchMove", touchPoints: points(40 + step * 6, step * 3)});
        await page.waitForTimeout(30);
    }
    await cdp.send("Input.dispatchTouchEvent", {type: "touchEnd", touchPoints: []});
    await page.waitForTimeout(600);
    assert.deepEqual(await pageGeometry(page), geometry, "Two-finger mobile input leaves the page and UI at their original scale");
    assert.ok(imageDifference(before, await screen(page, "mobile-map-pinch-orbit")) > .1, "Touch pinch and twist transform the rendered map");
    await action(page, "Open free roam game", true);
    const start = await startMode(page, 6, "Free flight", true);
    await page.waitForTimeout(200);
    await cameraOwnershipInput(cdp, page);
    const climb = await page.getByRole("button", {name: "Free roam climb", exact: true}).boundingBox();
    assert.ok(climb?.width > 0 && climb?.height > 0, "The mobile climb button is visible");
    await cdp.send("Input.dispatchTouchEvent", {type: "touchStart", touchPoints: [
        {id: 1, x: climb.x + climb.width / 2, y: climb.y + climb.height / 2}
    ]});
    try {
        await waitTelemetry(page, state => state.altitude > start.altitude + .5, "The first mobile button hold survives map focus loss");
    } finally { await cdp.send("Input.dispatchTouchEvent", {type: "touchEnd", touchPoints: []}); }
    await waitTelemetry(page, state => state.speed === 0, "Free flight stops after releasing climb");
    const move = await page.getByLabel("Free roam movement stick", {exact: true}).boundingBox();
    const look = await page.getByLabel("Free roam look stick", {exact: true}).boundingBox();
    assert.ok(move?.width > 0 && look?.width > 0, "Both independent mobile sticks are visible");
    assert.ok(move.x >= 0 && look.x > move.x + move.width && look.x + look.width <= 390,
        "Portrait stick semantics match their separate visible columns");
    assert.ok(move.y > 500 && look.y > 500, "Portrait sticks follow the bottom of the map viewport");
    const beforeLook = await screen(page, "mobile-game-before-look");
    const lookFrom = await telemetry(page);
    await cdp.send("Input.dispatchTouchEvent", {type: "touchStart", touchPoints: [
        {id: 1, x: look.x + look.width / 2 + 30, y: look.y + look.height / 2}
    ]});
    let looked = false;
    const lookDeadline = Date.now() + 10000;
    try {
        do {
            await page.waitForTimeout(100);
            looked = imageDifference(beforeLook, await screen(page, "mobile-game-after-look")) > .05;
        } while (!looked && Date.now() < lookDeadline);
    } finally { await cdp.send("Input.dispatchTouchEvent", {type: "touchEnd", touchPoints: []}); }
    const lookTo = await telemetry(page);
    assert.equal(lookTo.latitude, lookFrom.latitude, "The look stick preserves physical latitude");
    assert.equal(lookTo.longitude, lookFrom.longitude, "The look stick preserves physical longitude");
    assert.equal(lookTo.altitude, lookFrom.altitude, "The look stick preserves physical altitude");
    assert.ok(looked, "A native look-only hold rotates map artwork independently of movement");
    const stickPoints = [
        {id: 1, x: move.x + move.width / 2, y: move.y + move.height / 2 - 33},
        {id: 2, x: look.x + look.width / 2 + 30, y: look.y + look.height / 2 - 8}
    ];
    await cdp.send("Input.dispatchTouchEvent", {type: "touchStart", touchPoints: stickPoints});
    try {
        await waitTelemetry(page, state => state.distance > lookTo.distance, "Native dual sticks move the free camera");
    } finally { await cdp.send("Input.dispatchTouchEvent", {type: "touchEnd", touchPoints: []}); }
    const moved = await telemetry(page);
    assert.ok(moved.distance > start.distance, "Touch movement and look run simultaneously");
    assert.ok(moved.latitude !== start.latitude || moved.longitude !== start.longitude, "Touch movement updates geographic position");
    await screen(page, "mobile-dual-stick-flight");
    await action(page, "Pause free roam", true);
    const paused = await telemetry(page);
    await page.waitForTimeout(350);
    assert.deepEqual(await telemetry(page), paused, "Pausing releases both touch sticks");
    await page.setViewportSize({width: 844, height: 390});
    await page.waitForTimeout(500);
    await screen(page, "mobile-game-landscape");
    const landscapeMove = await page.getByLabel("Free roam movement stick", {exact: true}).boundingBox();
    const landscapeLook = await page.getByLabel("Free roam look stick", {exact: true}).boundingBox();
    const settings = await page.getByRole("group", {name: "Free roam settings", exact: true}).boundingBox();
    const playback = await page.getByRole("group", {name: "Free roam playback controls", exact: true}).boundingBox();
    const actions = await page.getByRole("group", {name: "Free roam action controls", exact: true}).boundingBox();
    assert.ok(settings.y + settings.height <= landscapeMove.y && settings.x + settings.width < landscapeLook.x,
        "The compact landscape HUD sits above the movement stick and clear of the look stick");
    const map = await page.getByRole("group", {name: "Interactive world map", exact: true}).boundingBox();
    assert.ok(settings.x + settings.width < map.x + map.width / 2,
        "The compact landscape HUD leaves the central gameplay aim visible");
    assert.ok(actions.y >= playback.y + playback.height && actions.height < 50,
        "Landscape flight actions occupy one unobstructed bottom row");
    await action(page, "Expand game settings", true);
    await page.waitForFunction(() => document.querySelector('[aria-label="Free roam settings content"]')?.getBoundingClientRect().height > 140);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const scroller = page.locator('[elementtype="ScrollContentPresenter"]:visible');
    assert.equal(await scroller.count(), 1, "The expanded game exposes one native settings scroll surface");
    const scrollBounds = await page.getByRole("region", {name: "Free roam settings content", exact: true}).boundingBox();
    let paintedStart = null;
    const initialScroll = await scroller.evaluate(element => element.scrollTop);
    const scrollX = scrollBounds.x + scrollBounds.width - 18;
    let scrollY = scrollBounds.y + scrollBounds.height * .65;
    await cdp.send("Input.dispatchTouchEvent", {type: "touchStart", touchPoints: [{id: 3, x: scrollX, y: scrollY}]});
    try {
        // Keep one native pan captured. The portal's content extent changes as
        // Uno translates it, so fine motion near its current limit avoids a
        // sequence of overshoots that leaves the last button rows clipped.
        for (let step = 0; step < 64 && scrollY > 12 && !paintedStart; step++) {
            const remaining = await scroller.evaluate(element => element.scrollHeight - element.clientHeight - element.scrollTop);
            scrollY = Math.max(12, scrollY - (remaining < 20 ? 1 : 8));
            await cdp.send("Input.dispatchTouchEvent", {type: "touchMove", touchPoints: [{id: 3, x: scrollX, y: scrollY}]});
            await page.waitForTimeout(50);
            if (remaining < 20 || step % 8 === 0)
                paintedStart = paintedStartButton(await screen(page, "mobile-landscape-settings-scrolled"), scrollBounds);
        }
        await page.waitForTimeout(150);
    } finally { await cdp.send("Input.dispatchTouchEvent", {type: "touchEnd", touchPoints: []}); }
    await page.waitForTimeout(100);
    paintedStart = paintedStartButton(await screen(page, "mobile-landscape-settings-scrolled"), scrollBounds);
    const finalScroll = await scroller.evaluate(element => element.scrollTop);
    assert.ok(finalScroll > initialScroll + 20, "The native landscape pan advances the actual settings scroll offset");
    assert.ok(paintedStart, "A fresh canvas frame paints the complete Start button inside the settings viewport");
    console.log("Verified native landscape scroll", {from: initialScroll, to: finalScroll});
    await page.touchscreen.tap(paintedStart.x, paintedStart.y);
    await page.getByRole("button", {name: "Expand game settings", exact: true}).waitFor({state: "visible"});
    await waitTelemetry(page, state => state.distance === 0, "Native activation of the painted landscape Start button restarts free roam");
    await screen(page, "mobile-landscape-restarted");
    await action(page, "Resume free roam", true);
    const exitViewport = page.viewportSize();
    await page.setViewportSize({width: 1200, height: 800});
    try {
        await page.waitForTimeout(500);
        const held = [{id: 1, x: 760, y: 400}, {id: 2, x: 840, y: 400}];
        await cdp.send("Input.dispatchTouchEvent", {type: "touchStart", touchPoints: held});
        try {
            await action(page, "Exit free roam");
            await page.getByRole("textbox", {name: "Search places or latitude and longitude", exact: true}).waitFor({state: "visible"});
            await page.waitForTimeout(400);
            const returned = await readCamera(page);
            await cdp.send("Input.dispatchTouchEvent", {type: "touchMove", touchPoints: [
                {id: 1, x: 730, y: 380}, {id: 2, x: 870, y: 420}
            ]});
            await page.waitForTimeout(300);
            assert.deepEqual(await readCamera(page), returned,
                "Exiting free roam cancels held contacts before restoring the map camera");
        } finally { await cdp.send("Input.dispatchTouchEvent", {type: "touchEnd", touchPoints: []}); }
    } finally { await page.setViewportSize(exitViewport); }
    await cdp.detach();
}

async function run() {
    if (evidence) await mkdir(evidence, {recursive: true});
    const browser = await chromium.launch({
        executablePath: process.env.CHROMIUM_PATH || undefined,
        args: ["--disable-dev-shm-usage", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"]
    });
    try {
        await defaultView(browser);
        const terrain = await open(browser, mountainView);
        await see(terrain.page, /GPU active.+Pixel rendering/);
        await renderingStyles(terrain.page);
        console.log("All four rendering styles and CPU/GPU switching passed.");
        await desktopInput(terrain.page);
        console.log("Desktop trackpad and WebKit gesture routing passed.");
        await terrain.context.close();
        const city = await open(browser, cityView);
        await freeRoam(city.page);
        console.log("Walking, driving, aircraft, free flight and camera controls passed.");
        await city.context.close();
        const phone = await open(browser, cityView, true);
        await mobileInput(phone.context, phone.page);
        await phone.context.close();
        assert.deepEqual(failures, [], "Exploration has no managed or browser runtime errors");
        console.log("Exploration checks passed for GPU defaults, four styles, native gestures, free movement, physical cameras, terrain and mobile controls.");
    } catch (error) {
        const page = browser.contexts().flatMap(context => context.pages()).findLast(page => !page.isClosed());
        if (evidence && page) {
            await Promise.allSettled([
                page.screenshot({path: join(evidence, "explore-failure.png"), timeout: 10000}),
                page.locator("body").ariaSnapshot({timeout: 10000})
                    .then(snapshot => writeFile(join(evidence, "explore-failure.txt"), snapshot))
            ]);
        }
        throw error;
    } finally { await browser.close(); }
}

module.exports = {open, action, choose, see, telemetry, waitForSettledFocus, failures};

if (require.main === module)
    run().catch(error => { console.error(error); process.exitCode = 1; });
