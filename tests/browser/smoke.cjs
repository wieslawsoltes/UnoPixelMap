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
    page.on("pageerror", error => failures.push(error.message));
    // The opening district is bundled. CI should not rely on public map services.
    await page.route("https://tiles.openfreemap.org/**", route => route.abort());
    await page.goto(address, {waitUntil: "domcontentloaded", timeout: 120000});
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
        await mobile.close();
        assert.deepEqual(failures, [], "browser runtime errors");
        console.log("Browser checks passed: seeded geometry, drag, wheel, coordinate search, camera deep link, night lighting, phone viewport and touch pinch.");
    } finally {
        await browser.close();
    }
}

run().catch(error => { console.error(error); process.exitCode = 1; });
