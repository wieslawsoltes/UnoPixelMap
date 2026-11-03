using System.Globalization;

namespace PixelMap.Data;

/// <summary>Keyless worldwide Terrain Tiles from the AWS Open Data registry. Decoded samples
/// share the existing cancellation, coalescing, parallelism and byte-bounded tile cache.</summary>
public sealed class ElevationTileSource
{
    public const int MaxZoom = 15;
    public const string Attribution = "Mapzen Terrain Tiles · © terrain data contributors";
    public const string AttributionUrl = "https://github.com/tilezen/joerd/blob/master/docs/attribution.md";
    public const string DefaultEndpoint = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png";
    private const long CacheBudget = 16 * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly string _endpoint;
    private readonly bool _useSeed;
    private readonly TileRequestCache<ElevationTile> _cache;

    /// <summary>Supply a Terrarium XYZ HTTPS endpoint to use another provider. A custom
    /// endpoint uses its own data for every tile, including the world overview.</summary>
    public ElevationTileSource(HttpClient client, string? endpointTemplate = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _endpoint = endpointTemplate ?? DefaultEndpoint;
        string example = _endpoint.Replace("{z}", "0", StringComparison.Ordinal)
            .Replace("{x}", "0", StringComparison.Ordinal).Replace("{y}", "0", StringComparison.Ordinal);
        if (!_endpoint.Contains("{z}", StringComparison.Ordinal) || !_endpoint.Contains("{x}", StringComparison.Ordinal) ||
            !_endpoint.Contains("{y}", StringComparison.Ordinal) ||
            !Uri.TryCreate(example, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Supply an HTTPS Terrarium XYZ template.", nameof(endpointTemplate));
        _useSeed = endpointTemplate is null || endpointTemplate == DefaultEndpoint;
        _cache = new(MaxZoom, CacheBudget, ReadAsync, static tile => tile.Size * (long)tile.Size * sizeof(float) + 128);
    }

    public Task<ElevationTile> GetTileAsync(TileId id, CancellationToken cancellationToken = default) =>
        _cache.GetTileAsync(id, cancellationToken);

    private async Task<ElevationTile> ReadAsync(TileId id, CancellationToken token)
    {
        using var seed = _useSeed ? TilePayload.OpenSeed(id, "Elevation", "png") : null;
        byte[] encoded;
        if (seed is not null)
            encoded = await TilePayload.ReadBoundedAsync(seed, TerrariumDecoder.MaxBytes, token).ConfigureAwait(false);
        else
        {
            string uri = _endpoint.Replace("{z}", id.Z.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{x}", id.X.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{y}", id.Y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            encoded = await TilePayload.ReadBoundedAsync(stream, TerrariumDecoder.MaxBytes, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        return TerrariumDecoder.Decode(id, encoded);
    }
}
