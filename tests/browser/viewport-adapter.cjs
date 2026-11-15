const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

// Execute the shipped adapter, including asynchronous location completion after
// unload/reload, without a browser or a parallel copy of the production logic.
const html = fs.readFileSync(path.resolve(__dirname, "../../src/PixelMap.App/Platforms/WebAssembly/index.html"), "utf8");
const source = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)]
    .map(match => match[1]).find(script => script.includes("globalThis.PixelAtlasViewport ="));
assert.ok(source, "The production viewport adapter must be present");

class Events {
    listeners = new Map();
    addEventListener(name, callback) {
        const list = this.listeners.get(name) ?? new Set();
        list.add(callback); this.listeners.set(name, list);
    }
    removeEventListener(name, callback) { this.listeners.get(name)?.delete(callback); }
    emit(name) { for (const callback of this.listeners.get(name) ?? []) callback(); }
    count() { return [...this.listeners.values()].reduce((total, list) => total + list.size, 0); }
}

const window = new Events();
window.innerHeight = 844;
window.visualViewport = Object.assign(new Events(), {height: 510, offsetTop: 0});
const probes = new Set(), frames = new Map(), requests = [];
let frameId = 0;
const style = {paddingTop: "47px", paddingRight: "0px", paddingBottom: "34px", paddingLeft: "0px"};
const document = {
    body: {append(element) { probes.add(element); }},
    createElement() { return {style: {}, setAttribute() {}, remove() { probes.delete(this); }}; }
};
const navigator = {geolocation: {
    getCurrentPosition(found, failed, options) { requests.push({found, failed, options}); }
}};
const sandbox = {window, document, navigator, getComputedStyle: () => style,
    requestAnimationFrame(callback) { const id = ++frameId; frames.set(id, callback); return id; },
    cancelAnimationFrame: id => frames.delete(id)};
vm.runInNewContext(source, sandbox);
const adapter = sandbox.PixelAtlasViewport;
function flush() { const queued = [...frames.values()]; frames.clear(); for (const callback of queued) callback(); }

const notifications = [];
adapter.attach(1, () => notifications.push(1));
adapter.attach(2, () => notifications.push(2));
assert.equal(window.count(), 1);
assert.equal(window.visualViewport.count(), 2);
window.emit("resize"); window.visualViewport.emit("resize"); window.visualViewport.emit("scroll");
assert.equal(frames.size, 1, "Native resize and scroll bursts coalesce into one layout callback");
flush();
assert.deepEqual(notifications, [1, 2]);
assert.deepEqual(Array.from(adapter.read()), [844, 510, 0, 47, 0, 34, 0]);
assert.equal(probes.size, 1, "Safe-area measurement uses one noninteractive probe");

window.visualViewport.height = 400;
window.visualViewport.offsetTop = 250;
assert.deepEqual(Array.from(adapter.read()), [844, 400, 250, 47, 0, 34, 0], "Panned keyboard metrics retain the visual origin");

const results = [];
adapter.locate(1, (latitude, longitude) => results.push({latitude, longitude}), message => results.push(message));
assert.equal(requests.length, 1);
assert.deepEqual({...requests[0].options}, {enableHighAccuracy: false, timeout: 12000, maximumAge: 60000});
adapter.detach(1);
assert.equal(window.count(), 1, "Another attachment retains the shared listener");
requests[0].found({coords: {latitude: 40, longitude: -74}});
requests[0].failed({code: 1});
assert.deepEqual(results, [], "An unloaded attachment cannot consume stale location success or failure");

adapter.attach(1, () => notifications.push("replacement"));
requests[0].found({coords: {latitude: 41, longitude: -75}});
assert.deepEqual(results, [], "A new attachment cannot inherit a previous location request");
adapter.locate(1, (latitude, longitude) => results.push({latitude, longitude}), message => results.push(message));
requests[1].found({coords: {latitude: 48.85, longitude: 2.35}});
assert.deepEqual(results, [{latitude: 48.85, longitude: 2.35}]);
adapter.locate(1, assert.fail, message => results.push(message));
requests[2].failed({code: 1});
assert.match(results[1], /Location access is off/);
adapter.locate(1, assert.fail, message => results.push(message));
requests[3].failed({code: 3});
assert.match(results[2], /could not be found in time/);

adapter.detach(1); adapter.detach(2);
assert.equal(frames.size, 0, "Final detachment cancels a queued layout callback");
assert.equal(window.count(), 0);
assert.equal(window.visualViewport.count(), 0);
assert.equal(probes.size, 0);
const requestCount = requests.length;
adapter.locate(1, assert.fail, assert.fail);
assert.equal(requests.length, requestCount, "Detached controls never request a new location");
window.emit("resize"); window.visualViewport.emit("scroll");
assert.equal(frames.size, 0);

window.visualViewport = null;
adapter.attach(3, () => {});
assert.deepEqual(Array.from(adapter.read()), [844, 844, 0, 47, 0, 34, 0], "Browsers without visualViewport retain their full layout viewport");
navigator.geolocation = null;
adapter.locate(3, assert.fail, message => assert.match(message, /does not provide location access/));
adapter.detach(3);
assert.equal(window.count(), 0);
assert.equal(probes.size, 0);
console.log("Passed viewport coalescing, safe areas, panned keyboard metrics, attachment lifecycle and stale-location checks.");
