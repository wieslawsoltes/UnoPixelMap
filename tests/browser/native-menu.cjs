// Native popup activation shared by integration and performance checks.
// Uno semantic peers can become visible before layout/focus restoration settles.
const assert = require("node:assert/strict");

async function clickTarget(page, target, name) {
    await target.waitFor({state: "visible"});
    // A semantic peer can appear before Uno paints the corresponding popup.
    // Let its layout reach the native hit-test surface before reading the rect.
    // This barrier is outside every measured input/rendering window.
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const bounds = await target.boundingBox();
    assert.ok(bounds?.width > 0 && bounds?.height > 0, name + " has a native input target");
    const size = page.viewportSize();
    const left = Math.max(0, bounds.x), right = Math.min(size.width, bounds.x + bounds.width);
    const top = Math.max(0, bounds.y), bottom = Math.min(size.height, bounds.y + bounds.height);
    assert.ok(right > left && bottom > top, name + " intersects the viewport");
    await page.mouse.click((left + right) / 2, (top + bottom) / 2);
}
async function click(page, name) {
    const target = page.getByRole("button", {name, exact: true, disabled: false})
        .or(page.getByRole("checkbox", {name, exact: true, disabled: false}));
    await clickTarget(page, target, name);
}
async function menu(page, name, submenu) {
    await click(page, "Map appearance");
    const item = text => page.getByRole("menuitem", {name: text, exact: true})
        .or(page.getByRole("menuitemcheckbox", {name: text, exact: true}));
    // The redesigned compact menu groups appearance choices. A frozen benchmark
    // harness also runs against the earlier flat menu, so discover the public
    // group before opening it; never infer application layout from a revision.
    if (!submenu) {
        const group = /^(Pixel art|Normal|Minecraft|Roblox) rendering$/.test(name) ? "Rendering style" :
            ["Daylight", "Golden hour", "After dark"].includes(name) ? "Lighting" :
            /^(Perspective lens|Camera angles and orbit|Rotate (left|right) 45 degrees|Switch to .+ view)$/.test(name) ? "Camera" : null;
        if (group) {
            const groupTarget = item(group);
            // Wait for the popup's public rows before deciding which layout is
            // present. An immediate visibility query can race peer creation.
            await groupTarget.or(item(name)).first().waitFor({state: "visible"});
            if (await groupTarget.isVisible()) submenu = group;
        }
    }
    // Popup opening/closure can restore an earlier DOM focus after a peer has
    // already gained focus. Native pointer activation addresses the painted row
    // directly, so setup does not wait for or send a key to that transient peer.
    if (submenu) await clickTarget(page, item(submenu), submenu);
    const command = item(name);
    await clickTarget(page, command, name);
    await command.waitFor({state: "hidden"});
    await page.mouse.move(page.viewportSize().width / 2, 20);
    // A dismissed popup alone is not proof that its command ran. Verify the
    // public effect as well, including when selecting the already-active preset.
    const quality = /^(Balanced|High|Ultra) quality$/.exec(name)?.[1];
    if (quality) await page.waitForFunction(expected => globalThis.PixelAtlasWebGpu?.quality === expected, quality);
    else if (name === "Share this view")
        await page.getByRole("textbox", {name: "Shareable map link", exact: true}).waitFor({state: "visible"});
}

module.exports = {clickTarget, click, menu};
