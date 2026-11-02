const assert = require("node:assert/strict");
const {mkdir, writeFile} = require("node:fs/promises");
const {join} = require("node:path");
const {chromium} = require("playwright");
const {PNG} = require("pngjs");

const url = process.argv[2] || "http://127.0.0.1:8000/";
const evidence = process.env.PIXELMAP_SHOTS;
const roofs = new Set([0xddb17e, 0xc28168, 0xb8c2af, 0xe1b598, 0xc6aa9e, 0x8fbab0,
    0xe5caa0, 0xa5afbc, 0x96b6c2, 0xd6b6a4, 0xa3bd9f, 0xce998a]);

function color(pixels, i) {
    return (pixels[i] << 16) | (pixels[i + 1] << 8) | pixels[i + 2];
}

function measure(frame) {
    const left = Math.floor(frame.width * .18), right = Math.floor(frame.width * .8);
    const top = Math.floor(frame.height * .24), bottom = Math.floor(frame.height * .73);
    let models = 0, light = 0, count = 0;
    for (let y = top; y < bottom; y += 2) {
        for (let x = left; x < right; x += 2) {
            const i = (y * frame.width + x) * 4;
            if (roofs.has(color(frame.data, i))) models++;
            light += (frame.data[i] + frame.data[i + 1] + frame.data[i + 2]) / 3;
            count++;
        }
    }
    return {roofRatio: models / count, brightness: light / count};
}

function changed(a, b) {
    let delta = 0, count = 0;
    for (let y = Math.floor(a.height * .28); y < a.height * .72; y += 3) {
        for (let x = Math.floor(a.width * .2); x < a.width * .78; x += 3) {
            const i = (y * a.width + x) * 4;
            if (color(a.data, i) !== color(b.data, i)) delta++;
            count++;
        }
    }
    return delta / count;
}

// Exact Earth raster inks for built land, deep water and green land. These are absent
// from the vector palette, so colored placeholders and building-only frames do not pass.
const earthInks = new Set([0x102541, 0x153555, 0x1a4968, 0x235e80, 0x133c45, 0x1b5056,
    0x23696b, 0x263a28, 0x334c32, 0x394531, 0x505a39, 0x42494c, 0x596063, 0x72797a,
    0x8b9190, 0xa7abaa, 0xc0c4be, 0x26303c, 0x3a485a, 0x52637a, 0x494641, 0x655f55,
    0x80796b, 0x9b9384, 0xb5ae9d]);

function hasEarthTerrain(frame) {
    const counts = new Map();
    for (let y = Math.floor(frame.height * .22); y < frame.height * .72; y += 2) {
        for (let x = Math.floor(frame.width * .13); x < frame.width * .84; x += 2) {
            const rgb = color(frame.data, (y * frame.width + x) * 4);
            if (earthInks.has(rgb)) counts.set(rgb, (counts.get(rgb) || 0) + 1);
        }
    }
    return [...counts.values()].filter(n => n >= 8).length >= 3 &&
        [...counts.values()].reduce((sum, n) => sum + n, 0) > 200;
}

async function clickMapAction(page, name, touch = false) {
    const control = page.getByRole("button", {name, exact: true})
        .or(page.getByRole("checkbox", {name, exact: true}));
    const bounds = await control.boundingBox();
    assert.ok(bounds && bounds.width && bounds.height, "Visible " + name);
    const x = bounds.x + bounds.width / 2, y = bounds.y + bounds.height / 2;
    if (touch) await page.touchscreen.tap(x, y);
    else await page.mouse.click(x, y);
}

async function checkEarth(page, initial) {
    const enable = page.getByRole("button", {name: "Enable accessibility"});
    if (await enable.count()) await enable.evaluate(element => element.click());
    assert.ok(!hasEarthTerrain(initial), "vector and satellite basemaps must be distinguishable");
    await clickMapAction(page, "Earth imagery layer");
    const iso = await frameWhere(page, frame => hasEarthTerrain(frame) && measure(frame).roofRatio > .01, "earth-isometric", 45000);
    assert.match(await page.locator("body").ariaSnapshot(), /EOxCloudless/);
    await clickMapAction(page, "Switch to 2D overhead");
    await page.getByRole("button", {name: "Switch to 3D isometric", exact: true}).waitFor({state: "attached"});
    await frameWhere(page, frame => hasEarthTerrain(frame) && changed(iso, frame) > .25, "earth-overhead");
    await clickMapAction(page, "Map layer");
    await page.waitForFunction(() => !document.body.innerText.includes("Contains modified Copernicus"));
    await clickMapAction(page, "Switch to 3D isometric");
    await page.waitForTimeout(800);
}

async function frameWhere(page, predicate, name, timeout = 25000) {
    const end = Date.now() + timeout;
    let frame, bytes;
    do {
        bytes = await page.screenshot({timeout: 15000});
        frame = PNG.sync.read(bytes);
        if (predicate(frame)) {
            if (evidence) await writeFile(join(evidence, name + ".png"), bytes);
            return frame;
        }
        await page.waitForTimeout(250);
    } while (Date.now() < end);
    if (evidence) await writeFile(join(evidence, name + "-failure.png"), bytes);
    throw new Error(name + " did not reach the expected frame. " + JSON.stringify(measure(frame)));
}

async function open(context, address, failures) {
    const page = await context.newPage();
    page.on("pageerror", error => {
        failures.push(error.message);
        console.error("Browser exception", error.message);
    });
    page.on("console", message => {
        if (message.type() === "error") console.error("Browser console", message.text().slice(0, 1800));
    });
    // The opening district is bundled. CI should not rely on public map services.
    await page.route("https://raw.githubusercontent.com/notofonts/**", route => {
        failures.push("The opening view requested an external fallback font: " + route.request().url());
        return route.abort();
    });
    await page.route("https://tiles.openfreemap.org/**", route => route.abort());
    await page.route("https://tiles.maps.eox.at/**", route => route.abort());
    await page.goto(address, {waitUntil: "domcontentloaded", timeout: 120000});
    await page.waitForFunction(() => {
        const canvas = document.getElementById("uno-canvas");
        return document.title.includes("Pixel Atlas") && canvas?.width > 0 &&
            document.elementFromPoint(innerWidth / 2, innerHeight / 2) === canvas;
    }, undefined, {timeout: 100000});
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    return page;
}

async function run() {
    if (evidence) await mkdir(evidence, {recursive: true});
    const browser = await chromium.launch({
        executablePath: process.env.CHROMIUM_PATH || undefined,
        args: ["--disable-dev-shm-usage", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"]
    });
    try {
        const failures = [];
        const desktop = await browser.newContext({viewport: {width: 1200, height: 780}, deviceScaleFactor: 1});
        const page = await open(desktop, url, failures);
        const initial = await frameWhere(page, frame => measure(frame).roofRatio > .018, "desktop-city", 110000);
        assert.match(await page.title(), /Pixel Atlas/i);
        await checkEarth(page, initial);

        await page.mouse.move(660, 390);
        await page.mouse.down();
        await page.mouse.move(750, 425, {steps: 7});
        await page.mouse.up();
        const panned = await frameWhere(page, frame => changed(initial, frame) > .25, "desktop-drag");
        await page.waitForTimeout(1700);
        const beforeWheel = PNG.sync.read(await page.screenshot());
        await page.mouse.wheel(0, -190);
        await frameWhere(page, frame => changed(beforeWheel, frame) > .25 && changed(panned, frame) > .25, "desktop-zoom");

        // Hit the real Uno TextBox: Skia creates the browser input when it receives focus.
        let geocoderRequests = 0;
        page.on("request", request => { if (request.url().includes("photon.komoot.io")) geocoderRequests++; });
        await page.mouse.click(128, 93);
        await page.keyboard.insertText("40.7093, -74.0132");
        await page.keyboard.press("Enter");
        await frameWhere(page, frame => {
            // The map pin uses a warm two-pixel border. Its ground point is the viewport center.
            let pin = 0;
            for (let y = 350; y < 435; y++) for (let x = 520; x < 680; x++)
                if (color(frame.data, (y * frame.width + x) * 4) === 0xf6c069) pin++;
            return pin > 35;
        }, "coordinate-pin");
        assert.equal(geocoderRequests, 0, "coordinate input should resolve locally");

        // Jump straight to a share link in a fresh tab and verify real night-mode light levels.
        const nightUri = new URL(url);
        nightUri.hash = "v1/40.708700/-74.010400/16.500/-15.00/55.00/Night";
        const night = await open(desktop, nightUri.href, failures);
        const daylight = measure(initial).brightness;
        await frameWhere(night, frame => {
            const m = measure(frame);
            if (m.brightness >= daylight - 48 || m.brightness < 25) return false;
            let windows = 0;
            for (let i = 0; i < frame.data.length; i += 16) if (color(frame.data, i) === 0xffd58b) windows++;
            return windows > 50;
        }, "night-deep-link", 110000);
        await desktop.close();

        const mobile = await browser.newContext({viewport: {width: 390, height: 790}, deviceScaleFactor: 1, isMobile: true, hasTouch: true});
        const phone = await open(mobile, url, failures);
        const handset = await frameWhere(phone, frame => measure(frame).roofRatio > .015, "phone-city", 110000);
        assert.equal(handset.width, 390, "responsive viewport");
        // Native touch via CDP exercises PointerPressed/Move/Released and the view's pinch handling.
        const cdp = await mobile.newCDPSession(phone);
        const fingers = (x1, x2) => [{x: x1, y: 370, id: 1}, {x: x2, y: 370, id: 2}];
        await cdp.send("Input.dispatchTouchEvent", {type: "touchStart", touchPoints: fingers(145, 225)});
        for (let i = 1; i <= 8; i++) {
            await cdp.send("Input.dispatchTouchEvent", {type: "touchMove", touchPoints: fingers(145 - i * 5, 225 + i * 5)});
        }
        await cdp.send("Input.dispatchTouchEvent", {type: "touchEnd", touchPoints: []});
        await frameWhere(phone, frame => changed(handset, frame) > .25 && measure(frame).roofRatio > .006, "phone-pinch");
        const enable = phone.getByRole("button", {name: "Enable accessibility"});
        if (await enable.count()) await enable.evaluate(element => element.click());
        await clickMapAction(phone, "Switch to 2D overhead", true);
        await phone.getByRole("button", {name: "Switch to 3D isometric", exact: true}).waitFor({state: "attached"});
        await clickMapAction(phone, "Earth imagery layer", true);
        await frameWhere(phone, hasEarthTerrain, "phone-earth");
        assert.match(await phone.locator("body").ariaSnapshot(), /Contains modified Copernicus Sentinel data/);
        await mobile.close();
        assert.deepEqual(failures, [], "browser runtime errors");
        console.log("Browser checks passed: seeded geometry, Earth imagery in isometric and overhead views, drag, wheel, coordinate search, camera deep link, night lighting, phone viewport and touch pinch.");
    } finally {
        await browser.close();
    }
}

run().catch(error => { console.error(error); process.exitCode = 1; });
