const assert = require("node:assert/strict");
const path = require("node:path");
const { chromium } = require("playwright");

// Physical browser input runs microtasks between DOM listeners. Synthetic
// dispatchEvent calls alone cannot verify the adapter's native routing lifetime.
async function run() {
    const browser = await chromium.launch({
        executablePath: process.env.CHROMIUM_PATH || undefined,
        args: ["--no-sandbox", "--disable-gpu"]
    });
    try {
        const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
        await page.setContent(`
            <style>
                body { margin: 0; }
                canvas { touch-action: none; }
                input { position: absolute; left: 550px; top: 40px; width: 200px; height: 40px; }
            </style>
            <canvas id="uno-canvas" width="800" height="600" tabindex="0"></canvas>
            <input aria-label="Native editor">
        `);
        await page.addScriptTag({ path: path.resolve(__dirname, "../../src/PixelMap.Controls/Browser/map-input.js") });
        await page.evaluate(() => {
            window.records = { wheels: [], keys: [], pointers: [] };
            PixelAtlasInput.attach(1, () => {}, () => {});
            document.body.addEventListener("wheel", event => {
                const sample = PixelAtlasInput.takeWheel(1);
                const duplicate = sample.length ? PixelAtlasInput.takeWheel(1) : [];
                records.wheels.push({ sample, duplicate, trusted: event.isTrusted,
                    prevented: event.defaultPrevented, target: event.target.tagName });
            }, { capture: true, passive: false });
            document.body.addEventListener("keydown", event => {
                records.keys.push({ repeat: event.repeat, sample: PixelAtlasInput.getKeyRepeat(1),
                    trusted: event.isTrusted, target: event.target.tagName });
            }, { capture: true });
            document.body.addEventListener("pointerdown", event => {
                PixelAtlasInput.capturePointer(1, event.pointerId);
                records.pointers.push({ captured: event.target.hasPointerCapture(event.pointerId),
                    trusted: event.isTrusted, prevented: event.defaultPrevented, target: event.target.tagName });
            }, { capture: true });
            document.body.addEventListener("pointerup", event => {
                PixelAtlasInput.releasePointer(1, event.pointerId);
            }, { capture: true });
        });

        await page.mouse.move(200, 200);
        await page.mouse.wheel(14.5, -8.25);
        await page.waitForFunction(() => records.wheels.length === 1);
        await page.keyboard.down("Control");
        await page.mouse.wheel(0, -24.5);
        await page.keyboard.up("Control");
        await page.waitForFunction(() => records.wheels.length === 2);
        await page.mouse.click(200, 200);
        await page.locator("canvas").focus();
        await page.keyboard.down("p");
        await page.keyboard.down("p");
        await page.keyboard.up("p");

        await page.getByRole("textbox", { name: "Native editor" }).click();
        await page.mouse.wheel(0, 12.5);
        await page.waitForFunction(() => records.wheels.length === 3);
        await page.keyboard.down("p");
        await page.keyboard.up("p");

        const records = await page.evaluate(() => window.records);
        assert.ok(Object.values(records).flat().every(record => record.trusted),
            "The browser regression did not exercise trusted native input");
        assert.deepEqual(records.wheels, [
            { sample: [0, 14.5, -8.25, 0, 0], duplicate: [-1, 0, 0, 0, 0],
                trusted: true, prevented: true, target: "CANVAS" },
            { sample: [2, 0, -24.5, 0, 0], duplicate: [-1, 0, 0, 0, 0],
                trusted: true, prevented: true, target: "CANVAS" },
            { sample: [], duplicate: [], trusted: true, prevented: false, target: "INPUT" }
        ], "Native wheel metadata expired before routing or leaked into a native editor");
        assert.deepEqual(records.pointers, [
            { captured: true, trusted: true, prevented: true, target: "CANVAS" },
            { captured: false, trusted: true, prevented: false, target: "INPUT" }
        ], "Native pointer capture missed the map or captured the native editor");
        assert.deepEqual(records.keys.filter(record => record.target === "CANVAS"), [
            { repeat: false, sample: 0, trusted: true, target: "CANVAS" },
            { repeat: true, sample: 1, trusted: true, target: "CANVAS" }
        ], "Native key-repeat metadata expired before the managed key handler");
        assert.deepEqual(records.keys.filter(record => record.target === "INPUT"), [
            { repeat: false, sample: -1, trusted: true, target: "INPUT" }
        ]);
        assert.deepEqual(await page.evaluate(() => ({
            wheel: PixelAtlasInput.takeWheel(1), repeat: PixelAtlasInput.getKeyRepeat(1)
        })), { wheel: [], repeat: -1 }, "Completed dispatches exposed stale native metadata");
        await page.evaluate(() => PixelAtlasInput.detach(1));
        console.log("Trusted browser input checks passed for wheel/pinch routing, pointer capture, key repeat and native editing.");
    } finally {
        await browser.close();
    }
}

run().catch(error => { console.error(error); process.exitCode = 1; });
