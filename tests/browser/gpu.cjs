const assert = require("node:assert/strict");
const { mkdir, writeFile } = require("node:fs/promises");
const { join } = require("node:path");

// Exercise the public menu with real input. MenuFlyoutItem accessibility peers do
// not consistently expose checked state, so the rendered backend status is the gate.
async function enableGpuRendering(page, expected = "GPU active") {
    try {
        return await activateGpuRendering(page, expected);
    } catch (error) {
        const [focus, accessibility] = await Promise.allSettled([
            page.evaluate(() => {
                const element = document.activeElement;
                return {
                    documentFocused: document.hasFocus(), visibility: document.visibilityState,
                    tag: element?.tagName, id: element?.id, role: element?.getAttribute("role"),
                    name: element?.getAttribute("aria-label"),
                    activeDescendant: element?.getAttribute("aria-activedescendant"),
                    html: element?.outerHTML
                };
            }),
            page.locator("body").ariaSnapshot({ timeout: 5000 })
        ]);
        const diagnostics = {
            url: page.url(), expected, error: error.message,
            focus: focus.status === "fulfilled" ? focus.value : String(focus.reason),
            accessibility: accessibility.status === "fulfilled" ? accessibility.value : String(accessibility.reason)
        };
        console.error("GPU activation failure", JSON.stringify(diagnostics, null, 2));
        if (process.env.PIXELMAP_SHOTS) {
            try {
                await mkdir(process.env.PIXELMAP_SHOTS, { recursive: true });
                const prefix = join(process.env.PIXELMAP_SHOTS, "gpu-activation-failure");
                await Promise.allSettled([
                    writeFile(prefix + ".json", JSON.stringify(diagnostics, null, 2)),
                    page.screenshot({ path: prefix + ".png", timeout: 10000 })
                ]);
            } catch (captureError) {
                console.error("GPU failure capture", captureError.message);
            }
        }
        throw error;
    }
}

async function activateGpuRendering(page, expected) {
    await page.waitForLoadState("networkidle", { timeout: 45000 });
    await page.screenshot();
    const accessibility = page.getByRole("button", { name: "Enable accessibility", exact: true });
    if (await accessibility.count()) await accessibility.evaluate(element => element.click());
    const menu = page.getByRole("button", { name: "Map appearance", exact: true });
    await menu.waitFor({ state: "visible" });
    const bounds = await menu.boundingBox();
    assert.ok(bounds && bounds.width > 0 && bounds.height > 0, "The View menu is visible");
    await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    const item = page.getByRole("menuitem", { name: "GPU rendering (experimental)", exact: true })
        .or(page.getByRole("menuitemcheckbox", { name: "GPU rendering (experimental)", exact: true }));
    await item.waitFor({ state: "visible" });
    // Uno may initially focus the anchor or the first menu item. Route focus to
    // the intended public command before sending real keyboard input.
    await item.focus();
    const menuItem = await item.elementHandle();
    assert.ok(menuItem, "The GPU rendering command has a visible element");
    try {
        await page.waitForFunction(element => document.activeElement === element, menuItem);
    } finally {
        await menuItem.dispose();
    }
    await page.keyboard.press("Enter");
    await item.waitFor({ state: "hidden" });
    await page.mouse.move(page.viewportSize().width / 2, 20);
    const deadline = Date.now() + 20000;
    let status = "No backend status exposed";
    while (Date.now() < deadline) {
        const snapshot = await page.locator("body").ariaSnapshot();
        status = snapshot.match(/^\s*- [^\n]*(?:GPU active|CPU fallback)[^\n]*$/m)?.[0] || status;
        if (status.includes(expected)) {
            console.log("Rendering backend confirmed", expected);
            return status.trim();
        }
        await page.waitForTimeout(150);
    }
    throw new Error("Expected " + expected + ". Observed " + status.trim());
}

module.exports = { enableGpuRendering };
