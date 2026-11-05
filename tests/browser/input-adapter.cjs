const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

// Run the production adapter with DOM capture/routed-event ordering, including
// native input's microtask checkpoints between listeners. dispatchEvent keeps
// its caller's JavaScript stack active and does not run those checkpoints.
const microtasks = [];
const frames = new Map();
let time = 0;
let frameSequence = 0;
let routed;

class Event {
    constructor(type, values = {}) {
        Object.assign(this, {
            type, bubbles: true, cancelable: true, defaultPrevented: false, eventPhase: 0,
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
        event.eventPhase = 0;
        return !event.defaultPrevented;
    }
    notify(event, capture) {
        for (const { handler, options } of [...(this.listeners.get(event.type) ?? [])]) {
            if (event.stopped) return;
            if (!!options.capture !== capture) continue;
            event.eventPhase = this === event.target ? 2 : capture ? 1 : 3;
            handler(event);
            if (event.nativeDispatch) flushMicrotasks();
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
document.parent = window;
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
for (const type of ["wheel", "pointerdown", "pointermove", "keydown"])
    body.addEventListener(type, event => routed?.(event), { capture: true });

const source = fs.readFileSync(path.resolve(__dirname, "../../src/PixelMap.Controls/Browser/map-input.js"), "utf8");
const sandbox = { document, window, Element, WheelEvent, AbortController,
    queueMicrotask: callback => microtasks.push(callback),
    requestAnimationFrame(callback) { const id = ++frameSequence; frames.set(id, callback); return id; },
    cancelAnimationFrame: id => frames.delete(id) };
vm.runInNewContext(source, sandbox);
const adapter = sandbox.PixelAtlasInput;
const suspended = [];
const motions = [];
adapter.attach(1, () => suspended.push(1), (x, y) => motions.push({ id: 1, x, y }));
adapter.attach(1, () => assert.fail("Attaching twice replaced the map owner"));
assert.equal(document.listeners.get("wheel").length, 1);
assert.equal(document.listeners.get("wheel")[0].options.passive, false);
assert.equal(document.listeners.get("wheel")[0].options.capture, true);

function emit(event, handler, target = canvas) {
    routed = handler;
    target.dispatchEvent(event);
    routed = undefined;
    flushMicrotasks();
    return event;
}

function flushMicrotasks() {
    while (microtasks.length) microtasks.shift()();
}

function wheel(values, owner = 1) {
    let sample;
    const event = emit(new WheelEvent("wheel", values), () => { sample = [...adapter.takeWheel(owner)]; });
    assert.ok(event.defaultPrevented, "A map-owned wheel event retained its browser default");
    assert.deepEqual([...adapter.takeWheel(owner)], [], "Raw events escaped their synchronous routing lifetime");
    return sample;
}

function drawFrame() {
    const callbacks = [...frames.values()];
    frames.clear();
    for (const callback of callbacks) callback();
}

let nativeSample;
const nativeWheel = emit(new WheelEvent("wheel", {
    deltaX: 14.5, deltaY: -8.25, nativeDispatch: true, timeStamp: 500
}), () => { nativeSample = [...adapter.takeWheel(1)]; });
assert.deepEqual(nativeSample, [0, 14.5, -8.25, 0, 0],
    "A native wheel event lost its deltas at the checkpoint before Uno routing");
assert.ok(nativeWheel.defaultPrevented);
assert.deepEqual([...adapter.takeWheel(1)], [], "A completed DOM dispatch exposed stale wheel deltas");

emit(new Event("keydown", { key: "p", repeat: true, nativeDispatch: true }), () => {
    assert.equal(adapter.getKeyRepeat(1), 1,
        "A native key event lost its repeat flag at the checkpoint before Uno routing");
});
assert.equal(adapter.getKeyRepeat(1), -1);

const nativeDown = emit(new Event("pointerdown", {
    pointerId: 17, pointerType: "mouse", button: 0, nativeDispatch: true
}), () => adapter.capturePointer(1, 17));
assert.ok(nativeDown.defaultPrevented);
assert.ok(canvas.hasPointerCapture(17), "A native pointer press lost its capture target before Uno routing");
adapter.releasePointer(1, 17);

const outerWheel = new WheelEvent("wheel", { deltaX: 5, deltaY: -2, timeStamp: 600 });
const innerWheel = new WheelEvent("wheel", { deltaY: -10, ctrlKey: true });
const editorWheel = new WheelEvent("wheel", { deltaY: 30, ctrlKey: true });
emit(outerWheel, event => {
    if (event === innerWheel) {
        assert.deepEqual([...adapter.takeWheel(1)], [2, 0, -10, 0, 0]);
        return;
    }
    if (event === editorWheel) {
        assert.deepEqual([...adapter.takeWheel(1)], [], "A nested editor event inherited map wheel metadata");
        return;
    }
    canvas.dispatchEvent(innerWheel);
    assert.deepEqual([...adapter.takeWheel(1)], [0, 5, -2, 0, 0],
        "A nested wheel event replaced the outer map event");
    inputChild.dispatchEvent(editorWheel);
    assert.deepEqual([...adapter.takeWheel(1)], [-1, 0, 0, 0, 0],
        "A nested editor event lost the outer event's consumption state");
});
assert.equal(editorWheel.defaultPrevented, false);
assert.deepEqual([...adapter.takeWheel(1)], []);

const outerKey = new Event("keydown", { key: "w", repeat: true });
const editorKey = new Event("keydown", { key: "p", repeat: false });
emit(outerKey, event => {
    if (event === editorKey) {
        assert.equal(adapter.getKeyRepeat(1), -1);
        return;
    }
    inputChild.dispatchEvent(editorKey);
    assert.equal(adapter.getKeyRepeat(1), 1, "A nested editor event replaced the outer key repeat flag");
});
assert.equal(adapter.getKeyRepeat(1), -1);

const outerDown = new Event("pointerdown", { pointerId: 18, pointerType: "mouse", button: 0 });
const editorDown = new Event("pointerdown", { pointerId: 19, pointerType: "mouse", button: 0 });
emit(outerDown, event => {
    if (event === editorDown) {
        adapter.capturePointer(1, 19);
        assert.equal(inputChild.captures.size, 0, "A native editor's pointer was captured by the map");
        return;
    }
    inputChild.dispatchEvent(editorDown);
    adapter.capturePointer(1, 18);
    assert.ok(canvas.hasPointerCapture(18), "A nested editor event replaced the outer pointer capture target");
});
assert.equal(editorDown.defaultPrevented, false);
adapter.releasePointer(1, 18);
adapter.capturePointer(1, 18);
assert.equal(canvas.captures.size, 0, "A completed DOM dispatch remained available for native capture");

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

assert.equal(adapter.getKeyRepeat(1), -1);
for (const repeat of [false, true]) {
    const key = emit(new Event("keydown", { key: "w", code: "KeyW", repeat }), () => {
        assert.equal(adapter.getKeyRepeat(1), repeat ? 1 : 0, "Native keyboard repeat metadata was lost");
        assert.equal(adapter.getKeyRepeat(99), -1, "An unregistered map read keyboard metadata");
    });
    assert.equal(key.defaultPrevented, false, "The keyboard adapter consumed a UI-owned event");
    assert.equal(adapter.getKeyRepeat(1), -1, "Keyboard metadata survived beyond synchronous Uno routing");
}
const editKey = emit(new Event("keydown", { key: "p", code: "KeyP", repeat: true }), () => {
    assert.equal(adapter.getKeyRepeat(1), -1, "A native text input exposed gameplay repeat metadata");
}, inputChild);
assert.equal(editKey.defaultPrevented, false);

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

// XAML game sticks capture their own pointers, independently of map capture.
const uiTouch = emit(new Event("pointerdown", { pointerId: 61, pointerType: "touch" }));
emit(new Event("pointerdown", { pointerId: 62, pointerType: "touch" }));
assert.equal(uiTouch.defaultPrevented, false, "The adapter consumed a UI-owned touch press");
assert.equal(canvas.captures.size, 0);
const rejectTouchWheel = () => assert.fail("A parallel Safari touch gesture became a map wheel event");
assert.ok(emit(new Event("gesturestart", { scale: 1 }), rejectTouchWheel).defaultPrevented);
assert.ok(emit(new Event("gesturechange", { scale: 1.5, rotation: 20 }), rejectTouchWheel).defaultPrevented);
emit(new Event("pointerup", { pointerId: 61, pointerType: "touch" }));
assert.ok(emit(new Event("gesturechange", { scale: 1.7 }), rejectTouchWheel).defaultPrevented);
emit(new Event("pointercancel", { pointerId: 62, pointerType: "touch" }));
emit(new Event("gestureend"));
emit(new Event("pointerdown", { pointerId: 63, pointerType: "touch" }), undefined, inputChild);
assert.equal(emit(new Event("gesturestart", { scale: 1 }), rejectTouchWheel, inputChild).defaultPrevented, false,
    "A native editor touch gesture lost its browser default");
emit(new Event("gesturestart", { scale: 1 }), routeGesture);
assert.deepEqual(safari.pop(), [3, 0, 0, 0, 0], "Released UI touches or editor touches suppressed a trackpad gesture");
emit(new Event("gestureend"), routeGesture);
emit(new Event("pointerup", { pointerId: 63, pointerType: "touch" }), undefined, inputChild);

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
assert.ok(emit(new Event("gesturechange", { scale: 1.2 }), rejectTouchWheel).defaultPrevented,
    "Releasing map capture ended a finger that was still touching the canvas");
emit(new Event("pointerup", { pointerId: -37, pointerType: "touch" }));
emit(new Event("gestureend"));

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
emit(new Event("mousemove", { movementX: -13.5, movementY: 6.25 }));
emit(new Event("pointermove", { pointerId: 8, pointerType: "mouse", movementX: -13.5, movementY: 6.25 }));
emit(new Event("mousemove", { movementX: 4.5, movementY: -2.25 }));
assert.equal(frames.size, 1, "High-rate locked mouse events were not coalesced");
assert.equal(motions.length, 0);
drawFrame();
assert.deepEqual(motions, [{ id: 1, x: -9, y: 4 }],
    "Relative mouse-only movement was lost or applied again through pointer events");
emit(new Event("mousemove", { movementX: 12, movementY: 3 }));
document.exitPointerLock();
assert.equal(frames.size, 0, "Unlock retained a queued relative mouse frame");
drawFrame();
assert.equal(motions.length, 1);
assert.deepEqual(suspended, [1], "Escape did not suspend the mouse-locked game");
assert.equal(adapter.isMouseCaptured(1), false);
emit(new Event("mousemove", { movementX: 50, movementY: 30 }));
assert.equal(frames.size, 0, "An unlocked mouse bypassed managed UI hit testing");
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

adapter.attach(2, () => suspended.push(2), (x, y) => motions.push({ id: 2, x, y }));
assert.equal(document.listeners.get("wheel").length, 1, "Multiple maps installed duplicate global listeners");
emit(new Event("pointerdown", { pointerId: 9, pointerType: "touch" }), () => adapter.capturePointer(2, 90));
adapter.releasePointer(1, 90);
assert.ok(canvas.hasPointerCapture(9), "One map released another map's pointer capture");
adapter.captureMouse(2);
emit(new Event("mousemove", { movementX: 7, movementY: 1 }));
drawFrame();
assert.deepEqual(motions.at(-1), { id: 2, x: 7, y: 1 }, "Mouse movement reached a different map owner");
emit(new Event("mousemove", { movementX: 2, movementY: 8 }));
emit(new Event("blur", { bubbles: false }), undefined, inputChild);
assert.deepEqual(suspended, [1], "An internal focus transfer suspended the game");
assert.ok(canvas.hasPointerCapture(9), "An internal focus transfer released touch capture");
assert.equal(adapter.isMouseCaptured(2), true, "An internal focus transfer released relative mouse capture");
assert.equal(frames.size, 1, "An internal focus transfer discarded relative mouse motion");
window.dispatchEvent(new Event("blur", { bubbles: false }));
assert.equal(frames.size, 0, "Window deactivation retained relative mouse motion");
assert.equal(canvas.captures.size, 0);
assert.equal(document.pointerLockElement, null);
assert.deepEqual(suspended, [1, 1, 2]);
emit(new Event("gesturestart", { scale: 1 }), routeGesture);
assert.deepEqual(safari.pop(), [3, 0, 0, 0, 0], "Window deactivation retained a canvas touch");
emit(new Event("gestureend"), routeGesture);
document.hidden = false;
document.dispatchEvent(new Event("visibilitychange"));
assert.equal(suspended.length, 3);
document.hidden = true;
document.dispatchEvent(new Event("visibilitychange"));
assert.deepEqual(suspended, [1, 1, 2, 1, 2]);

adapter.detach(1);
assert.equal(document.listeners.get("wheel").length, 1);
assert.deepEqual(wheel({ deltaY: 2.5, timeStamp: 5000 }, 2), [0, 0, 2.5, 0, 0]);
adapter.captureMouse(2);
emit(new Event("mousemove", { movementX: 4, movementY: 9 }));
emit(new Event("pointerdown", { pointerId: 81, pointerType: "touch" }));
adapter.detach(2);
assert.equal(frames.size, 0, "Disposal retained a managed mouse callback");
for (const type of ["wheel", "pointerdown", "pointermove", "pointerup", "pointercancel", "mousemove", "keydown", "gesturestart", "gesturechange", "gestureend", "contextmenu", "pointerlockchange", "pointerlockerror", "visibilitychange"])
    assert.equal(document.listeners.get(type).length, 0, `Disposal retained a ${type} listener`);
assert.equal(window.listeners.get("blur").length, 0);
assert.equal(adapter.captureMouse(2), false);
adapter.attach(3, () => suspended.push(3), (x, y) => motions.push({ id: 3, x, y }));
assert.equal(document.listeners.get("wheel").length, 1);
assert.deepEqual(wheel({ deltaY: -5, ctrlKey: true }, 3), [2, 0, -5, 0, 0]);
emit(new Event("gesturestart", { scale: 1 }), () => safari.push([...adapter.takeWheel(3)]));
assert.deepEqual(safari.pop(), [3, 0, 0, 0, 0], "Detaching the last map retained an old canvas touch");
emit(new Event("gestureend"));
adapter.detach(3);

// Native grants can arrive after release/disposal. Control both event delivery
// and Promise settlement so a stale completion can be tested against a new owner.
const immediatePointerLock = canvas.requestPointerLock;
const deferredLocks = [];
let promiseLocks = true;
canvas.requestPointerLock = () => {
    const request = {};
    deferredLocks.push(request);
    if (!promiseLocks) return;
    return { then(resolve, reject) { request.resolve = resolve; request.reject = reject; } };
};
function grantDeferredLock() {
    document.pointerLockElement = canvas;
    document.dispatchEvent(new Event("pointerlockchange"));
}

document.hidden = false;
adapter.attach(4, () => suspended.push(4), () => {});
assert.equal(adapter.captureMouse(4), true);
adapter.releaseMouse(4);
grantDeferredLock();
deferredLocks[0].resolve();
assert.equal(document.pointerLockElement, null, "A grant arriving after release left the browser locked");
assert.equal(adapter.isMouseCaptured(4), false);

adapter.captureMouse(4);
adapter.releaseMouse(4);
adapter.detach(4);
adapter.attach(5, () => suspended.push(5), () => {});
adapter.captureMouse(5);
assert.equal(deferredLocks.length, 2, "A pending canvas request was duplicated during ownership transfer");
grantDeferredLock();
assert.equal(adapter.isMouseCaptured(5), true, "A late grant discarded the new authorized owner");
adapter.releaseMouse(4);
assert.equal(adapter.isMouseCaptured(5), true, "A disposed owner released another map's lock");
adapter.releaseMouse(5);

adapter.captureMouse(5);
deferredLocks[1].resolve();
deferredLocks[2].reject();
adapter.captureMouse(5);
document.dispatchEvent(new Event("pointerlockerror"));
grantDeferredLock();
deferredLocks[3].resolve();
assert.equal(adapter.isMouseCaptured(5), true, "A stale completion or error event invalidated a newer request");
adapter.releaseMouse(5);

adapter.captureMouse(5);
adapter.detach(5);
grantDeferredLock();
deferredLocks[4].resolve();
assert.equal(document.pointerLockElement, null, "Disposal failed to release a pending Promise-based grant");
assert.equal(document.listeners.get("pointerlockchange").length, 0);
assert.equal(document.listeners.get("pointerlockerror").length, 0);

promiseLocks = false;
adapter.attach(6, () => suspended.push(6), () => {});
adapter.captureMouse(6);
adapter.detach(6);
grantDeferredLock();
assert.equal(document.pointerLockElement, null, "Disposal failed to release a pending event-only grant");
assert.equal(document.listeners.get("pointerlockchange").length, 0);
assert.equal(document.listeners.get("pointerlockerror").length, 0);
canvas.requestPointerLock = immediatePointerLock;

console.log("Browser input checks passed for map-scoped pinch, two-axis trackpad motion, wheel units, Safari gestures, pointer capture/lock, UI pass-through and disposal.");
