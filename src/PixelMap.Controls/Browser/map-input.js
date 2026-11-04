// Keep browser deltas intact until Uno routes the event to the map. Managed hit
// testing owns the gesture, including every XAML control drawn over the canvas.
(() => {
    "use strict";

    const maps = new Map();
    const captures = new Map();
    const gestures = new WeakMap();
    let position = { x: 0, y: 0, target: null };
    let listeners, wheel, pointer, keyboard, safariGesture, contextPointer, lockOwner = 0;

    function nativeInput(target) {
        return target instanceof Element && target.closest("input,textarea,select,iframe,[contenteditable]");
    }

    function retainWheel(event) {
        if (nativeInput(event.target)) return;
        position = { x: event.clientX, y: event.clientY, target: event.target };
        if (safariGesture?.owned && event.ctrlKey && !gestures.has(event)) {
            event.preventDefault();
            event.stopImmediatePropagation();
            return;
        }
        const current = wheel = { event, owner: 0 };
        queueMicrotask(() => { if (wheel === current) wheel = null; });
    }

    function retainPointer(event) {
        if (event.type === "pointerdown") contextPointer = null;
        position = { x: event.clientX, y: event.clientY, target: event.target };
        const current = pointer = { event, consumed: false };
        queueMicrotask(() => { if (pointer === current) pointer = null; });
    }

    function retainKeyboard(event) {
        if (nativeInput(event.target)) { keyboard = null; return; }
        keyboard = event;
        queueMicrotask(() => { if (keyboard === event) keyboard = null; });
    }

    function releasePointer(id, pointerId) {
        const capture = captures.get(pointerId);
        if (!capture || capture.owner !== id) return;
        captures.delete(pointerId);
        if (capture.element.hasPointerCapture(capture.pointerId)) capture.element.releasePointerCapture(capture.pointerId);
    }

    function releaseMouse(id) {
        if (lockOwner !== id) return;
        lockOwner = 0;
        if (document.pointerLockElement) document.exitPointerLock();
    }

    function releasePointers(id) {
        for (const [pointerId, capture] of [...captures])
            if (capture.owner === id) releasePointer(id, pointerId);
    }

    function suspend() {
        safariGesture = contextPointer = keyboard = null;
        for (const [id, map] of [...maps]) {
            releaseMouse(id);
            releasePointers(id);
            map.suspend();
        }
    }

    function safari(event) {
        if (nativeInput(event.target)) return;
        // Touch PointerEvents already drive Uno's two-finger gesture. Safari's
        // parallel GestureEvents must only cancel browser zoom in this case.
        if ([...captures.values()].some(capture => capture.pointerType === "touch")) {
            event.preventDefault();
            return;
        }
        if (event.type === "gesturestart")
            safariGesture = { scale: 1, rotation: 0, owned: false,
                target: event.target instanceof Element ? event.target : position.target ?? document.body };
        const gesture = safariGesture;
        if (!gesture) return;
        if (event.type === "gestureend") {
            if (gesture.owned) event.preventDefault();
            safariGesture = null;
            return;
        }
        const scale = Number.isFinite(event.scale) && event.scale > 0 ? event.scale : gesture.scale;
        const rotation = Number.isFinite(event.rotation) ? event.rotation : gesture.rotation;
        const delta = [Math.log2(scale / gesture.scale), gesture.rotation - rotation];
        const routed = new WheelEvent("wheel", {
            bubbles: true, cancelable: true, ctrlKey: true,
            clientX: Number.isFinite(event.clientX) ? event.clientX : position.x,
            clientY: Number.isFinite(event.clientY) ? event.clientY : position.y,
            // Keep the zero-delta start visible to native wheel routers too.
            deltaY: delta[0] === 0 ? Number.EPSILON : -delta[0] * 100
        });
        gestures.set(routed, delta);
        gesture.target.dispatchEvent(routed);
        gesture.owned ||= routed.defaultPrevented;
        if (gesture.owned) event.preventDefault();
        gesture.scale = scale;
        gesture.rotation = rotation;
    }

    function subscribe() {
        listeners = new AbortController();
        const options = { capture: true, passive: false, signal: listeners.signal };
        document.addEventListener("wheel", retainWheel, options);
        document.addEventListener("pointerdown", retainPointer, options);
        document.addEventListener("pointermove", retainPointer, options);
        document.addEventListener("keydown", retainKeyboard, options);
        document.addEventListener("gesturestart", safari, options);
        document.addEventListener("gesturechange", safari, options);
        document.addEventListener("gestureend", safari, options);
        document.addEventListener("contextmenu", event => {
            if (contextPointer && maps.has(contextPointer.owner) && event.button === 2 && !nativeInput(event.target))
                event.preventDefault();
        }, options);
        document.addEventListener("pointerlockchange", () => {
            if (document.pointerLockElement || !lockOwner) return;
            const previous = lockOwner;
            lockOwner = 0;
            maps.get(previous)?.suspend();
        }, options);
        document.addEventListener("pointerlockerror", () => {
            if (!document.pointerLockElement) lockOwner = 0;
        }, options);
        document.addEventListener("visibilitychange", () => {
            if (document.hidden) suspend();
        }, options);
        window.addEventListener("blur", event => {
            if (event.target === window) suspend();
        }, { ...options, capture: false });
    }

    globalThis.PixelAtlasInput = {
        attach(id, onSuspend) {
            if (maps.has(id)) return;
            if (maps.size === 0) subscribe();
            maps.set(id, { suspend: onSuspend, wheelTime: -Infinity, wheelKind: 0 });
        },

        detach(id) {
            releaseMouse(id);
            releasePointers(id);
            if (contextPointer?.owner === id) contextPointer = null;
            maps.delete(id);
            if (maps.size !== 0) return;
            listeners?.abort();
            listeners = wheel = pointer = keyboard = safariGesture = contextPointer = null;
        },

        takeWheel(id) {
            const map = maps.get(id);
            if (!map || !wheel) return [];
            const event = wheel.event;
            event.preventDefault();
            // Uno may raise separate X and Y wheel events for this same DOM event.
            if (wheel.owner) return [-1, 0, 0, 0, 0];
            wheel.owner = id;
            const gesture = gestures.get(event);
            if (gesture) return [3, gesture[0], gesture[1], 0, 0];
            const unit = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 32
                : event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? document.documentElement.clientHeight : 1;
            const horizontalUnit = event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? document.documentElement.clientWidth : unit;
            const x = Math.max(-2000, Math.min(2000, event.deltaX * horizontalUnit));
            const y = Math.max(-2000, Math.min(2000, event.deltaY * unit));
            let kind = 0;
            if (event.ctrlKey || event.metaKey) kind = 2;
            else {
                // WheelEvent does not expose a device type. Line/page deltas and
                // discrete legacy wheel notches identify ordinary mouse wheels.
                // Continuous pixel bursts, including momentum, retain pan behavior.
                const legacy = event.wheelDeltaY ?? event.wheelDelta;
                const detent = event.deltaMode !== WheelEvent.DOM_DELTA_PIXEL ||
                    (x === 0 && Number.isFinite(legacy) && Math.abs(legacy) >= 120 && Math.abs(legacy) % 120 === 0);
                const fresh = event.timeStamp - map.wheelTime > 250;
                kind = x !== 0 ? 0 : fresh ? (detent ? 1 : 0) : map.wheelKind;
                map.wheelKind = kind;
                map.wheelTime = event.timeStamp;
            }
            return [kind, x, y, event.shiftKey ? 1 : 0, event.altKey ? 1 : 0];
        },

        getKeyRepeat(id) {
            return maps.has(id) && keyboard ? (keyboard.repeat ? 1 : 0) : -1;
        },

        capturePointer(id, pointerId) {
            const event = pointer?.event;
            if (!maps.has(id) || event?.type !== "pointerdown") return;
            const element = event.target;
            if (!(element instanceof Element) || !element.setPointerCapture) return;
            try {
                element.setPointerCapture(event.pointerId);
                captures.set(pointerId, { owner: id, element, pointerId: event.pointerId,
                    pointerType: event.pointerType });
                if (event.button === 2) contextPointer = { owner: id };
                event.preventDefault();
            } catch (error) {
                if (error.name !== "NotFoundError") throw error;
            }
        },

        releasePointer,
        releasePointers,

        captureMouse(id) {
            if (!maps.has(id)) return false;
            const element = document.getElementById("uno-canvas");
            if (!element?.requestPointerLock) return false;
            lockOwner = id;
            try {
                const requested = element.requestPointerLock();
                requested?.catch(() => { if (lockOwner === id && !document.pointerLockElement) lockOwner = 0; });
                return true;
            } catch {
                lockOwner = 0;
                return false;
            }
        },

        releaseMouse,

        takeMouseMotion(id) {
            if (lockOwner !== id || !document.pointerLockElement || !pointer || pointer.consumed ||
                pointer.event.type !== "pointermove" || pointer.event.pointerType !== "mouse") return [];
            pointer.consumed = true;
            return [pointer.event.movementX, pointer.event.movementY];
        },

        isMouseCaptured(id) { return lockOwner === id && document.pointerLockElement !== null; }
    };
})();
