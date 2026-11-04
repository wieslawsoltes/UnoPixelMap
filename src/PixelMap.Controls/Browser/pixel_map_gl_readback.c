// Uno 6.7 resolves GLCanvasElement entry points through native Emscripten GL.
// Its browser readback still requests BGRA, which only Uno's managed/JS fallback
// translates. Wrap that resolver at link time so the native route performs the
// same RGBA read and channel conversion. All other entry points retain Uno's
// implementation, including its sized-texture-format adapters.

#include <GLES3/gl3.h>
#include <emscripten.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>

#define PIXEL_MAP_BGRA 0x80E1

extern int __real_uno_gl_resolve(const char *name);

// Each control owns two transfers. The saved WebGL context permits timer-driven
// completion without changing Emscripten's current context or scheduling a paint.
// All temporary bindings are restored before returning to the native GL bridge.
EM_JS(int, pixel_map_js_create, (), {
    const context = typeof GL !== 'undefined' ? GL.currentContext : null;
    const gl = context && context.GLctx;
    if (!gl || context.version < 2 || gl.isContextLost()) return 0;
    let manager = Module['pixelMapReadbacks'];
    if (!manager) {
        manager = Module['pixelMapReadbacks'] = {
            nextHandle: 1,
            surfaces: new Map(),
            selected: new WeakMap(),

            fail(state, reason) {
                if (!state.failed) console.warn('Pixel Atlas asynchronous GPU readback failed. ' + reason);
                state.failed = true;
                return -1;
            },

            get(handle) {
                const state = this.surfaces.get(handle);
                if (!state || state.failed) return null;
                if (state.gl.isContextLost()) {
                    this.fail(state, 'The WebGL context was lost.');
                    return null;
                }
                return state;
            },

            releaseSlot(state, slot) {
                if (slot.sync) state.gl.deleteSync(slot.sync);
                slot.sync = null;
                slot.frame = null;
                if (state.ready === slot) state.ready = null;
            },

            select(handle) {
                const state = this.get(handle);
                if (!state) return -1;
                if (GL.currentContext.GLctx !== state.gl)
                    return this.fail(state, 'A different GL context owns the rendering callback.');
                this.selected.set(state.gl, { handle, framebuffer: state.gl.getParameter(state.gl.READ_FRAMEBUFFER_BINDING) });
                return 1;
            },

            capacity(handle) {
                const state = this.get(handle);
                if (!state) return -1;
                return state.slots.filter(slot => !slot.frame).length;
            },

            begin(handle, low, high, width, height) {
                const state = this.get(handle);
                if (!state) return -1;
                if (state.armed) return this.fail(state, 'A frame was reserved before its preceding readback.');
                const bytes = width * height * 4;
                if (width <= 0 || height <= 0 || !Number.isSafeInteger(bytes) || bytes > 33554432)
                    return this.fail(state, 'The readback exceeds the bounded transfer size.');
                const slot = state.slots.find(candidate => !candidate.frame);
                if (!slot) return 0;
                slot.frame = { low: low >>> 0, high: high >>> 0, width, height, sequence: ++state.sequence };
                state.armed = slot;
                if (this.select(handle) < 0) return -1;
                return 1;
            },

            cancel(handle) {
                const state = this.surfaces.get(handle);
                if (state) {
                    if (this.selected.get(state.gl)?.handle === handle) this.selected.delete(state.gl);
                    if (state.armed) {
                        state.armed.frame = null;
                        state.armed = null;
                    }
                }
            },

            intercept(x, y, width, height) {
                const gl = GL.currentContext && GL.currentContext.GLctx;
                const selection = gl && this.selected.get(gl);
                if (!selection || gl.getParameter(gl.READ_FRAMEBUFFER_BINDING) !== selection.framebuffer) return 0;
                this.selected.delete(gl);
                const state = this.get(selection.handle);
                if (!state || !state.armed) return 1;
                const slot = state.armed;
                state.armed = null;
                if (slot.frame.width !== width || slot.frame.height !== height) {
                    this.fail(state, 'The framebuffer size changed after preparing its retained frame.');
                    return 1;
                }
                const previousBuffer = gl.getParameter(gl.PIXEL_PACK_BUFFER_BINDING);
                const alignment = gl.getParameter(gl.PACK_ALIGNMENT);
                const rowLength = gl.getParameter(gl.PACK_ROW_LENGTH);
                const skipRows = gl.getParameter(gl.PACK_SKIP_ROWS);
                const skipPixels = gl.getParameter(gl.PACK_SKIP_PIXELS);
                try {
                    if (!slot.buffer) slot.buffer = gl.createBuffer();
                    if (!slot.buffer) throw new Error('The GPU could not allocate a pixel transfer buffer.');
                    gl.bindBuffer(gl.PIXEL_PACK_BUFFER, slot.buffer);
                    const bytes = width * height * 4;
                    if (slot.bytes !== bytes) {
                        gl.bufferData(gl.PIXEL_PACK_BUFFER, bytes, gl.STREAM_READ);
                        slot.bytes = bytes;
                    }
                    gl.pixelStorei(gl.PACK_ALIGNMENT, 1);
                    gl.pixelStorei(gl.PACK_ROW_LENGTH, 0);
                    gl.pixelStorei(gl.PACK_SKIP_ROWS, 0);
                    gl.pixelStorei(gl.PACK_SKIP_PIXELS, 0);
                    gl.readPixels(x, y, width, height, gl.RGBA, gl.UNSIGNED_BYTE, 0);
                    slot.sync = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
                    if (!slot.sync) throw new Error('The GPU could not create a transfer fence.');
                    gl.flush();
                    // Uno validates GL errors after its readback callback. Leave that
                    // validation there instead of adding another submission round trip.
                } catch (error) {
                    this.fail(state, error.message || String(error));
                } finally {
                    gl.bindBuffer(gl.PIXEL_PACK_BUFFER, previousBuffer);
                    gl.pixelStorei(gl.PACK_ALIGNMENT, alignment);
                    gl.pixelStorei(gl.PACK_ROW_LENGTH, rowLength);
                    gl.pixelStorei(gl.PACK_SKIP_ROWS, skipRows);
                    gl.pixelStorei(gl.PACK_SKIP_PIXELS, skipPixels);
                }
                // GLCanvas's visual is hidden. Its bitmap is never used for an
                // asynchronous frame, so its client-memory destination stays untouched.
                return 1;
            },

            poll(handle, metadata) {
                const state = this.get(handle);
                if (!state) return -1;
                if (!state.ready) {
                    let latest = null;
                    for (const slot of state.slots) {
                        if (!slot.sync) continue;
                        const status = state.gl.clientWaitSync(slot.sync, 0, 0);
                        if (status === state.gl.WAIT_FAILED)
                            return this.fail(state, 'The pixel transfer fence failed.');
                        if ((status === state.gl.ALREADY_SIGNALED || status === state.gl.CONDITION_SATISFIED) &&
                            (!latest || slot.frame.sequence > latest.frame.sequence)) latest = slot;
                    }
                    // Copy only a signaled transfer. A deadline cannot make GPU work
                    // complete, and getBufferSubData would block input and simulation
                    // until it does. The two occupied slots bound submission while
                    // the host coalesces camera changes into its next request.
                    if (!latest) return 0;
                    state.ready = latest;
                }
                const frame = state.ready.frame;
                HEAPU32[(metadata >>> 2)] = frame.low;
                HEAPU32[(metadata >>> 2) + 1] = frame.high;
                HEAP32[(metadata >>> 2) + 2] = frame.width;
                HEAP32[(metadata >>> 2) + 3] = frame.height;
                return 1;
            },

            copy(handle, low, high, destination, capacity) {
                const state = this.get(handle);
                if (!state) return -1;
                const slot = state.ready;
                if (!slot || slot.frame.low !== (low >>> 0) || slot.frame.high !== (high >>> 0))
                    return this.fail(state, 'The retained frame does not match the completed pixel transfer.');
                if (destination) {
                    const bytes = slot.frame.width * slot.frame.height * 4;
                    const pointer = destination >>> 0;
                    if (capacity < bytes || pointer + bytes > HEAPU8.length)
                        return this.fail(state, 'The completed pixel destination is too small.');
                    const gl = state.gl;
                    const previousBuffer = gl.getParameter(gl.PIXEL_PACK_BUFFER_BINDING);
                    try {
                        gl.bindBuffer(gl.PIXEL_PACK_BUFFER, slot.buffer);
                        gl.getBufferSubData(gl.PIXEL_PACK_BUFFER, 0, HEAPU8, pointer, bytes);
                        const error = gl.getError();
                        if (error) throw new Error('WebGL error ' + error + ' while copying a completed pixel transfer.');
                    } catch (error) {
                        return this.fail(state, error.message || String(error));
                    } finally {
                        gl.bindBuffer(gl.PIXEL_PACK_BUFFER, previousBuffer);
                    }
                }
                // GPU completion of the selected transfer also completes every older
                // command on this context. Until this point, all slots stay occupied.
                const sequence = slot.frame.sequence;
                for (const pending of state.slots)
                    if (pending.frame && pending.frame.sequence <= sequence) this.releaseSlot(state, pending);
                return 1;
            },

            dispose(handle) {
                const state = this.surfaces.get(handle);
                if (!state) return;
                if (this.selected.get(state.gl)?.handle === handle) this.selected.delete(state.gl);
                for (const slot of state.slots) {
                    if (slot.sync) state.gl.deleteSync(slot.sync);
                    if (slot.buffer) state.gl.deleteBuffer(slot.buffer);
                }
                this.surfaces.delete(handle);
            }
        };
    }
    if (manager.nextHandle > 2147483647) return 0;
    const handle = manager.nextHandle++;
    manager.surfaces.set(handle, {
        gl, slots: Array.from({ length: 2 }, () => ({ buffer: null, bytes: 0, sync: null, frame: null })),
        armed: null, ready: null, sequence: 0, failed: false
    });
    return handle;
});

EM_JS(int, pixel_map_js_select, (int handle), {
    return Module['pixelMapReadbacks'].select(handle);
});

EM_JS(int, pixel_map_js_capacity, (int handle), {
    return Module['pixelMapReadbacks'].capacity(handle);
});

EM_JS(int, pixel_map_js_begin, (int handle, uint32_t low, uint32_t high, int width, int height), {
    return Module['pixelMapReadbacks'].begin(handle, low, high, width, height);
});

EM_JS(void, pixel_map_js_cancel, (int handle), {
    Module['pixelMapReadbacks'].cancel(handle);
});

EM_JS(int, pixel_map_js_intercept, (int x, int y, int width, int height), {
    const manager = Module['pixelMapReadbacks'];
    return manager ? manager.intercept(x, y, width, height) : 0;
});

EM_JS(int, pixel_map_js_poll, (int handle, int32_t *metadata), {
    return Module['pixelMapReadbacks'].poll(handle, metadata);
});

EM_JS(int, pixel_map_js_copy, (int handle, uint32_t low, uint32_t high, void *destination, int capacity), {
    return Module['pixelMapReadbacks'].copy(handle, low, high, destination, capacity);
});

EM_JS(void, pixel_map_js_dispose, (int handle), {
    const manager = Module['pixelMapReadbacks'];
    if (manager) manager.dispose(handle);
});

EMSCRIPTEN_KEEPALIVE int pixel_map_readback_create(void) { return pixel_map_js_create(); }
EMSCRIPTEN_KEEPALIVE int pixel_map_readback_select(int handle) { return pixel_map_js_select(handle); }
EMSCRIPTEN_KEEPALIVE int pixel_map_readback_capacity(int handle) { return pixel_map_js_capacity(handle); }
EMSCRIPTEN_KEEPALIVE int pixel_map_readback_begin(int handle, uint32_t low, uint32_t high, int width, int height)
{
    return pixel_map_js_begin(handle, low, high, width, height);
}
EMSCRIPTEN_KEEPALIVE void pixel_map_readback_cancel(int handle) { pixel_map_js_cancel(handle); }
EMSCRIPTEN_KEEPALIVE int pixel_map_readback_poll(int handle, int32_t *metadata) { return pixel_map_js_poll(handle, metadata); }
EMSCRIPTEN_KEEPALIVE int pixel_map_readback_copy(int handle, uint32_t low, uint32_t high, void *destination, int capacity)
{
    return pixel_map_js_copy(handle, low, high, destination, capacity);
}
EMSCRIPTEN_KEEPALIVE void pixel_map_readback_dispose(int handle) { pixel_map_js_dispose(handle); }

static void pixel_map_read_pixels(GLint x, GLint y, GLsizei width, GLsizei height,
                                  GLenum format, GLenum type, void *pixels)
{
    if (format != PIXEL_MAP_BGRA || type != GL_UNSIGNED_BYTE || pixels == NULL || width <= 0 || height <= 0)
    {
        glReadPixels(x, y, width, height, format, type, pixels);
        return;
    }

    if (pixel_map_js_intercept(x, y, width, height)) return;

    GLint pack_buffer = 0;
    glGetIntegerv(GL_PIXEL_PACK_BUFFER_BINDING, &pack_buffer);
    if (pack_buffer != 0)
    {
        // A PBO argument is a buffer offset. Native validation handles unsupported
        // BGRA PBO requests and preserves their existing GL error behavior.
        glReadPixels(x, y, width, height, format, type, pixels);
        return;
    }

    GLint row_length = 0, skip_rows = 0, skip_pixels = 0, alignment = 4;
    glGetIntegerv(GL_PACK_ROW_LENGTH, &row_length);
    glGetIntegerv(GL_PACK_SKIP_ROWS, &skip_rows);
    glGetIntegerv(GL_PACK_SKIP_PIXELS, &skip_pixels);
    glGetIntegerv(GL_PACK_ALIGNMENT, &alignment);

    glReadPixels(x, y, width, height, GL_RGBA, type, pixels);

    // Keep GL's error flag available to GLCanvasElement's readback validation.
    // As with glReadPixels, the caller owns storage for the selected pack layout.
    size_t row_bytes = (size_t)(row_length > 0 ? row_length : width) * 4;
    size_t pack_alignment = alignment > 0 ? (size_t)alignment : 4;
    size_t stride = (row_bytes + pack_alignment - 1) & ~(pack_alignment - 1);
    GLsizei columns_before_last_row = stride < (size_t)width * 4 ? (GLsizei)(stride / 4) : width;
    uint8_t *first_row = (uint8_t *)pixels + (size_t)skip_rows * stride + (size_t)skip_pixels * 4;
    for (GLsizei row = 0; row < height; row++)
    {
        uint8_t *pixel = first_row + (size_t)row * stride;
        // Overlapping packed rows share storage. Convert each written pixel once.
        GLsizei columns = row + 1 < height ? columns_before_last_row : width;
        for (GLsizei column = 0; column < columns; column++, pixel += 4)
        {
            uint8_t red = pixel[0];
            pixel[0] = pixel[2];
            pixel[2] = red;
        }
    }
}

EMSCRIPTEN_KEEPALIVE
int __wrap_uno_gl_resolve(const char *name)
{
    if (name != NULL && strcmp(name, "glReadPixels") == 0)
        return (int)(intptr_t)&pixel_map_read_pixels;
    return __real_uno_gl_resolve(name);
}
