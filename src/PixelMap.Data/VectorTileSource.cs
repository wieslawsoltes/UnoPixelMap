using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace PixelMap.Data;

/// <summary>Streams the current OpenFreeMap planet. Concurrent callers share one request per tile.
/// The last departing waiter cancels queued or in-flight network work.</summary>
public sealed class VectorTileSource
{
    public const int MaxZoom = 14;
    public const string Attribution = "OpenFreeMap · © OpenMapTiles · Data from OpenStreetMap";
    private const string Catalog = "https://tiles.openfreemap.org/planet";
    private const int CacheBudget = 32 * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly TileRequestCache<MapTile> _cache;
    private readonly SemaphoreSlim _catalogLock = new(1);
    private string? _template;
    private DateTimeOffset _expires;

    public VectorTileSource(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _cache = new(MaxZoom, CacheBudget, ReadAsync, EstimatedSize);
    }

    public Task<MapTile> GetTileAsync(TileId id, CancellationToken cancellationToken = default) =>
        _cache.GetTileAsync(id, cancellationToken);

    private static long EstimatedSize(MapTile tile)
    {
        long bytes = 128;
        foreach (var f in tile.Features)
        {
            bytes += 208 + (f.Name.Length + f.Class.Length) * 2L;
            foreach (var path in f.Paths) bytes += 32 + path.Length * 8L;
        }
        return bytes;
    }

    private async Task<MapTile> ReadAsync(TileId id, CancellationToken token)
    {
        using var seed = TilePayload.OpenSeed(id, "Seed", "pbf.gz");
        if (seed != null)
        {
            using var gzip = new GZipStream(seed, CompressionMode.Decompress);
            return VectorTileDecoder.Decode(id, await TilePayload.ReadBoundedAsync(gzip, VectorTileDecoder.MaxBytes, token).ConfigureAwait(false));
        }

        string template = await GetTemplateAsync(token).ConfigureAwait(false);
        string url = template.Replace("{z}", id.Z.ToString(CultureInfo.InvariantCulture))
            .Replace("{x}", id.X.ToString(CultureInfo.InvariantCulture))
            .Replace("{y}", id.Y.ToString(CultureInfo.InvariantCulture));
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var network = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] data = await TilePayload.ReadBoundedAsync(network, VectorTileDecoder.MaxBytes, token).ConfigureAwait(false);
        // Browser fetch normally removes Content-Encoding. A raw gzip tile is also valid input.
        if (data is [0x1f, 0x8b, ..])
        {
            using var gzip = new GZipStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
            data = await TilePayload.ReadBoundedAsync(gzip, VectorTileDecoder.MaxBytes, token).ConfigureAwait(false);
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
            using var result = await _client.GetAsync(Catalog, token).ConfigureAwait(false);
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
}
