const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const contexts = [];
const timers = new Map();
const listeners = new Map();
let sequence = 0;

class Parameter {
    value = 0;
    setTargetAtTime(value) { this.value = value; }
    setValueAtTime(value) { this.value = value; }
    exponentialRampToValueAtTime(value) { this.value = value; }
}

class Node {
    gain = new Parameter();
    frequency = new Parameter();
    Q = new Parameter();
    stopped = false;
    disconnected = false;
    connections = [];
    connect(target) { this.connections.push(target); return target; }
    disconnect() { this.disconnected = true; }
    start(time) { this.startedAt = time ?? 0; }
    stop(time) { this.stopped = true; this.stoppedAt = time ?? 0; }
    end() { this.onended?.(); }
}

class AudioContext {
    state = "suspended";
    currentTime = 0;
    sampleRate = 48000;
    destination = new Node();
    nodes = [];
    oscillators = [];
    resumes = 0;
    constructor() { contexts.push(this); }
    node() { const node = new Node(); this.nodes.push(node); return node; }
    createGain() { return this.node(); }
    createBiquadFilter() { return this.node(); }
    createBufferSource() { return this.node(); }
    createOscillator() { const node = this.node(); this.oscillators.push(node); return node; }
    createBuffer(channels, length) { return { getChannelData: () => new Float32Array(length) }; }
    async resume() { this.state = "running"; this.resumes++; }
    async suspend() { this.state = "suspended"; }
    async close() { this.state = "closed"; }
}

const document = { visibilityState: "visible", addEventListener: (event, handler) => listeners.set(event, handler) };
const sandbox = {
    AudioContext, document,
    setTimeout(callback) { const id = ++sequence; timers.set(id, callback); return id; },
    clearTimeout(id) { timers.delete(id); }
};
const source = fs.readFileSync(path.resolve(__dirname, "../../src/PixelMap.Audio/Browser/journey-audio.js"), "utf8");
vm.runInNewContext(source, sandbox);
const audio = sandbox.PixelAtlasAudio;
assert.equal(contexts.length, 0, "Loading the adapter leaves audio dormant");
audio.enable(1, false);
audio.update(1, "Drive", 1, 1, true);
audio.cue(1, "Drive", "Horn");
assert.equal(contexts.length, 0, "Muted activity cannot construct a context");
audio.enable(1, true);
const first = contexts[0];
assert.equal(first.state, "running");
assert.equal(first.oscillators.length, 2, "Two long-lived oscillators");
const frequencies = new Set();
for (const mode of ["Drive", "Boat", "Flight", "Train", "Bike", "Walk"]) {
    audio.update(1, mode, .6, .7, true);
    const motor = first.oscillators[0];
    assert.ok(Number.isFinite(motor.frequency.value));
    frequencies.add(motor.frequency.value);
}
assert.equal(frequencies.size, 6, "Every vehicle receives its own sound signature");
for (let i = 0; i < 30; i++) audio.cue(1, "Train", "Horn");
assert.equal(first.oscillators.length, 20, "Transient voices are capped at 18");
for (const oscillator of first.oscillators.slice(2)) {
    assert.ok(oscillator.stoppedAt > oscillator.startedAt);
    oscillator.end();
    assert.ok(oscillator.disconnected, "Completed voices release their graph");
}
audio.cue(1, "Drive", "Checkpoint");
assert.equal(first.oscillators.length, 22, "Completed voices free capacity");
audio.update(1, "Drive", 0, 0, false);
for (const [id, callback] of [...timers]) { timers.delete(id); callback(); }
assert.equal(first.state, "suspended", "A paused journey suspends after its release envelope");
audio.update(1, "Drive", .3, .5, true);
assert.equal(first.state, "running");
document.visibilityState = "hidden";
listeners.get("visibilitychange")();
assert.equal(first.state, "suspended", "Hidden pages suspend their graphs");
const resumeCount = first.resumes;
audio.update(1, "Drive", .4, .7, true);
assert.equal(first.resumes, resumeCount, "Background ticks cannot wake audio");
document.visibilityState = "visible";
listeners.get("visibilitychange")();
assert.equal(first.state, "running");
audio.enable(2, true);
assert.equal(contexts.length, 2, "Maps have independent graphs");
audio.close(1);
assert.equal(first.state, "closed");
assert.ok(first.oscillators.every(oscillator => oscillator.stopped));
assert.equal(contexts[1].state, "running", "Disposing a map preserves the other map");
audio.close(1);
audio.enable(1, true);
assert.equal(contexts.length, 3, "A reloaded map can recreate its graph");
audio.close(1);
audio.close(2);
const unsupported = { setTimeout, clearTimeout };
vm.runInNewContext(source, unsupported);
assert.doesNotThrow(() => unsupported.PixelAtlasAudio.enable(1, true));
console.log("Audio checks passed for six instruments, activation, bounded cues, pause, visibility and disposal.");
