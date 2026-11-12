namespace PixelMap.Rendering;

/// <summary>Publishes scene reuse only after submission. Overlay-only frames do not
/// advance residency age, so the buffers they still reference remain pinned.</summary>
internal sealed class WebGpuScenePreparation
{
    private Key current;
    private bool submitted;
    internal long Generation { get; private set; }

    internal bool Begin(in Key key)
    {
        if (submitted && current.Matches(key)) return false;
        current = key;
        submitted = false;
        Generation++;
        return true;
    }

    internal void Commit() => submitted = current.Frame is not null;

    internal void Invalidate()
    {
        current = default;
        submitted = false;
    }

    internal bool IsExpired(long lastUsed) => Generation - lastUsed > 180;

    internal readonly record struct Key(GpuMapFrame Frame, WebGpuQuality Quality,
        uint TargetWidth, uint TargetHeight, int RenderWidth, int RenderHeight, int ShadowSize)
    {
        internal bool Matches(in Key other) => ReferenceEquals(Frame, other.Frame) && Quality == other.Quality &&
            TargetWidth == other.TargetWidth && TargetHeight == other.TargetHeight &&
            RenderWidth == other.RenderWidth && RenderHeight == other.RenderHeight && ShadowSize == other.ShadowSize;
    }
}
