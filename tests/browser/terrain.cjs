const assert = require("node:assert/strict");
const {mkdir, writeFile} = require("node:fs/promises");
const {join} = require("node:path");
const {chromium} = require("playwright");
const {PNG} = require("pngjs");
const {enableGpuRendering} = require("./gpu.cjs");

const base = new URL(process.argv[2] || "http://127.0.0.1:8000/");
const evidence = process.env.PIXELMAP_SHOTS;
const mountainView = "v4/45.875/10.85/12.3/-25/58/Day/Elevation/Perspective/0/Relief/1.00";
const failures = [];

function color(frame, x, y) {
    const index = (y * frame.width + x) * 4;
    return (frame.data[index] << 16) | (frame.data[index + 1] << 8) | frame.data[index + 2];
}

// Exclude the header, search, legend, destination card and map buttons. Checks use
// rendered terrain pixels, alongside independent public UI state assertions.
function scene(frame, visit) {
    const phone = frame.width < 600;
    const left = Math.floor(frame.width * (phone ? .12 : .34));
    const right = Math.floor(frame.width * .88);
    const top = Math.floor(frame.height * .24), bottom = Math.floor(frame.height * .62);
    for (let y = top; y < bottom; y += 2)
        for (let x = left; x < right; x += 2) visit(x, y);
}

function measure(frame) {
    const colors = new Set();
    let count = 0, sum = 0, squared = 0, nearBlack = 0;
    scene(frame, (x, y) => {
        const rgb = color(frame, x, y);
        const light = ((rgb >> 16) + ((rgb >> 8) & 255) + (rgb & 255)) / 3;
        colors.add(rgb);
        sum += light;
        squared += light * light;
        if (light < 8) nearBlack++;
        count++;
    });
    return {colors: colors.size, deviation: Math.sqrt(Math.max(0, squared / count - (sum / count) ** 2)),
        brightness: sum / count, blackRatio: nearBlack / count};
}

function hasTerrain(frame) {
    const result = measure(frame);
    return result.colors > 35 && result.deviation > 12 && result.brightness > 35 && result.blackRatio < .02;
}

function changed(before, after) {
    assert.equal(before.width, after.width);
    assert.equal(before.height, after.height);
    let count = 0, difference = 0;
    scene(before, (x, y) => {
        if (color(before, x, y) !== color(after, x, y)) difference++;
        count++;
    });
    return difference / count;
}

async function frameWhere(page, predicate, name, timeout = 30000) {
    const deadline = Date.now() + timeout;
    let bytes, frame;
    do {
        bytes = await page.screenshot({timeout: 15000});
        frame = PNG.sync.read(bytes);
        if (predicate(frame)) {
            if (evidence) await writeFile(join(evidence, name + ".png"), bytes);
            console.log(name, JSON.stringify(measure(frame)));
            return frame;
        }
        await page.waitForTimeout(250);
    } while (Date.now() < deadline);
    if (evidence) await writeFile(join(evidence, name + "-failure.png"), bytes);
    throw new Error(name + " did not produce the expected terrain frame. " + JSON.stringify(measure(frame)));
}

async function see(page, expected, timeout = 20000) {
    const deadline = Date.now() + timeout;
    let snapshot;
    do {
        snapshot = await page.locator("body").ariaSnapshot();
        if (typeof expected === "function" ? expected(snapshot) : expected.test(snapshot)) return snapshot;
        await page.waitForTimeout(150);
    } while (Date.now() < deadline);
    if (evidence) {
        await page.screenshot({path: join(evidence, "terrain-state-failure.png"), timeout: 15000});
        await writeFile(join(evidence, "terrain-state-failure.txt"), snapshot);
    }
    throw new Error("Missing terrain state " + expected + "\n" + snapshot);
}

async function tap(page, locator, touch = false) {
    await locator.waitFor({state: "visible"});
    // The Skia legend and credits can move the layer strip before Uno updates the
    // accessibility peer bounds. Let that layout settle before sending physical input.
    await page.waitForTimeout(300);
    const bounds = await locator.boundingBox();
    const viewport = page.viewportSize();
    assert.ok(bounds && bounds.width > 0 && bounds.height > 0, "The requested control is visible");
    assert.ok(bounds.x >= -1 && bounds.y >= -1 && bounds.x + bounds.width <= viewport.width + 1 &&
        bounds.y + bounds.height <= viewport.height + 1, "The requested control fits the viewport");
    const x = bounds.x + bounds.width / 2, y = bounds.y + bounds.height / 2;
    if (touch) await page.touchscreen.tap(x, y);
    else await page.mouse.click(x, y);
}

async function action(page, name, touch = false) {
    const locator = page.getByRole("button", {name, exact: true})
        .or(page.getByRole("checkbox", {name, exact: true}));
    await tap(page, locator, touch);
}

async function menu(page, name) {
    await action(page, "Map appearance");
    const item = page.getByRole("menuitem", {name, exact: true})
        .or(page.getByRole("menuitemcheckbox", {name, exact: true}));
    await item.waitFor({state: "visible"});
    await item.focus();
    await page.keyboard.press("Enter");
    await item.waitFor({state: "hidden"});
    await page.mouse.move(page.viewportSize().width / 2, 20);
}

async function open(context, address) {
    const page = await context.newPage();
    if (process.env.PIXELMAP_GPU === "1") await page.addInitScript(() => {
        const counts = window.pixelMapTerrainGpu = {depthDraws: 0, artViewports: {}, readbacks: 0};
        const prototype = WebGL2RenderingContext.prototype;
        for (const name of ["drawElements", "drawElementsInstanced", "drawArrays", "drawArraysInstanced"]) {
            const original = prototype[name];
            prototype[name] = function (...args) {
                if (this.isEnabled(this.DEPTH_TEST)) {
                    const viewport = this.getParameter(this.VIEWPORT);
                    if (viewport[2] > 0 && viewport[2] <= Math.ceil(innerWidth / 2) &&
                        viewport[3] > 0 && viewport[3] <= Math.ceil(innerHeight / 2)) {
                        counts.depthDraws++;
                        const size = viewport[2] + "x" + viewport[3];
                        counts.artViewports[size] = (counts.artViewports[size] || 0) + 1;
                    }
                }
                return Reflect.apply(original, this, args);
            };
        }
        const readPixels = prototype.readPixels;
        prototype.readPixels = function (...args) {
            counts.readbacks++;
            return Reflect.apply(readPixels, this, args);
        };
    });
    const expectedFailures = new Set();
    page.setDefaultTimeout(20000);
    page.on("pageerror", error => {
        failures.push(error.message);
        console.error("Terrain browser exception", error.message);
    });
    page.on("console", message => {
        const text = message.text();
        if (text.includes("Pixel Atlas GPU terrain")) console.log(text);
        if (message.type() !== "error") return;
        if (text === "Failed to load resource: net::ERR_FAILED" && expectedFailures.has(message.location().url)) return;
        console.error("Terrain browser console", text.slice(0, 1800));
        if (/Unhandled (?:managed )?exception|RuntimeError:|System\.[\w.]*Exception/.test(text)) failures.push(text);
    });
    await page.route("https://raw.githubusercontent.com/notofonts/**", route => {
        failures.push("Terrain requested an external fallback font " + route.request().url());
        return route.abort();
    });
    // The world and 3×3 Garda cluster are bundled. Deliberately fail halo and parent
    // requests to exercise graceful fallback without depending on public services.
    for (const pattern of ["https://tiles.openfreemap.org/**", "https://tiles.maps.eox.at/**",
        "https://s3.amazonaws.com/elevation-tiles-prod/**", "https://elevation-tiles-prod.s3.amazonaws.com/**"])
        await page.route(pattern, route => {
            expectedFailures.add(route.request().url());
            return route.abort();
        });
    await page.goto(address, {waitUntil: "domcontentloaded", timeout: 120000});
    await page.waitForFunction(() => {
        const canvas = document.getElementById("uno-canvas");
        return document.title.includes("Pixel Atlas") && canvas?.width > 0 &&
            document.elementFromPoint(innerWidth / 2, innerHeight / 2) === canvas;
    }, undefined, {timeout: 100000});
    await page.waitForLoadState("networkidle", {timeout: 45000});
    await page.screenshot();
    const accessibility = page.getByRole("button", {name: "Enable accessibility", exact: true});
    if (await accessibility.count()) await accessibility.evaluate(element => element.click());
    await page.getByRole("button", {name: "Map appearance", exact: true}).waitFor({state: "visible"});
    if (process.env.PIXELMAP_GPU === "1") await enableGpuRendering(page);
    return page;
}

async function assertGpuTerrain(page) {
    if (process.env.PIXELMAP_GPU !== "1") return;
    await see(page, /GPU active/);
    const work = await page.evaluate(() => window.pixelMapTerrainGpu);
    assert.ok(work.depthDraws > 0, "Terrain submits depth-tested geometry at pixel-art resolution");
    assert.ok(work.readbacks > 0, "Uno presents the hardware-rendered terrain framebuffer");
    console.log("GPU terrain geometry", JSON.stringify(work));
}

async function share(page) {
    await menu(page, "Share this view");
    const input = page.getByRole("textbox", {name: "Shareable map link", exact: true});
    await tap(page, input);
    const address = new URL(await input.inputValue());
    const fields = address.hash.split("/");
    assert.equal(address.pathname, base.pathname, "Sharing retains the GitHub Pages project path");
    assert.equal(fields[0], "#v4");
    assert.equal(fields.length, 12, "The share link contains layer, relief and exaggeration");
    assert.equal(fields[7], "Elevation");
    assert.equal(fields[8], "Perspective");
    assert.equal(fields[10], "Relief");
    assert.ok(Number(fields[11]) >= 1 && Number(fields[11]) <= 4);
    await action(page, "Close panel");
    return address;
}

async function inspectTerrain(page) {
    const before = await page.locator("body").ariaSnapshot();
    const x = 800, y = 350;
    await page.mouse.click(x, y);
    const after = await see(page, snapshot => snapshot.split("\n").some(line =>
        / · -?\d+ m elevation/.test(line) && !before.split("\n").includes(line)));
    assert.match(after, /-?\d+ m elevation/);
    await frameWhere(page, frame => {
        let border = 0;
        for (let row = y - 85; row < y + 35; row++)
        for (let column = x - 140; column < x + 140; column++)
            if (color(frame, column, row) === 0xf6c069) border++;
        return border > 30;
    }, "terrain-picked-elevation");
    await page.keyboard.press("Escape");
}

async function run() {
    if (evidence) await mkdir(evidence, {recursive: true});
    const browser = await chromium.launch({executablePath: process.env.CHROMIUM_PATH || undefined,
        args: ["--disable-dev-shm-usage", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"]});
    try {
        const desktop = await browser.newContext({viewport: {width: 1200, height: 800}, deviceScaleFactor: 1});
        const address = new URL(base);
        address.hash = mountainView;
        const page = await open(desktop, address.href);
        await see(page, /61 m elevation · 1× relief/, 60000);
        await see(page, /Terrain Tiles \/ Mapzen/);
        const mountain = await frameWhere(page, hasTerrain, "terrain-elevation-perspective", 60000);
        await assertGpuTerrain(page);

        await menu(page, "Terrain relief");
        await see(page, /2D altitude/);
        const flat = await frameWhere(page, frame => hasTerrain(frame) && changed(mountain, frame) > .12,
            "terrain-flat-comparison");
        await menu(page, "Terrain relief");
        await see(page, /1× relief/);
        const restored = await frameWhere(page, frame => hasTerrain(frame) && changed(flat, frame) > .12,
            "terrain-restored-relief");
        await inspectTerrain(page);
        if (process.env.PIXELMAP_GPU === "1") {
            await menu(page, "GPU rendering (experimental)");
            await see(page, snapshot => !/GPU active|CPU fallback/.test(snapshot));
            await frameWhere(page, hasTerrain, "terrain-gpu-disabled");
            await enableGpuRendering(page);
            await frameWhere(page, hasTerrain, "terrain-gpu-recreated");
            await assertGpuTerrain(page);
        }

        await action(page, "Switch to 2D overhead");
        await page.getByRole("button", {name: "Switch to 3D perspective", exact: true}).waitFor({state: "visible"});
        const overhead = await frameWhere(page, frame => hasTerrain(frame) && changed(restored, frame) > .20,
            "terrain-elevation-overhead");
        await action(page, "Map layer");
        const map = await frameWhere(page, frame => hasTerrain(frame) && changed(overhead, frame) > .15,
            "terrain-vector-overhead");
        await action(page, "Earth imagery layer");
        await see(page, /Contains modified Copernicus Sentinel data/);
        const earth = await frameWhere(page, frame => hasTerrain(frame) && changed(map, frame) > .20,
            "terrain-earth-overhead");
        await action(page, "Switch to 3D perspective");
        await frameWhere(page, frame => hasTerrain(frame) && changed(earth, frame) > .20, "terrain-earth-perspective");
        await action(page, "Elevation layer");
        const beforeOrbit = await frameWhere(page, hasTerrain, "terrain-before-orbit");
        await page.mouse.move(690, 360);
        await page.mouse.down({button: "right"});
        await page.mouse.move(755, 382, {steps: 8});
        await page.mouse.up({button: "right"});
        const orbited = await frameWhere(page, frame => hasTerrain(frame) && changed(beforeOrbit, frame) > .12,
            "terrain-orbit");
        await page.mouse.move(690, 370);
        await page.mouse.down();
        await page.mouse.move(735, 390, {steps: 10});
        await page.waitForTimeout(120);
        await page.mouse.up();
        await frameWhere(page, frame => hasTerrain(frame) && changed(orbited, frame) > .10, "terrain-drag");

        await menu(page, "Camera angles and orbit");
        const slider = page.getByRole("slider", {name: "Terrain vertical exaggeration", exact: true});
        await slider.focus();
        await page.keyboard.press("End");
        await action(page, "Close panel");
        await see(page, /4× relief/);
        const shared = await share(page);
        assert.equal(shared.hash.split("/")[11], "4.00", "The public slider changes shared vertical exaggeration");
        const roundTrip = await open(desktop, shared.href);
        await see(roundTrip, /4× relief/, 60000);
        await frameWhere(roundTrip, hasTerrain, "terrain-share-round-trip", 60000);
        const sharedAgain = await share(roundTrip);
        assert.equal(sharedAgain.hash, shared.hash, "A fresh page restores the complete v4 terrain camera state");
        await assertGpuTerrain(roundTrip);
        await desktop.close();

        const mobile = await browser.newContext({viewport: {width: 390, height: 830}, deviceScaleFactor: 1,
            isMobile: true, hasTouch: true});
        const phone = await open(mobile, address.href);
        await see(phone, /61 m elevation · 1× relief/, 60000);
        const portrait = await frameWhere(phone, hasTerrain, "terrain-phone-perspective", 60000);
        assert.equal(portrait.width, 390, "Terrain respects the handset viewport");
        await action(phone, "Switch to 2D overhead", true);
        const phoneOverhead = await frameWhere(phone, frame => hasTerrain(frame) && changed(portrait, frame) > .20,
            "terrain-phone-overhead");
        await action(phone, "Earth imagery layer", true);
        await see(phone, /Contains modified Copernicus Sentinel data/);
        const phoneEarth = await frameWhere(phone, frame => hasTerrain(frame) && changed(phoneOverhead, frame) > .20,
            "terrain-phone-earth");
        await action(phone, "Elevation layer", true);
        await see(phone, /button "Elevation layer" \[pressed\]/);
        await frameWhere(phone, frame => hasTerrain(frame) && changed(phoneEarth, frame) > .20, "terrain-phone-elevation");
        const fits = await phone.evaluate(() => document.documentElement.scrollWidth <= innerWidth);
        assert.ok(fits, "Terrain controls and credits introduce no horizontal overflow");
        await assertGpuTerrain(phone);
        await mobile.close();

        if (process.env.PIXELMAP_GPU === "1") {
            const highDpi = await browser.newContext({viewport: {width: 840, height: 600}, deviceScaleFactor: 2});
            const dense = await open(highDpi, address.href);
            const initial = await frameWhere(dense, hasTerrain, "terrain-gpu-high-dpi", 60000);
            assert.equal(initial.width, 1680, "The Skia foreground preserves physical display resolution");
            await assertGpuTerrain(dense);
            await dense.setViewportSize({width: 960, height: 640});
            const resized = await frameWhere(dense, frame => frame.width === 1920 && hasTerrain(frame),
                "terrain-gpu-high-dpi-resized", 60000);
            assert.equal(resized.height, 1280);
            await assertGpuTerrain(dense);
            await highDpi.close();
        }
        assert.deepEqual(failures, [], "Terrain has no browser runtime exceptions or missing fonts");
        console.log("Terrain browser checks passed for real Garda relief, flat comparison, elevation, Map and Earth layers, 2D/3D, picking, orbit, drag, exaggeration, v4 sharing and phone controls.");
    } finally {
        await browser.close();
    }
}

run().catch(error => { console.error(error); process.exitCode = 1; });
