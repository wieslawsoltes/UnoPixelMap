const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

// Run the production adapter with synchronous capture/routed-event ordering.
// The physical browser suite separately exercises Uno's native hit testing.
const microtasks = [];
let time = 0;
let routed;

class Event {
    constructor(type, values = {}) {
        Object.assign(this, {
            type, bubbles: true, cancelable: true, defaultPrevented: false,
            clientX: 420, clientY: 350, timeStamp: ++time,
            ctrlKey: false, metaKey: false, shiftKey: false, altKey: false
        }, values);
    }
    preventDefault() { if (this.cancelable) this.defaultPrevented = true; }
    stopImmediatePropagation() { this.stopped = true; }
}

class WheelEvent extends Event {
    static DOM_DELTA_PIXEL = 0;
    static DOM_DELTA_LINE = 1;
    static DOM_DELTA_PAGE = 2;
    constructor(type, values = {}) { super(type, { deltaX: 0, deltaY: 0, deltaMode: 0, ...values }); }
}

class EventTarget {
    listeners = new Map();
    addEventListener(type, handler, options = {}) {
        const listener = { handler, options };
        const collection = this.listeners.get(type) ?? [];
        collection.push(listener);
        this.listeners.set(type, collection);
        options.signal?.addEventListener("abort", () => collection.splice(collection.indexOf(listener), 1), { once: true });
    }
    dispatchEvent(event) {
        event.target ??= this;
        const ancestors = [];
        for (let target = this; target; target = target.parent) ancestors.push(target);
        for (const target of [...ancestors].reverse()) target.notify(event, true);
        for (const target of ancestors) {
            target.notify(event, false);
            if (!event.bubbles) break;
        }
        return !event.defaultPrevented;
    }
    notify(event, capture) {
        for (const { handler, options } of [...(this.listeners.get(event.type) ?? [])]) {
            if (event.stopped) return;
            if (!!options.capture === capture) handler(event);
        }
    }
}

class Element extends EventTarget {
    captures = new Set();
    nativeInput = false;
    closest() {
        for (let element = this; element instanceof Element; element = element.parent)
            if (element.nativeInput) return element;
        return null;
    }
    setPointerCapture(pointerId) { this.captures.add(pointerId); }
    hasPointerCapture(pointerId) { return this.captures.has(pointerId); }
    releasePointerCapture(pointerId) { this.captures.delete(pointerId); }
    requestPointerLock() {
        if (this.lockError) throw this.lockError;
        if (this.legacyLockError) {
            document.dispatchEvent(new Event("pointerlockerror"));
            return;
        }
        document.pointerLockElement = this;
        document.dispatchEvent(new Event("pointerlockchange"));
    }
}

const document = new EventTarget();
const window = new EventTarget();
const body = new Element();
const canvas = new Element();
const input = new Element();
const inputChild = new Element();
document.body = body;
document.documentElement = { clientWidth: 1200, clientHeight: 800 };
document.pointerLockElement = null;
document.hidden = false;
document.getElementById = id => id === "uno-canvas" ? canvas : null;
document.exitPointerLock = () => {
    document.pointerLockElement = null;
    document.dispatchEvent(new Event("pointerlockchange"));
};
body.parent = document;
canvas.parent = input.parent = body;
inputChild.parent = input;
input.nativeInput = true;
for (const type of ["wheel", "pointerdown", "pointermove"])
    body.addEventListener(type, event => routed?.(event), { capture: true });

const source = fs.readFileSync(path.resolve(__dirname, "../../src/PixelMap.Controls/Browser/map-input.js"), "utf8");
const sandbox = { document, window, Element, WheelEvent, AbortController,
    queueMicrotask: callback => microtasks.push(callback) };
vm.runInNewContext(source, sandbox);
const adapter = sandbox.PixelAtlasInput;
const suspended = [];
adapter.attach(1, () => suspended.push(1));
adapter.attach(1, () => assert.fail("Attaching twice replaced the map owner"));
assert.equal(document.listeners.get("wheel").length, 1);
assert.equal(document.listeners.get("wheel")[0].options.passive, false);
assert.equal(document.listeners.get("wheel")[0].options.capture, true);

function emit(event, handler, target = canvas) {
    routed = handler;
    target.dispatchEvent(event);
    routed = undefined;
    while (microtasks.length) microtasks.shift()();
    return event;
}

function wheel(values, owner = 1) {
    let sample;
    const event = emit(new WheelEvent("wheel", values), () => { sample = [...adapter.takeWheel(owner)]; });
    assert.ok(event.defaultPrevented, "A map-owned wheel event retained its browser default");
    assert.deepEqual([...adapter.takeWheel(owner)], [], "Raw events escaped their synchronous routing lifetime");
    return sample;
}

assert.deepEqual(wheel({ deltaY: 100, wheelDeltaY: -120, timeStamp: 1000 }), [1, 0, 100, 0, 0]);
assert.deepEqual(wheel({ deltaY: -18.25, ctrlKey: true, timeStamp: 1400 }), [2, 0, -18.25, 0, 0]);
assert.deepEqual(wheel({ deltaY: 12, metaKey: true, timeStamp: 1500 }), [2, 0, 12, 0, 0]);
assert.deepEqual(wheel({ deltaY: 3, deltaMode: 1, timeStamp: 2000 }), [1, 0, 96, 0, 0]);
assert.deepEqual(wheel({ deltaX: .5, deltaY: -1, deltaMode: 2, timeStamp: 2400 }), [0, 600, -800, 0, 0]);
assert.deepEqual(wheel({ deltaY: 100, deltaMode: 2, timeStamp: 2800 }), [1, 0, 2000, 0, 0]);
assert.deepEqual(wheel({ deltaX: -7.25, deltaY: 9.5, shiftKey: true, altKey: true, timeStamp: 3200 }),
    [0, -7.25, 9.5, 1, 1]);

const diagonal = [];
emit(new WheelEvent("wheel", { deltaX: 8.5, deltaY: -12.25, timeStamp: 3600 }), () => {
    diagonal.push([...adapter.takeWheel(1)], [...adapter.takeWheel(1)]);
});
assert.deepEqual(diagonal, [[0, 8.5, -12.25, 0, 0], [-1, 0, 0, 0, 0]],
    "Uno's separate X/Y notifications applied a diagonal browser event twice");

assert.deepEqual(wheel({ deltaY: 3.5, wheelDeltaY: -10.5, timeStamp: 4000 }), [0, 0, 3.5, 0, 0]);
assert.deepEqual(wheel({ deltaY: 40, wheelDeltaY: -120, timeStamp: 4016 }), [0, 0, 40, 0, 0],
    "A continuous trackpad burst changed into wheel zoom at an integral delta");
assert.deepEqual(wheel({ deltaY: .75, timeStamp: 4032 }), [0, 0, .75, 0, 0]);
assert.deepEqual(wheel({ deltaY: 100, wheelDeltaY: -120, timeStamp: 4400 }), [1, 0, 100, 0, 0]);

let unowned = emit(new WheelEvent("wheel", { deltaY: 50, ctrlKey: true }));
assert.equal(unowned.defaultPrevented, false, "A XAML overlay's wheel event was consumed before managed hit testing");
unowned = emit(new WheelEvent("wheel", { deltaY: 50, ctrlKey: true }), () => {
    assert.deepEqual([...adapter.takeWheel(1)], []);
}, inputChild);
assert.equal(unowned.defaultPrevented, false, "An editable HTML descendant lost native input behavior");

const safari = [];
const routeGesture = () => safari.push([...adapter.takeWheel(1)]);
let gesture = emit(new Event("gesturestart", { scale: 1, rotation: 0 }), routeGesture);
assert.ok(gesture.defaultPrevented);
assert.deepEqual(safari.pop(), [3, 0, 0, 0, 0]);
gesture = emit(new Event("gesturechange", { scale: 2, rotation: 25 }), routeGesture);
assert.ok(gesture.defaultPrevented);
assert.deepEqual(safari.pop(), [3, 1, -25, 0, 0]);
gesture = emit(new Event("gesturechange", { scale: 4, rotation: 40 }), routeGesture);
assert.deepEqual(safari.pop(), [3, 1, -15, 0, 0], "Safari scale or twist accumulated the full gesture twice");
const duplicate = emit(new WheelEvent("wheel", { ctrlKey: true, deltaY: -15 }), () => {
    assert.fail("Safari's duplicate ctrl-wheel reached Uno after its GestureEvent was owned");
});
assert.ok(duplicate.defaultPrevented);
assert.ok(emit(new Event("gestureend"), routeGesture).defaultPrevented);
assert.equal(emit(new Event("gesturestart", { scale: 1 })).defaultPrevented, false);
assert.equal(emit(new Event("gestureend")).defaultPrevented, false);

const down = emit(new Event("pointerdown", { pointerId: -37, pointerType: "touch" }), () => {
    adapter.capturePointer(1, 4294967259);
});
assert.ok(down.defaultPrevented);
assert.ok(canvas.hasPointerCapture(-37), "The managed pointer id replaced Safari's native pointer id");
assert.ok(emit(new Event("gesturestart", { scale: 1 }), () => {
    assert.fail("Touch pointer pinch was applied again through Safari GestureEvents");
}).defaultPrevented);
adapter.releasePointer(1, 4294967259);
assert.equal(canvas.captures.size, 0);

emit(new Event("pointerdown", { pointerId: 10, pointerType: "mouse", button: 2 }),
    () => adapter.capturePointer(1, 10));
adapter.releasePointer(1, 10);
assert.ok(emit(new Event("contextmenu", { button: 2 })).defaultPrevented,
    "A map-owned right-button orbit opened the browser context menu");
assert.equal(emit(new Event("contextmenu", { button: 0 })).defaultPrevented, false);
emit(new Event("pointerdown", { pointerId: 11, pointerType: "mouse", button: 2 }));
assert.equal(emit(new Event("contextmenu", { button: 2 })).defaultPrevented, false,
    "A UI-owned right button inherited the map's context-menu suppression");

assert.equal(adapter.captureMouse(1), true);
assert.equal(adapter.isMouseCaptured(1), true);
emit(new Event("pointermove", { pointerId: 8, pointerType: "mouse", movementX: -13.5, movementY: 6.25 }), () => {
    assert.deepEqual([...adapter.takeMouseMotion(1)], [-13.5, 6.25]);
    assert.deepEqual([...adapter.takeMouseMotion(1)], []);
});
document.exitPointerLock();
assert.deepEqual(suspended, [1], "Escape did not suspend the mouse-locked game");
assert.equal(adapter.isMouseCaptured(1), false);
canvas.lockError = new Error("Pointer lock denied");
assert.equal(adapter.captureMouse(1), false);
canvas.lockError = null;
canvas.legacyLockError = true;
adapter.captureMouse(1);
assert.equal(adapter.isMouseCaptured(1), false, "An event-only pointer-lock rejection retained capture state");
canvas.legacyLockError = false;
adapter.captureMouse(1);
adapter.releaseMouse(1);
assert.deepEqual(suspended, [1], "An intentional pointer-lock release invoked an unexpected pause");

adapter.attach(2, () => suspended.push(2));
assert.equal(document.listeners.get("wheel").length, 1, "Multiple maps installed duplicate global listeners");
emit(new Event("pointerdown", { pointerId: 9, pointerType: "touch" }), () => adapter.capturePointer(2, 90));
adapter.releasePointer(1, 90);
assert.ok(canvas.hasPointerCapture(9), "One map released another map's pointer capture");
adapter.captureMouse(2);
window.dispatchEvent(new Event("blur"));
assert.equal(canvas.captures.size, 0);
assert.equal(document.pointerLockElement, null);
assert.deepEqual(suspended, [1, 1, 2]);
document.hidden = false;
document.dispatchEvent(new Event("visibilitychange"));
assert.equal(suspended.length, 3);
document.hidden = true;
document.dispatchEvent(new Event("visibilitychange"));
assert.deepEqual(suspended, [1, 1, 2, 1, 2]);

adapter.detach(1);
assert.equal(document.listeners.get("wheel").length, 1);
assert.deepEqual(wheel({ deltaY: 2.5, timeStamp: 5000 }, 2), [0, 0, 2.5, 0, 0]);
adapter.detach(2);
for (const type of ["wheel", "pointerdown", "pointermove", "gesturestart", "gesturechange", "gestureend", "contextmenu", "pointerlockchange", "pointerlockerror", "visibilitychange"])
    assert.equal(document.listeners.get(type).length, 0, `Disposal retained a ${type} listener`);
assert.equal(window.listeners.get("blur").length, 0);
assert.equal(adapter.captureMouse(2), false);
adapter.attach(3, () => suspended.push(3));
assert.equal(document.listeners.get("wheel").length, 1);
assert.deepEqual(wheel({ deltaY: -5, ctrlKey: true }, 3), [2, 0, -5, 0, 0]);
adapter.detach(3);

console.log("Browser input checks passed for map-scoped pinch, two-axis trackpad motion, wheel units, Safari gestures, pointer capture/lock, UI pass-through and disposal.");
