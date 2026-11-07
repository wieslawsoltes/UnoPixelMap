using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

return await SceneMemory.Run(args);

internal static class SceneMemory
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private static readonly Regex UrlTile = new(@"(?:^|/)(?<z>\d{1,2})/(?<x>\d+)/(?<y>\d+)(?:\.(?:pbf|mvt|png|bin)(?:\.gz)?)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FileTile = new(@"(?<z>\d{1,2})[-_](?<x>\d+)[-_](?<y>\d+)(?:\.(?:pbf|mvt|png|bin)(?:\.gz)?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly PropertyInfo? PathApproximate = typeof(SKPath).GetProperty("ApproximateBytesUsed", Members);

    internal static async Task<int> Run(string[] args)
    {
        _ = Option(args, "source-revision") ?? throw new ArgumentException("Supply --source-revision matching the loaded PixelMap.Rendering.dll; the report also records its exact SHA256.");
        string output = Path.GetFullPath(Option(args, "output") ?? "/tmp/unopixelmap-scene-memory/bundled.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string progress = Path.ChangeExtension(output, ".progress.ndjson");
        File.WriteAllText(progress, "");
        void Progress(string stage, object values) => File.AppendAllText(progress,
            JsonSerializer.Serialize(new { AtUtc = DateTimeOffset.UtcNow, Stage = stage, Values = values }) + "\n");
        int width = Number(args, "width", 1800), height = Number(args, "height", 807), pixelSize = Number(args, "pixel-size", 2);
        int heldCount = Number(args, "held-frames", 2), maximumTiles = Number(args, "max-tiles", 512);
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || pixelSize is < 1 or > 8 || heldCount is < 1 or > 4)
            throw new ArgumentException("Viewport must be 1..8192, pixel size 1..8, and held frames 1..4.");
        string? manifest = Option(args, "manifest"), directory = Option(args, "directory"), posesFile = Option(args, "poses");
        if (manifest is not null && directory is not null) throw new ArgumentException("Select either --manifest or --directory.");
        string source = manifest ?? directory ?? (args.Contains("--flat")
            ? "nine bundled Manhattan z14 tiles with flat terrain"
            : "nine bundled Manhattan z14 tiles plus the bundled z0 DEM");
        Progress("start", new { Source = source, width, height, pixelSize, heldCount });
        var reports = new List<object>();
        var snapshots = new List<object>();
        var inputRows = new List<object>();
        var warnings = new List<string>();
        var vectors = new Dictionary<TileId, MapTile>();
        var elevations = new Dictionary<TileId, ElevationTile>();
        snapshots.Add(Memory("startup", true));
        if (manifest is null && directory is null)
        {
            using var http = new HttpClient(new OfflineHandler());
            var vectorSource = new VectorTileSource(http);
            for (int y = 6159; y <= 6161; y++)
            for (int x = 4822; x <= 4824; x++)
            {
                var id = new TileId(14, x, y);
                var tile = await vectorSource.GetTileAsync(id);
                vectors.Add(id, tile);
                inputRows.Add(new { Kind = "vector", Tile = Id(id), Source = "embedded", Features = tile.Features.Count });
            }
            if (!args.Contains("--flat"))
            {
                var tile = await new ElevationTileSource(http).GetTileAsync(new(0, 0, 0));
                elevations.Add(tile.Id, tile);
                inputRows.Add(new { Kind = "elevation", Tile = Id(tile.Id), Source = "embedded", tile.Size });
            }
        }
        else
        {
            var entries = manifest is not null ? ReadManifest(Path.GetFullPath(manifest), warnings, Option(args, "body-root")) : ReadDirectory(Path.GetFullPath(directory!));
            if (entries.Count > maximumTiles) throw new InvalidDataException($"Corpus has {entries.Count} tile responses; --max-tiles is {maximumTiles}.");
            foreach (var item in entries.OrderBy(e => e.Order))
            {
                byte[] body = await File.ReadAllBytesAsync(item.File);
                string sha = Hash(body);
                if (item.Sha256 is not null && !sha.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Body SHA256 mismatch: {item.File}");
                if (item.Kind == "vector")
                {
                    byte[] decoded = Gunzip(body);
                    var tile = VectorTileDecoder.Decode(item.Id, decoded);
                    vectors[item.Id] = tile;
                    inputRows.Add(new { item.Order, item.Kind, Tile = Id(item.Id), item.Url, item.File, BodyBytes = body.Length,
                        DecodedBytes = decoded.Length, Sha256 = sha, Features = tile.Features.Count, item.Stage });
                }
                else
                {
                    var tile = TerrariumDecoder.Decode(item.Id, body);
                    elevations[item.Id] = tile;
                    inputRows.Add(new { item.Order, item.Kind, Tile = Id(item.Id), item.Url, item.File, BodyBytes = body.Length,
                        Sha256 = sha, tile.Size, HeightArrayBytes = (long)tile.Size * tile.Size * sizeof(float), item.Stage });
                }
            }
            if (vectors.Count == 0) throw new InvalidDataException("The corpus did not contain any supported vector tile bodies.");
        }
        snapshots.Add(Memory("decoded_corpus", true));
        Progress("decoded_corpus", new { VectorTiles = vectors.Count, ElevationTiles = elevations.Count,
            Features = vectors.Values.Sum(t => t.Features.Count), Memory = snapshots[^1] });
        if (args.Contains("--mesh-construct-only"))
            return await ConstructionReport(args, output, source, vectors.Values.ToArray());
        var tiles = vectors.Values.ToArray();
        var elevation = new ElevationSnapshot(elevations.Values, tiles);
        TerrainOptions? terrain = !args.Contains("--flat") && elevations.Count > 0 ? new(elevation) : null;
        double ground = terrain is null ? 0 : elevation.SampleSurfaceMeters(40.7087, -74.0104);
        var poses = posesFile is null ? DefaultPoses(width, height, ground, terrain) :
            JsonSerializer.Deserialize<Pose[]>(await File.ReadAllTextAsync(posesFile), Json) ?? throw new InvalidDataException("Expected a pose array.");
        if (poses.Length is < 1 or > 32) throw new InvalidDataException("Use 1..32 deterministic poses.");
        var renderer = new PixelMapRenderer();
        using var typeface = SKTypeface.FromFile(Option(args, "font") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf");
        if (typeface is null || typeface.GlyphCount == 0) throw new InvalidOperationException("A valid diagnostic font is required.");
        ((SKFont)Get(renderer, "font")).Typeface = typeface;
        var held = new Queue<GpuMapFrame>();
        object? finalGeometry = null;
        try
        {
            foreach (var pose in poses)
            {
                int w = pose.Width > 0 ? pose.Width : width, h = pose.Height > 0 ? pose.Height : height;
                int pixels = pose.PixelSize > 0 ? pose.PixelSize : pixelSize;
                var before = Memory("before_prepare", false);
                Progress("before_prepare", new { pose.Name, Width = w, Height = h, PixelSize = pixels, pose.Camera, Memory = before });
                var frame = renderer.PrepareGpuFrame(w, h, pose.Camera, tiles, PixelStyle.Night, pixels, null, terrain, MapRenderStyle.Pixel);
                held.Enqueue(frame);
                while (held.Count > heldCount) held.Dequeue().Dispose();
                var after = Memory("after_prepare_before_accounting", false);
                var collected = Memory("after_prepare_full_gc", true);
                var geometry = Geometry(renderer);
                var baking = BakingInputs(renderer, frame);
                var buffers = Buffers(renderer, held);
                finalGeometry = geometry;
                reports.Add(new { pose.Name, Width = w, Height = h, frame.PixelSize, Camera = pose.Camera,
                    Eye = Eye(pose.Camera, h), frame.TerrainPatchCount, frame.BuildingTriangleCount,
                    HeldFrames = held.Count, BeforePrepare = before, AfterPrepare = after, AfterFullGc = collected,
                    GeometryTotals = geometry.Totals, GeometryLargestPaths = geometry.LargestPaths,
                    Buffers = buffers, Depth = Depth(Get(frame, "Depth")), Baking = baking,
                    LastActualMergedShadow = PathCounts((SKPath)Get(renderer, "shadowMask"), "last_actual_shadowMask", null, null) });
                Progress("completed_pose", reports[^1]);
                Console.WriteLine($"{pose.Name}: {geometry.Tiles.Length} geometry tiles, {geometry.Totals.Points:N0} path points; " +
                    $"{buffers.UniqueManagedArrayPayloadBytes:N0} attributed managed array bytes; " +
                    $"largest candidate merged shadow {baking.LargestMergedShadow?.Points ?? 0:N0} points.");
            }
        }
        finally
        {
            while (held.TryDequeue(out var frame)) frame.Dispose();
            renderer.Dispose();
        }
        snapshots.Add(Memory("after_disposal_full_gc", true));
        var result = new
        {
            SchemaVersion = 1, GeneratedUtc = DateTimeOffset.UtcNow, Source = source,
            SourceRevision = Option(args, "source-revision"),
            Scope = new[] {
                "Deterministic structural/native x64 accounting, not a timing benchmark and not an exact WebAssembly heap measurement.",
                "Actual public PrepareGpuFrame performs normal terrain baking, mesh preparation, labels and demanded depth geometry. No GL context is created.",
                "Captured bodies are decoded in network body-completion order when every timing is available, otherwise observed completion order, otherwise header response order with a warning. Duplicate IDs use the last decoded response. This does not reproduce browser parsing/publication, viewport membership, cancellation or cache eviction.",
                "Default poses use a constant body-ground reference and the captured failure region, not the unknown raw live wheel events or a claim of exact live camera reconstruction.",
                "The corpus remains strongly referenced during after-disposal measurement. Full GC checkpoints intentionally measure retained memory and alter the live allocation schedule.",
                "Array payload bytes exclude object/array headers, dictionaries, source object graphs, renderer fields and native/GL allocations. Distinct references are deduplicated within each aggregate.",
                "Buffers.UniqueManagedArrayPayloadBytes excludes the separately reported current-frame depth workspace. The report does not attribute all managed retention or released depth-pool buffers.",
                "SKPath point and verb counts are exact. Linear point+verb payload is a lower bound excluding native capacity, headers and raster edge expansion. ApproximateBytesUsed is null when unavailable in this SkiaSharp API.",
                "Baking input enumeration replays production patch selection and native clip transforms without rasterizing extra paths. It includes cached blocks, and reports source paths before stroke/dash expansion; it is not an intercepted count of actual native draw calls.",
                "Linux mallinfo2 measures process-wide malloc allocations, including runtime/native libraries; it is not a Skia-only measurement. RSS and GC committed bytes must not be added to those values.",
                "Native x64 pointer sizes, GC layout, allocator and Skia code differ from wasm32. Fixed element counts and source identity are comparable; total process/native byte readings are not the browser's 2GiB heap usage." },
            Runtime = RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), PointerBytes = IntPtr.Size,
            SkiaPathApproximateBytesAvailable = PathApproximate is not null,
            Assemblies = new[] { typeof(SceneMemory).Assembly, typeof(PixelMapRenderer).Assembly, typeof(MapTile).Assembly, typeof(SKPath).Assembly }
                .ToDictionary(a => a.GetName().Name!, a => new { a.Location, Sha256 = Hash(File.ReadAllBytes(a.Location)) }),
            Font = new { typeface.FamilyName, typeface.GlyphCount },
            Corpus = new { VectorTiles = vectors.Count, ElevationTiles = elevations.Count, Features = tiles.Sum(t => t.Features.Count),
                SourcePathPoints = tiles.Sum(t => t.Features.Sum(f => f.Paths.Sum(p => (long)p.Length))),
                ElevationArrayBytes = elevations.Values.Sum(t => (long)t.Size * t.Size * sizeof(float)), ReferenceGroundMeters = ground,
                Relief = terrain is not null, Rows = inputRows, Warnings = warnings },
            MemoryMilestones = snapshots, Frames = reports, FinalGeometry = finalGeometry
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, Json));
        Console.WriteLine(output);
        return 0;
    }

    private sealed record Input(int Order, string Kind, TileId Id, string File, string? Url = null, string? Sha256 = null, string? Stage = null);
    internal sealed record Pose(string Name, MapCamera Camera, int Width = 0, int Height = 0, int PixelSize = 0);
    private static List<Input> ReadManifest(string file, List<string> warnings, string? bodyRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("corpusTruncated", out var truncated) && truncated.GetBoolean())
            warnings.Add("The capture reports a truncated corpus; this is a subset, not the full live response set.");
        var rows = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("requests", out var requests) ? requests : root.GetProperty("tiles");
        var result = new List<Input>();
        var networkCompletion = new List<double?>(); var observedCompletion = new List<double?>(); int ordinal = 0;
        foreach (var row in rows.EnumerateArray())
        {
            ordinal++;
            string? kind = String(row, "kind"), body = String(row, "body") ?? String(row, "file") ?? String(row, "path"), url = String(row, "url");
            if (kind is "vector-catalog" || String(row, "capture") is { } capture && capture != "saved") continue;
            if (body is null) continue;
            if (row.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() != 200) continue;
            kind = NormalizeKind(kind, url ?? body);
            if (kind is null) continue;
            var id = ParseId(row, url ?? body);
            int order = row.TryGetProperty("responseOrder", out var response) && response.ValueKind == JsonValueKind.Number && response.TryGetInt32(out var n) ? n : ordinal;
            result.Add(new(order, kind, id, Path.GetFullPath(body, bodyRoot is null ? Path.GetDirectoryName(file)! : Path.GetFullPath(bodyRoot)), url,
                String(row, "sha256"), String(row, "responseStage") ?? String(row, "requestedStage")));
            double? completed = null;
            if (row.TryGetProperty("timing", out var timing) && timing.ValueKind == JsonValueKind.Object &&
                Numeric(timing, "startTime") is { } start && Numeric(timing, "responseEnd") is >= 0 and var end)
                completed = start + end;
            networkCompletion.Add(completed);
            observedCompletion.Add(Numeric(row, "finishedMs") ?? Numeric(row, "finishedObservedMs"));
        }
        var times = networkCompletion.All(t => t.HasValue) ? networkCompletion : observedCompletion.All(t => t.HasValue) ? observedCompletion : null;
        if (times is null)
        {
            warnings.Add("Complete body-arrival timings were not available for every row; duplicate resolution uses header responseOrder.");
            return result;
        }
        return result.Select((entry, index) => (Entry: entry, Time: times[index]!.Value))
            .OrderBy(item => item.Time).ThenBy(item => item.Entry.Order).Select((item, index) => item.Entry with { Order = index + 1 }).ToList();
    }
    private static List<Input> ReadDirectory(string directory)
    {
        var result = new List<Input>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string? kind = NormalizeKind(null, file);
            if (kind is null) continue;
            result.Add(new(result.Count + 1, kind, ParseId(default, Path.GetRelativePath(directory, file).Replace('\\', '/')), file));
        }
        return result;
    }
    private static string? NormalizeKind(string? kind, string source) => kind switch
    {
        "vector" or "pbf" or "mvt" => "vector", "elevation" or "terrain" or "dem" => "elevation",
        _ => source.Contains("terrarium", StringComparison.OrdinalIgnoreCase) || source.Split('?')[0].EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? "elevation" : Regex.IsMatch(source.Split('?')[0], @"\.(pbf|mvt)(\.gz)?$", RegexOptions.IgnoreCase) ? "vector" : null
    };
    private static TileId ParseId(JsonElement row, string source)
    {
        if (row.ValueKind == JsonValueKind.Object)
        {
            var item = row.TryGetProperty("tile", out var tile) ? tile : row;
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("z", out var z) && item.TryGetProperty("x", out var x) && item.TryGetProperty("y", out var y))
                return new(z.GetInt32(), x.GetInt32(), y.GetInt32());
        }
        string path = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsolutePath : source.Split('?')[0];
        var match = UrlTile.Match(path);
        if (!match.Success) match = FileTile.Match(Path.GetFileName(path));
        if (!match.Success) throw new InvalidDataException($"Cannot infer XYZ tile coordinates from {source}.");
        int zz = int.Parse(match.Groups["z"].Value), xx = int.Parse(match.Groups["x"].Value), yy = int.Parse(match.Groups["y"].Value);
        if (zz is < 0 or > 22 || xx < 0 || yy < 0 || xx >= 1 << zz || yy >= 1 << zz) throw new InvalidDataException("Tile coordinates are out of range.");
        return new(zz, xx, yy);
    }
    private static byte[] Gunzip(byte[] body)
    {
        if (body is not [0x1f, 0x8b, ..]) return body;
        using var source = new GZipStream(new MemoryStream(body, false), CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] chunk = new byte[65536]; int count;
        while ((count = source.Read(chunk)) != 0)
        {
            if (output.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Decoded vector tile exceeds the production8MiB limit.");
            output.Write(chunk, 0, count);
        }
        return output.ToArray();
    }
    private static Pose[] DefaultPoses(int width, int height, double ground, TerrainOptions? terrain)
    {
        var first = Orbit(90);
        var last = Orbit(176.4);
        return [new("opening", new MapCamera(40.7087, -74.0104, 16.5, -15, 55, true).OnTerrain(terrain, height)),
            new("orbit_66", first), new("horizon", first.Interpolate(last, .7018416744)),
            new("close_orbit", last), new("reverse_toward_horizon", first.Interpolate(last, .6432968))];
        MapCamera Orbit(double look)
        {
            var body = new RoutePoint(40.7087, -74.0104);
            double angle = (look - 24) * Math.PI / 180, horizontal = 6 * Math.Sin(angle);
            double altitude = ground + Math.Max(.35, .85 + 6 * Math.Cos(angle));
            var eye = body.Move(165, horizontal);
            var camera = MapCamera.FromEye(eye.Latitude, eye.Longitude, altitude, -15,
                Math.Atan2(horizontal, altitude - ground - .85) * 180 / Math.PI, width, height, 6000, ground);
            if (!camera.TryAimAt(body.Latitude, body.Longitude, ground + .85, width * .5, height * .5, width, height, out var aimed))
                throw new InvalidOperationException("Derived orbit could not aim at the reference body.");
            return aimed;
        }
    }

    private sealed record PathReport(string? Tile, string Kind, string? Ink, int? Bucket, int Points, int Verbs,
        long LinearPointVerbPayloadLowerBoundBytes, long? NativeApproximateBytesUsed);
    private sealed record Totals(int Tiles, int Buildings, int Labels, int Paths, long Points, long Verbs, long LinearPointVerbPayloadLowerBoundBytes);
    private sealed record GeometryReport(object[] Tiles, Totals Totals, PathReport[] LargestPaths);
    private static GeometryReport Geometry(PixelMapRenderer renderer)
    {
        var paths = new List<PathReport>(); var tiles = new List<object>(); int buildings = 0, labels = 0;
        foreach (DictionaryEntry item in (IDictionary)Get(renderer, "geometry"))
        {
            var tile = item.Value!; string id = Id((TileId)item.Key); var local = new List<PathReport>();
            foreach (string field in new[] { "Batches", "Shadows" })
            foreach (var batch in Items(Get(tile, field)))
            {
                var key = Get(batch, "Key");
                local.Add(PathCounts((SKPath)Get(batch, "Path"), field, id, key));
            }
            int modelCount = Count(Get(tile, "Buildings")), labelCount = Count(Get(tile, "Labels"));
            buildings += modelCount; labels += labelCount; paths.AddRange(local);
            tiles.Add(new { Tile = id, Features = ((MapTile)Get(tile, "Source")).Features.Count, Buildings = modelCount, Labels = labelCount,
                Paths = local.Count, Points = local.Sum(p => (long)p.Points), Verbs = local.Sum(p => (long)p.Verbs),
                LinearPointVerbPayloadLowerBoundBytes = local.Sum(p => p.LinearPointVerbPayloadLowerBoundBytes),
                MaterialPoints = local.Where(p => p.Kind == "Batches").Sum(p => (long)p.Points),
                ShadowPoints = local.Where(p => p.Kind == "Shadows").Sum(p => (long)p.Points),
                LargestPath = local.OrderByDescending(p => p.Points).FirstOrDefault() });
        }
        return new(tiles.ToArray(), new(tiles.Count, buildings, labels, paths.Count, paths.Sum(p => (long)p.Points),
            paths.Sum(p => (long)p.Verbs), paths.Sum(p => p.LinearPointVerbPayloadLowerBoundBytes)), paths.OrderByDescending(p => p.Points).Take(10).ToArray());
    }
    private static PathReport PathCounts(SKPath path, string kind, string? tile, object? key)
    {
        long? approximate = PathApproximate?.GetValue(path) is { } value ? Convert.ToInt64(value) : null;
        return new(tile, kind, key is null ? null : Get(key, "Ink").ToString(), key is null ? null : Convert.ToInt32(Get(key, "Bucket")),
            path.PointCount, path.VerbCount, path.PointCount * 8L + path.VerbCount, approximate);
    }

    private sealed record BufferReport(long UniqueManagedArrayPayloadBytes, Dictionary<string, long> BytesByCategory,
        object[] BuildingMeshes, object TerrainCaches, int DistinctBuildingMeshes, int UniqueArrays);
    private static BufferReport Buffers(PixelMapRenderer renderer, IEnumerable<GpuMapFrame> frames)
    {
        var arrays = new HashSet<Array>(ReferenceEqualityComparer.Instance);
        var meshes = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var totals = new Dictionary<string, long>(); var rows = new List<object>();
        void Add(string category, Array array)
        {
            if (!arrays.Add(array)) return;
            long bytes = array.LongLength * ElementBytes(array.GetType().GetElementType()!);
            totals[category] = totals.GetValueOrDefault(category) + bytes;
        }
        void Mesh(object mesh)
        {
            if (!meshes.Add(mesh)) return;
            var tile = (MapTile)Get(Get(mesh, "Source"), "Source");
            var fields = new[] { "Vertices", "Indices", "RoofEdges", "Models", "Ranges" };
            var sizes = fields.ToDictionary(n => n, n => ((Array)Get(mesh, n)).LongLength * ElementBytes(((Array)Get(mesh, n)).GetType().GetElementType()!));
            rows.Add(new { Tile = Id(tile.Id), LogicalArrayBytes = sizes, TotalLogicalArrayBytes = sizes.Values.Sum(),
                VertexCount = ((Array)Get(mesh, "Vertices")).Length / (((Array)Get(mesh, "Vertices")).GetType().GetElementType() == typeof(float) ? 12 : 1),
                VertexElementBytes = ElementBytes(((Array)Get(mesh, "Vertices")).GetType().GetElementType()!), IndexCount = ((Array)Get(mesh, "Indices")).Length,
                RimVertexCount = ((Array)Get(mesh, "RoofEdges")).Length / 4, Models = ((Array)Get(mesh, "Models")).Length / 8 });
            foreach (string field in fields) Add("building_" + field, (Array)Get(mesh, field));
        }
        foreach (DictionaryEntry entry in (IDictionary)Get(renderer, "gpuBuildings"))
        {
            Mesh(Get(entry.Value!, "Mesh"));
            Add("building_selection_scratch", (Array)Get(entry.Value!, "SelectionScratch"));
            Selection(GetOrNull(entry.Value!, "Selection"));
        }
        void Selection(object? selection)
        {
            if (selection is null) return;
            foreach (string field in new[] { "Models", "Indices", "RoofEdges" }) Add("selection_" + field, (Array)Get(selection, field));
        }
        foreach (var frame in frames)
        {
            foreach (var draw in Items(Get(frame, "Buildings"))) { Mesh(Get(draw, "Mesh")); Selection(GetOrNull(draw, "Selection")); }
            foreach (var draw in Items(Get(frame, "Terrain")))
            {
                var mesh = Get(draw, "Mesh"); Add("terrain_vertices", (Array)Get(mesh, "Vertices"));
                if (GetOrNull(mesh, "HeightLight") is Array blocks) Add("terrain_height_light", blocks);
                Add("terrain_texels", (Array)Get(Get(draw, "Texture"), "Texels"));
            }
        }
        var surface = Get(renderer, "reliefSurface"); long bitmapBytes = 0; int textureCount = 0;
        foreach (DictionaryEntry entry in (IDictionary)Get(surface, "textures"))
        {
            textureCount++;
            if (GetOrNull(entry.Value!, "bitmap") is SKBitmap bitmap) bitmapBytes += (long)bitmap.RowBytes * bitmap.Height;
            if (GetOrNull(entry.Value!, "Gpu") is { } gpu) Add("terrain_texels", (Array)Get(gpu, "Texels"));
        }
        foreach (DictionaryEntry entry in (IDictionary)Get(surface, "gpuMeshes")) Add("terrain_vertices", (Array)Get(entry.Value!, "Vertices"));
        foreach (DictionaryEntry entry in (IDictionary)Get(surface, "meshes"))
            foreach (string field in new[] { "Heights", "Light" }) Add("terrain_source_" + field, (Array)Get(entry.Value!, field));
        int rasterBlocks = 0; long rasterBytes = 0;
        foreach (var entry in Items(Get(Get(renderer, "terrain"), "usage")))
        { var image = (SKImage)Get(entry, "Image"); rasterBlocks++; rasterBytes += (long)image.Width * image.Height * 4; }
        foreach (DictionaryEntry entry in (IDictionary)Get(renderer, "geometry"))
        foreach (var building in Items(Get(entry.Value!, "Buildings")))
            if (GetOrNull(building, "vertices") is Array vertices) Add("prepared_building_vertices", vertices);
        return new(totals.Values.Sum(), totals, rows.ToArray(), new { SurfaceTextureEntries = textureCount,
            SurfaceNativeBitmapPixelBytes = bitmapBytes, SourceRasterImageCount = rasterBlocks,
            SourceRasterImagePixelBytes = rasterBytes,
            Caveat = "Native pixel payload only; excludes Skia object/capacity/cache overhead. Mesh row totals can share arrays; aggregate categories deduplicate references." }, meshes.Count, arrays.Count);
    }

    private sealed record BakingReport(int DistinctCandidateBlocks, int MaterialPathInputs, PathReport? LargestMaterial,
        ShadowReport? LargestMergedShadow, string Scope);
    private sealed record ShadowReport(string Tile, int Lod, int X, int Y, int SourcePaths, long Points, long Verbs,
        long LinearPointVerbPayloadLowerBoundBytes);
    private static BakingReport BakingInputs(PixelMapRenderer renderer, GpuMapFrame frame)
    {
        var sourceTiles = Items(Get(renderer, "reliefTiles")).ToArray();
        var patchIds = Items(Get(frame, "Terrain")).Select(d => (TileId)Get(Get(d, "Mesh"), "Id")).Distinct();
        var visited = new HashSet<(TileId, int, int, int)>(); int pathInputs = 0;
        PathReport? largest = null; ShadowReport? shadow = null;
        using var scratch = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(scratch);
        var textureBounds = new SKRect(0, 0, 256, 256);
        foreach (var patch in patchIds)
        {
            int coveringZoom = -1;
            foreach (var tile in sourceTiles)
            { var id = ((MapTile)Get(tile, "Source")).Id; if (Area(patch, id).Contains(textureBounds)) coveringZoom = Math.Max(coveringZoom, id.Z); }
            foreach (var tile in sourceTiles)
            {
                var id = ((MapTile)Get(tile, "Source")).Id;
                if (id.Z < coveringZoom) continue;
                var area = Area(patch, id);
                if (!area.IntersectsWith(textureBounds)) continue;
                float nativeScale = area.Width / 4096, scale = Math.Min(1, nativeScale);
                var matrix = SKMatrix.CreateScaleTranslation(nativeScale, nativeScale, area.Left, area.Top);
                canvas.Save(); canvas.SetMatrix(in matrix); canvas.ClipRect(new(0, 0, 4096, 4096));
                var visible = canvas.LocalClipBounds;
                int lod = (int)Math.Clamp(Math.Round(Math.Log2(scale * 4096 / 256)), 0, 11), n = 1 << lod;
                float units = 4096f / n, density = 256 / units;
                int x0 = Math.Clamp((int)MathF.Floor(visible.Left / units), 0, n), x1 = Math.Clamp((int)MathF.Ceiling(visible.Right / units), 0, n);
                int y0 = Math.Clamp((int)MathF.Floor(visible.Top / units), 0, n), y1 = Math.Clamp((int)MathF.Ceiling(visible.Bottom / units), 0, n);
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var bounds = new SKRect(x * units, y * units, (x + 1) * units, (y + 1) * units);
                    if (canvas.QuickReject(bounds) || !visited.Add((id, lod, x, y))) continue;
                    var clip = bounds; clip.Inflate(32 / density, 32 / density);
                    foreach (var batch in Items(Get(tile, "Batches")))
                    {
                        var b = (SKRect)Get(batch, "Bounds");
                        if (b.Left > clip.Right || b.Right < clip.Left || b.Top > clip.Bottom || b.Bottom < clip.Top) continue;
                        pathInputs++;
                        var count = PathCounts((SKPath)Get(batch, "Path"), "candidate_material_input", Id(id), Get(batch, "Key"));
                        if (largest is null || count.Points > largest.Points) largest = count;
                    }
                    double zoom = id.Z + lod + Math.Log2(frame.PixelSize) - 1;
                    if (zoom < 13 || frame.Camera.Pitch < 1) continue;
                    long points = 0, verbs = 0; int paths = 0;
                    foreach (var source in Items(Get(tile, "ShadowSources")))
                    {
                        var local = clip; local.Offset(-Convert.ToSingle(Get(source, "X")), -Convert.ToSingle(Get(source, "Y")));
                        foreach (var batch in Items(Get(Get(source, "Tile"), "Shadows")))
                            if (((SKRect)Get(batch, "Bounds")).IntersectsWith(local))
                            { var path = (SKPath)Get(batch, "Path"); points += path.PointCount; verbs += path.VerbCount; paths++; }
                    }
                    if (shadow is null || points > shadow.Points) shadow = new(Id(id), lod, x, y, paths, points, verbs, points * 8 + verbs);
                }
                canvas.Restore();
            }
        }
        return new(visited.Count, pathInputs, largest, shadow,
            "Map-only input enumeration for actual selected terrain patches with native SKCanvas clip transforms. Includes cache hits and excludes post-stroke/dash expansion. Merged shadow counts sum whole SKPath append inputs; no giant diagnostic shadow is allocated.");
    }
    private static SKRect Area(TileId patch, TileId source)
    {
        double relative = Math.Pow(2, patch.Z - source.Z); float size = (float)(256 * relative);
        float left = (float)((source.X * relative - patch.X) * 256), top = (float)((source.Y * relative - patch.Y) * 256);
        return new(left, top, left + size, top + size);
    }
    private static object Depth(object depth)
    {
        var geometry = new Dictionary<string, object>();
        foreach (string field in new[] { "triangles", "edges", "groups" })
        {
            var list = Get(depth, field); int count = Count(list), capacity = Convert.ToInt32(Get(list, "Capacity"));
            int bytes = ElementBytes(list.GetType().GetGenericArguments()[0]);
            geometry[field] = new { Count = count, Capacity = capacity, ElementBytes = bytes, CapacityPayloadBytes = capacity * (long)bytes };
        }
        var projected = (Array)Get(Get(depth, "storage"), "ProjectedVertices");
        return new { Geometry = geometry, ProjectedVertices = projected.Length,
            ProjectedPayloadBytes = projected.LongLength * ElementBytes(projected.GetType().GetElementType()!),
            IndexCapacityBytes = Get(depth, "indexCapacityBytes"), CoarseIndex = Get(depth, "hasCoarseIndex"), ExactIndex = Get(depth, "hasExactIndex"),
            Samples = Count(Get(depth, "samples")), GridCells = ((Array)Get(depth, "cells")).Length };
    }

    private static async Task<int> ConstructionReport(string[] arguments, string output, string input, MapTile[] tiles)
    {
        var assembly = typeof(PixelMapRenderer).Assembly;
        var geometryType = assembly.GetType("PixelMap.Rendering.TileGeometry")!;
        var meshType = assembly.GetType("PixelMap.Rendering.GpuBuildingMesh")!;
        var geometryConstructor = geometryType.GetConstructor([typeof(MapTile), typeof(MapRenderStyle)])!;
        var meshConstructor = meshType.GetConstructor(Members, binder: null, [geometryType], modifiers: null)!;
        var geometries = new List<IDisposable>();
        var meshes = new List<object>();
        var reports = new List<object>();
        long allocatedTotal = 0, arrayTotal = 0, preparedTotal = 0;
        var before = Memory("before_construction", true);
        try
        {
            foreach (var tile in tiles)
            {
                var geometry = geometryConstructor.Invoke([tile, MapRenderStyle.Pixel]);
                geometries.Add((IDisposable)geometry);
                object[] parameters = [geometry];
                long start = GC.GetAllocatedBytesForCurrentThread();
                object mesh = meshConstructor.Invoke(parameters);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
                meshes.Add(mesh);
                var fields = new[] { "Vertices", "Indices", "RoofEdges", "Models", "Ranges" };
                var bytes = fields.ToDictionary(name => name, name => ((Array)Get(mesh, name)).LongLength * ElementBytes(((Array)Get(mesh, name)).GetType().GetElementType()!));
                long preparedBytes = Items(Get(geometry, "Buildings")).Sum(model => GetOrNull(model, "vertices") is Array data
                    ? data.LongLength * ElementBytes(data.GetType().GetElementType()!) : 0L);
                allocatedTotal += allocated; arrayTotal += bytes.Values.Sum(); preparedTotal += preparedBytes;
                reports.Add(new { Tile = Id(tile.Id), AllocatedBytes = allocated, ArrayPayloadBytes = bytes,
                    PreparedCpuEdgePayloadBytes = preparedBytes, BuildingModels = Count(Get(geometry, "Buildings")) });
            }
            var result = new
            {
                SchemaVersion = 1, GeneratedUtc = DateTimeOffset.UtcNow,
                SourceRevision = Option(arguments, "source-revision"), Input = input,
                Scope = "Single structural/allocation pass over each decoded tile. Per-constructor managed allocated bytes include tessellation and output/staging arrays, exclude TileGeometry creation and diagnostic accounting. Geometry/mesh outputs stay retained through the final GC snapshot. Reflection invocation overhead is included equally. This is native x64, not exact WASM heap occupancy or a timing benchmark.",
                RendererSha256 = Hash(File.ReadAllBytes(assembly.Location)), HarnessSha256 = Hash(File.ReadAllBytes(typeof(SceneMemory).Assembly.Location)),
                TileCount = tiles.Length, ConstructorAllocatedBytes = allocatedTotal, RetainedMeshArrayPayloadBytes = arrayTotal,
                RetainedPreparedCpuEdgesBytes = preparedTotal, Before = before, After = Memory("all_meshes_retained", true), Tiles = reports
            };
            GC.KeepAlive(meshes);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, Json));
            Console.WriteLine($"{tiles.Length} mesh constructors: {allocatedTotal:N0} allocated bytes; {arrayTotal:N0} final mesh-array bytes; {preparedTotal:N0} prepared CPU-edge bytes.");
            Console.WriteLine(output);
        }
        finally { foreach (var geometry in geometries) geometry.Dispose(); }
        return 0;
    }
    private static object Memory(string name, bool collect)
    {
        long before = GC.GetTotalMemory(false);
        if (collect) { GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false); }
        var gc = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        object? malloc = null;
        if (OperatingSystem.IsLinux())
        {
            try { var m = MallInfo(); malloc = new { AllocatedArenaBytes = (ulong)m.Uordblks, MappedAllocationBytes = (ulong)m.Hblkhd,
                TotalAllocatedBytes = (ulong)m.Uordblks + (ulong)m.Hblkhd, ArenaReservedBytes = (ulong)m.Arena, FreeArenaBytes = (ulong)m.Fordblks }; }
            catch (EntryPointNotFoundException) { }
        }
        return new { Name = name, FullGcRequested = collect, ManagedBeforeOptionalGc = before, ManagedBytes = GC.GetTotalMemory(false),
            gc.HeapSizeBytes, gc.FragmentedBytes, gc.TotalCommittedBytes, WorkingSetBytes = process.WorkingSet64,
            VirtualMemoryBytes = process.VirtualMemorySize64, Malloc = malloc,
            Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2) };
    }
    [StructLayout(LayoutKind.Sequential)] private struct MallInfo2
    { public nuint Arena, Ordblks, Smblks, Hblks, Hblkhd, Usmblks, Fsmblks, Uordblks, Fordblks, Keepcost; }
    [DllImport("libc", EntryPoint = "mallinfo2")] private static extern MallInfo2 MallInfo();
    private static object Eye(MapCamera camera, int height) { var eye = camera.GetEye(height); return new { eye.Latitude, eye.Longitude, eye.AltitudeMeters }; }
    private static int ElementBytes(Type type) => (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type).Invoke(null, null)!;
    private static object Get(object value, string name) => GetOrNull(value, name) ?? throw new MissingMemberException(value.GetType().FullName, name);
    private static object? GetOrNull(object value, string name) => value.GetType().GetField(name, Members)?.GetValue(value) ?? value.GetType().GetProperty(name, Members)?.GetValue(value);
    private static int Count(object value) => value is ICollection collection ? collection.Count : Convert.ToInt32(Get(value, "Count"));
    private static IEnumerable<object> Items(object value) => ((IEnumerable)value).Cast<object>();
    private static string? String(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static double? Numeric(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetDouble() : null;
    private static string Id(TileId id) => $"{id.Z}/{id.X}/{id.Y}";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string? Option(string[] args, string key) => args.FirstOrDefault(a => a.StartsWith("--" + key + "=", StringComparison.Ordinal))?[(key.Length + 3)..];
    private static int Number(string[] args, string key, int fallback) => Option(args, key) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
    private sealed class OfflineHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("This accounting harness performs no network requests: " + request.RequestUri); }
}
