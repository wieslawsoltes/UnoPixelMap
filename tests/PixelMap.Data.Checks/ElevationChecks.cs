using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using PixelMap.Data;

internal static class ElevationChecks
{
    internal static async Task Run()
    {
        CheckSampling();
        CheckWaterSurfaces();
        CheckPngDecoding();
        await CheckSource();
        Console.WriteLine("Elevation decoding, seamless sampling, water surfaces and request lifetime checks passed.");
    }

    private static void CheckSampling()
    {
        float[] original = [0, 100, 200, 300];
        var tile = new ElevationTile(new(0, 0, 0), 2, original);
        original[0] = 999;
        var snapshot = new ElevationSnapshot([tile]);
        Equal(0, tile[0, 0], "An elevation tile owns a copy of its samples.");
        Equal(0, snapshot.SampleMercator(.25, .25), "Samples are located at pixel centres.");
        Equal(300, snapshot.SampleMercator(.75, .75), "The last pixel centre retains its height.");
        Equal(150, snapshot.SampleMercator(.5, .5), "Interior heights use bilinear interpolation.");
        Equal(50, snapshot.SampleMercator(0, .25), "World sampling blends through the dateline.");
        Equal(50, snapshot.SampleMercator(1, .25), "Both sides of the dateline agree.");
        Equal(0, snapshot.SampleMercator(.25, -1), "The northern edge clamps.");
        Equal(200, snapshot.SampleMercator(.25, 2), "The southern edge clamps.");
        Equal(snapshot.SampleMeters(20, 47), snapshot.SampleMeters(20, 407), "Longitudes wrap geographically.");
        Equal(snapshot.SampleMeters(90, 0), snapshot.SampleMeters(85.0511287798066, 0), "Geographic poles clamp to Mercator.");
        Require(!snapshot.TrySampleMercator(double.NaN, .5, out _) &&
            !snapshot.TrySampleMeters(0, double.PositiveInfinity, out _) &&
            snapshot.SampleSurfaceMercator(.5, double.NegativeInfinity) == 0,
            "Non-finite coordinates produce unavailable samples.");
        Require(snapshot.Count == 1 && snapshot.MaximumZoom == 0 && snapshot.MinimumMeters == 0 && snapshot.MaximumMeters == 300,
            "Snapshot bounds reflect its immutable samples.");
        Require(!ElevationSnapshot.Empty.TrySampleMeters(0, 0, out _) && ElevationSnapshot.Empty.MaximumZoom == 0,
            "An empty field has a safe unavailable/zero fallback.");

        var parent = Constant(new(0, 0, 0), 50);
        var child = Constant(new(1, 0, 0), 100);
        var mixed = new ElevationSnapshot([parent, child]);
        Equal(100, mixed.SampleMercator(.125, .125), "The finest loaded level wins.");
        Equal(50, mixed.SampleMercator(.8, .8), "Missing tiles use their loaded parent.");
        Equal(75, mixed.SampleMercator(.5, .125), "Tile boundaries blend a fine corner with its parent neighbour.");
        Require(Math.Abs(mixed.SampleMercator(.5 - 1e-8, .125) - mixed.SampleMercator(.5 + 1e-8, .125)) < 1e-4,
            "A missing adjacent tile does not introduce a height discontinuity.");
        var voidTile = new ElevationTile(new(1, 0, 0), 2, [float.NaN, 100, 100, 100]);
        Equal(50, new ElevationSnapshot([parent, voidTile]).SampleMercator(.125, .125), "A void corner uses parent data.");
        var noData = new ElevationTile(new(1, 0, 0), 2, [float.NaN, float.NaN, float.NaN, float.NaN]);
        Require(!new ElevationSnapshot([noData]).TrySampleMercator(.125, .125, out _), "An entirely void field is unavailable.");
        Equal(50, new ElevationSnapshot([parent, noData]).SampleMercator(.125, .125), "An entirely void tile falls back to its parent.");
        Equal(75, new ElevationSnapshot([parent, Constant(parent.Id, 75)]).SampleMercator(.5, .5), "A later duplicate tile replaces the prior tile.");

        Throws<ArgumentException>(() => new ElevationTile(new(0, 0, 0), 2, [1, 2, 3]), "Invalid sample counts are rejected.");
        Throws<ArgumentOutOfRangeException>(() => new ElevationTile(new(0, 0, 0), 3, new float[9]), "Unsupported sample grids are rejected.");
        Throws<ArgumentOutOfRangeException>(() => Constant(new(2, 4, 0), 0), "Tile coordinates must be canonical.");
        Throws<ArgumentOutOfRangeException>(() => Constant(new(0, 0, 0), float.PositiveInfinity), "Infinite heights are rejected.");
        Throws<ArgumentOutOfRangeException>(() => Constant(new(0, 0, 0), 32768), "Heights outside the encoding range are rejected.");
        Throws<ArgumentException>(() => new ElevationSnapshot([child, Constant(new(1, 1, 0), 0, 4)]),
            "One zoom must use a consistent pixel grid.");

        const int side = 16, size = 4;
        var heights = new float[side * side];
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++) heights[y * side + x] = x * x + y * 9 - x * y * .25f;
        var tiles = new List<ElevationTile>();
        for (int tileY = 0; tileY < 4; tileY++)
        for (int tileX = 0; tileX < 4; tileX++)
        {
            var samples = new float[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) samples[y * size + x] = heights[(tileY * size + y) * side + tileX * size + x];
            tiles.Add(new(new(2, tileX, tileY), size, samples));
        }
        var contiguous = new ElevationSnapshot(tiles);
        tiles.Clear();
        Require(contiguous.Count == 16 && contiguous.MaximumZoom == 2, "A snapshot owns its tile membership.");
        var random = new Random(3581);
        for (int i = 0; i < 20_000; i++)
        {
            double x = random.NextDouble() * 3 - 1, y = random.NextDouble() * 1.4 - .2;
            Equal(ReferenceSample(heights, side, x, y), contiguous.SampleMercator(x, y),
                "Tiled sampling matches a single continuous image across all edges and corners.");
        }
        for (int i = 0; i < 1000; i++) contiguous.SampleMercator(i * .00013, i * .00017);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double total = 0;
        for (int i = 0; i < 10_000; i++) total += contiguous.SampleMercator(i * .00013, i * .00017);
        Require(GC.GetAllocatedBytesForCurrentThread() == before && total > 0,
            "Steady-state bilinear sampling allocates no managed objects.");
    }

    private static void CheckWaterSurfaces()
    {
        var outer = Ring(0, 0, 4096, 4096);
        var hole = Ring(1024, 1024, 3072, 3072);
        var ocean = Water("ocean", [outer, hole]);
        var world = new MapTile(new(0, 0, 0), [ocean]);
        var ground = Constant(new(0, 0, 0), -100);
        var snapshot = new ElevationSnapshot([ground], [world]);
        Equal(-100, snapshot.SampleMercator(.1, .1), "Raw bathymetry stays signed.");
        Equal(0, snapshot.SampleSurfaceMercator(.1, .1), "Mapped oceans use the sea surface.");
        Equal(0, snapshot.SampleSurfaceMercator(.1, 1), "The clamped southern world edge uses its adjacent water surface.");
        Equal(-100, snapshot.SampleSurfaceMercator(.5, .5), "An ocean hole retains below-sea-level land.");
        var lake = Water("lake", [Ring(0, 0, 1024, 1024)]);
        var overlapping = new ElevationSnapshot([ground], [new MapTile(world.Id, [ocean, lake])]);
        Equal(-100, overlapping.SampleSurfaceMercator(.1, .1), "Inland water retains signed elevation even over coarse ocean coverage.");
        var detailed = new ElevationSnapshot([ground], [world, new MapTile(new(1, 0, 0), [])]);
        Equal(-100, detailed.SampleSurfaceMercator(.1, .1), "The highest covering vector tile determines the surface.");
        Equal(0, detailed.SampleSurfaceMercator(.9, .9), "Absent detailed coverage uses the world water index.");
        Require(!detailed.MayContainOcean(new(1, 0, 0)) && detailed.MayContainOcean(new(1, 1, 1)),
            "Ocean bounds respect a higher-resolution authoritative land tile.");
        var covered = new ElevationSnapshot([ground], [world, new MapTile(new(1, 0, 0), []),
            new MapTile(new(1, 0, 1), []), new MapTile(new(1, 1, 0), []), new MapTile(new(1, 1, 1), [])]);
        Require(!covered.MayContainOcean(new(0, 0, 0)), "Four inland children fully replace a coarse ocean tile.");
        var smallOcean = new ElevationSnapshot([ground], [new MapTile(world.Id,
            [Water("ocean", [Ring(70, 70, 90, 90)], 1024)])]);
        Require(smallOcean.MayContainOcean(new(3, 0, 0)) && !smallOcean.MayContainOcean(new(3, 1, 1)),
            "Conservative bounds find small coastal surfaces without sampling only a few grid points.");
        var detailedOcean = new ElevationSnapshot([ground], [new MapTile(world.Id, []),
            new MapTile(new(3, 4, 4), [Water("ocean", [Ring(1024, 1024, 2048, 2048)])])]);
        Require(detailedOcean.MayContainOcean(new(0, 0, 0)) && detailedOcean.MayContainOcean(new(5, 17, 17)) &&
            !detailedOcean.MayContainOcean(new(5, 19, 19)) && !detailedOcean.MayContainOcean(new(1, 0, 0)),
            "Sparse fine ocean geometry propagates through ancestors and narrows to its local bounds.");
        outer[0] = new(3000, 3000);
        Equal(0, snapshot.SampleSurfaceMercator(.1, .1), "Prepared water geometry is independent of source array mutation.");

        // Concave rings, a hole and a disconnected island exercise the interval and cell indexes.
        TilePoint[][] rings = [
            [new(0, 0), new(800, 0), new(800, 200), new(400, 200), new(400, 800), new(0, 800)],
            Ring(50, 100, 250, 350), Ring(850, 850, 1000, 1000)];
        var polygon = Water("sea", rings, 1000);
        var indexed = new ElevationSnapshot([ground], [new MapTile(world.Id, [polygon])]);
        var random = new Random(113);
        for (int i = 0; i < 10_000; i++)
        {
            double x = random.NextDouble(), y = random.NextDouble();
            bool inside = Contains(rings, x * 1000, y * 1000);
            Equal(inside ? 0 : -100, indexed.SampleSurfaceMercator(x, y),
                "Indexed ocean classification matches the independent even-odd ring test.");
            if (inside) Require(indexed.MayContainOcean(new(8, (int)(x * 256), (int)(y * 256))),
                "Conservative ocean bounds never omit a sampled ocean surface.");
        }
        for (int i = 0; i < 1000; i++) indexed.SampleSurfaceMercator(i * .00013, i * .00017);
        long before = GC.GetAllocatedBytesForCurrentThread();
        double total = 0;
        for (int i = 0; i < 10_000; i++) total += indexed.SampleSurfaceMercator(i * .00013, i * .00017);
        Require(GC.GetAllocatedBytesForCurrentThread() == before && total < 0,
            "Water-aware surface sampling allocates no managed objects.");
    }

    private static void CheckPngDecoding()
    {
        static double Height(int x, int y) => (x - 127) * 3.25 + (y - 130) * .265625;
        byte[] png = MakePng(256, 3, (x, y) => Color(Height(x, y)));
        var tile = TerrariumDecoder.Decode(new(3, 4, 2), png);
        Require(tile.Size == 256 && tile.ValidSampleCount == 65536 && tile.Id == new TileId(3, 4, 2),
            "RGB PNG decoding preserves dimensions and request identity.");
        for (int y = 0; y < 256; y++)
        for (int x = 0; x < 256; x++) Equal(Height(x, y), tile[x, y], "Every PNG filter preserves signed fractional heights.");

        var gutters = TerrariumDecoder.Decode(new(0, 0, 0), MakePng(260, 3, (x, y) => Color(x + y * .25)));
        Require(gutters.Size == 256, "Two-pixel gutters are removed.");
        Equal(2.5, gutters[0, 0], "The upper-left gutter is excluded.");
        Equal(321.25, gutters[255, 255], "The lower-right gutter is excluded.");
        var rgba = TerrariumDecoder.Decode(new(0, 0, 0), MakePng(516, 4,
            (x, y) => Color(2523.265625, x == 3 ? (byte)0 : x == 4 ? (byte)128 : (byte)255)));
        Require(rgba.Size == 512 && float.IsNaN(rgba[1, 0]) && float.IsNaN(rgba[2, 0]),
            "Transparent and partially transparent pixels are void samples.");
        Equal(2523.265625, rgba[0, 0], "RGBA channels are decoded without premultiplication or colour conversion.");
        var sentinel = TerrariumDecoder.Decode(new(0, 0, 0), MakePng(256, 3, (_, _) => 255));
        Require(sentinel.ValidSampleCount == 0, "The -32768 encoding sentinel is unavailable data.");
        var transparency = ReadChunks(MakePng(256, 3, (x, _) => Color(x)));
        transparency.Insert(1, new("tRNS", [0, 128, 0, 0, 0, 0]));
        var keyed = TerrariumDecoder.Decode(new(0, 0, 0), WriteChunks(transparency));
        Require(float.IsNaN(keyed[0, 0]) && keyed[1, 0] == 1, "RGB transparency keys preserve the remaining heights.");

        Bad(png[..^1], "Truncated chunks are rejected.");
        byte[] crc = (byte[])png.Clone();
        crc[^1] ^= 1;
        Bad(crc, "Invalid PNG checksums are rejected.");
        Bad([.. png, 0], "Trailing bytes after IEND are rejected.");
        foreach ((int field, byte value) in new (int, byte)[] { (8, 16), (9, 0), (12, 1), (0, 127) })
        {
            var chunks = ReadChunks(png);
            chunks[0].Data[field] = value;
            Bad(WriteChunks(chunks), "Unsupported or oversized headers are rejected before allocation.");
        }
        var missingEnd = ReadChunks(png);
        missingEnd.RemoveAt(missingEnd.Count - 1);
        Bad(WriteChunks(missingEnd), "An IEND chunk is required.");
        var critical = ReadChunks(png);
        critical.Insert(1, new("ABCD", []));
        Bad(WriteChunks(critical), "Unknown critical chunks are rejected.");
        var separated = ReadChunks(png);
        separated.Insert(2, new("tEXt", "comment\0test"u8.ToArray()));
        Bad(WriteChunks(separated), "IDAT chunks must be consecutive.");
        var invalidFilter = ReadChunks(png);
        byte[] raw = Inflate(invalidFilter);
        raw[0] = 5;
        ReplacePixels(invalidFilter, raw);
        Bad(WriteChunks(invalidFilter), "Undefined row filters are rejected.");
        var excessPixels = ReadChunks(png);
        ReplacePixels(excessPixels, [.. Inflate(excessPixels), 0]);
        Bad(WriteChunks(excessPixels), "Decompressed bytes are limited to the declared image.");
        var shortPixels = ReadChunks(png);
        ReplacePixels(shortPixels, Inflate(shortPixels)[..^1]);
        Bad(WriteChunks(shortPixels), "Truncated decompressed rows are rejected.");
    }

    private static async Task CheckSource()
    {
        byte[] png = MakePng(256, 3, (_, _) => Color(123.25));
        using var handler = new ElevationHandler(png);
        using var http = new HttpClient(handler);
        var source = new ElevationTileSource(http);
        var world = await source.GetTileAsync(new(0, 0, 0));
        var garda = new List<ElevationTile>();
        for (int y = 1458; y <= 1460; y++)
        for (int x = 2170; x <= 2172; x++) garda.Add(await source.GetTileAsync(new(12, x, y)));
        Require(handler.Requests == 0 && world.MinimumMeters < -1000 && world.MaximumMeters > 1000,
            "World terrain and all nine Garda tiles are available offline with real relief.");
        Require(garda.Max(tile => tile.MaximumMeters) > 2000 && garda.Min(tile => tile.MinimumMeters) < 100,
            "Garda fixtures include high mountains and the lake shore.");
        var mapWorld = await new VectorTileSource(http).GetTileAsync(new(0, 0, 0));
        var field = new ElevationSnapshot([world, .. garda], [mapWorld]);
        Require(field.SampleMeters(0, -140) < -1000 && field.SampleSurfaceMeters(0, -140) == 0,
            "Real global MVT water geometry raises Pacific bathymetry to the ocean surface.");
        double riva = field.SampleMeters(45.875, 10.85);
        Require(riva is > 40 and < 200, "The Garda camera anchor lies near the real valley floor.");
        Require(ReferenceEquals(garda[0], await source.GetTileAsync(garda[0].Id)), "Decoded DEM fixtures are reused.");

        const string endpoint = "https://unit.test/height/{z}/{x}/{y}.png";
        var custom = new ElevationTileSource(http, endpoint);
        Equal(123.25, (await custom.GetTileAsync(new(0, 0, 0)))[0, 0], "A custom provider supplies its own world tile.");
        Require(handler.Requests == 1 && handler.LastUri?.AbsolutePath == "/height/0/0/0.png",
            "Custom templates receive XYZ coordinates and bypass provider fixtures.");
        Throws<ArgumentException>(() => new ElevationTileSource(http, "http://unit.test/{z}/{x}/{y}"), "Providers require HTTPS.");
        Throws<ArgumentException>(() => new ElevationTileSource(http, "https:///{z}/{x}/{y}"), "Provider templates require a valid host.");
        int before = handler.Requests;
        await ThrowsAsync<ArgumentOutOfRangeException>(() => custom.GetTileAsync(new(16, 0, 0)), "Provider zoom is bounded.");
        await ThrowsAsync<ArgumentOutOfRangeException>(() => custom.GetTileAsync(new(2, -1, 0)), "Provider tile IDs are canonical.");
        Require(handler.Requests == before, "Invalid requests never reach the network.");

        handler.Pause();
        using var firstCancellation = new CancellationTokenSource();
        var first = custom.GetTileAsync(new(4, 2, 2), firstCancellation.Token);
        var second = custom.GetTileAsync(new(4, 2, 2));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        firstCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => first, "One consumer can cancel independently.");
        Require(!second.IsCompleted && handler.Requests == before + 1, "DEM consumers share one pending request.");
        handler.Release.TrySetResult();
        await second;

        handler.Pause();
        using var lastCancellation = new CancellationTokenSource();
        var abandoned = custom.GetTileAsync(new(4, 3, 2), lastCancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        lastCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => abandoned, "The final consumer can leave.");
        await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        handler.Release.TrySetResult();
        await custom.GetTileAsync(new(4, 3, 2));
        Require(handler.Requests == before + 3, "Canceled upstream requests are retryable.");

        handler.Payload = "Provider error"u8.ToArray();
        await ThrowsAsync<InvalidDataException>(() => custom.GetTileAsync(new(4, 4, 2)), "Malformed provider responses are rejected.");
        handler.Payload = png;
        await custom.GetTileAsync(new(4, 4, 2));
        handler.Status = HttpStatusCode.ServiceUnavailable;
        await ThrowsAsync<HttpRequestException>(() => custom.GetTileAsync(new(4, 5, 2)), "Failed HTTP responses are rejected.");
        handler.Status = HttpStatusCode.OK;
        await custom.GetTileAsync(new(4, 5, 2));
        handler.Payload = new byte[2 * 1024 * 1024 + 1];
        await ThrowsAsync<InvalidDataException>(() => custom.GetTileAsync(new(4, 6, 2)), "Encoded payloads are bounded before decoding.");
        handler.Payload = png;

        handler.Pause();
        var requests = Enumerable.Range(0, 12).Select(x => custom.GetTileAsync(new(5, x, 5))).ToArray();
        await handler.FourActive.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(handler.Active == 4 && handler.MaximumActive == 4, "Elevation fetch concurrency is limited to four.");
        handler.Release.TrySetResult();
        await Task.WhenAll(requests);
        Require(handler.MaximumActive == 4, "Queued elevation work retains the concurrency bound.");

        handler.Payload = MakePng(512, 3, (_, _) => Color(12));
        var bounded = new ElevationTileSource(http, endpoint);
        var oldest = await bounded.GetTileAsync(new(6, 0, 0));
        ElevationTile? newest = null;
        for (int x = 1; x < 17; x++) newest = await bounded.GetTileAsync(new(6, x, 0));
        before = handler.Requests;
        Require(ReferenceEquals(newest, await bounded.GetTileAsync(new(6, 16, 0))), "Recent decoded terrain remains cached.");
        Require(!ReferenceEquals(oldest, await bounded.GetTileAsync(new(6, 0, 0))) && handler.Requests == before + 1,
            "The 16 MiB decoded budget evicts old terrain tiles.");
        Console.WriteLine($"Offline DEM range {field.MinimumMeters:F0} to {field.MaximumMeters:F0} m, Riva anchor {riva:F1} m.");
    }

    private static ElevationTile Constant(TileId id, float value, int size = 2) =>
        new(id, size, Enumerable.Repeat(value, size * size).ToArray());

    private static MapFeature Water(string kind, TilePoint[][] rings, int extent = 4096) =>
        new("water", kind, "", "", 0, extent, GeometryType.Polygon, rings, null, null, false, 0, 0, "");

    private static TilePoint[] Ring(int left, int top, int right, int bottom) =>
        [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];

    private static bool Contains(TilePoint[][] rings, double x, double y)
    {
        bool inside = false;
        foreach (var ring in rings)
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static double ReferenceSample(float[] heights, int size, double x, double y)
    {
        double pixelX = (x - Math.Floor(x)) * size - .5;
        double pixelY = Math.Clamp(y, 0, 1) * size - .5;
        int left = (int)Math.Floor(pixelX), top = (int)Math.Floor(pixelY);
        double fractionX = pixelX - left, fractionY = pixelY - top;
        double At(int column, int row) => heights[Math.Clamp(row, 0, size - 1) * size + (column + size) % size];
        double a = At(left, top) * (1 - fractionX) + At(left + 1, top) * fractionX;
        double b = At(left, top + 1) * (1 - fractionX) + At(left + 1, top + 1) * fractionX;
        return a * (1 - fractionY) + b * fractionY;
    }

    private static uint Color(double height, byte alpha = 255) =>
        ((uint)Math.Round((height + 32768) * 256) << 8) | alpha;

    private static byte[] MakePng(int width, int channels, Func<int, int, uint> color)
    {
        int stride = width * channels;
        byte[] raw = new byte[(stride + 1) * width], previous = new byte[stride];
        for (int y = 0; y < width; y++)
        {
            var pixels = new byte[stride];
            for (int x = 0; x < width; x++)
            {
                uint value = color(x, y);
                for (int channel = 0; channel < channels; channel++) pixels[x * channels + channel] = (byte)(value >> (24 - channel * 8));
            }
            int offset = y * (stride + 1), filter = y % 5;
            raw[offset++] = (byte)filter;
            for (int i = 0; i < stride; i++)
            {
                int left = i < channels ? 0 : pixels[i - channels], above = previous[i], upperLeft = i < channels ? 0 : previous[i - channels];
                int prediction = left + above - upperLeft;
                int paeth = Math.Abs(prediction - left) <= Math.Abs(prediction - above) && Math.Abs(prediction - left) <= Math.Abs(prediction - upperLeft)
                    ? left : Math.Abs(prediction - above) <= Math.Abs(prediction - upperLeft) ? above : upperLeft;
                int value = filter switch { 1 => left, 2 => above, 3 => (left + above) / 2, 4 => paeth, _ => 0 };
                raw[offset + i] = unchecked((byte)(pixels[i] - value));
            }
            previous = pixels;
        }
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), width);
        header[8] = 8;
        header[9] = channels == 3 ? (byte)2 : (byte)6;
        byte[] compressed = Deflate(raw);
        return WriteChunks([new("IHDR", header), new("IDAT", compressed[..(compressed.Length / 2)]),
            new("IDAT", compressed[(compressed.Length / 2)..]), new("IEND", [])]);
    }

    private sealed record Chunk(string Kind, byte[] Data);

    private static List<Chunk> ReadChunks(byte[] png)
    {
        var result = new List<Chunk>();
        for (int offset = 8; offset < png.Length;)
        {
            int count = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            result.Add(new(Encoding.ASCII.GetString(png, offset + 4, 4), png.AsSpan(offset + 8, count).ToArray()));
            offset += count + 12;
        }
        return result;
    }

    private static byte[] WriteChunks(List<Chunk> chunks)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> value = stackalloc byte[4];
        foreach (var chunk in chunks)
        {
            BinaryPrimitives.WriteInt32BigEndian(value, chunk.Data.Length);
            output.Write(value);
            byte[] type = Encoding.ASCII.GetBytes(chunk.Kind);
            output.Write(type);
            output.Write(chunk.Data);
            uint crc = uint.MaxValue;
            foreach (byte item in type.Concat(chunk.Data))
            {
                crc ^= item;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
            BinaryPrimitives.WriteUInt32BigEndian(value, ~crc);
            output.Write(value);
        }
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var encoder = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true)) encoder.Write(raw);
        return output.ToArray();
    }

    private static byte[] Inflate(List<Chunk> chunks)
    {
        using var input = new MemoryStream(chunks.Where(chunk => chunk.Kind == "IDAT").SelectMany(chunk => chunk.Data).ToArray());
        using var decoder = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static void ReplacePixels(List<Chunk> chunks, byte[] raw)
    {
        chunks.RemoveAll(chunk => chunk.Kind == "IDAT");
        chunks.Insert(1, new("IDAT", Deflate(raw)));
    }

    private static void Bad(byte[] png, string message) => Throws<InvalidDataException>(() => TerrariumDecoder.Decode(new(0, 0, 0), png), message);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal(double expected, double actual, string message) =>
        Require(double.IsFinite(actual) && Math.Abs(expected - actual) < 1e-7, $"{message} Expected {expected}, got {actual}.");

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class ElevationHandler(byte[] payload) : HttpMessageHandler
    {
        internal byte[] Payload = payload;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal int Requests, Active, MaximumActive;
        internal Uri? LastUri;
        internal TaskCompletionSource Started = Signal(), Release = Completed(), Canceled = Signal(), FourActive = Signal();

        internal void Pause()
        {
            Started = Signal();
            Release = Signal();
            Canceled = Signal();
            FourActive = Signal();
            MaximumActive = 0;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            LastUri = request.RequestUri;
            Interlocked.Increment(ref Requests);
            int active = Interlocked.Increment(ref Active);
            int maximum;
            do { maximum = MaximumActive; }
            while (active > maximum && Interlocked.CompareExchange(ref MaximumActive, active, maximum) != maximum);
            if (active == 4) FourActive.TrySetResult();
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(token);
                return new(Status) { Content = new ByteArrayContent(Payload) };
            }
            catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
            finally { Interlocked.Decrement(ref Active); }
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static TaskCompletionSource Completed()
        {
            var result = Signal();
            result.SetResult();
            return result;
        }
    }
}
