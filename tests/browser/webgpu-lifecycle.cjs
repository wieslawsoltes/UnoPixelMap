// Subscription lifecycle checks. These exercise the reusable control asset, not
// a GPU implementation; webgpu.cjs separately verifies actual browser device loss.
const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const { runInNewContext } = require("node:vm");

const script = readFileSync(join(__dirname, "../../src/PixelMap.Controls/Browser/map-input.js"), "utf8");
const html = readFileSync(join(__dirname, "../../src/PixelMap.App/Platforms/WebAssembly/index.html"), "utf8");
const applicationScript = html.match(/<script>([\s\S]*?)<\/script>/)?.[1];
assert.ok(applicationScript, "The sample application provides its optional graphics recovery UI");

function environment(includeApplication = true) {
    const windowEvents = new EventTarget();
    const nodes = new Map();
    const log = [];
    class Adapter { requestDevice() { throw new Error("A lifecycle observer must not request devices"); } }
    const requestDevice = Adapter.prototype.requestDevice;
    const document = {
        readyState: "complete",
        documentElement: { dataset: {} },
        addEventListener() {},
        getElementById(id) {
            if (!nodes.has(id)) nodes.set(id, { hidden: true, textContent: "", focus() {} });
            return nodes.get(id);
        }
    };
    const host = { document, window: windowEvents, GPUAdapter: Adapter, queueMicrotask,
        addEventListener: windowEvents.addEventListener.bind(windowEvents),
        console: { error: (...args) => log.push(args) } };
    runInNewContext(script, host, { filename: "map-input.js" });
    if (includeApplication) runInNewContext(applicationScript, host, { filename: "PixelAtlasGraphics" });
    assert.equal(Adapter.prototype.requestDevice, requestDevice, "Observation must not patch global device creation");
    return { adapter: host.PixelAtlasInput, graphics: host.PixelAtlasGraphics,
        diagnostics: host.PixelAtlasWebGpu, document, log, pagehide(persisted) {
        const event = new Event("pagehide");
        event.persisted = persisted;
        windowEvents.dispatchEvent(event);
    } };
}

class DeviceEvents extends EventTarget {
    constructor() {
        super();
        this.listeners = new Set();
        this.lossSubscriptions = 0;
        this.destroyCalls = 0;
        const lost = new Promise(resolve => { this.completeLoss = resolve; });
        this.lost = { then: callback => { this.lossSubscriptions++; return lost.then(callback); } };
    }
    addEventListener(type, handler, options) {
        if (type === "uncapturederror") this.listeners.add(handler);
        super.addEventListener(type, handler, options);
        if (this.failAttachment) throw new Error("Attachment failed");
    }
    removeEventListener(type, handler, options) {
        if (type === "uncapturederror") this.listeners.delete(handler);
        super.removeEventListener(type, handler, options);
    }
    lose(message = "Device was destroyed.") { this.completeLoss({ reason: "destroyed", message }); }
    error(message) {
        const event = new Event("uncapturederror");
        event.error = { message };
        this.dispatchEvent(event);
    }
    destroy() { this.destroyCalls++; this.lose(); }
}

const flush = () => new Promise(resolve => queueMicrotask(resolve));

(async () => {
    {
        const { adapter, graphics } = environment();
        const old = new DeviceEvents(), current = new DeviceEvents(), unrelated = new DeviceEvents();
        const callbacks = [];
        for (let i = 0; i < 100; i++) {
            const subscription = adapter.observeWebGpuDevice(old, value => callbacks.push(value));
            assert.equal(old.listeners.size, 1);
            adapter.stopObservingWebGpuDevice(subscription);
            adapter.stopObservingWebGpuDevice(subscription);
            assert.equal(subscription.failed, null, "Detach releases the managed delegate");
            assert.equal(subscription.record, null, "Detach releases the device record");
            assert.equal(old.listeners.size, 0);
        }
        assert.equal(old.lossSubscriptions, 1, "Reloads must not accumulate pending lost-promise handlers");
        adapter.observeWebGpuDevice(current, value => callbacks.push(value));
        old.lose();
        unrelated.lose();
        old.error("Stale error");
        unrelated.error("Unrelated error");
        await flush();
        assert.equal(callbacks.length, 0, "Retired and unrelated devices cannot fail the active renderer");
        assert.equal(graphics.failure, null);
        current.lose();
        await flush();
        assert.equal(callbacks.length, 1, "The shared device's loss must notify managed rendering");
        assert.match(callbacks[0], /device was lost/i);
        assert.equal(graphics.state, "unavailable");
        assert.equal(current.listeners.size, 0, "Failure detaches uncaptured-error listeners");
        current.error("Later error");
        assert.equal(callbacks.length, 1, "Failure notification is one-shot");
        assert.equal(old.destroyCalls + current.destroyCalls + unrelated.destroyCalls, 0,
            "Observing and detaching never destroy a borrowed device");
    }
    {
        const { adapter, graphics } = environment();
        const device = new DeviceEvents();
        let first = 0, second = 0;
        const a = adapter.observeWebGpuDevice(device, () => { first++; adapter.stopObservingWebGpuDevice(a); });
        adapter.observeWebGpuDevice(device, () => { second++; });
        assert.equal(device.listeners.size, 1, "A shared device uses one error listener");
        device.error("Invalid bind group");
        assert.equal(first, 1);
        assert.equal(second, 1);
        assert.match(graphics.failure, /Invalid bind group/);
        assert.equal(device.listeners.size, 0);
        device.lose();
        await flush();
        assert.equal(first + second, 2);
        let late = 0;
        const cancelled = adapter.observeWebGpuDevice(device, () => { late++; });
        adapter.stopObservingWebGpuDevice(cancelled);
        await flush();
        assert.equal(late, 0, "A detached late subscriber cannot receive a queued loss");
        adapter.observeWebGpuDevice(device, () => { late++; });
        await flush();
        assert.equal(late, 1, "Attaching to an already-lost device must fail managed initialization");
        assert.equal(device.lossSubscriptions, 1);
    }
    {
        const { adapter, graphics, document, log } = environment();
        const device = new DeviceEvents();
        adapter.observeWebGpuDevice(device, () => { throw new Error("Managed runtime is unavailable"); });
        device.error("Validation failed");
        assert.equal(device.listeners.size, 0);
        assert.equal(document.getElementById("pixelmap-graphics-error").hidden, false,
            "HTML diagnostics survive a failed managed callback");
        assert.match(graphics.failure, /Validation failed/);
        assert.equal(log.length, 1);
        const broken = new DeviceEvents();
        broken.failAttachment = true;
        let called = false;
        assert.throws(() => adapter.observeWebGpuDevice(broken, () => { called = true; }), /Attachment failed/);
        assert.equal(broken.listeners.size, 0, "Partially attached observers must be cleaned up");
        broken.lose();
        await flush();
        assert.equal(called, false);
    }
    {
        const { adapter, graphics, diagnostics } = environment(false);
        assert.equal(graphics, undefined, "A reusable control host does not need the sample application's globals");
        const device = new DeviceEvents();
        let failure;
        const subscription = adapter.observeWebGpuDevice(device, message => {
            failure = message;
            adapter.stopObservingWebGpuDevice(subscription);
            adapter.failWebGpu(message);
        });
        device.lose();
        await flush();
        assert.match(failure, /device was lost/i, "Device loss reaches managed rendering without application HTML");
        assert.equal(diagnostics.failure, failure, "The control's failure import remains available in a bare host");
        assert.equal(subscription.active, false);
        assert.equal(subscription.failed, null);
        assert.equal(device.listeners.size, 0);
        assert.equal(device.destroyCalls, 0);
    }
    {
        const { adapter, graphics, diagnostics, log } = environment(false);
        const device = new DeviceEvents();
        adapter.observeWebGpuDevice(device, () => { throw new Error("Managed runtime is unavailable"); });
        device.error("Bare-host validation failed");
        assert.equal(graphics, undefined);
        assert.match(diagnostics.failure, /Bare-host validation failed/,
            "Failure diagnostics survive managed teardown without an optional HTML callback");
        assert.equal(device.listeners.size, 0);
        assert.equal(log.length, 1);
    }
    {
        const { adapter, graphics, pagehide } = environment();
        const device = new DeviceEvents();
        let calls = 0;
        adapter.observeWebGpuDevice(device, () => { calls++; });
        pagehide(true);
        assert.equal(device.listeners.size, 1, "A page retained for history keeps its live observer");
        pagehide(false);
        assert.equal(device.listeners.size, 0, "Page teardown detaches live observers");
        device.lose();
        await flush();
        assert.equal(calls, 0);
        assert.equal(adapter.observeWebGpuDevice(new DeviceEvents(), () => { calls++; }).active, false);
    }
    console.log("Passed scoped WebGPU observer lifecycle checks: shared-device identity, managed callback, deterministic detach, reload reuse, pending-loss cancellation, optional application UI, error fallback and page teardown.");
})().catch(error => { console.error(error); process.exitCode = 1; });
