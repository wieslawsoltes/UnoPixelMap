const assert = require("node:assert/strict");
const { mkdir, writeFile, readFile } = require("node:fs/promises");
const path = require("node:path");
const { chromium } = require("playwright");
const { PNG } = require("pngjs");

const base = new URL(process.argv[2] || "http://127.0.0.1:8000/");
const shots = process.env.PIXELMAP_SHOTS;
const driveColor = 0x65cfee;
const flightColor = 0xffc37a;
const runtimeErrors = [];

// Observe the actual browser graph without replacing its audio nodes or clock.
function observeAudio() {
    const NativeAudioContext = globalThis.AudioContext || globalThis.webkitAudioContext;
    const state = globalThis.journeyAudioTrace = {
        available: !!NativeAudioContext, contexts: 0, resumes: 0, suspends: 0,
        oscillators: 0, ended: 0, frequencyWrites: 0, frequencies: [], stops: [], currentState: "absent"
    };
    if (!NativeAudioContext) return;
    class ObservedAudioContext extends NativeAudioContext {
        constructor(...args) {
            super(...args);
            state.contexts++;
            state.currentState = this.state;
            this.addEventListener("statechange", () => { state.currentState = this.state; });
        }
        resume() { state.resumes++; return super.resume(); }
        suspend() { state.suspends++; return super.suspend(); }
        createOscillator() {
            const oscillator = super.createOscillator();
            state.oscillators++;
            const setTarget = oscillator.frequency.setTargetAtTime.bind(oscillator.frequency);
            oscillator.frequency.setTargetAtTime = (value, ...args) => {
                state.frequencyWrites++;
                if (state.frequencies.length < 2000) state.frequencies.push(value);
                return setTarget(value, ...args);
            };
            const stop = oscillator.stop.bind(oscillator);
            oscillator.stop = when => {
                state.stops.push((when ?? this.currentTime) - this.currentTime);
                return stop(when);
            };
            oscillator.addEventListener("ended", () => { state.ended++; });
            return oscillator;
        }
    }
    globalThis.AudioContext = ObservedAudioContext;
    if (globalThis.webkitAudioContext) globalThis.webkitAudioContext = ObservedAudioContext;
}

async function openDemo(browser, mode, phone = false) {
    const context = await browser.newContext({
        viewport: phone ? { width: 390, height: 830 } : { width: 1200, height: 850 },
        deviceScaleFactor: 1, isMobile: phone, hasTouch: phone
    });
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    page.on("pageerror", e => { runtimeErrors.push(mode + ": " + e.message); console.error("App runtime", e.message); });
    await page.addInitScript(observeAudio);
    for (const host of ["tiles.openfreemap.org", "tiles.maps.eox.at", "routing.openstreetmap.de"])
        await page.route("https://" + host + "/**", r => r.abort());
    const url = new URL(base);
    url.hash = "journey/" + mode;
    await page.goto(url.href, { waitUntil: "domcontentloaded", timeout: 120000 });
    await page.waitForFunction(() => {
        const canvas = document.getElementById("uno-canvas");
        return canvas?.width > 0 && document.elementFromPoint(innerWidth / 2, innerHeight / 2) === canvas;
    }, undefined, { timeout: 100000 });
    // Uno loads font metrics and the bundled map after mounting the canvas. Enable
    // accessibility only once that first layout has settled, as a user would after loading.
    await page.waitForLoadState("networkidle", {timeout: 45000});
    await page.screenshot();
    await page.getByRole("button", { name: "Enable accessibility" }).evaluate(el => el.click());
    await page.getByRole("button", { name: "Preview route", exact: true }).waitFor({ state: "attached" });
    return { context, page };
}

async function activate(page, role, name, touch = false) {
    const locator = page.getByRole(role, { name, exact: true });
    await locator.waitFor({state: "visible"});
    const b = await locator.boundingBox();
    assert.ok(b && b.width > 0 && b.height > 0, "Visible " + name);
    const viewport = page.viewportSize();
    const left = Math.max(0, b.x), right = Math.min(viewport.width, b.x + b.width);
    const top = Math.max(0, b.y), bottom = Math.min(viewport.height, b.y + b.height);
    assert.ok(right > left && bottom > top, "Onscreen " + name);
    const x = (left + right) / 2, y = (top + bottom) / 2;
    if (touch) await page.touchscreen.tap(x, y); else await page.mouse.click(x, y);
}

async function click(page, name, touch = false) { await activate(page, "button", name, touch); }

async function see(page, pattern, timeout = 15000) {
    const end = Date.now() + timeout;
    do {
        const snapshot = await page.locator("body").ariaSnapshot();
        if (pattern.test(snapshot)) return snapshot;
        await page.waitForTimeout(180);
    } while (Date.now() < end);
    if (shots) await page.screenshot({path: path.join(shots, "journey-state-failure.png"), timeout: 10000}).catch(() => {});
    throw new Error("Missing " + pattern + " on\n" + await page.locator("body").ariaSnapshot());
}

async function routeQuery(page, text) {
    const b = await page.getByRole("textbox", {name: "Find a route stop", exact: true}).boundingBox();
    assert.ok(b, "Route-stop text field is present");
    await page.mouse.click(b.x + b.width / 2, b.y + b.height / 2);
    await page.keyboard.press("ControlOrMeta+A");
    await page.keyboard.insertText(text);
}

async function checkRoutePixels(page, rgb, name) {
    // Downtown streets can be fully hidden by towers in 3D. The overhead view
    // exposes each mode's opaque geographic trace before the follow-camera test.
    if (page.viewportSize().width >= 600) {
        const projection = page.getByRole("button", {name: "Switch to 2D overhead", exact: true});
        if (await projection.count()) await click(page, "Switch to 2D overhead");
        await page.getByRole("button", {name: "Switch to 3D isometric", exact: true}).waitFor({state: "attached"});
        await click(page, "Fit route");
    }
    const end = Date.now() + 18000;
    let lastFrame;
    do {
        const bytes = await page.screenshot();
        lastFrame = bytes;
        const image = PNG.sync.read(bytes);
        let pixels = 0;
        const phone = image.width < 600;
        for (let y = phone ? Math.floor(image.height * .60) : 65; y < image.height - 40; y += 2) {
            for (let x = phone ? 10 : 365; x < image.width - 55; x += 2) {
                const i = 4 * (y * image.width + x);
                if (((image.data[i] << 16) | (image.data[i + 1] << 8) | image.data[i + 2]) === rgb) pixels++;
            }
        }
        if (pixels >= 5) {
            if (shots) await writeFile(path.join(shots, name + ".png"), bytes);
            return;
        }
        await page.waitForTimeout(300);
    } while (Date.now() < end);
    if (shots && lastFrame) await writeFile(path.join(shots, name + "-failure.png"), lastFrame);
    throw new Error("Missing route ink " + rgb.toString(16));
}

async function checkLens(page) {
    await click(page, "Close journeys");
    // The same scene and camera, with only the projection lens changed.
    await page.waitForTimeout(700);
    const clip = { x: 430, y: 180, width: 530, height: 450 };
    const before = PNG.sync.read(await page.screenshot({ clip }));
    await click(page, "Map appearance");
    const item = page.getByRole("menuitemcheckbox", {name: "Perspective lens", exact: true})
        .or(page.getByRole("menuitem", {name: "Perspective lens", exact: true}))
        .or(page.getByRole("checkbox", {name: "Perspective lens", exact: true}));
    await item.waitFor({state: "visible"});
    await activate(page, await item.getAttribute("role"), "Perspective lens");
    await item.waitFor({state: "hidden"});
    const deadline = Date.now() + 9000;
    do {
        await page.waitForTimeout(250);
        const after = PNG.sync.read(await page.screenshot({ clip }));
        let changed = 0;
        for (let i = 0; i < before.data.length; i += 16)
            if (before.data[i] !== after.data[i] || before.data[i + 1] !== after.data[i + 1] || before.data[i + 2] !== after.data[i + 2]) changed++;
        if (changed > before.data.length / 16 * .08) return;
    } while (Date.now() < deadline);
    throw new Error("The perspective lens did not visibly reproject map geometry");
}

async function checkShare(page) {
    await click(page, "Map appearance");
    await activate(page, "menuitem", "Share this view");
    await activate(page, "textbox", "Shareable map link");
    await see(page, /textbox "Shareable map link": .*#v3\//);
    const shared = await page.getByRole("textbox", {name: "Shareable map link", exact: true}).inputValue();
    assert.ok(shared, "The share field contains a copyable URL");
    const address = new URL(shared);
    const fields = address.hash.split("/");
    assert.equal(fields[0], "#v3");
    assert.equal(fields.length, 10, "Version 3 keeps lens and target altitude");
    assert.match(fields[8], /^(Perspective|Orthographic)$/);
    assert.ok(Number.isFinite(Number(fields[9])) && Number(fields[9]) >= 0 && Number(fields[9]) <= 15000);
    assert.equal(address.pathname, base.pathname, "Shared views preserve the Pages base path");
}

async function choices(page) {
    // Exercise the real OSRM decoder and UI with two different captured street geometries.
    const seed = path.resolve(__dirname, "../../src/PixelMap.Routing/Seed");
    const car = JSON.parse(await readFile(path.join(seed, "car.json"), "utf8"));
    const bicycle = JSON.parse(await readFile(path.join(seed, "bike.json"), "utf8"));
    const response = {code: "Ok", routes: [car.routes[0], bicycle.routes[0]]};
    let requested = false;
    await page.route("https://routing.openstreetmap.de/**", route => {
        requested = new URL(route.request().url()).searchParams.has("alternatives");
        return route.fulfill({json: response, headers: {"Access-Control-Allow-Origin": "*"}});
    });
    await click(page, "Edit route stops");
    await click(page, "Calculate route");
    const selector = page.getByRole("combobox", {name: "Alternative routes", exact: true});
    await activate(page, "combobox", "Alternative routes");
    assert.ok(requested, "provider returned alternative paths");
    await page.waitForTimeout(200);
    if (await selector.getAttribute("aria-expanded") !== "true") await page.keyboard.press("Alt+ArrowDown");
    await page.waitForFunction(() => document.querySelector('[aria-label="Alternative routes"]')?.getAttribute("aria-expanded") === "true");
    await page.keyboard.press("End");
    await page.keyboard.press("Enter");
    await page.waitForFunction(() => document.querySelector('[aria-label="Alternative routes"]')?.getAttribute("aria-expanded") === "false");
    await see(page, new RegExp(Math.round(bicycle.routes[0].distance) + " m"));
    await page.getByRole("listbox").waitFor({state: "hidden"});
}

async function sound(page) {
    assert.equal(await page.evaluate(() => journeyAudioTrace.contexts), 0, "The muted journey creates no AudioContext");
    await click(page, "Vehicle sound");
    await click(page, "Cruise control");
    await page.waitForFunction(() => journeyAudioTrace.contexts === 1 && journeyAudioTrace.frequencyWrites >= 4);
    const first = await page.evaluate(() => journeyAudioTrace);
    assert.ok(new Set(first.frequencies).size > 2, "Vehicle motion modulates the real oscillator frequency");
    const stops = first.stops.length, ended = first.ended;
    await click(page, "Vehicle horn or signal");
    await page.waitForFunction(n => journeyAudioTrace.stops.length >= n + 2, stops);
    const lifetimes = await page.evaluate(n => journeyAudioTrace.stops.slice(n), stops);
    assert.ok(lifetimes.every(seconds => seconds > 0 && seconds < 1), "Horn sources have bounded, short lifetimes");
    await page.waitForFunction(n => journeyAudioTrace.ended >= n + 2, ended);
    await click(page, "Vehicle sound");
    await page.waitForFunction(() => journeyAudioTrace.currentState === "suspended");
    const resumes = await page.evaluate(() => journeyAudioTrace.resumes);
    await click(page, "Vehicle sound");
    await page.waitForFunction(n => journeyAudioTrace.resumes > n && journeyAudioTrace.currentState === "running", resumes);
    assert.equal(await page.evaluate(() => journeyAudioTrace.contexts), 1, "Unmuting reuses the existing graph");
    await click(page, "Vehicle sound");
    await click(page, "Cruise control");
    const observed = await page.evaluate(() => ({contexts: journeyAudioTrace.contexts, resumes: journeyAudioTrace.resumes,
        frequencyWrites: journeyAudioTrace.frequencyWrites, endedSources: journeyAudioTrace.ended}));
    console.log("Verified browser audio", observed);
}

async function drive(browser) {
    const { context, page } = await openDemo(browser, "drive");
    try {
        await see(page, /Wall Street to City Hall Park/);
        await checkRoutePixels(page, driveColor, "journey-drive");
        await choices(page);
        await click(page, "Preview route");
        await see(page, /Route preview/);
        await click(page, "Pause or resume journey");
        await see(page, /Journey paused/);
        await click(page, "Take manual control");
        await see(page, /Manual control/);
        await page.keyboard.down("w");
        await page.waitForTimeout(1150);
        await page.keyboard.up("w");
        await see(page, /\d+\/\d+ gates/);
        await sound(page);
        await click(page, "Cruise control");
        if (shots) await page.screenshot({path: path.join(shots, "journey-driving.png")});
        await click(page, "Recover at checkpoint");
        await click(page, "Reset journey");
        await see(page, /Trip ready/);
        await click(page, "Show or hide directions");
        await see(page, /1\. /);
        await checkLens(page);
        await checkShare(page);
    } finally { await context.close(); }
}

async function groundModes(browser) {
    for (const [mode, badge, ink] of [["walk", "WALKING", 0xb4df79], ["bike", "BICYCLE", 0x67dba8], ["train", "TRAIN", 0xe9a2e1]]) {
        const {context, page} = await openDemo(browser, mode);
        try {
            await see(page, new RegExp(badge));
            await checkRoutePixels(page, ink, "journey-" + mode);
            await click(page, "Preview route");
            await see(page, /Route preview/);
            await click(page, "Pause or resume journey");
            await see(page, /Journey paused/);
            await click(page, "Take manual control");
            if (mode === "train") assert.ok(await page.getByRole("button", {name: "Steer left", exact: true}).isDisabled(), "Train stays on its track");
            await page.keyboard.down("w");
            try { await see(page, /[1-9]\d* km\/h/); }
            finally { await page.keyboard.up("w"); }
            await see(page, /\d+\/\d+ gates/);
            await click(page, "Reset journey");
            await click(page, "Show or hide directions");
            await see(page, /1\. /);
            console.log("Verified vehicle mode", mode);
        } finally { await context.close(); }
    }
}

async function planVia(browser) {
    const { context, page } = await openDemo(browser, "flight");
    try {
        await see(page, /LaGuardia Airport to Newark Liberty/);
        await checkRoutePixels(page, flightColor, "journey-flight");
        let placeSearches = 0;
        await page.route("https://photon.komoot.io/**", route => {
            placeSearches++;
            return route.fulfill({json: {type: "FeatureCollection", features: [{
                type: "Feature", geometry: {type: "Point", coordinates: [-74.1745, 40.6895]},
                properties: {name: "Newark Liberty Airport", state: "New Jersey", country: "United States", type: "airport"}
            }]}, headers: {"Access-Control-Allow-Origin": "*"}});
        });
        await click(page, "Edit route stops");
        await routeQuery(page, "Newark Liberty Airport");
        await click(page, "Search for route stop");
        await click(page, "Newark Liberty Airport · New Jersey, United States");
        assert.equal(placeSearches, 1, "Submitted route-stop search reaches the geocoder");
        await click(page, "Add intermediate stop");
        await click(page, "Pick selected route stop on map");
        await see(page, /Tap the map to set via 1/i);
        await page.mouse.click(760, 390);
        await see(page, /Set Via 1:/);
        await click(page, /^Set Destination:/);
        await routeQuery(page, "40.6895, -74.1745");
        // Calculating resolves the coordinate draft even before Enter or Find is pressed.
        await click(page, "Calculate route");
        await see(page, /2 legs/);
        await see(page, /LaGuardia Airport to Dropped pin/);
        assert.equal(placeSearches, 1, "Coordinate drafts resolve locally");
        await click(page, "Go to route start");
        await click(page, "Go to route end");
        await click(page, "Fit route");
        await click(page, "Take manual control");
        await see(page, /Climb aircraft/);
    } finally { await context.close(); }
}

async function phoneRide(browser) {
    const { context, page } = await openDemo(browser, "boat", true);
    try {
        await see(page, /DUMBO/);
        await checkRoutePixels(page, 0x85dcea, "journey-boat");
        await click(page, "Take manual control", true);
        const b = await page.getByRole("button", { name: "Accelerate vehicle" }).boundingBox();
        assert.ok(b && b.x >= 0 && b.y >= 0 && b.x + b.width <= 391 && b.y + b.height <= 831, "Phone controls fit");
        const cdp = await context.newCDPSession(page);
        await cdp.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x: b.x + b.width / 2, y: b.y + b.height / 2, id: 1 }] });
        await page.waitForTimeout(1500);
        await cdp.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
        await see(page, /[1-9]\d* km\/h/);
        if (shots) await page.screenshot({ path: path.join(shots, "journey-boat-phone.png") });
    } finally { await context.close(); }
}

(async () => {
    if (shots) await mkdir(shots, { recursive: true });
    for (const scenario of [drive, groundModes, planVia, phoneRide]) {
        const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
            args: ["--disable-dev-shm-usage", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"] });
        try { await scenario(browser); console.log("Passed journey scenario", scenario.name); }
        finally { await browser.close(); }
    }
    assert.deepEqual(runtimeErrors, [], "No unhandled browser runtime errors");
})().catch(e => { console.error(e); process.exitCode = 1; });
