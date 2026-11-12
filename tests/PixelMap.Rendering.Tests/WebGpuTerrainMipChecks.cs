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
        Assert(mips.PreparedLayers.IsEmpty && mips.PreparedCopies.IsEmpty, "A Pixel draw must not start smooth mip work.");
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
        CheckCopyRuns();
        Console.WriteLine("Passed terrain mip ownership and array-copy checks: deferred Pixel uploads, bounded layers, failed-submit retry, reassignment, unchanged holes, coalesced runs and exact resident bytes.");
    }

    private static void CheckCopyRuns()
    {
        // Exhaust all dirty subsets in a small array. Expand the planned copies
        // independently, then compare them with the source mask, including all
        // clean holes. Native/GPU tests separately verify the actual texel data.
        for (int mask = 0; mask < 1024; mask++)
        {
            var mips = new WebGpuTerrainMipmaps(10);
            var expected = Enumerable.Range(0, 10).Where(layer => (mask & (1 << layer)) != 0).Select(layer => (uint)layer).ToArray();
            foreach (uint layer in expected) mips.MarkDirty((int)layer);
            mips.Prepare(smooth: true);
            var copied = new List<uint>();
            foreach (var run in mips.PreparedCopies)
            {
                Assert(run.LayerCount > 0 && run.FirstLayer + run.LayerCount <= 10, "An array copy exceeds the physical layer bounds.");
                for (uint offset = 0; offset < run.LayerCount; offset++) copied.Add(run.FirstLayer + offset);
            }
            Assert(copied.SequenceEqual(expected), "An array copy touched a clean layer, duplicated work or omitted a dirty layer.");
            int expectedRuns = System.Numerics.BitOperations.PopCount((uint)(mask & ~(mask << 1)));
            Assert(mips.PreparedCopies.Length == expectedRuns, "Consecutive layers did not share the minimal number of array copies.");
            mips.Commit();
            Assert(mips.PreparedCopies.IsEmpty, "A submitted batch retained pending copy ranges.");
        }

        var full = new WebGpuTerrainMipmaps(96);
        for (int layer = 95; layer >= 0; layer--) full.MarkDirty(layer);
        full.Prepare(smooth: true);
        Assert(full.PreparedCopies.SequenceEqual([new WebGpuTerrainMipmaps.CopyRun(0, 96)]),
            "The complete 96-layer upload must require one copy per mip, not 96.");
        // A failed encoding is retried with the exact same dirty range.
        full.Prepare(smooth: true);
        Assert(full.PreparedCopies.SequenceEqual([new WebGpuTerrainMipmaps.CopyRun(0, 96)]), "A failed batch lost its coalesced layer range.");
        full.Commit();
        full.MarkDirty(0);
        full.MarkDirty(95);
        full.Prepare(smooth: true);
        Assert(full.PreparedCopies.SequenceEqual([new WebGpuTerrainMipmaps.CopyRun(0, 1), new WebGpuTerrainMipmaps.CopyRun(95, 1)]),
            "Wraparound layer reuse must not copy the unchanged array interior.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
