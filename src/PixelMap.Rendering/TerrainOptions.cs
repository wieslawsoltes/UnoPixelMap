using PixelMap.Data;

namespace PixelMap.Rendering;

/// <summary>Immutable terrain inputs. Relief changes geometry, while hypsometric tint
/// shows signed elevation in either the flat or three-dimensional view.</summary>
public sealed record TerrainOptions(ElevationSnapshot Elevation, bool Relief = true,
    bool Hypsometric = false, double Exaggeration = 1)
{
    internal double HeightScale => double.IsFinite(Exaggeration) ? Math.Clamp(Exaggeration, .25, 8) : 1;

    /// <summary>Convert an absolute physical altitude to the camera's rendered altitude.
    /// Terrain exaggeration affects the ground, preserving object heights above ground.</summary>
    public double RenderAltitude(MapCamera camera, double latitude, double longitude, double absoluteMsl)
    {
        double surface = Elevation.SampleSurfaceMeters(latitude, longitude);
        return Relief ? camera.GroundAltitudeMeters + (surface - camera.GroundAltitudeMeters) * HeightScale + absoluteMsl - surface
            : absoluteMsl - surface;
    }

    internal double SurfaceMeters(double latitude, double longitude, TerrainSurface? mesh) =>
        mesh is not null && mesh.TryHeight((longitude + 180) / 360, MapCamera.MercatorY(latitude), out double height)
            ? height : Elevation.SampleSurfaceMeters(latitude, longitude);

    internal double RenderAltitude(MapCamera camera, double latitude, double longitude, double absoluteMsl, TerrainSurface? mesh)
    {
        double physical = Elevation.SampleSurfaceMeters(latitude, longitude);
        double surface = SurfaceMeters(latitude, longitude, mesh);
        return camera.GroundAltitudeMeters + LocalAltitude(camera, surface) + absoluteMsl - physical;
    }

    internal double LocalAltitude(MapCamera camera, double surface) =>
        Relief ? (surface - camera.GroundAltitudeMeters) * HeightScale : 0;
}

/// <summary>The geographic intersection with the visible terrain mesh. Height is
/// the unexaggerated surface elevation in metres above mean sea level.</summary>
public readonly record struct TerrainHit(double Latitude, double Longitude, double ElevationMeters);
