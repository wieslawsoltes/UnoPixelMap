const assert = require("node:assert/strict");
const { mkdir, writeFile } = require("node:fs/promises");
const { join } = require("node:path");

// Exercise the public menu with real input. MenuFlyoutItem accessibility peers do
// not consistently expose checked state, so the rendered backend status is the gate.
async function enableGpuRendering(page, expected = "GPU active") {
    return setGpuRendering(page, true, expected);
}

async function setGpuRendering(page, enabled, expected = enabled ? "GPU active" : "CPU rendering") {
    try {
        return await activateGpuRendering(page, enabled, expected);
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
            url: page.url(), enabled, expected, error: error.message,
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

function backendStatus(snapshot) {
    return snapshot.match(/^\s*- [^\n]*(?:GPU active|CPU fallback)[^\n]*$/m)?.[0]?.trim() || "";
}

async function activateMenuItem(page, item) {
    await item.waitFor({ state: "visible" });
    await page.getByRole("menu").waitFor({ state: "visible" });
    const settledFocus = async target => {
        const active = document.activeElement;
        if (!active?.isConnected || active.getClientRects().length === 0) return false;
        if (target && active !== target) return false;
        if (!target && !active.matches('[role="menu"], [role="menuitem"], [role="menuitemcheckbox"]')) return false;
        for (let frame = 0; frame < 2; frame++) {
            await new Promise(requestAnimationFrame);
            if (document.activeElement !== active) return false;
        }
        return true;
    };
    // Item peers can appear before Uno's opening focus reaches the popup, and
    // can live at the semantic root. Observe that initial focus before taking it.
    await page.waitForFunction(settledFocus, null);
    const menuItem = await item.elementHandle();
    assert.ok(menuItem, "The menu command has a visible element");
    try {
        await item.focus();
        await page.waitForFunction(settledFocus, menuItem);
        await page.keyboard.press("Enter");
        await item.waitFor({ state: "hidden" });
    } finally {
        await menuItem.dispose();
    }
}

async function activateGpuRendering(page, enabled, expected) {
    await page.waitForLoadState("networkidle", { timeout: 45000 });
    await page.screenshot();
    const accessibility = page.getByRole("button", { name: "Enable accessibility", exact: true });
    if (await accessibility.count()) await accessibility.evaluate(element => element.click());
    let status = backendStatus(await page.locator("body").ariaSnapshot());
    const selected = status.length > 0;
    if (selected !== enabled) {
        const menu = page.getByRole("button", { name: "Map appearance", exact: true });
        await menu.waitFor({ state: "visible" });
        const bounds = await menu.boundingBox();
        assert.ok(bounds && bounds.width > 0 && bounds.height > 0, "The View menu is visible");
        await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
        const commandName = /^GPU rendering(?: \(experimental\))?$/;
        const item = page.getByRole("menuitem", { name: commandName })
            .or(page.getByRole("menuitemcheckbox", { name: commandName }));
        await activateMenuItem(page, item);
    }
    await page.mouse.move(page.viewportSize().width / 2, 20);
    const deadline = Date.now() + 20000;
    while (Date.now() < deadline) {
        const snapshot = await page.locator("body").ariaSnapshot();
        status = backendStatus(snapshot);
        if (enabled ? status.includes(expected) : status.length === 0) {
            console.log("Rendering backend confirmed", expected);
            return status || "CPU rendering";
        }
        await page.waitForTimeout(150);
    }
    throw new Error("Expected " + expected + ". Observed " + status.trim());
}

module.exports = { activateMenuItem, enableGpuRendering, setGpuRendering };
