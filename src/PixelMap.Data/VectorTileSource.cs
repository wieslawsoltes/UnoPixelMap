using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace PixelMap.Data;

/// <summary>Streams the current OpenFreeMap planet. Concurrent callers share one request per tile.
/// The last departing waiter cancels queued or in-flight network work.</summary>
public sealed class VectorTileSource(HttpClient client)
{
    public const int MaxZoom = 14;
    public const string Attribution = "OpenFreeMap · © OpenMapTiles · Data from OpenStreetMap";
    private const string Catalog = "https://tiles.openfreemap.org/planet";
    private const int CacheBudget = 32 * 1024 * 1024;

    private readonly object _sync = new();
    private readonly Dictionary<TileId, Request> _pending = [];
    private readonly Dictionary<TileId, LinkedListNode<CachedTile>> _index = [];
    private readonly LinkedList<CachedTile> _lru = new();
    private readonly SemaphoreSlim _slots = new(4);
    private readonly SemaphoreSlim _catalogLock = new(1);
    private string? _template;
    private DateTimeOffset _expires;
    private long _cacheBytes;

    private sealed record CachedTile(MapTile Tile, long Bytes);
    private sealed class Request
    {
        public int Waiters;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<MapTile> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task<MapTile> GetTileAsync(TileId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id.Z is < 0 or > MaxZoom || (uint)id.X >= 1u << id.Z || (uint)id.Y >= 1u << id.Z)
            throw new ArgumentOutOfRangeException(nameof(id), "Supply a canonical XYZ tile at a native zoom.");
        Request request;
        bool start = false;
        lock (_sync)
        {
            if (_index.TryGetValue(id, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Tile;
            }
            if (!_pending.TryGetValue(id, out request!))
            {
                _pending.Add(id, request = new());
                start = true;
            }
            request.Waiters++;
        }

        if (start) _ = ProduceAsync(id, request);
        try { return await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                if (--request.Waiters == 0 && _pending.TryGetValue(id, out var current) && current == request)
                {
                    _pending.Remove(id);
                    request.Cancellation.Cancel();
                }
            }
        }
    }

    private async Task ProduceAsync(TileId id, Request request)
    {
        try
        {
            var token = request.Cancellation.Token;
            await _slots.WaitAsync(token).ConfigureAwait(false);
            MapTile tile;
            try { tile = await ReadAsync(id, token).ConfigureAwait(false); }
            finally { _slots.Release(); }

            long bytes = 128;
            foreach (var f in tile.Features)
            {
                bytes += 208 + (f.Name.Length + f.Class.Length) * 2L;
                foreach (var path in f.Paths) bytes += 32 + path.Length * 8L;
            }

            lock (_sync)
            {
                if (!token.IsCancellationRequested)
                {
                    _index[id] = _lru.AddFirst(new CachedTile(tile, bytes));
                    _cacheBytes += bytes;
                    while ((_cacheBytes > CacheBudget || _lru.Count > 96) && _lru.Last is { } last)
                    {
                        _index.Remove(last.Value.Tile.Id);
                        _cacheBytes -= last.Value.Bytes;
                        _lru.RemoveLast();
                    }
                }
                Retire(id, request);
            }
            request.Completion.TrySetResult(tile);
        }
        catch (Exception error)
        {
            lock (_sync) Retire(id, request);
            if (error is OperationCanceledException) request.Completion.TrySetCanceled();
            else request.Completion.TrySetException(error);
        }
        finally { request.Cancellation.Dispose(); }
    }

    private void Retire(TileId id, Request request)
    {
        if (_pending.TryGetValue(id, out var current) && current == request) _pending.Remove(id);
    }

    private async Task<MapTile> ReadAsync(TileId id, CancellationToken token)
    {
        if (id is { Z: 14, X: >= 4822 and <= 4824, Y: >= 6159 and <= 6161 })
        {
            var resource = FormattableString.Invariant($"PixelMap.Seed.{id.Z}-{id.X}-{id.Y}.pbf.gz");
            using var seed = typeof(VectorTileSource).Assembly.GetManifestResourceStream(resource);
            if (seed != null)
            {
                using var gzip = new GZipStream(seed, CompressionMode.Decompress);
                return VectorTileDecoder.Decode(id, await ReadBoundedAsync(gzip, token).ConfigureAwait(false));
            }
        }

        string template = await GetTemplateAsync(token).ConfigureAwait(false);
        string url = template.Replace("{z}", id.Z.ToString(CultureInfo.InvariantCulture))
            .Replace("{x}", id.X.ToString(CultureInfo.InvariantCulture))
            .Replace("{y}", id.Y.ToString(CultureInfo.InvariantCulture));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var network = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] data = await ReadBoundedAsync(network, token).ConfigureAwait(false);
        // Browser fetch normally removes Content-Encoding. A raw gzip tile is also valid input.
        if (data is [0x1f, 0x8b, ..])
        {
            using var gzip = new GZipStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
            data = await ReadBoundedAsync(gzip, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        return VectorTileDecoder.Decode(id, data);
    }

    private async Task<string> GetTemplateAsync(CancellationToken token)
    {
        await _catalogLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_template != null && DateTimeOffset.UtcNow < _expires) return _template;
            using var result = await client.GetAsync(Catalog, token).ConfigureAwait(false);
            result.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await result.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
            string? template = json.RootElement.GetProperty("tiles")[0].GetString();
            if (template is null || !template.StartsWith("https://", StringComparison.Ordinal) ||
                !template.Contains("{z}") || !template.Contains("{x}") || !template.Contains("{y}"))
                throw new InvalidDataException("The tile catalog does not contain an HTTPS XYZ template.");
            _template = template;
            _expires = DateTimeOffset.UtcNow.Add(result.Headers.CacheControl?.MaxAge ?? TimeSpan.FromHours(1));
            return template;
        }
        finally { _catalogLock.Release(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0) return output.ToArray();
                if (output.Length + read > VectorTileDecoder.MaxBytes)
                    throw new InvalidDataException("Vector tile exceeds the byte budget.");
                output.Write(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
