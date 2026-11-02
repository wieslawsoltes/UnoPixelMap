using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PixelMap.Data;

public sealed record MapPlace(string Name, string Detail, double Latitude, double Longitude, double Zoom);

/// <summary>Explicit-submit place search with an injectable Photon-compatible endpoint.</summary>
public sealed class PhotonGeocoder
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<MapPlace> Results)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PhotonGeocoder(HttpClient http, Uri? endpoint = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _endpoint = endpoint ?? new Uri("https://photon.komoot.io/api/");
    }

    public static bool TryParseCoordinates(string query, out MapPlace place)
    {
        place = null!;
        var parts = query.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
            !double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            Math.Abs(latitude) > 85.05112878 || Math.Abs(longitude) > 180)
            return false;

        place = new MapPlace("Dropped pin", FormattableString.Invariant($"{latitude:0.#####}, {longitude:0.#####}"), latitude, longitude, 16.5);
        return true;
    }

    public async Task<IReadOnlyList<MapPlace>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = query.Trim();
        if (query.Length is < 1 or > 200) return Array.Empty<MapPlace>();
        if (TryParseCoordinates(query, out var point)) return new[] { point };

        lock (_cache)
        {
            if (_cache.TryGetValue(query, out var entry) && DateTimeOffset.UtcNow - entry.At < TimeSpan.FromHours(1))
                return entry.Results;
        }

        var url = new UriBuilder(_endpoint) { Query = $"q={Uri.EscapeDataString(query)}&limit=6" }.Uri;
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var results = new List<MapPlace>(6);
        foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
        {
            if (!feature.TryGetProperty("geometry", out var geometry) ||
                !geometry.TryGetProperty("coordinates", out var coordinates) ||
                coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() < 2 ||
                !coordinates[0].TryGetDouble(out var lon) || !coordinates[1].TryGetDouble(out var lat) ||
                !double.IsFinite(lon) || !double.IsFinite(lat) || Math.Abs(lon) > 180 || Math.Abs(lat) > 85.05112878 ||
                !feature.TryGetProperty("properties", out var properties))
                continue;
            var street = string.Join(" ", new[] { Get(properties, "housenumber"), Get(properties, "street") }.Where(s => s.Length > 0));
            var name = Get(properties, "name");
            if (name.Length == 0) name = street;
            if (name.Length == 0) name = Get(properties, "city");
            if (name.Length == 0) continue;
            var address = new[] { street, Get(properties, "district"), Get(properties, "city"), Get(properties, "state"), Get(properties, "country") };
            var detail = string.Join(", ", address.Where(s => s.Length > 0 && !s.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase));
            var type = Get(properties, "type");
            double zoom = type switch
            {
                "country" => 6, "state" => 9, "county" => 11, "city" => 14,
                "district" => 15, "locality" => 15.5, "street" => 16.5, _ => 17
            };
            results.Add(new MapPlace(name, detail.Length > 0 ? detail : type, lat, lon, zoom));
            if (results.Count == 6) break;
        }

        var answer = results.AsReadOnly();
        lock (_cache)
        {
            if (_cache.Count >= 64 && !_cache.ContainsKey(query))
                _cache.Remove(_cache.MinBy(entry => entry.Value.At).Key);
            _cache[query] = (DateTimeOffset.UtcNow, answer);
        }
        return answer;
    }

    private static string Get(JsonElement properties, string key) =>
        properties.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";
}
