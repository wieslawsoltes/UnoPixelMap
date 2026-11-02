namespace PixelMap.Data;

/// <summary>Encoded 256 × 256 JPEG tile in the Web Mercator XYZ grid.</summary>
public sealed record ImageryTile(TileId Id, byte[] Encoded);

/// <summary>Natural-colour 2016 Sentinel-2 cloudless mosaic. Imagery remains independent of camera pitch.</summary>
public sealed class EarthImagerySource
{
    public const int MaxZoom = 14;
    public const string Attribution = "EOxCloudless by EOX IT Services GmbH (Contains modified Copernicus Sentinel data 2016 & 2017)";
    public const string AttributionUrl = "https://cloudless.eox.at";
    public const string LicenseUrl = "https://creativecommons.org/licenses/by/4.0/";
    private const int MaxTileBytes = 512 * 1024;
    private const string Endpoint = "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless_3857/default/GoogleMapsCompatible";

    private readonly HttpClient _client;
    private readonly TileRequestCache<ImageryTile> _cache;

    public EarthImagerySource(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _cache = new(MaxZoom, 4 * 1024 * 1024, ReadAsync, static tile => tile.Encoded.Length + 64);
    }

    public Task<ImageryTile> GetTileAsync(TileId id, CancellationToken cancellationToken = default) =>
        _cache.GetTileAsync(id, cancellationToken);

    private async Task<ImageryTile> ReadAsync(TileId id, CancellationToken token)
    {
        using var seed = TilePayload.OpenSeed(id, "Imagery", "jpg");
        byte[] content;
        if (seed != null)
        {
            content = await TilePayload.ReadBoundedAsync(seed, MaxTileBytes, token).ConfigureAwait(false);
        }
        else
        {
            // WMTS path order is matrix / row / column, while the public API uses z / x / y.
            var uri = FormattableString.Invariant($"{Endpoint}/{id.Z}/{id.Y}/{id.X}.jpg");
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            content = await TilePayload.ReadBoundedAsync(stream, MaxTileBytes, token).ConfigureAwait(false);
        }
        if (content is not [0xff, 0xd8, 0xff, .., 0xff, 0xd9])
            throw new InvalidDataException("Expected a complete JPEG tile from EOxCloudless.");
        return new(id, content);
    }
}
