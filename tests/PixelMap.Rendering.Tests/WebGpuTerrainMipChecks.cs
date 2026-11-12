using PixelMap.Rendering;

internal static class WebGpuTerrainMipChecks
{
    internal static void Run()
    {
        Assert(WebGpuTerrainMipmaps.LevelCount == 9 &&
            GpuTerrainTexture.Size >> (WebGpuTerrainMipmaps.LevelCount - 1) == 1,
            "Terrain artwork must retain its complete 256-to-1 mip chain.");
        Assert(WebGpuTerrainMipmaps.ArtworkBytes(96) == 33_554_304 &&
            WebGpuTerrainMipmaps.ScratchBytes(96) == 6_291_456,
            "Resident accounting must include every artwork mip and the single-level scratch array.");

        var mips = new WebGpuTerrainMipmaps(96);
        mips.MarkDirty(3);
        mips.MarkDirty(95);
        mips.Prepare(smooth: false);
        Assert(mips.PreparedLayers.IsEmpty, "A Pixel draw must not start smooth mip work.");
        mips.Commit();
        mips.Prepare(smooth: true);
        Assert(mips.PreparedLayers.SequenceEqual([3u, 95u]),
            "Smooth transition must include all earlier Pixel uploads, including the final array layer.");

        // A frame that was encoded but never submitted must keep both dirty
        // layers. Reusing a physical layer changes its source identity again.
        mips.MarkDirty(3);
        mips.Prepare(smooth: true);
        Assert(mips.PreparedLayers.SequenceEqual([3u, 95u]), "A failed submit discarded pending mip work.");
        mips.Commit();
        mips.Prepare(smooth: true);
        Assert(mips.PreparedLayers.IsEmpty, "A retained scene regenerated unchanged terrain mips.");

        mips.MarkDirty(3);
        mips.Prepare(smooth: true);
        mips.MarkDirty(3);
        mips.MarkDirty(8);
        mips.Commit();
        mips.Prepare(smooth: true);
        Assert(mips.PreparedLayers.SequenceEqual([3u, 8u]),
            "Committing an older batch must not acknowledge later writes or array-layer reassignment.");
        mips.Commit();

        mips.MarkDirty(1);
        mips.Prepare(smooth: true);
        // An interrupted smooth frame can be followed by a successful art frame.
        mips.Prepare(smooth: false);
        mips.Commit();
        mips.Prepare(smooth: true);
        Assert(mips.PreparedLayers.SequenceEqual([1u]), "Pixel submission acknowledged an unsubmitted smooth batch.");
        mips.Commit();
        mips.Prepare(smooth: true);
        Assert(mips.PreparedLayers.IsEmpty, "A successful mip batch remained dirty.");
        Console.WriteLine("Passed terrain mip ownership checks: deferred Pixel uploads, bounded array layers, failed-submit retry, reassignment and exact resident bytes.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
