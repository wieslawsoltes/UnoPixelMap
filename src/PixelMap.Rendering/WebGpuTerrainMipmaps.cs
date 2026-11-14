using System.Numerics;

namespace PixelMap.Rendering;

/// <summary>Tracks uploaded array layers until their complete mip chain has been
/// submitted. Encoding, a failed submission, and a Pixel draw do not make a dirty
/// chain reusable. Layer reassignment invalidates even an already prepared batch.</summary>
internal sealed class WebGpuTerrainMipmaps(int capacity)
{
    internal static readonly int LevelCount = BitOperations.Log2((uint)GpuTerrainTexture.Size) + 1;
    internal const int Anisotropy = 8;
    internal const int WorkgroupSize = 8;
    internal static int ScratchSize => GpuTerrainTexture.Size / 2;

    private readonly ulong[] revisions = new ulong[capacity];
    private readonly ulong[] submitted = new ulong[capacity];
    private readonly ulong[] encoded = new ulong[capacity];
    private readonly uint[] layers = new uint[capacity];
    private readonly CopyRun[] copies = new CopyRun[capacity];
    private int count, copyCount;

    internal ReadOnlySpan<uint> PreparedLayers => layers.AsSpan(0, count);
    internal ReadOnlySpan<CopyRun> PreparedCopies => copies.AsSpan(0, copyCount);

    // Scratch and artwork share physical array-layer IDs. One array copy can
    // cover each consecutive dirty run without touching an unchanged layer.
    internal readonly record struct CopyRun(uint FirstLayer, uint LayerCount);

    internal void MarkDirty(int layer) => revisions[layer]++;

    internal void Prepare(bool smooth)
    {
        count = copyCount = 0;
        if (!smooth) return;
        for (int i = 0; i < revisions.Length; i++)
        {
            if (revisions[i] == submitted[i]) continue;
            layers[count++] = (uint)i;
            encoded[i] = revisions[i];
            if (copyCount > 0 && copies[copyCount - 1].FirstLayer + copies[copyCount - 1].LayerCount == i)
            {
                var previous = copies[copyCount - 1];
                copies[copyCount - 1] = previous with { LayerCount = previous.LayerCount + 1 };
            }
            else copies[copyCount++] = new((uint)i, 1);
        }
    }

    internal void Commit()
    {
        foreach (uint layer in PreparedLayers) submitted[layer] = encoded[layer];
        count = copyCount = 0;
    }

    internal static long ArtworkBytes(int layers)
    {
        long texels = 0;
        for (int size = GpuTerrainTexture.Size; size > 0; size /= 2) texels += (long)size * size;
        return texels * layers * sizeof(uint);
    }

    internal static long ScratchBytes(int layers) => (long)ScratchSize * ScratchSize * layers * sizeof(uint);
}
