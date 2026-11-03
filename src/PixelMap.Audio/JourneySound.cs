using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using PixelMap.Simulation;

namespace PixelMap.Audio;

/// <summary>Supplies normalized motion parameters and discrete game cues to a replaceable audio device.</summary>
public interface IJourneyAudioDevice
{
    void Enable(bool enabled);
    void Update(string instrument, double speed, double load, bool running);
    void Play(string instrument, JourneyCue cue);
    /// <summary>Release the current audio graph. Repeated calls are safe, and a later Enable(true)
    /// may recreate resources when a map view reloads.</summary>
    void Close();
}

public sealed class JourneySound(IJourneyAudioDevice device) : IDisposable
{
    private readonly IJourneyAudioDevice _device = device ?? throw new ArgumentNullException(nameof(device));
    private bool _enabled, _disposed;
    private (string Instrument, double Speed, double Load, bool Running)? _previous;
    public void Update(JourneySession session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);
        bool enabled = !session.Muted;
        if (_enabled != enabled)
        {
            _enabled = enabled;
            _device.Enable(enabled);
            _previous = null;
        }
        if (!enabled) return;
        double speed = Math.Clamp(session.SpeedMetersPerSecond / session.Vehicle.MaxSpeed, 0, 1);
        var frame = (Instrument: session.Vehicle.Mode.ToString(), Speed: Math.Round(speed, 2),
            Load: Math.Round(session.EngineLoad, 2), Running: session.Active);
        if (_previous != frame) { _device.Update(frame.Instrument, frame.Speed, frame.Load, frame.Running); _previous = frame; }
    }
    public void Play(JourneySession session, JourneyCue cue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Muted) { Update(session); _device.Play(session.Vehicle.Mode.ToString(), cue); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device.Close();
        _enabled = false;
        _previous = null;
    }
}

[SupportedOSPlatform("browser")]
public sealed partial class BrowserAudioDevice : IJourneyAudioDevice
{
    private static int _sequence;
    private readonly int _id = Interlocked.Increment(ref _sequence);
    [JSImport("globalThis.PixelAtlasAudio.enable")] private static partial void EnableVoice(int id, bool enabled);
    [JSImport("globalThis.PixelAtlasAudio.update")] private static partial void UpdateVoice(int id, string mode, double speed, double load, bool running);
    [JSImport("globalThis.PixelAtlasAudio.cue")] private static partial void PlayCue(int id, string mode, string cue);
    [JSImport("globalThis.PixelAtlasAudio.close")] private static partial void CloseVoice(int id);
    public void Enable(bool enabled) => EnableVoice(_id, enabled);
    public void Update(string instrument, double speed, double load, bool running) => UpdateVoice(_id, instrument, speed, load, running);
    public void Play(string instrument, JourneyCue cue) => PlayCue(_id, instrument, cue.ToString());
    public void Close() => CloseVoice(_id);
}
