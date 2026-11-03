// Each map owns an independent, gesture-activated Web Audio graph.
(() => {
    const voices = new Map();
    const instruments = {
        Drive: { wave: "sawtooth", idle: 31, range: 91, motor: .22, air: .08, cutoff: 650, rhythm: 0 },
        Boat: { wave: "triangle", idle: 33, range: 57, motor: .37, air: .27, cutoff: 930, rhythm: 0 },
        Flight: { wave: "sawtooth", idle: 44, range: 74, motor: .22, air: .31, cutoff: 1700, rhythm: 0 },
        Train: { wave: "sine", idle: 48, range: 165, motor: .28, air: .13, cutoff: 1120, rhythm: 13 },
        Bike: { wave: "triangle", idle: 64, range: 28, motor: .025, air: .17, cutoff: 1380, rhythm: 15 },
        Walk: { wave: "sine", idle: 53, range: 17, motor: 0, air: 0, cutoff: 500, rhythm: 3.3 }
    };

    function create(id) {
        const Audio = globalThis.AudioContext || globalThis.webkitAudioContext;
        if (!Audio) return;
        const ctx = new Audio({ latencyHint: "interactive" });
        const master = ctx.createGain();
        const bus = ctx.createGain();
        const motor = ctx.createOscillator();
        const motorGain = ctx.createGain();
        const low = ctx.createBiquadFilter();
        const noise = ctx.createBufferSource();
        const wind = ctx.createBiquadFilter();
        const windGain = ctx.createGain();
        const pulse = ctx.createOscillator();
        const pulseGain = ctx.createGain();
        const beat = ctx.createGain();
        const beatFilter = ctx.createBiquadFilter();
        const buffer = ctx.createBuffer(1, ctx.sampleRate * 2, ctx.sampleRate);
        const samples = buffer.getChannelData(0);
        let seed = 174077;
        for (let i = 0; i < samples.length; i++) {
            seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
            samples[i] = seed / 2147483648 - 1;
        }
        noise.buffer = buffer;
        noise.loop = true;
        low.type = "lowpass";
        wind.type = "bandpass";
        wind.Q.value = .45;
        beatFilter.type = "lowpass";
        beatFilter.frequency.value = 550;
        motor.connect(low).connect(motorGain).connect(bus);
        noise.connect(wind).connect(windGain).connect(bus);
        noise.connect(beatFilter).connect(beat).connect(bus);
        // A positive bias keeps the wheel and footstep noise envelope above zero.
        pulse.type = "sine";
        pulse.connect(pulseGain).connect(beat.gain);
        bus.gain.value = motorGain.gain.value = windGain.gain.value = beat.gain.value = pulseGain.gain.value = 0;
        bus.connect(master);
        master.gain.value = .23;
        master.connect(ctx.destination);
        motor.start();
        noise.start();
        pulse.start();
        const voice = {
            ctx, master, bus, motor, motorGain, low, noise, wind, windGain,
            pulse, pulseGain, beat, beatFilter, enabled: true, running: false, timer: 0, cues: new Set()
        };
        voices.set(id, voice);
        return voice;
    }

    function ramp(parameter, value, context, duration = .045) {
        parameter.setTargetAtTime(value, context.currentTime, duration);
    }

    function visible() {
        return typeof document === "undefined" || document.visibilityState !== "hidden";
    }

    function wake(voice) {
        clearTimeout(voice.timer);
        if (!visible()) {
            if (voice.ctx.state === "running") voice.ctx.suspend().catch(() => {});
            return;
        }
        if (voice.ctx.state === "suspended") voice.ctx.resume().catch(() => {});
    }

    function sleep(voice) {
        clearTimeout(voice.timer);
        voice.timer = setTimeout(() => {
            if (voice.ctx.state === "running") voice.ctx.suspend().catch(() => {});
        }, 750);
    }

    function enable(id, enabled) {
        let voice = voices.get(id);
        if (!voice && enabled) {
            try { voice = create(id); } catch { return; }
        }
        if (!voice) return;
        voice.enabled = enabled;
        ramp(voice.master.gain, enabled ? .23 : 0, voice.ctx, .025);
        if (enabled) wake(voice); else sleep(voice);
    }

    function update(id, mode, speed, load, running) {
        const voice = voices.get(id);
        if (!voice || !voice.enabled) return;
        voice.running = running;
        const instrument = instruments[mode] || instruments.Drive;
        const context = voice.ctx;
        if (!running) {
            ramp(voice.bus.gain, 0, context);
            sleep(voice);
            return;
        }
        wake(voice);
        const normalizedSpeed = Math.min(1, Math.max(0, speed));
        voice.motor.type = instrument.wave;
        const gear = mode === "Drive" ? (normalizedSpeed * 4.5) % 1 : normalizedSpeed;
        const revs = mode === "Drive" ? .65 * gear + .35 * normalizedSpeed : normalizedSpeed;
        ramp(voice.motor.frequency, instrument.idle + instrument.range * revs + load * 8, context);
        ramp(voice.low.frequency, instrument.cutoff * (.6 + 1.5 * normalizedSpeed + .4 * load), context);
        ramp(voice.motorGain.gain, instrument.motor * (.42 + .58 * Math.max(load, normalizedSpeed)), context);
        ramp(voice.wind.frequency, 330 + normalizedSpeed * 1500, context);
        ramp(voice.windGain.gain, instrument.air * normalizedSpeed, context);
        const steps = instrument.rhythm * normalizedSpeed;
        const volume = steps > 0 ? (mode === "Walk" ? .30 : .095) : 0;
        ramp(voice.pulse.frequency, Math.max(.01, steps), context);
        ramp(voice.pulseGain.gain, volume, context);
        ramp(voice.beat.gain, volume, context);
        ramp(voice.bus.gain, 1, context);
    }

    const horns = { Train: [311, 392, 465], Boat: [155, 196], Flight: [740, 990] };
    const signals = { Contact: [64, 47], Finish: [523, 659, 784], Checkpoint: [660, 880], Depart: [330, 440] };

    function cue(id, mode, name) {
        const voice = voices.get(id);
        if (!voice || !voice.enabled) return;
        const notes = name === "Horn" ? horns[mode] || [392, 494] : signals[name] || signals.Depart;
        if (voice.cues.size + notes.length > 18) return;
        wake(voice);
        const context = voice.ctx;
        notes.forEach((frequency, index) => {
            const oscillator = context.createOscillator();
            const amplitude = context.createGain();
            const start = context.currentTime + (name === "Horn" ? 0 : index * .09);
            const duration = name === "Horn" ? .42 : .18;
            oscillator.type = name === "Contact" ? "sawtooth" : name === "Horn" ? "triangle" : "sine";
            oscillator.frequency.setValueAtTime(frequency, start);
            amplitude.gain.setValueAtTime(.0001, start);
            amplitude.gain.exponentialRampToValueAtTime(.22, start + .018);
            amplitude.gain.exponentialRampToValueAtTime(.0001, start + duration);
            oscillator.connect(amplitude).connect(voice.master);
            voice.cues.add(oscillator);
            oscillator.onended = () => {
                voice.cues.delete(oscillator);
                oscillator.disconnect();
                amplitude.disconnect();
            };
            oscillator.start(start);
            oscillator.stop(start + duration + .02);
        });
        if (!voice.running) sleep(voice);
    }

    function close(id) {
        const voice = voices.get(id);
        if (!voice) return;
        voices.delete(id);
        clearTimeout(voice.timer);
        for (const source of [voice.motor, voice.noise, voice.pulse, ...voice.cues]) {
            try { source.stop(); } catch { /* Already ended sources are safe to release. */ }
        }
        voice.master.disconnect();
        voice.ctx.close().catch(() => {});
    }

    if (typeof document !== "undefined") document.addEventListener("visibilitychange", () => {
        for (const voice of voices.values()) {
            if (visible() && voice.enabled && (voice.running || voice.cues.size > 0)) wake(voice);
            else if (voice.ctx.state === "running") voice.ctx.suspend().catch(() => {});
        }
    });
    globalThis.PixelAtlasAudio = Object.freeze({ enable, update, cue, close });
})();
