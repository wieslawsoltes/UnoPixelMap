using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

internal static class WebGpuOverlayChecks
{
    internal static void Run()
    {
        Assert(Marshal.SizeOf<WebGpuOverlayVertex>() == 48, "The WebGPU overlay vertex ABI must be 48 bytes.");
        LabelAtlasMatchesLegacyText();
        VehicleAndRouteGeometry();
        AntimeridianAndNearPlane();
        OrthographicReliefRouteDepth();
        ParticleContinuity();
        Console.WriteLine("Passed WebGPU overlay checks: exact label atlas pixels/reuse, six vehicle modes in four styles, homogeneous clipping, antimeridian routing, premultiplication and bounded stationary/teleport particle lifecycle.");
    }

    private static void LabelAtlasMatchesLegacyText()
    {
        const int width = 903, height = 603;
        var tileId = new TileId(14, 8192, 8192);
        var center = new RoutePoint(Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (tileId.Y + .5) / (1 << tileId.Z)))) * 180 / Math.PI,
            (tileId.X + .5) / (1 << tileId.Z) * 360 - 180);
        var camera = new MapCamera(center.Latitude, center.Longitude, 15.4, 0, 0);
        var tile = new MapTile(tileId, [Label(1, "Racibórz", 1400, 1400), Label(2, "Helena Street", 2700, 2048),
            Label(3, "Śródmieście", 2048, 2700)]);
        using var renderer = new PixelMapRenderer();
        TestLabelFont.Apply(renderer);
        using var frame = renderer.PrepareGpuFrame(width, height, camera, [tile], PixelStyle.Day, 3, null, null,
            MapRenderStyle.Pixel, prepareLegacyOverlay: false);
        Assert(frame.Overlay is null, "The native WebGPU path recorded a legacy Skia frame.");
        using var overlay = new WebGpuSceneOverlay();
        renderer.PrepareWebGpuOverlay(frame, overlay);
        Assert(overlay.LabelCount >= 2 && overlay.Atlas is not null, "Label atlas fixture must contain readable Unicode text.");
        Validate(overlay.ScreenVertices, overlay.ScreenIndices);
        using var expected = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var actual = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(expected))
        {
            canvas.Clear(SKColors.Transparent);
            var method = typeof(PixelMapRenderer).GetMethod("DrawLabels", BindingFlags.Instance | BindingFlags.NonPublic)!;
            method.CreateDelegate<Action<SKCanvas, int, int, double, int, PixelPalette>>(renderer)
                (canvas, width, height, camera.Zoom, 3, PixelPalette.Day);
        }
        using (var canvas = new SKCanvas(actual))
        using (var atlasImage = SKImage.FromBitmap(overlay.Atlas!))
        {
            canvas.Clear(SKColors.Transparent);
            var vertices = overlay.ScreenVertices;
            for (int i = 0; i < vertices.Length; i += 4)
            {
                var a = vertices[i]; var c = vertices[i + 2];
                var source = new SKRect(MathF.Round(a.Uv.X * overlay.Atlas!.Width), MathF.Round(a.Uv.Y * overlay.Atlas.Height),
                    MathF.Round(c.Uv.X * overlay.Atlas.Width), MathF.Round(c.Uv.Y * overlay.Atlas.Height));
                var destination = new SKRect(MathF.Round((a.Position.X + 1) * width * .5f), MathF.Round((1 - a.Position.Y) * height * .5f),
                    MathF.Round((c.Position.X + 1) * width * .5f), MathF.Round((1 - c.Position.Y) * height * .5f));
                canvas.DrawImage(atlasImage, source, destination, new SKSamplingOptions(SKFilterMode.Nearest));
            }
        }
        Assert(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()), "Glyph atlas composition changed legacy text/halo pixels.");
        long revision = overlay.AtlasRevision;
        long screenRevision = overlay.ScreenRevision;
        var retained = overlay.ScreenVertices.ToArray();
        overlay.AcknowledgeAtlasUpload(revision);
        renderer.PrepareWebGpuOverlay(frame, overlay, vehicle: new(new(center.Latitude, center.Longitude), 0, Moving: false));
        Assert(overlay.AtlasRevision == revision && overlay.AtlasDirtyBounds.IsEmpty, "Unchanged map labels re-rasterized or dirtied the atlas during simulation.");
        Assert(MemoryMarshal.AsBytes(retained.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(overlay.ScreenVertices)), "Simulation rebuilt or moved static labels.");
        Assert(overlay.ScreenRevision == screenRevision, "Vehicle updates invalidated retained label vertex/index buffers.");
        renderer.PrepareWebGpuOverlay(frame, overlay, vehicle: new(new(center.Latitude, center.Longitude), 0, AnimationSeconds: 1, Moving: false));
        Assert(overlay.ScreenRevision == screenRevision, "Time-only simulation invalidated static label uploads.");
        using var future = renderer.PrepareGpuFrame(width, height, camera with { Bearing = 21 }, [tile], PixelStyle.Day, 3,
            null, null, MapRenderStyle.Pixel, false);
        renderer.PrepareWebGpuOverlay(future, overlay);
        Assert(overlay.ScreenRevision > screenRevision, "A changed camera did not invalidate projected label buffers.");
        var futureLabels = overlay.ScreenVertices.ToArray();
        renderer.PrepareWebGpuOverlay(frame, overlay);
        Assert(MemoryMarshal.AsBytes(retained.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(overlay.ScreenVertices)),
            "Reactivating a retained frame used a newer camera's label projection.");
        renderer.PrepareWebGpuOverlay(future, overlay);
        Assert(MemoryMarshal.AsBytes(futureLabels.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(overlay.ScreenVertices)),
            "Returning to the newest retained frame reused an older camera's label projection.");
        long populatedRevision = overlay.ScreenRevision;
        using var empty = renderer.PrepareGpuFrame(width, height, camera, [], PixelStyle.Day, 3,
            null, null, MapRenderStyle.Pixel, false);
        renderer.PrepareWebGpuOverlay(empty, overlay);
        Assert(overlay.ScreenRevision > populatedRevision && overlay.ScreenVertices.IsEmpty && overlay.ScreenIndices.IsEmpty,
            "Removing all labels did not invalidate the retained GPU draw count.");
    }

    private static void VehicleAndRouteGeometry()
    {
        var point = new RoutePoint(0, 0);
        using var renderer = new PixelMapRenderer();
        using var overlay = new WebGpuSceneOverlay();
        foreach (var style in Enum.GetValues<MapRenderStyle>())
        {
            var camera = new MapCamera(0, 0, 19, 24, 55, true);
            using var frame = renderer.PrepareGpuFrame(600, 400, camera, [], PixelStyle.Day, 3, null, null, style, false);
            foreach (var mode in Enum.GetValues<TravelMode>())
            {
                var pose = new VehiclePose(point, 37, .5, 0, false);
                renderer.PrepareWebGpuOverlay(frame, overlay, vehicle: pose, mode: mode);
                Assert(overlay.WorldTriangleCount >= 12, $"Missing physical {mode}/{style} geometry.");
                Assert(overlay.OccludedIndices.IsEmpty, "Physical free-roam models must use scene depth, not ghost through buildings.");
                Validate(overlay.WorldVertices, overlay.WorldIndices);
                var route = Route(mode, point.Move(270, 100), point.Move(90, 100));
                renderer.PrepareWebGpuOverlay(frame, overlay, route, pose with { Progress = .5 }, mode, new(point.Move(0, 3), 0, 0));
                Assert(overlay.RouteSegmentCount > 0 && overlay.OccludedIndices.Length > 0, $"Missing route/occlusion geometry for {mode}/{style}.");
                Validate(overlay.WorldVertices, overlay.WorldIndices);
                Validate(overlay.OccludedVertices, overlay.OccludedIndices);
            }
        }
    }

    private static void AntimeridianAndNearPlane()
    {
        using var renderer = new PixelMapRenderer();
        using var overlay = new WebGpuSceneOverlay();
        var route = Route(TravelMode.Drive, new(0, 179.95), new(0, -179.95));
        using (var frame = renderer.PrepareGpuFrame(900, 600, new(0, 180, 12, 0, 35, true), [], PixelStyle.Day, 3,
            null, null, MapRenderStyle.Pixel, false))
        {
            renderer.PrepareWebGpuOverlay(frame, overlay, route);
            Assert(overlay.RouteSegmentCount > 0, "A route crossing the antimeridian disappeared.");
            Validate(overlay.WorldVertices, overlay.WorldIndices);
            foreach (var vertex in overlay.WorldVertices)
                Assert(MathF.Abs(vertex.Position.X / vertex.Position.W) < 1.1f, "Antimeridian route traverses the long way around the Earth.");
        }
        var eye = MapCamera.FromEye(0, 0, 1.8, 0, 90, 900, 600);
        using (var frame = renderer.PrepareGpuFrame(900, 600, eye, [], PixelStyle.Day, 2,
            null, null, MapRenderStyle.Normal, false))
        {
            renderer.PrepareWebGpuOverlay(frame, overlay, Route(TravelMode.Walk, new(-.01, 0), new(.01, 0)));
            Assert(overlay.RouteSegmentCount > 0, "A route crossing the eye's near plane disappeared.");
            Validate(overlay.WorldVertices, overlay.WorldIndices);
            foreach (var vertex in overlay.WorldVertices)
                Assert(vertex.Position.W > 0, "A clipped route emitted behind-eye vertices.");
        }
    }

    private static void OrthographicReliefRouteDepth()
    {
        using var renderer = new PixelMapRenderer();
        using var overlay = new WebGpuSceneOverlay();
        var elevation = new ElevationSnapshot([new ElevationTile(new(0, 0, 0), 2, [100, 100, 100, 100])]);
        var terrain = new TerrainOptions(elevation, Relief: true);
        var camera = new MapCamera(0, 0, 21, 0, 55, false) { GroundAltitudeMeters = 100 };
        using var frame = renderer.PrepareGpuFrame(900, 600, camera, [], PixelStyle.Day, 2,
            null, terrain, MapRenderStyle.Pixel, false);
        var center = new RoutePoint(0, 0);
        renderer.PrepareWebGpuOverlay(frame, overlay, Route(TravelMode.Drive, center.Move(270, 4), center.Move(90, 4)));
        Assert(overlay.RouteSegmentCount > 0 && !overlay.WorldVertices.IsEmpty,
            "An orthographic route draped above flat relief produced no geometry.");
        foreach (var vertex in overlay.WorldVertices)
            Assert(vertex.Position.Z >= 0 && vertex.Position.Z <= vertex.Position.W,
                "The route's depth bias moved a nearest-layer orthographic annotation outside WebGPU's clip interval.");
        foreach (var vertex in overlay.OccludedVertices)
            Assert(vertex.Position.Z >= 0 && vertex.Position.Z <= vertex.Position.W,
                "An orthographic route silhouette exceeded WebGPU's clip interval.");
    }

    private static void ParticleContinuity()
    {
        using var renderer = new PixelMapRenderer();
        using var overlay = new WebGpuSceneOverlay();
        using var frame = renderer.PrepareGpuFrame(600, 400, new(0, 0, 19, 0, 50, true), [], PixelStyle.Day, 2,
            null, null, MapRenderStyle.Normal, false);
        var point = new RoutePoint(0, 0);
        renderer.PrepareWebGpuOverlay(frame, overlay, vehicle: new(point, 0, 0, SpeedMetersPerSecond: 10));
        for (int i = 1; i <= 120; i++)
        {
            point = point.Move(0, 1);
            renderer.PrepareWebGpuOverlay(frame, overlay, vehicle: new(point, 0, i * .1, SpeedMetersPerSecond: 10));
            Assert(overlay.ParticleCount <= 56, "Particle GPU geometry storage grew beyond its fixed budget.");
        }
        Assert(overlay.ParticleCount > 0, "Moving vehicle emitted no particles.");
        int count = overlay.ParticleCount;
        renderer.PrepareWebGpuOverlay(frame, overlay, vehicle: new(point, 0, 1000, Moving: false, SpeedMetersPerSecond: 0));
        Assert(overlay.ParticleCount == count, "Stationary time incorrectly aged the frozen simulation effects.");
        long revision = overlay.Revision;
        var idle = new VehiclePose(point, 0, 2000, Moving: false, SpeedMetersPerSecond: 0);
        Assert(overlay.TryAdvanceStationaryVehicle(TravelMode.Drive, idle), "An identical stopped vehicle could not skip frame submission.");
        Assert(overlay.Revision == revision && overlay.ParticleCount == count, "The idle timestamp changed retained GPU geometry.");
        Assert(!overlay.TryAdvanceStationaryVehicle(TravelMode.Drive, idle with { Bearing = 10 }), "A changed pose skipped rendering.");
        Assert(!overlay.TryAdvanceStationaryVehicle(TravelMode.Drive, idle with { AnimationSeconds = 1999 }), "A backwards simulation clock skipped rendering.");
        point = point.Move(0, 1);
        renderer.PrepareWebGpuOverlay(frame, overlay, vehicle: new(point, 0, 2000.1, SpeedMetersPerSecond: 10));
        Assert(overlay.ParticleCount > 0 && overlay.ParticleCount <= count + 1, "Resuming after idle emitted a catch-up burst.");
        renderer.PrepareWebGpuOverlay(frame, overlay, vehicle: new(point, 0, 2000.2, Continuity: 1));
        Assert(overlay.ParticleCount == 0, "Teleport left a stale particle trail.");
    }

    private static void Validate(ReadOnlySpan<WebGpuOverlayVertex> vertices, ReadOnlySpan<uint> indices)
    {
        Assert(indices.Length % 3 == 0, "Incomplete GPU overlay triangle.");
        foreach (uint index in indices) Assert(index < vertices.Length, "GPU overlay index is out of range.");
        foreach (var vertex in vertices)
        {
            var position = vertex.Position;
            Assert(float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z) && float.IsFinite(position.W), "Non-finite homogeneous GPU overlay vertex.");
            var color = vertex.Color;
            Assert(color.W is >= 0 and <= 1 && color.X >= 0 && color.Y >= 0 && color.Z >= 0 &&
                color.X <= color.W + .00001f && color.Y <= color.W + .00001f && color.Z <= color.W + .00001f,
                "GPU overlay color must be premultiplied.");
            Assert(vertex.Uv.X is >= 0 and <= 1 && vertex.Uv.Y is >= 0 and <= 1, "Atlas UV outside its texture.");
        }
    }

    private static MapFeature Label(ulong id, string name, int x, int y) => new("place", "city", "", name, id, 4096,
        GeometryType.Point, [[new(x, y)]], null, null, false, 0, 0, "");

    private static RoutePlan Route(TravelMode mode, RoutePoint a, RoutePoint b)
    {
        var start = new RouteStop("Start", a); var end = new RouteStop("Finish", b);
        return new(mode, [start, end], [new(start, end, [a, b], a.DistanceTo(b), TimeSpan.FromSeconds(30), [])], "Fixture", false);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
