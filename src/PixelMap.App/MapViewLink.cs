using System;
using System.Globalization;
using PixelMap.Rendering;

namespace PixelMap.App;

/// <summary>Versioned camera fragments are locale independent and preserve the Pages project base path.</summary>
internal static class MapViewLink
{
    internal static Uri Create(Uri page, MapCamera camera, PixelStyle palette, bool earth) =>
        new UriBuilder(page)
        {
            Fragment = FormattableString.Invariant($"v3/{camera.Latitude:F6}/{camera.Longitude:F6}/{camera.Zoom:F3}/{Math.IEEERemainder(camera.Bearing, 360):F2}/{camera.Pitch:F2}/{palette}/{(earth ? "Earth" : "Map")}/{(camera.Perspective ? "Perspective" : "Orthographic")}/{camera.TargetAltitudeMeters:F1}")
        }.Uri;

    internal static Uri Create(Uri page, MapCamera camera, PixelStyle palette, bool earth,
        bool elevation, bool terrain, double exaggeration) =>
        new UriBuilder(page)
        {
            Fragment = FormattableString.Invariant($"v4/{camera.Latitude:F6}/{camera.Longitude:F6}/{camera.Zoom:F3}/{Math.IEEERemainder(camera.Bearing, 360):F2}/{camera.Pitch:F2}/{palette}/{(elevation ? "Elevation" : earth ? "Earth" : "Map")}/{(camera.Perspective ? "Perspective" : "Orthographic")}/{camera.TargetAltitudeMeters:F1}/{(terrain ? "Relief" : "Flat")}/{exaggeration:F2}")
        }.Uri;

    internal static bool TryRead(Uri page, out MapCamera camera, out PixelStyle palette, out bool earth) =>
        TryRead(page, out camera, out palette, out earth, out _, out _, out _);

    internal static bool TryRead(Uri page, out MapCamera camera, out PixelStyle palette, out bool earth,
        out bool elevation, out bool terrain, out double exaggeration)
    {
        camera = default;
        palette = PixelStyle.Day;
        earth = false;
        elevation = terrain = false;
        exaggeration = 1;
        var fields = page.Fragment.Split('/');
        bool perspective = false;
        double targetAltitude = 0;
        if (fields is ["#v4", _, _, _, _, _, _, "Map" or "Earth" or "Elevation", "Perspective" or "Orthographic", _, "Relief" or "Flat", _])
        {
            earth = fields[7] == "Earth";
            elevation = fields[7] == "Elevation";
            perspective = fields[8] == "Perspective";
            terrain = fields[10] == "Relief";
            if (!double.TryParse(fields[9], NumberStyles.Float, CultureInfo.InvariantCulture, out targetAltitude) ||
                !double.IsFinite(targetAltitude) || targetAltitude is < 0 or > 15000 ||
                !double.TryParse(fields[11], NumberStyles.Float, CultureInfo.InvariantCulture, out exaggeration) ||
                !double.IsFinite(exaggeration) || exaggeration is < 1 or > 4) return false;
        }
        else if (fields is ["#v3", _, _, _, _, _, _, "Map" or "Earth", "Perspective" or "Orthographic"] or
            ["#v3", _, _, _, _, _, _, "Map" or "Earth", "Perspective" or "Orthographic", _])
        {
            earth = fields[7] == "Earth";
            perspective = fields[8] == "Perspective";
            if (fields.Length == 10 && (!double.TryParse(fields[9], NumberStyles.Float, CultureInfo.InvariantCulture, out targetAltitude) ||
                !double.IsFinite(targetAltitude) || targetAltitude is < 0 or > 15000)) return false;
        }
        else if (fields is ["#v2", _, _, _, _, _, _, "Map" or "Earth"])
            earth = fields[7] == "Earth";
        else if (fields is not ["#v1", _, _, _, _, _, _]) return false;
        Span<double> numbers = stackalloc double[5];
        for (var i = 0; i < numbers.Length; i++)
            if (!double.TryParse(fields[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || !double.IsFinite(numbers[i]))
                return false;
        if (Math.Abs(numbers[0]) > 85.051129 || Math.Abs(numbers[1]) > 180 ||
            numbers[2] < MapCamera.MinZoom || numbers[2] > MapCamera.MaxZoom || Math.Abs(numbers[3]) > 360 ||
            numbers[4] is < 0 or > 72 || !Enum.TryParse(fields[6], out palette) || !Enum.IsDefined(palette))
            return false;
        camera = new MapCamera(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], perspective, targetAltitude);
        return true;
    }
}
