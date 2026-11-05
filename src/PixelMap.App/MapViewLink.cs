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

    internal static Uri Create(Uri page, MapCamera camera, PixelStyle palette, bool earth,
        bool elevation, bool terrain, double exaggeration, MapRenderStyle renderStyle) =>
        new UriBuilder(page)
        {
            Fragment = FormattableString.Invariant($"v5/{camera.Latitude.ToString(camera.IsFreeLook ? "G17" : "F9", CultureInfo.InvariantCulture)}/{camera.Longitude:F9}/{camera.Zoom:F6}/{Math.IEEERemainder(camera.Bearing, 360):F4}/{camera.Pitch:F4}/{palette}/{(elevation ? "Elevation" : earth ? "Earth" : "Map")}/{(camera.Perspective ? "Perspective" : "Orthographic")}/{camera.TargetAltitudeMeters:F3}/{(terrain ? "Relief" : "Flat")}/{exaggeration:F2}/{renderStyle}/{(camera.IsFreeLook ? "FreeLook" : "Orbit")}/{camera.ViewDistanceMeters:F2}/{camera.GroundAltitudeMeters:F3}/{camera.ViewportHeight:F2}")
        }.Uri;

    internal static bool TryRead(Uri page, out MapCamera camera, out PixelStyle palette, out bool earth) =>
        TryRead(page, out camera, out palette, out earth, out _, out _, out _);

    internal static bool TryRead(Uri page, out MapCamera camera, out PixelStyle palette, out bool earth,
        out bool elevation, out bool terrain, out double exaggeration)
        => TryRead(page, out camera, out palette, out earth, out elevation, out terrain, out exaggeration, out _);

    internal static bool TryRead(Uri page, out MapCamera camera, out PixelStyle palette, out bool earth,
        out bool elevation, out bool terrain, out double exaggeration, out MapRenderStyle renderStyle)
    {
        camera = default;
        palette = PixelStyle.Day;
        earth = false;
        elevation = terrain = false;
        exaggeration = 1;
        renderStyle = MapRenderStyle.Pixel;
        var fields = page.Fragment.Split('/');
        bool perspective = false, freeLook = false;
        double targetAltitude = 0, viewDistance = 0, groundAltitude = 0, viewportHeight = 0;
        if (fields is ["#v5", _, _, _, _, _, _, "Map" or "Earth" or "Elevation", "Perspective" or "Orthographic", _, "Relief" or "Flat", _, _, "Orbit" or "FreeLook", _, _, _])
        {
            earth = fields[7] == "Earth";
            elevation = fields[7] == "Elevation";
            perspective = fields[8] == "Perspective";
            terrain = fields[10] == "Relief";
            freeLook = fields[13] == "FreeLook";
            if (!Number(fields[9], out targetAltitude) || targetAltitude < (freeLook ? -1e7 : 0) || targetAltitude > 1e7 ||
                !Number(fields[11], out exaggeration) || exaggeration is < 1 or > 4 ||
                !Enum.TryParse(fields[12], out renderStyle) || !Enum.IsDefined(renderStyle) ||
                !Number(fields[14], out viewDistance) || viewDistance is < 0 or > 1e7 ||
                !Number(fields[15], out groundAltitude) || Math.Abs(groundAltitude) > 32768 ||
                !Number(fields[16], out viewportHeight) || viewportHeight is < 0 or > 1e7 ||
                freeLook && (!perspective || viewDistance <= 0 || viewportHeight <= 0)) return false;
        }
        else if (fields is ["#v4", _, _, _, _, _, _, "Map" or "Earth" or "Elevation", "Perspective" or "Orthographic", _, "Relief" or "Flat", _])
        {
            earth = fields[7] == "Earth";
            elevation = fields[7] == "Elevation";
            perspective = fields[8] == "Perspective";
            terrain = fields[10] == "Relief";
            if (!Number(fields[9], out targetAltitude) || targetAltitude is < 0 or > 1e7 ||
                !Number(fields[11], out exaggeration) || exaggeration is < 1 or > 4) return false;
        }
        else if (fields is ["#v3", _, _, _, _, _, _, "Map" or "Earth", "Perspective" or "Orthographic"] or
            ["#v3", _, _, _, _, _, _, "Map" or "Earth", "Perspective" or "Orthographic", _])
        {
            earth = fields[7] == "Earth";
            perspective = fields[8] == "Perspective";
            if (fields.Length == 10 && (!Number(fields[9], out targetAltitude) || targetAltitude is < 0 or > 1e7)) return false;
        }
        else if (fields is ["#v2", _, _, _, _, _, _, "Map" or "Earth"])
            earth = fields[7] == "Earth";
        else if (fields is not ["#v1", _, _, _, _, _, _]) return false;
        Span<double> numbers = stackalloc double[5];
        for (var i = 0; i < numbers.Length; i++)
            if (!Number(fields[i + 1], out numbers[i]))
                return false;
        if (Math.Abs(numbers[0]) > (freeLook ? MapCamera.MaxFreeLookLatitude : 85.051129) || Math.Abs(numbers[1]) > 180 ||
            numbers[2] < MapCamera.MinZoom || numbers[2] > MapCamera.MaxZoom || Math.Abs(numbers[3]) > 360 ||
            numbers[4] != MapCamera.ClampPitch(numbers[4], perspective) || !Enum.TryParse(fields[6], out palette) || !Enum.IsDefined(palette))
            return false;
        camera = new MapCamera(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], perspective, targetAltitude)
        {
            IsFreeLook = freeLook,
            ViewDistanceMeters = viewDistance,
            GroundAltitudeMeters = groundAltitude,
            ViewportHeight = viewportHeight
        };
        return true;
    }

    private static bool Number(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
}
