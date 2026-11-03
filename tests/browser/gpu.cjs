const assert = require("node:assert/strict");

// Exercise the public menu with real input. MenuFlyoutItem accessibility peers do
// not consistently expose checked state, so the rendered backend status is the gate.
async function enableGpuRendering(page, expected = "GPU active") {
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
    // Enter the menu, return to its first item, then select the second. The menu
    // may initially focus either its anchor or its first item after opening.
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Home");
    await page.keyboard.press("ArrowDown");
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
