const assert = require("node:assert/strict");
const {mkdir, writeFile} = require("node:fs/promises");
const {join} = require("node:path");
const {chromium} = require("playwright");
const {PNG} = require("pngjs");
const {setGpuRendering} = require("./gpu.cjs");

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

async function tap(page, locator, touch = false) {
    await locator.waitFor({state: "visible"});
    await page.waitForTimeout(150);
    const bounds = await locator.boundingBox();
    const viewport = page.viewportSize();
    assert.ok(bounds?.width > 0 && bounds?.height > 0, "The control has visible bounds");
    const left = Math.max(0, bounds.x), right = Math.min(viewport.width, bounds.x + bounds.width);
    const top = Math.max(0, bounds.y), bottom = Math.min(viewport.height, bounds.y + bounds.height);
    assert.ok(right > left && bottom > top, "The control intersects the viewport");
    if (touch) await page.touchscreen.tap((left + right) / 2, (top + bottom) / 2);
    else await page.mouse.click((left + right) / 2, (top + bottom) / 2);
}

async function action(page, name, touch = false) {
    const target = page.getByRole("button", {name, exact: true})
        .or(page.getByRole("checkbox", {name, exact: true}))
        .or(page.getByRole("switch", {name, exact: true}));
    if (touch) await tap(page, target, true);
    else {
        await target.waitFor({state: "visible"});
        await target.focus();
        await page.keyboard.press(await target.getAttribute("role") === "switch" ? "Space" : "Enter");
    }
}

async function menu(page, name) {
    await action(page, "Map appearance");
    const item = page.getByRole("menuitem", {name, exact: true})
        .or(page.getByRole("menuitemcheckbox", {name, exact: true}));
    await item.waitFor({state: "visible"});
    await item.focus();
    await page.keyboard.press("Enter");
    await item.waitFor({state: "hidden"});
    await page.mouse.move(page.viewportSize().width / 2, 15);
}

async function choose(page, name, index, touch = false) {
    const selector = page.getByRole("combobox", {name, exact: true});
    if (touch) await tap(page, selector, true);
    else await selector.focus();
    await page.waitForTimeout(150);
    if (await selector.getAttribute("aria-expanded") !== "true") await page.keyboard.press("Alt+ArrowDown");
    await page.waitForFunction(name => document.querySelector('[aria-label="' + name + '"]')?.getAttribute("aria-expanded") === "true", name);
    await page.keyboard.press("Home");
    for (let item = 0; item < index; item++) await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    await page.waitForFunction(name => document.querySelector('[aria-label="' + name + '"]')?.getAttribute("aria-expanded") === "false", name);
    await page.getByRole("listbox").waitFor({state: "hidden"});
}

async function open(browser, hash, phone = false) {
    const context = await browser.newContext({
        viewport: phone ? {width: 390, height: 844} : {width: 1200, height: 800},
        deviceScaleFactor: 1, isMobile: phone, hasTouch: phone
    });
    const page = await context.newPage();
    await page.addInitScript(() => {
        window.pixelExploreGpu = {depthDraws: 0};
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
    await action(page, "Open free roam game");
    const walkedFrom = await startMode(page, 0, "Walking");
    await page.keyboard.down("w");
    try {
        await waitTelemetry(page, state => state.distance > walkedFrom.distance, "Walking advances without a planned route");
    } finally { await page.keyboard.up("w"); }
    await page.keyboard.down("Space");
    await waitTelemetry(page, state => state.clearance > .1, "Walking jumps above the elevation surface");
    await waitTelemetry(page, state => state.clearance < .1, "Gravity returns the walker to terrain");
    await page.waitForTimeout(800);
    assert.ok((await telemetry(page)).clearance < .1, "Holding jump through landing does not start another jump");
    await page.keyboard.up("Space");
    await screen(page, "game-walking-first-person");

    // A real canvas click requests browser pointer lock. Escape must both release
    // relative mouse input and pause the simulation before any GUI action resumes it.
    await page.mouse.click(800, 400);
    await page.waitForFunction(() => document.pointerLockElement !== null);
    const beforeLook = await readCamera(page);
    await page.mouse.move(940, 450, {steps: 5});
    await page.waitForTimeout(250);
    assert.notEqual((await readCamera(page)).bearing, beforeLook.bearing, "Relative mouse input rotates the first-person view");
    await page.keyboard.press("Escape");
    await page.waitForFunction(() => document.pointerLockElement === null);
    await see(page, /Resume free roam/);
    const paused = await telemetry(page);
    await holdKey(page, "w", 300);
    assert.equal((await telemetry(page)).distance, paused.distance, "Paused free roam releases movement input");
    await pausedRendering(page, paused);
    await action(page, "Resume free roam");

    const carFrom = await startMode(page, 1, "Driving");
    await page.keyboard.down("w");
    try {
        await waitTelemetry(page, state => state.distance > carFrom.distance, "The car drives freely through the streamed world");
    } finally { await page.keyboard.up("w"); }
    await screen(page, "game-car-chase");
    await expandSettings(page);
    for (const camera of [1, 2, 3, 4, 0]) await choose(page, "Free roam camera", camera);
    await action(page, "Collapse game settings");

    await startMode(page, 6, "Free flight");
    await page.keyboard.down("Shift");
    await holdKey(page, "PageUp", 3500);
    await page.keyboard.up("Shift");
    const flyingFrom = await startMode(page, 4, "Flying");
    await page.keyboard.down("w");
    await holdKey(page, "PageUp", 1600);
    await page.keyboard.up("w");
    const flyingTo = await telemetry(page);
    assert.ok(flyingTo.altitude > flyingFrom.altitude, "The aircraft climbs over real terrain");
    await screen(page, "game-aircraft");

    const freeFrom = await startMode(page, 6, "Free flight");
    await holdKey(page, "PageUp", 800);
    assert.ok((await telemetry(page)).altitude > freeFrom.altitude, "Independent free flight moves vertically");
    await holdKey(page, "w", 900);
    assert.ok((await telemetry(page)).distance > freeFrom.distance, "Independent free flight moves horizontally");
    await action(page, "Respawn free roam vehicle");
    assert.ok((await telemetry(page)).distance < 1, "Respawn resets free-roam progress");
    await screen(page, "game-free-flight");
    await action(page, "Exit free roam");
    await page.getByRole("textbox", {name: "Search places or latitude and longitude", exact: true}).waitFor({state: "visible"});
    await action(page, "Open route planner");
    await page.getByRole("button", {name: "Calculate route", exact: true}).waitFor({state: "visible"});
    await action(page, "Close journeys");
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
    const move = await page.getByLabel("Free roam movement stick", {exact: true}).boundingBox();
    const look = await page.getByLabel("Free roam look stick", {exact: true}).boundingBox();
    assert.ok(move?.width > 0 && look?.width > 0, "Both independent mobile sticks are visible");
    const stickPoints = [
        {id: 1, x: move.x + move.width / 2, y: move.y + move.height / 2 - 33},
        {id: 2, x: look.x + look.width / 2 + 30, y: look.y + look.height / 2 - 8}
    ];
    await cdp.send("Input.dispatchTouchEvent", {type: "touchStart", touchPoints: stickPoints});
    await page.waitForTimeout(1400);
    await cdp.send("Input.dispatchTouchEvent", {type: "touchEnd", touchPoints: []});
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
    await action(page, "Resume free roam", true);
    await action(page, "Exit free roam", true);
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

run().catch(error => { console.error(error); process.exitCode = 1; });
