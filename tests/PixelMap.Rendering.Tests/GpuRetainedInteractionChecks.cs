using System.Reflection;
using PixelMap.Data;
using PixelMap.Rendering;
using SkiaSharp;

/// <summary>GPU-only views must expose the same retained terrain/feature
/// interaction as frames with a legacy picture, with unchanged readiness guards.</summary>
internal static class GpuRetainedInteractionChecks
{
    private const int Width = 256, Height = 192;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private sealed class Totals
    {
        internal int Comparisons, TerrainHits, Projections, Features, Views;
    }

    internal static void Run()
    {
        var totals = new Totals();
        var tiles = GpuDepthIndexChecks.Tiles();
        var terrain = new TerrainOptions(GpuDepthIndexChecks.Elevation(), Exaggeration: 2.1);
        var center = GpuDepthIndexChecks.Geo(.5, .5);
        double ground = terrain.Elevation.SampleSurfaceMeters(center.Latitude, center.Longitude);
        var origin = new MapCamera(center.Latitude, center.Longitude, 15.4, 23, 55);
        var cameras = new[]
        {
            origin with { Pitch = 0 }, origin, origin with { Perspective = true },
            MapCamera.FromEye(center.Latitude, center.Longitude, ground + 1.7, 23, 90, Width, Height),
            origin with { Longitude = origin.Longitude + 360, Bearing = 131, Perspective = true }
        };
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        {
            using var gpu = new PixelMapRenderer();
            using var legacy = new PixelMapRenderer();
            RejectUnavailable(gpu, cameras[0], Width, Height, "unprepared renderer", totals);
            foreach (var source in cameras)
            {
                var camera = source.OnTerrain(terrain, Height);
                using var expected = legacy.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null, terrain, style);
                using var actual = gpu.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null, terrain, style,
                    prepareLegacyOverlay: false);
                Assert(actual.Overlay is null && Field(gpu, "lastPicture") is null,
                    "The GPU-only interaction fixture accidentally prepared a legacy picture.");
                Compare(gpu, legacy, camera, Width, Height, tiles, $"{style}, pitch {camera.Pitch}", totals);
                RejectMismatched(gpu, camera, Width, Height, totals);
            }
        }
        RetainedFrames(tiles, terrain, cameras[2], totals);
        RetainedOwnership(tiles, terrain, cameras[2], totals);
        CpuTransitions(tiles, terrain, cameras[2], totals);
        NoVisualTerrain(tiles, terrain, cameras[2], totals);
        Assert(totals.TerrainHits > 0 && totals.Projections > 0 && totals.Features > 0,
            "The retained interaction fixture did not exercise successful terrain, projection and feature answers.");
        Console.WriteLine($"Passed {totals.Comparisons:N0} exact retained-view interaction checks across {totals.Views} views: " +
            $"{totals.TerrainHits:N0} terrain hits, {totals.Projections:N0} mesh projections, {totals.Features:N0} feature picks; " +
            "GPU-only/legacy equality, A→B→A, caller/renderer ownership, cached frame wrappers, " +
            "camera/viewport guards, pooled reuse and GPU→CPU→GPU transitions.");
    }

    private static void RetainedFrames(MapTile[] tiles, TerrainOptions terrain, MapCamera source, Totals totals)
    {
        using var gpu = new PixelMapRenderer();
        using var legacy = new PixelMapRenderer();
        var camera = source.OnTerrain(terrain, Height);
        using var expected = legacy.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Night, 2, null, terrain);
        using var first = gpu.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Night, 2, null, terrain, MapRenderStyle.Pixel,
            prepareLegacyOverlay: false);
        Compare(gpu, legacy, camera, Width, Height, tiles, "retained A before B", totals);
        var changed = new TerrainOptions(GpuDepthIndexChecks.Elevation(95), Exaggeration: 3.2);
        var otherCamera = (source with { Bearing = 167, Pitch = 72 }).OnTerrain(changed, Height + 29);
        using (var otherReference = new PixelMapRenderer())
        using (var otherExpected = otherReference.PrepareGpuFrame(Width + 37, Height + 29, otherCamera, tiles,
            PixelStyle.Day, 1, null, changed, MapRenderStyle.Roblox))
        using (var next = gpu.PrepareGpuFrame(Width + 37, Height + 29, otherCamera, tiles,
            PixelStyle.Day, 1, null, changed, MapRenderStyle.Roblox, prepareLegacyOverlay: false))
        {
            Compare(gpu, otherReference, otherCamera, Width + 37, Height + 29, tiles, "retained B", totals);
            RejectUnavailable(gpu, camera, Width, Height, "unactivated held frame A", totals);
            gpu.ActivateGpuFrame(first);
            Assert(Field(gpu, "lastPicture") is null, "Reactivating GPU-only A created a legacy picture.");
            Compare(gpu, legacy, camera, Width, Height, tiles, "retained A while B is held", totals);
            gpu.ActivateGpuFrame(next);
            Compare(gpu, otherReference, otherCamera, Width + 37, Height + 29, tiles, "retained B again", totals);
        }
        gpu.ActivateGpuFrame(first);
        Compare(gpu, legacy, camera, Width, Height, tiles, "retained A after B disposal", totals);
    }

    private static void RetainedOwnership(MapTile[] tiles, TerrainOptions terrain, MapCamera source, Totals totals)
    {
        var camera = source.OnTerrain(terrain, Height);
        foreach (bool picture in new[] { false, true })
        {
            using var renderer = new PixelMapRenderer();
            using var reference = new PixelMapRenderer();
            using var expected = reference.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null, terrain);
            using var frame = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null, terrain, MapRenderStyle.Pixel,
                prepareLegacyOverlay: picture);
            Assert((Field(renderer, "lastPicture") is not null) == picture, "The disposal fixture has the wrong picture state.");
            var depth = frame.Depth;
            object storage = Field(depth, "storage")!;
            Assert(DepthReferences(depth) == 2, "The caller and renderer did not independently retain depth.");
            int beforeHits = totals.TerrainHits, beforeProjections = totals.Projections, beforeFeatures = totals.Features;
            Compare(renderer, reference, camera, Width, Height, tiles, $"before caller disposal, picture={picture}", totals);
            Assert(totals.TerrainHits > beforeHits && totals.Projections > beforeProjections && totals.Features > beforeFeatures,
                "The ownership fixture did not establish successful pre-disposal interaction results.");
            frame.Dispose();
            Assert(DepthReferences(depth) == 1 && ReferenceEquals(Field(renderer, "activeGpuDepth"), depth),
                "Caller disposal released the renderer's retained view.");
            // The separate reference frame stays alive and unchanged. Exact
            // agreement before and after disposal anchors the retained answers
            // to the same successful pre-disposal terrain/feature/projection view.
            Compare(renderer, reference, camera, Width, Height, tiles, $"caller released frame, picture={picture}", totals);
            RejectMismatched(renderer, camera, Width, Height, totals);
            ThrowsDisposed(() => renderer.ActivateGpuFrame(frame), "A disposed frame could be reactivated.");
            totals.Comparisons++;

            // A style change releases the old renderer reference. Its workspace
            // can immediately belong to another frame, while the disposed caller
            // object still exists. Eligibility must track the current owned depth.
            var nextCamera = (source with { Bearing = source.Bearing + 37 }).OnTerrain(terrain, Height + 17);
            using var other = reference.PrepareGpuFrame(Width + 23, Height + 17, nextCamera, tiles,
                PixelStyle.Night, 1, null, terrain, MapRenderStyle.Normal);
            using var next = renderer.PrepareGpuFrame(Width + 23, Height + 17, nextCamera, tiles,
                PixelStyle.Night, 1, null, terrain, MapRenderStyle.Normal, prepareLegacyOverlay: picture);
            Assert(DepthReferences(depth) == 0 && !ReferenceEquals(next.Depth, depth) &&
                ReferenceEquals(Field(next.Depth, "storage"), storage),
                "Replacing the caller-released view did not release and reuse its depth workspace.");
            RejectUnavailable(renderer, camera, Width, Height, "released view after replacement", totals);
            Compare(renderer, reference, nextCamera, Width + 23, Height + 17, tiles, "new owner of recycled depth storage", totals);
            next.Dispose();
            Assert(DepthReferences(next.Depth) == 1, "Caller disposal invalidated the replacement view.");
            ReleaseGpuScene(renderer);
            Assert(Field(renderer, "activeGpuDepth") is null && Field(renderer, "activeGpuScene") is null &&
                DepthReferences(next.Depth) == 0 && (Field(renderer, "lastPicture") is not null) == picture,
                "Releasing renderer ownership did not invalidate the view independently of its picture.");
            RejectUnavailable(renderer, nextCamera, Width + 23, Height + 17, "released renderer depth with old picture", totals);
            using var unrelated = new PixelMapRenderer();
            using var unrelatedFrame = unrelated.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null,
                terrain, MapRenderStyle.Pixel, prepareLegacyOverlay: false);
            Assert(ReferenceEquals(Field(unrelatedFrame.Depth, "storage"), storage),
                "The ownership fixture did not reuse released storage in an unrelated renderer.");
            RejectUnavailable(renderer, nextCamera, Width + 23, Height + 17, "released view after unrelated pool reuse", totals);
        }
        CachedFrameOwnership(tiles, terrain, camera, totals);
        var disposed = new PixelMapRenderer();
        using var held = disposed.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null, terrain, MapRenderStyle.Pixel,
            prepareLegacyOverlay: false);
        disposed.Dispose();
        ThrowsDisposed(() => disposed.TryPickTerrain(Width * .5, Height * .7, Width, Height, camera, out _),
            "A disposed renderer accepted terrain picking.");
        ThrowsDisposed(() => disposed.TryProjectTerrain(camera.Latitude, camera.Longitude, Width, Height, camera, out _),
            "A disposed renderer accepted terrain projection.");
        ThrowsDisposed(() => disposed.Pick(Width * .5, Height * .7, Width, Height, camera, tiles),
            "A disposed renderer accepted feature picking.");
        totals.Comparisons += 3;
    }

    private static void CachedFrameOwnership(MapTile[] tiles, TerrainOptions terrain, MapCamera camera, Totals totals)
    {
        using var renderer = new PixelMapRenderer();
        using var reference = new PixelMapRenderer();
        using var expected = reference.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null, terrain);
        using var first = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null,
            terrain, MapRenderStyle.Pixel, prepareLegacyOverlay: false);
        using var repeated = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null,
            terrain, MapRenderStyle.Pixel, prepareLegacyOverlay: false);
        Assert(!ReferenceEquals(first, repeated) && ReferenceEquals(first.Depth, repeated.Depth) &&
            DepthReferences(first.Depth) == 3, "The cached-frame fixture did not share independently owned depth.");
        Compare(renderer, reference, camera, Width, Height, tiles, "both cached wrappers owned", totals);
        first.Dispose();
        Assert(DepthReferences(repeated.Depth) == 2, "Disposing the first wrapper released another owner's depth.");
        Compare(renderer, reference, camera, Width, Height, tiles, "first cached wrapper released", totals);
        repeated.Dispose();
        Assert(DepthReferences(repeated.Depth) == 1, "Disposing both wrappers released renderer-owned depth.");
        Compare(renderer, reference, camera, Width, Height, tiles, "both cached wrappers released", totals);
        ThrowsDisposed(() => renderer.ActivateGpuFrame(first), "A disposed original wrapper could be activated.");
        ThrowsDisposed(() => renderer.ActivateGpuFrame(repeated), "A disposed cached wrapper could be activated.");
        totals.Comparisons += 2;
        // Cached preparation may also create a new caller wrapper after every
        // previous caller has released its frame, using the renderer's ownership.
        using var renewed = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null,
            terrain, MapRenderStyle.Pixel, prepareLegacyOverlay: false);
        Assert(ReferenceEquals(renewed.Depth, first.Depth) && DepthReferences(renewed.Depth) == 2,
            "Cached preparation failed to renew caller ownership of the renderer's retained depth.");
        Compare(renderer, reference, camera, Width, Height, tiles, "renewed wrapper after earlier callers disposed", totals);
        renewed.Dispose();
        Assert(DepthReferences(renewed.Depth) == 1, "The renewed wrapper released renderer ownership.");
        ThrowsDisposed(() => renderer.ActivateGpuFrame(renewed), "A disposed renewed wrapper could be activated.");
        totals.Comparisons++;
        ReleaseGpuScene(renderer);
        Assert(DepthReferences(repeated.Depth) == 0, "The renderer failed to release its cached-wrapper depth.");
        RejectUnavailable(renderer, camera, Width, Height, "all cached-wrapper owners released", totals);
    }

    private static void CpuTransitions(MapTile[] tiles, TerrainOptions terrain, MapCamera source, Totals totals)
    {
        using var renderer = new PixelMapRenderer();
        using var gpuReference = new PixelMapRenderer();
        using var cpuReference = new PixelMapRenderer();
        var camera = source.OnTerrain(terrain, Height);
        using var held = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null, terrain, MapRenderStyle.Pixel,
            prepareLegacyOverlay: false);
        using var expected = gpuReference.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null, terrain);
        using var actualBitmap = new SKBitmap(Width, Height);
        using var expectedBitmap = new SKBitmap(Width, Height);
        using var actualCanvas = new SKCanvas(actualBitmap);
        using var expectedCanvas = new SKCanvas(expectedBitmap);
        var cpuCamera = (source with { Bearing = 94, Perspective = false, Pitch = 62 }).OnTerrain(terrain, Height);
        renderer.Draw(actualCanvas, Width, Height, cpuCamera, tiles, PixelStyle.Day, 2, null, false, false, terrain);
        cpuReference.Draw(expectedCanvas, Width, Height, cpuCamera, tiles, PixelStyle.Day, 2, null, false, false, terrain);
        Assert(ReferenceEquals(Field(renderer, "activeGpuScene"), held),
            "The CPU-transition fixture did not retain its stale GPU scene.");
        Assert(actualBitmap.GetPixelSpan().SequenceEqual(expectedBitmap.GetPixelSpan()), "The retained-view guard changed CPU drawing.");
        Compare(renderer, cpuReference, cpuCamera, Width, Height, tiles, "GPU→CPU with retained GPU scene", totals);
        RejectUnavailable(renderer, camera, Width, Height, "old GPU camera after CPU draw", totals);
        renderer.ActivateGpuFrame(held);
        Compare(renderer, gpuReference, camera, Width, Height, tiles, "GPU→CPU→GPU", totals);
        Assert(Field(renderer, "lastPicture") is not null,
            "The GPU-reactivation fixture did not retain the unrelated CPU picture.");
        held.Dispose();
        Assert(DepthReferences(held.Depth) == 1, "The caller released the GPU view still owned by the renderer.");
        Compare(renderer, gpuReference, camera, Width, Height, tiles, "caller-released GPU view with unrelated CPU picture", totals);
        // A later successful CPU draw owns its eligibility even though the old
        // GPU frame's caller has disposed that wrapper.
        renderer.Draw(actualCanvas, Width, Height, cpuCamera, tiles, PixelStyle.Day, 2, null, false, false, terrain);
        Compare(renderer, cpuReference, cpuCamera, Width, Height, tiles, "CPU after GPU caller disposal", totals);
        ReleaseGpuScene(renderer);
        Assert(DepthReferences(held.Depth) == 0, "Releasing the stale GPU view failed to release its depth.");
        Compare(renderer, cpuReference, cpuCamera, Width, Height, tiles, "CPU after renderer releases GPU ownership", totals);
    }

    private static void NoVisualTerrain(MapTile[] tiles, TerrainOptions terrain, MapCamera camera, Totals totals)
    {
        using var renderer = new PixelMapRenderer();
        using (var frame = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null, null, MapRenderStyle.Pixel,
            prepareLegacyOverlay: false))
            RejectUnavailable(renderer, camera, Width, Height, "GPU frame without terrain", totals);
        using (var frame = renderer.PrepareGpuFrame(Width, Height, camera, tiles, PixelStyle.Day, 2, null,
            terrain with { Relief = false }, MapRenderStyle.Pixel, prepareLegacyOverlay: false))
            RejectUnavailable(renderer, camera, Width, Height, "hidden relief without elevation tint", totals);
        using var reference = new PixelMapRenderer();
        var flat = terrain with { Relief = false, Hypsometric = true };
        var view = camera.OnTerrain(flat, Height);
        using var expected = reference.PrepareGpuFrame(Width, Height, view, tiles, PixelStyle.Day, 2, null, flat);
        using var actual = renderer.PrepareGpuFrame(Width, Height, view, tiles, PixelStyle.Day, 2, null, flat, MapRenderStyle.Pixel,
            prepareLegacyOverlay: false);
        Compare(renderer, reference, view, Width, Height, tiles, "flat elevation tint", totals);
    }

    private static void Compare(PixelMapRenderer actual, PixelMapRenderer expected, MapCamera camera,
        int width, int height, MapTile[] tiles, string context, Totals totals)
    {
        for (int index = 0; index < 96; index++)
        {
            double x = (index * 97 + 13) % width + .37, y = (index * 67 + 5) % height + .83;
            bool a = actual.TryPickTerrain(x, y, width, height, camera, out var hit);
            bool b = expected.TryPickTerrain(x, y, width, height, camera, out var reference);
            Assert(a == b && Exact(hit, reference), $"Retained terrain pick differs at {x},{y}: {context}.");
            totals.Comparisons++;
            if (a) totals.TerrainHits++;
            if (index % 3 == 0)
            {
                var feature = actual.Pick(x, y, width, height, camera, tiles);
                var otherFeature = expected.Pick(x, y, width, height, camera, tiles);
                Assert(feature == otherFeature, $"Retained feature pick differs at {x},{y}: {context}.");
                totals.Comparisons++;
                if (feature is not null) totals.Features++;
            }
        }
        for (int row = 0; row <= 6; row++)
        for (int column = 0; column <= 6; column++)
        {
            var point = GpuDepthIndexChecks.Geo(.2 + column * .1, .2 + row * .1);
            bool a = actual.TryProjectTerrain(point.Latitude, point.Longitude, width, height, camera, out var screen);
            bool b = expected.TryProjectTerrain(point.Latitude, point.Longitude, width, height, camera, out var other);
            Assert(a == b && Exact(screen.X, other.X) && Exact(screen.Y, other.Y),
                $"Retained geographic projection differs at {point}: {context}.");
            totals.Comparisons++;
            if (a) totals.Projections++;
        }
        totals.Views++;
    }

    private static void RejectMismatched(PixelMapRenderer renderer, MapCamera camera, int width, int height, Totals totals)
    {
        RejectUnavailable(renderer, camera with { Bearing = camera.Bearing + 1 }, width, height, "camera mismatch", totals);
        RejectUnavailable(renderer, camera, width + 1, height, "width mismatch", totals);
        RejectUnavailable(renderer, camera, width, height + 1, "height mismatch", totals);
        foreach (var point in new[] { (-1d, height * .5), (width * .5, -1d), ((double)width, height * .5),
                     (width * .5, (double)height), (double.NaN, 0d), (0d, double.PositiveInfinity) })
        {
            Assert(!renderer.TryPickTerrain(point.Item1, point.Item2, width, height, camera, out var hit) && Exact(hit, default),
                "An invalid screen position was accepted for retained terrain picking.");
            totals.Comparisons++;
        }
        foreach (double invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        {
            Assert(!renderer.TryProjectTerrain(invalid, camera.Longitude, width, height, camera, out var screen) &&
                Exact(screen.X, 0) && Exact(screen.Y, 0), "Nonfinite latitude was accepted by retained terrain projection.");
            Assert(!renderer.TryProjectTerrain(camera.Latitude, invalid, width, height, camera, out screen) &&
                Exact(screen.X, 0) && Exact(screen.Y, 0), "Nonfinite longitude was accepted by retained terrain projection.");
            totals.Comparisons += 2;
        }
    }

    private static void RejectUnavailable(PixelMapRenderer renderer, MapCamera camera, int width, int height,
        string context, Totals totals)
    {
        Assert(!renderer.TryPickTerrain(width * .5, height * .7, width, height, camera, out var hit) && Exact(hit, default),
            $"Unavailable terrain view accepted picking: {context}.");
        Assert(!renderer.TryProjectTerrain(camera.Latitude, camera.Longitude, width, height, camera, out var screen) &&
            Exact(screen.X, 0) && Exact(screen.Y, 0), $"Unavailable terrain view accepted projection: {context}.");
        totals.Comparisons += 2;
    }

    private static object? Field(object value, string name) => value.GetType().GetField(name, Members)!.GetValue(value);
    private static int DepthReferences(GpuSceneDepth depth) => (int)Field(depth, "references")!;
    private static void ReleaseGpuScene(PixelMapRenderer renderer) =>
        typeof(PixelMapRenderer).GetMethod("ReleaseGpuScene", Members)!.Invoke(renderer, null);
    private static bool Exact(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    private static bool Exact(TerrainHit a, TerrainHit b) => Exact(a.Latitude, b.Latitude) && Exact(a.Longitude, b.Longitude) &&
        Exact(a.ElevationMeters, b.ElevationMeters);
    private static void ThrowsDisposed(Action action, string message)
    {
        try { action(); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
