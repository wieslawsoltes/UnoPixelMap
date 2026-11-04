// Compile readback-fixture.c with the production adapter as readback-fixture.js
// and readback-fixture.wasm in a chosen directory, then run this file with that
// directory as its argument. The full Emscripten command is in docs/performance.md.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require('playwright');

async function serveFixture(directory) {
    const assets = new Map([
        ['/readback-fixture.js', ['readback-fixture.js', 'text/javascript']],
        ['/readback-fixture.wasm', ['readback-fixture.wasm', 'application/wasm']]
    ]);
    await Promise.all([...assets.values()].map(([name]) => fs.access(path.join(directory, name))));
    const server = http.createServer(async (request, response) => {
        if (request.url === '/') {
            response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
            response.end('<!doctype html><canvas id="one" width="64" height="64"></canvas>' +
                '<canvas id="two" width="64" height="64"></canvas><script src="readback-fixture.js"></script>');
            return;
        }
        const asset = assets.get(request.url);
        if (!asset) { response.writeHead(404); response.end(); return; }
        try {
            const bytes = await fs.readFile(path.join(directory, asset[0]));
            response.writeHead(200, { 'Content-Type': asset[1] });
            response.end(bytes);
        } catch {
            response.writeHead(500);
            response.end('The compiled fixture could not be read.');
        }
    });
    await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', resolve);
    });
    return server;
}

(async () => {
    assert.ok(process.argv[2], 'Provide the directory containing the compiled readback fixture.');
    const server = await serveFixture(path.resolve(process.argv[2]));
    let browser;
    try {
        browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined,
            args: ['--disable-dev-shm-usage', '--use-angle=swiftshader', '--enable-unsafe-swiftshader',
                ...JSON.parse(process.env.PIXELMAP_BROWSER_ARGS || '[]')] });
        const page = await browser.newPage();
        const messages = [], errors = [];
        page.on('console', message => messages.push(message.text()));
        page.on('pageerror', error => errors.push(error.message));
        await page.goto('http://127.0.0.1:' + server.address().port + '/');
        const result = await page.evaluate(async () => {
            const module = await PixelMapProof();
            const check = (condition, message) => { if (!condition) throw new Error(message); };
            const delay = () => new Promise(resolve => setTimeout(resolve, 2));
            const context1 = module._proof_context(0);
            check(context1, 'First native context');
            const gl1 = module.GL.currentContext.GLctx;
            const handle1 = module._pixel_map_readback_create();
            check(handle1 > 0, 'First native readback handle');
            const context2 = module._proof_context(1);
            check(context2, 'Second native context');
            const gl2 = module.GL.currentContext.GLctx;
            const handle2 = module._pixel_map_readback_create();
            check(handle2 > 0 && handle2 !== handle1, 'Independent readback handles');
            const destination = module._proof_alloc(65536);
            const metadata = module._proof_alloc(16);
            const framebuffer = (gl, width, height, color) => {
                const buffer = gl.createFramebuffer();
                const texture = gl.createTexture();
                gl.bindTexture(gl.TEXTURE_2D, texture);
                gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
                gl.bindFramebuffer(gl.FRAMEBUFFER, buffer);
                gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
                check(gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE, 'Framebuffer complete');
                gl.clearColor(...color.map(value => value / 255));
                gl.clear(gl.COLOR_BUFFER_BIT);
                return { buffer, texture, width, height, color };
            };
            const waitReady = async handle => {
                for (let attempt = 0; attempt < 1000; attempt++) {
                    const status = module._pixel_map_readback_poll(handle, metadata);
                    check(status >= 0, 'Polling remains successful');
                    if (status) return Array.from(module.HEAPU32.subarray(metadata >>> 2, (metadata >>> 2) + 4));
                    await delay();
                }
                throw new Error('Completion timeout');
            };
            const expectColor = (frame, expected) => {
                const bytes = frame[2] * frame[3] * 4;
                for (let offset = 0; offset < bytes; offset += 4)
                    for (let channel = 0; channel < 4; channel++)
                        check(module.HEAPU8[destination + offset + channel] === expected[channel], 'Exact RGBA output');
            };
            const first = framebuffer(gl1, 7, 5, [221, 37, 66, 255]);
            const other = framebuffer(gl1, 9, 6, [29, 83, 177, 255]);
            const second = framebuffer(gl2, 13, 11, [11, 141, 205, 255]);
            module.GL.makeContextCurrent(context1);
            gl1.bindFramebuffer(gl1.FRAMEBUFFER, first.buffer);
            check(module._pixel_map_readback_select(handle1) === 1, 'Select first surface');
            check(module._pixel_map_readback_begin(handle1, 0xabcdef01, 0x12345678, 7, 5) === 1, 'Reserve full 64-bit token');
            gl1.bindFramebuffer(gl1.FRAMEBUFFER, other.buffer);
            check(module._proof_read(9, 6, destination) === 0, 'Foreign framebuffer uses synchronous adapter');
            check(module.HEAPU8[destination] === 177 && module.HEAPU8[destination + 2] === 29, 'Foreign framebuffer receives BGRA');
            gl1.bindFramebuffer(gl1.FRAMEBUFFER, first.buffer);
            const foreignPack = gl1.createBuffer();
            gl1.bindBuffer(gl1.PIXEL_PACK_BUFFER, foreignPack);
            gl1.bufferData(gl1.PIXEL_PACK_BUFFER, 4096, gl1.STREAM_READ);
            gl1.pixelStorei(gl1.PACK_ALIGNMENT, 8);
            gl1.pixelStorei(gl1.PACK_ROW_LENGTH, 17);
            gl1.pixelStorei(gl1.PACK_SKIP_ROWS, 2);
            gl1.pixelStorei(gl1.PACK_SKIP_PIXELS, 3);
            module.HEAPU8.fill(0x5a, destination, destination + 1024);
            check(module._proof_read(7, 5, destination) === 0, 'Asynchronous submission succeeds');
            check(module.HEAPU8[destination] === 0x5a, 'Hidden Uno bitmap remains untouched');
            check(gl1.getParameter(gl1.PIXEL_PACK_BUFFER_BINDING) === foreignPack, 'Foreign pack buffer restored');
            check(gl1.getParameter(gl1.PACK_ALIGNMENT) === 8 && gl1.getParameter(gl1.PACK_ROW_LENGTH) === 17 &&
                gl1.getParameter(gl1.PACK_SKIP_ROWS) === 2 && gl1.getParameter(gl1.PACK_SKIP_PIXELS) === 3, 'Pack layout restored');
            module.GL.makeContextCurrent(context2);
            gl2.bindFramebuffer(gl2.FRAMEBUFFER, second.buffer);
            check(module._pixel_map_readback_select(handle2) === 1, 'Select second surface');
            check(module._pixel_map_readback_begin(handle2, 19, 0, 13, 11) === 1, 'Reserve second surface');
            check(module._proof_read(13, 11, destination) === 0, 'Submit second surface');
            const ready1 = await waitReady(handle1);
            check(ready1.join(',') === [0xabcdef01, 0x12345678, 7, 5].join(','), 'Token halves and dimensions exact');
            check(module._pixel_map_readback_copy(handle1, ready1[0], ready1[1], destination, 65536) === 1, 'Copy first completion');
            expectColor(ready1, first.color);
            check(module.GL.currentContext.handle === context2, 'Polling preserves Emscripten current context');
            check(gl1.getParameter(gl1.PIXEL_PACK_BUFFER_BINDING) === foreignPack, 'Completed copy restores foreign pack binding');
            const ready2 = await waitReady(handle2);
            check(module._pixel_map_readback_copy(handle2, ready2[0], ready2[1], destination, 65536) === 1, 'Copy second completion');
            expectColor(ready2, second.color);

            module.GL.makeContextCurrent(context1);
            gl1.bindBuffer(gl1.PIXEL_PACK_BUFFER, null);
            gl1.pixelStorei(gl1.PACK_ALIGNMENT, 4);
            gl1.pixelStorei(gl1.PACK_ROW_LENGTH, 0);
            gl1.pixelStorei(gl1.PACK_SKIP_ROWS, 0);
            gl1.pixelStorei(gl1.PACK_SKIP_PIXELS, 0);
            gl1.bindFramebuffer(gl1.FRAMEBUFFER, first.buffer);
            check(module._pixel_map_readback_select(handle1) === 1, 'Select cancellation surface');
            check(module._pixel_map_readback_begin(handle1, 20, 0, 7, 5) === 1, 'Reserve canceled frame');
            module._pixel_map_readback_cancel(handle1);
            check(module._pixel_map_readback_capacity(handle1) === 2, 'Cancellation releases reservation');
            check(module._proof_read(7, 5, destination) === 0 && module.HEAPU8[destination] === 66, 'Cancellation clears interception scope');
            for (const [token, frame] of [[21, first], [22, other]]) {
                gl1.bindFramebuffer(gl1.FRAMEBUFFER, frame.buffer);
                check(module._pixel_map_readback_select(handle1) === 1, 'Select queued frame');
                check(module._pixel_map_readback_begin(handle1, token, 0, frame.width, frame.height) === 1, 'Reserve queued frame');
                check(module._proof_read(frame.width, frame.height, destination) === 0, 'Submit queued frame');
            }
            check(module._pixel_map_readback_capacity(handle1) === 0, 'Two-slot capacity is bounded');
            check(module._pixel_map_readback_begin(handle1, 23, 0, 7, 5) === 0, 'Third frame receives backpressure');
            await new Promise(resolve => setTimeout(resolve, 50));
            const latest = await waitReady(handle1);
            check(latest[0] === 22 && latest[2] === 9 && latest[3] === 6, 'Latest completed token retains resized dimensions');
            check(module._pixel_map_readback_copy(handle1, latest[0], latest[1], 0, 0) === 1, 'Discard without pixel transfer');
            check(module._pixel_map_readback_capacity(handle1) === 2, 'Coalesced older completion also released');
            module.HEAPU8.fill(0x39, destination, destination + 100);
            check(module._pixel_map_readback_select(handle1) === 1, 'Select no-op paint');
            check(module._proof_read(9, 6, destination) === 0 && module.HEAPU8[destination] === 0x39, 'No-op paint avoids synchronous read');


            // Exercise production deadline selection without overriding any fence status.
            // Staying in this JavaScript task keeps WebGL's actual cached fence unsignaled.
            const blockForDeadline = () => {
                const until = performance.now() + 18;
                while (performance.now() < until) { }
            };
            const queue = (token, frame) => {
                gl1.bindFramebuffer(gl1.FRAMEBUFFER, frame.buffer);
                check(module._pixel_map_readback_begin(handle1, token, 0, frame.width, frame.height) === 1, 'Reserve production deadline frame');
                check(module._proof_read(frame.width, frame.height, destination) === 0, 'Submit production deadline frame');
            };
            const slotState = module.pixelMapReadbacks.surfaces.get(handle1);
            const retainedBuffers = slotState.slots.map(slot => slot.buffer);
            const originalCopy = gl1.getBufferSubData;
            let copiedTransfers = 0;
            gl1.getBufferSubData = function (...args) {
                copiedTransfers++;
                return Reflect.apply(originalCopy, this, args);
            };
            queue(61, first);
            blockForDeadline();
            check(module._pixel_map_readback_poll(handle1, metadata) === 1, 'Overdue transfer becomes ready for copy');
            check(gl1.clientWaitSync(slotState.ready.sync, 0, 0) === gl1.TIMEOUT_EXPIRED, 'Deadline selection preserves the true unsignaled fence');
            const overdue = Array.from(module.HEAPU32.subarray(metadata >>> 2, (metadata >>> 2) + 4));
            check(overdue.join(',') === [61, 0, 7, 5].join(','), 'Overdue metadata matches its exact retained frame');
            check(module._pixel_map_readback_copy(handle1, 61, 0, destination, 65536) === 1, 'Actual buffer copy completes overdue pixels');
            expectColor(overdue, first.color);
            check(module._pixel_map_readback_capacity(handle1) === 2, 'Copied overdue slot is released');

            queue(62, first);
            queue(63, other);
            blockForDeadline();
            check(module._pixel_map_readback_poll(handle1, metadata) === 1, 'Canceled newest token is selected after its deadline');
            check(module.HEAPU32[metadata >>> 2] === 63, 'Newest queued token is selected');
            check(module._pixel_map_readback_capacity(handle1) === 0, 'Selection retains both busy native slots');
            const beforeDiscard = copiedTransfers;
            check(module._pixel_map_readback_copy(handle1, 63, 0, 0, 0) === 0, 'Unsignaled discard remains pending');
            check(slotState.ready === null && slotState.slots.every(slot => slot.frame && slot.sync), 'Pending discard clears selection while retaining both transfers');
            check(module._pixel_map_readback_capacity(handle1) === 0, 'Pending canceled transfers still bound the queue');
            check(module._pixel_map_readback_begin(handle1, 64, 0, 7, 5) === 0, 'Canceled pending transfers cannot be overwritten');
            check(copiedTransfers === beforeDiscard, 'Pending discard never copies pixels');
            let discarded = false;
            for (let attempt = 0; attempt < 1000 && !discarded; attempt++) {
                await delay();
                const selected = module._pixel_map_readback_poll(handle1, metadata);
                check(selected >= 0, 'Canceled transfer remains valid while waiting for a true signal');
                if (selected) discarded = module._pixel_map_readback_copy(handle1,
                    module.HEAPU32[metadata >>> 2], module.HEAPU32[(metadata >>> 2) + 1], 0, 0) === 1;
            }
            check(discarded && module._pixel_map_readback_capacity(handle1) === 2, 'True GPU completion releases canceled slots');
            check(copiedTransfers === beforeDiscard, 'Signaled discard also avoids a pixel copy');

            queue(64, first);
            blockForDeadline();
            check(module._pixel_map_readback_poll(handle1, metadata) === 1, 'Select a single canceled overdue transfer');
            check(module._pixel_map_readback_copy(handle1, 64, 0, 0, 0) === 0, 'Keep its unsignaled slot');
            queue(65, other);
            blockForDeadline();
            check(module._pixel_map_readback_poll(handle1, metadata) === 1, 'A later valid transfer supplies backpressure');
            const recovered = Array.from(module.HEAPU32.subarray(metadata >>> 2, (metadata >>> 2) + 4));
            check(recovered.join(',') === [65, 0, 9, 6].join(','), 'Later valid token and resized image remain paired');
            check(module._pixel_map_readback_capacity(handle1) === 0, 'Both slots stay occupied until the later copy');
            check(module._pixel_map_readback_copy(handle1, 65, 0, destination, 65536) === 1, 'Copy later valid transfer');
            expectColor(recovered, other.color);
            check(module._pixel_map_readback_capacity(handle1) === 2, 'Later completion also retires the canceled predecessor');
            check(slotState.slots.every((slot, index) => slot.buffer === retainedBuffers[index]), 'Two PBO objects are reused across every resize and cancellation');
            gl1.getBufferSubData = originalCopy;

            const mismatch = module._pixel_map_readback_create();
            check(module._pixel_map_readback_select(mismatch) === 1, 'Select dimension fault');
            check(module._pixel_map_readback_begin(mismatch, 1, 0, 7, 5) === 1, 'Reserve dimension fault');
            check(module._proof_read(9, 6, destination) === 0, 'Dimension fault leaves GL state clean');
            check(module._pixel_map_readback_poll(mismatch, metadata) === -1, 'Dimension fault is reported');
            module._pixel_map_readback_dispose(mismatch);
            module._pixel_map_readback_dispose(handle1);
            check(!module['pixelMapReadbacks'].surfaces.has(handle1), 'First handle releases retained objects');
            const loss = gl2.getExtension('WEBGL_lose_context');
            loss.loseContext();
            await delay();
            check(module._pixel_map_readback_poll(handle2, metadata) === -1, 'Context loss is reported');
            module._pixel_map_readback_dispose(handle2);
            check(module['pixelMapReadbacks'].surfaces.size === 0, 'All native queues released');
            module._proof_free(metadata);
            module._proof_free(destination);
            return { passed: true, cases: ['two contexts', 'foreign framebuffer scope', '64-bit tokens', 'exact RGBA',
                'pack-state restoration', 'context preservation', 'resize', 'two-slot backpressure',
                'latest completion coalescing', 'no-copy cancellation', 'no-op paint suppression',
                'dimension failure', 'context loss', 'disposal', 'production deadline copy',
                'unsignaled cancellation remains bounded', 'later valid token after cancellation'] };
        });
        assert.deepEqual(errors, [], 'The native readback fixture has no browser exceptions.');
        console.log(JSON.stringify({ ...result, messages }, null, 2));
    } finally {
        try { await browser?.close(); }
        finally { await new Promise(resolve => server.close(resolve)); }
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
