// Keep browser deltas intact until Uno routes the event to the map. Managed hit
// testing owns the gesture, including every XAML control drawn over the canvas.
(() => {
    "use strict";

    const maps = new Map();
    const captures = new Map();
    const canvasTouches = new Set();
    const gestures = new WeakMap();
    let position = { x: 0, y: 0, target: null };
    let listeners, wheel, pointer, keyboard, safariGesture, contextPointer, mouseRequest, lockOwner = 0;
    // A single reused object keeps diagnostics bounded, including during long drives.
    // These are submission counters; frameMilliseconds does not measure GPU completion.
    const graphics = globalThis.PixelAtlasWebGpu = { backend: "WebGPU", frames: 0, preparations: 0 };

    // Each canvas supplies Uno's actual borrowed GPUDevice. Keep observation in
    // the reusable control asset; application-specific recovery UI is optional.
    const watchedDevices = new WeakMap();
    const activeObservations = new Set();
    let closing = false;

    function detachWebGpuDevice(subscription) {
        if (!subscription?.active) return;
        subscription.active = false;
        subscription.failed = null;
        const record = subscription.record;
        subscription.record = null;
        activeObservations.delete(subscription);
        record.subscriptions.delete(subscription);
        if (!record.subscriptions.size)
            record.device.removeEventListener("uncapturederror", record.error);
    }

    function failWebGpu(message) {
        graphics.failure = String(message);
        globalThis.PixelAtlasGraphics?.fail?.(graphics.failure);
    }

    function reportDeviceFailure(subscription, message) {
        if (!subscription.active || closing) return;
        const failed = subscription.failed;
        // Clear the marshalled delegate before invoking it: managed Fail disposes
        // the same subscription, and queued notifications must stop.
        detachWebGpuDevice(subscription);
        try { failed(message); }
        catch (error) { console.error("The map could not notify its renderer of WebGPU failure.", error); }
        finally {
            // Preserve diagnostics even if managed teardown prevents its callback.
            if (!graphics.failure) failWebGpu(message);
        }
    }

    function observeWebGpuDevice(device, failed) {
        if (!device || typeof device.lost?.then !== "function" ||
            typeof device.addEventListener !== "function" || typeof failed !== "function")
            throw new Error("Uno did not expose a live shared browser GPUDevice.");
        if (closing) return { active: false, record: null, failed: null };
        let record = watchedDevices.get(device);
        if (!record) {
            record = { device, subscriptions: new Set(), loss: null, error: null };
            record.error = event => {
                const message = "WebGPU could not render this scene. " + (event.error?.message || "An uncaptured GPU error occurred.");
                for (const subscription of [...record.subscriptions]) reportDeviceFailure(subscription, message);
            };
            watchedDevices.set(device, record);
            // A promise cannot be unsubscribed. Install it only once per device;
            // detached subscriptions retain no managed callbacks.
            device.lost.then(info => {
                record.loss = "The graphics device was lost. Reload the page to rebuild its resources. " + info.message;
                for (const subscription of [...record.subscriptions]) reportDeviceFailure(subscription, record.loss);
            });
        }
        const subscription = { active: true, record, failed };
        record.subscriptions.add(subscription);
        activeObservations.add(subscription);
        try {
            if (record.subscriptions.size === 1) device.addEventListener("uncapturederror", record.error);
            if (record.loss !== null) queueMicrotask(() => reportDeviceFailure(subscription, record.loss));
            return subscription;
        } catch (error) {
            detachWebGpuDevice(subscription);
            throw error;
        }
    }

    window.addEventListener("pagehide", event => {
        if (event.persisted) return;
        closing = true;
        for (const subscription of [...activeObservations]) detachWebGpuDevice(subscription);
    });

    function nativeInput(target) {
        return target instanceof Element && target.closest("input,textarea,select,iframe,[contenteditable]");
    }

    function activeInput(input) {
        // Native input runs microtasks between DOM listeners. eventPhase remains
        // nonzero for the entire dispatch, including Uno's later routed callback.
        // Restore an outer dispatch after a nested event has finished.
        while (input && input.event.eventPhase === 0) input = input.previous;
        return input;
    }

    function retainWheel(event) {
        wheel = { event, owner: 0, previous: activeInput(wheel) };
        if (nativeInput(event.target)) return;
        position = { x: event.clientX, y: event.clientY, target: event.target };
        if (safariGesture?.owned && event.ctrlKey && !gestures.has(event)) {
            event.preventDefault();
            event.stopImmediatePropagation();
            return;
        }
    }

    function retainPointer(event) {
        pointer = { event, previous: activeInput(pointer) };
        if (event.type === "pointerdown") {
            contextPointer = null;
            if (event.pointerType === "touch" && event.target === document.getElementById("uno-canvas") &&
                !nativeInput(event.target)) {
                canvasTouches.add(event.pointerId);
                safariGesture = null;
            }
        }
        position = { x: event.clientX, y: event.clientY, target: event.target };
    }

    function endPointer(event) {
        canvasTouches.delete(event.pointerId);
    }

    function retainKeyboard(event) {
        keyboard = { event, previous: activeInput(keyboard) };
    }

    function releasePointer(id, pointerId) {
        const capture = captures.get(pointerId);
        if (!capture || capture.owner !== id) return;
        captures.delete(pointerId);
        if (capture.element.hasPointerCapture(capture.pointerId)) capture.element.releasePointerCapture(capture.pointerId);
    }

    function clearMouseMotion(map) {
        if (!map) return;
        if (map.mouseFrame) cancelAnimationFrame(map.mouseFrame);
        map.mouseFrame = map.mouseX = map.mouseY = 0;
    }

    function ownsMouse(id) {
        return lockOwner === id && document.pointerLockElement !== null &&
            document.pointerLockElement === document.getElementById("uno-canvas");
    }

    function moveLockedMouse(event) {
        const owner = lockOwner;
        const map = maps.get(owner);
        if (!map || !ownsMouse(owner)) return;
        map.mouseX += Number.isFinite(event.movementX) ? event.movementX : 0;
        map.mouseY += Number.isFinite(event.movementY) ? event.movementY : 0;
        if (map.mouseFrame || map.mouseX === 0 && map.mouseY === 0) return;
        // Pointer Lock supplies relative MouseEvents even when no XAML PointerMoved
        // event is raised. Preserve high-rate mouse deltas with one managed update/frame.
        map.mouseFrame = requestAnimationFrame(() => {
            map.mouseFrame = 0;
            const x = map.mouseX, y = map.mouseY;
            map.mouseX = map.mouseY = 0;
            if (ownsMouse(owner) && maps.get(owner) === map)
                map.mouseMove(x, y);
        });
    }

    function completeMouseRequest(request, granted) {
        if (mouseRequest !== request) return;
        mouseRequest = null;
        request.events.abort();
        if (!granted) {
            clearMouseMotion(maps.get(lockOwner));
            lockOwner = 0;
        } else if (!maps.has(lockOwner) && document.pointerLockElement === request.element) {
            document.exitPointerLock();
        }
    }

    function releaseMouse(id) {
        if (lockOwner !== id) return;
        clearMouseMotion(maps.get(id));
        lockOwner = 0;
        if (document.pointerLockElement === document.getElementById("uno-canvas")) document.exitPointerLock();
    }

    function releasePointers(id) {
        for (const [pointerId, capture] of [...captures])
            if (capture.owner === id) releasePointer(id, pointerId);
    }

    function suspend() {
        wheel = pointer = safariGesture = contextPointer = keyboard = null;
        canvasTouches.clear();
        for (const [id, map] of [...maps]) {
            releaseMouse(id);
            releasePointers(id);
            map.suspend();
        }
    }

    function safari(event) {
        if (nativeInput(event.target)) return;
        // XAML owns every canvas touch, including UI sticks and pads whose captures
        // are independent of the map. Parallel Safari gestures must not apply twice.
        if (canvasTouches.size !== 0) {
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
        document.addEventListener("pointerup", endPointer, options);
        document.addEventListener("pointercancel", endPointer, options);
        document.addEventListener("mousemove", moveLockedMouse, options);
        document.addEventListener("keydown", retainKeyboard, options);
        document.addEventListener("gesturestart", safari, options);
        document.addEventListener("gesturechange", safari, options);
        document.addEventListener("gestureend", safari, options);
        document.addEventListener("contextmenu", event => {
            if (contextPointer && maps.has(contextPointer.owner) && event.button === 2 && !nativeInput(event.target))
                event.preventDefault();
        }, options);
        document.addEventListener("pointerlockchange", () => {
            if (mouseRequest || !lockOwner || ownsMouse(lockOwner)) return;
            const previous = lockOwner;
            clearMouseMotion(maps.get(previous));
            lockOwner = 0;
            maps.get(previous)?.suspend();
        }, options);
        document.addEventListener("pointerlockerror", () => {
            if (!mouseRequest && !ownsMouse(lockOwner)) {
                clearMouseMotion(maps.get(lockOwner));
                lockOwner = 0;
            }
        }, options);
        document.addEventListener("visibilitychange", () => {
            if (document.hidden) suspend();
        }, options);
        window.addEventListener("blur", event => {
            if (event.target === window) suspend();
        }, { ...options, capture: false });
    }

    globalThis.PixelAtlasInput = {
        attach(id, onSuspend, onMouseMove) {
            if (maps.has(id)) return;
            if (maps.size === 0) subscribe();
            maps.set(id, { suspend: onSuspend, mouseMove: onMouseMove,
                mouseFrame: 0, mouseX: 0, mouseY: 0, wheelTime: -Infinity, wheelKind: 0 });
        },

        detach(id) {
            releaseMouse(id);
            releasePointers(id);
            if (contextPointer?.owner === id) contextPointer = null;
            maps.delete(id);
            if (maps.size !== 0) return;
            listeners?.abort();
            canvasTouches.clear();
            listeners = wheel = pointer = keyboard = safariGesture = contextPointer = null;
        },

        takeWheel(id) {
            const map = maps.get(id);
            wheel = activeInput(wheel);
            if (!map || !wheel || nativeInput(wheel.event.target)) return [];
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
            keyboard = activeInput(keyboard);
            return maps.has(id) && keyboard && !nativeInput(keyboard.event.target)
                ? (keyboard.event.repeat ? 1 : 0) : -1;
        },

        capturePointer(id, pointerId) {
            pointer = activeInput(pointer);
            const event = pointer?.event;
            if (!maps.has(id) || event?.type !== "pointerdown" || nativeInput(event.target)) return;
            const element = event.target;
            if (!(element instanceof Element) || !element.setPointerCapture) return;
            try {
                element.setPointerCapture(event.pointerId);
                captures.set(pointerId, { owner: id, element, pointerId: event.pointerId });
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
            if (lockOwner !== id) clearMouseMotion(maps.get(lockOwner));
            lockOwner = id;
            // One native request serves the shared canvas. A new authorized owner
            // can take over a pending request without an older completion undoing it.
            if (ownsMouse(id) || mouseRequest) return true;
            const request = mouseRequest = { element, events: new AbortController(), hasPromise: false };
            const options = { signal: request.events.signal };
            // These completion listeners outlive adapter disposal while a request
            // is pending, including browsers whose API returns no Promise.
            document.addEventListener("pointerlockchange", () => {
                if (document.pointerLockElement === element) completeMouseRequest(request, true);
            }, options);
            document.addEventListener("pointerlockerror", () => {
                if (!request.hasPromise) completeMouseRequest(request, false);
            }, options);
            try {
                const requested = element.requestPointerLock();
                if (requested) {
                    request.hasPromise = true;
                    requested.then(() => completeMouseRequest(request, true), () => completeMouseRequest(request, false));
                }
                return true;
            } catch {
                completeMouseRequest(request, false);
                return false;
            }
        },

        releaseMouse,

        isMouseCaptured(id) { return ownsMouse(id); },

        observeWebGpuDevice,
        stopObservingWebGpuDevice: detachWebGpuDevice,

        reportWebGpu(frames, preparations, width, height, physicalWidth, physicalHeight,
            pixelSize, patches, triangles, draws, uploadBytes, residentBytes,
            frameMilliseconds, submissionMilliseconds, style, quality, renderWidth, renderHeight,
            attachmentBytes, imageryCount, relief, elevation, zoom) {
            Object.assign(graphics, { frames, preparations, width, height, physicalWidth, physicalHeight,
                pixelSize, patches, triangles, draws, uploadBytes, residentBytes,
                frameMilliseconds, submissionMilliseconds, style, quality, renderWidth, renderHeight,
                attachmentBytes, imageryCount, relief, elevation, zoom, failure: null });
        },

        failWebGpu
    };
})();
