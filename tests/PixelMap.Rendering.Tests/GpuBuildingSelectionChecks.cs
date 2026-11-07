using PixelMap.Data;
using PixelMap.Rendering;

/// <summary>Physical-camera transitions that change only one immutable model stream.</summary>
internal static class GpuBuildingSelectionChecks
{
    private const int Width = 360, Height = 240, Pixels = 3;
    private const byte Faces = 7, Rim = 8;
    private static readonly TileId Tile = new(14, 8192, 8192);
    private static readonly TerrainOptions Terrain = new(ElevationSnapshot.Empty, Relief: false);
    private sealed record View(MapCamera Camera, GpuBuildingSelection Selection);

    internal static void Run()
    {
        using var geometry = new TileGeometry(CreateTile());
        var mesh = new GpuBuildingMesh(geometry);
        var faces = new Dictionary<string, View>();
        var rims = new Dictionary<string, View>();
        var lengths = new Dictionary<(int, int), View>();
        (View Before, View After)? rimOnly = null, faceOnly = null, equalLength = null;
        byte[] scratch = [];
        int poses = 0;
        foreach (var camera in Cameras())
        {
            var selection = Prepare(mesh, camera, null, ref scratch);
            var view = new View(camera, selection);
            poses++;
            // Both streams must contain a proper subset. Reusing an empty array or
            // the immutable complete source would not exercise compact stream reuse.
            if (selection.Indices.Length == 0 || selection.Indices.Length == mesh.Indices.Length ||
                selection.RoofEdges.Length == 0 || selection.RoofEdges.Length == mesh.RoofEdges.Length) continue;
            string faceKey = Key(selection.Models, Faces), rimKey = Key(selection.Models, Rim);
            if (faces.TryGetValue(faceKey, out var sameFaces))
            {
                if (rimOnly is null && !sameFaces.Selection.RoofEdges.AsSpan().SequenceEqual(selection.RoofEdges))
                    rimOnly = (sameFaces, view);
            }
            else faces.Add(faceKey, view);
            if (rims.TryGetValue(rimKey, out var sameRims))
            {
                if (faceOnly is null && !sameRims.Selection.Indices.AsSpan().SequenceEqual(selection.Indices))
                    faceOnly = (sameRims, view);
            }
            else rims.Add(rimKey, view);
            var lengthKey = (selection.Indices.Length, selection.RoofEdges.Length);
            if (lengths.TryGetValue(lengthKey, out var sameLength))
            {
                if (equalLength is null &&
                    !sameLength.Selection.Indices.AsSpan().SequenceEqual(selection.Indices) &&
                    !sameLength.Selection.RoofEdges.AsSpan().SequenceEqual(selection.RoofEdges))
                    equalLength = (sameLength, view);
            }
            else lengths.Add(lengthKey, view);
            if (rimOnly is not null && faceOnly is not null && equalLength is not null) break;
        }
        Assert(rimOnly is not null && faceOnly is not null && equalLength is not null,
            "Physical-camera fixtures did not exercise independent and equal-length compact stream changes.");
        Check(mesh, rimOnly!.Value, reuseFaces: true, reuseRims: false, "rim-only change");
        Check(mesh, faceOnly!.Value, reuseFaces: false, reuseRims: true, "face-only change");
        Check(mesh, equalLength!.Value, reuseFaces: false, reuseRims: false, "equal-length content change");
        Console.WriteLine($"Passed independent building face/rim reuse, changed-stream invalidation, retained contents and cold-selection equivalence from {poses} physical-camera poses.");
    }

    private static void Check(GpuBuildingMesh mesh, (View Before, View After) pair,
        bool reuseFaces, bool reuseRims, string name)
    {
        var previous = pair.Before.Selection;
        byte[] models = (byte[])previous.Models.Clone();
        uint[] indices = (uint[])previous.Indices.Clone();
        float[] edges = (float[])previous.RoofEdges.Clone();
        byte[] scratch = [];
        var current = Prepare(mesh, pair.After.Camera, previous, ref scratch);
        var expected = pair.After.Selection;
        Assert(!ReferenceEquals(current, previous), $"The {name} reused stale model visibility.");
        Assert(ReferenceEquals(current.Indices, previous.Indices) == reuseFaces,
            $"The {name} did not preserve/invalidate its face upload identity correctly.");
        Assert(ReferenceEquals(current.RoofEdges, previous.RoofEdges) == reuseRims,
            $"The {name} did not preserve/invalidate its rim upload identity correctly.");
        Assert(current.Models.AsSpan().SequenceEqual(expected.Models) &&
            current.Indices.AsSpan().SequenceEqual(expected.Indices) &&
            current.RoofEdges.AsSpan().SequenceEqual(expected.RoofEdges),
            $"The {name} changed stream contents or order relative to a cold selection.");
        Assert(previous.Models.AsSpan().SequenceEqual(models) && previous.Indices.AsSpan().SequenceEqual(indices) &&
            previous.RoofEdges.AsSpan().SequenceEqual(edges), $"The {name} mutated a held selection.");
        Assert(ReferenceEquals(Prepare(mesh, pair.After.Camera, current, ref scratch), current),
            $"The {name} stopped reusing an entirely unchanged selection.");
        var restored = Prepare(mesh, pair.Before.Camera, current, ref scratch);
        Assert(restored.Models.AsSpan().SequenceEqual(models) && restored.Indices.AsSpan().SequenceEqual(indices) &&
            restored.RoofEdges.AsSpan().SequenceEqual(edges), $"The {name} did not restore the earlier view exactly.");
    }

    private static GpuBuildingSelection Prepare(GpuBuildingMesh mesh, MapCamera camera,
        GpuBuildingSelection? previous, ref byte[] scratch)
    {
        var transform = new TileProjection(camera, Width, Height, Pixels).Get(Tile, 0).Transform;
        float meters = (float)(camera.WorldSize / Pixels / (MapCamera.EarthCircumference *
            Math.Cos(Math.Clamp(camera.Latitude, -MapCamera.MaxLatitude, MapCamera.MaxLatitude) * Math.PI / 180)));
        return GpuBuildingSelection.Prepare(mesh, transform, camera, Terrain, meters,
            (Width + Pixels - 1) / Pixels, (Height + Pixels - 1) / Pixels, previous, ref scratch);
    }

    private static string Key(byte[] values, byte mask)
    {
        Span<byte> key = stackalloc byte[values.Length];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(values[i] & mask);
        return Convert.ToHexString(key);
    }

    private static IEnumerable<MapCamera> Cameras()
    {
        double latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (Tile.Y + .5) / (1 << Tile.Z)))) * 180 / Math.PI;
        double longitude = (Tile.X + .5) / (1 << Tile.Z) * 360 - 180;
        foreach (double altitude in new[] { 16d, 28, 44, 64, 96 })
        for (int bearing = 0; bearing < 360; bearing += 30)
        for (int pitch = 15; pitch <= 150; pitch += 3)
            yield return MapCamera.FromEye(latitude, longitude, altitude, bearing, pitch, Width, Height,
                viewDistanceMeters: 400);
    }

    private static MapTile CreateTile()
    {
        var features = new List<MapFeature>();
        for (int y = -2; y <= 2; y++)
        for (int x = -2; x <= 2; x++)
        {
            if (x == 0 && y == 0) continue;
            int left = 2048 + x * 140 - 36, top = 2048 + y * 140 - 36;
            TilePoint[] box = [new(left, top), new(left + 72, top), new(left + 72, top + 72), new(left, top + 72)];
            features.Add(new("building", "", "", "", (ulong)(features.Count + 1), 4096, GeometryType.Polygon,
                [box], 16 + (Math.Abs(x) + Math.Abs(y)) * 12, (x + y) % 3 == 0 ? 12 : 0, false, 0, 0, ""));
        }
        return new(Tile, features);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
