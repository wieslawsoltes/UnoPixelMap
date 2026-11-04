using Microsoft.UI.Xaml;
using PixelMap.Data;
using PixelMap.Rendering;

namespace PixelMap.Controls;

public sealed partial class PixelMapControl
{
    public static readonly DependencyProperty IsElevationLayerEnabledProperty = DependencyProperty.Register(nameof(IsElevationLayerEnabled),
        typeof(bool), typeof(PixelMapControl), new PropertyMetadata(false, (o, _) => ((PixelMapControl)o).ElevationLayerChanged()));
    public static readonly DependencyProperty IsTerrainEnabledProperty = DependencyProperty.Register(nameof(IsTerrainEnabled),
        typeof(bool), typeof(PixelMapControl), new PropertyMetadata(false, (o, _) => ((PixelMapControl)o).TerrainChanged()));
    public static readonly DependencyProperty TerrainExaggerationProperty = DependencyProperty.Register(nameof(TerrainExaggeration),
        typeof(double), typeof(PixelMapControl), new PropertyMetadata(1d, (o, _) => ((PixelMapControl)o).TerrainChanged()));

    private readonly ElevationTileSource _elevationSource;
    private readonly TileViewport<ElevationTile> _elevations;
    private ElevationTile[] _snapshotTiles = [];
    private MapTile[] _snapshotVectors = [];
    private ElevationSnapshot _elevationSnapshot = ElevationSnapshot.Empty;
    private TerrainOptions? _terrain;
    private bool UsesElevation => IsTerrainEnabled || IsElevationLayerEnabled;

    private readonly record struct InteractionView(MapCamera Camera, TerrainOptions? Terrain,
        double Width, double Height);

    public bool IsElevationLayerEnabled { get => (bool)GetValue(IsElevationLayerEnabledProperty); set => SetValue(IsElevationLayerEnabledProperty, value); }
    public bool IsTerrainEnabled { get => (bool)GetValue(IsTerrainEnabledProperty); set => SetValue(IsTerrainEnabledProperty, value); }
    public double TerrainExaggeration
    {
        get => (double)GetValue(TerrainExaggerationProperty);
        set => SetValue(TerrainExaggerationProperty, double.IsFinite(value) ? Math.Clamp(value, 1, 4) : 1d);
    }
    public double CenterElevationMeters => _elevationSnapshot.SampleMeters(Camera.Latitude, Camera.Longitude);
    public bool HasElevation => _elevationSnapshot.Count > 0;

    /// <summary>Pick the rendered terrain surface and return unexaggerated elevation in metres.</summary>
    public bool TryGetLocation(double x, double y, out TerrainHit hit)
    {
        hit = default;
        var view = _surface.GetInteractionView(Camera, _terrain, ActualWidth, ActualHeight);
        if (view.Terrain is { } terrain)
        {
            if (terrain.Relief && _surface.TryPickTerrain(x, y, (int)Math.Ceiling(view.Width),
                (int)Math.Ceiling(view.Height), view.Camera, out hit)) return true;
            return view.Camera.TryUnprojectTerrain(x, y, view.Width, view.Height, terrain, out hit);
        }
        if (!view.Camera.TryUnproject(x, y, view.Width, view.Height, out var location)) return false;
        hit = new(location.Latitude, location.Longitude, 0);
        return true;
    }

    private void ElevationLayerChanged()
    {
        if (IsElevationLayerEnabled)
        {
            IsEarthImageryEnabled = false;
            IsTerrainEnabled = true;
        }
        TerrainChanged();
    }

    private void TerrainChanged()
    {
        if (!UsesElevation) ClearElevation();
        if (!_active) return;
        RefreshElevationSnapshot();
        Camera = Camera.OnTerrain(_terrain, ActualHeight);
        _worldContacts.Update(_vectors.Display, _elevationSnapshot);
        Journey.RefreshSurface();
        if (!Journey.Active) _wasTracking = false;
        UpdateRideCamera();
        RefreshTiles(_longFlight ? _to : Camera);
        LayerChanged();
        PositionMarker();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private TileId[] RequestTiles(MapCamera view, int maxZoom)
    {
        var tiles = view.VisibleTiles(ActualWidth, ActualHeight, maxZoom);
        if (!UsesElevation || tiles.Length == 0) return tiles;
        // A halo covers elevated silhouettes and supplies equal-resolution samples along seams.
        var expanded = new HashSet<TileId>(tiles);
        foreach (var tile in tiles)
        {
            int size = 1 << tile.Z;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int y = tile.Y + dy;
                if (y >= 0 && y < size) expanded.Add(new(tile.Z, (tile.X + dx + size) % size, y));
            }
        }
        double centerX = (view.Longitude + 180) / 360;
        double centerY = (1 - Math.Asinh(Math.Tan(view.Latitude * Math.PI / 180)) / Math.PI) * .5;
        return expanded.OrderBy(tile =>
        {
            double size = 1 << tile.Z;
            double dx = (tile.X + .5) / size - centerX;
            dx -= Math.Floor(dx + .5);
            double dy = (tile.Y + .5) / size - centerY;
            return dx * dx + dy * dy;
        }).Take(64).ToArray();
    }

    private void RefreshElevationTiles(MapCamera view)
    {
        var wanted = RequestTiles(view, ElevationTileSource.MaxZoom);
        var parents = wanted.Where(tile => tile.Z > 2)
            .Select(tile => new TileId(tile.Z - 2, tile.X >> 2, tile.Y >> 2)).Distinct();
        _elevations.Update(new[] { new TileId(0, 0, 0) }.Concat(wanted).Concat(parents).Distinct().ToArray());
    }

    private bool RefreshElevationSnapshot()
    {
        if (!UsesElevation) { _terrain = null; return false; }
        var tiles = _elevations.Ready.Concat(_elevations.Display).DistinctBy(tile => tile.Id)
            .OrderBy(tile => tile.Id.Z).ThenBy(tile => tile.Id.X).ThenBy(tile => tile.Id.Y).ToArray();
        var vectors = _vectors.Display.Concat(_world).DistinctBy(tile => tile.Id)
            .OrderBy(tile => tile.Id.Z).ThenBy(tile => tile.Id.X).ThenBy(tile => tile.Id.Y).ToArray();
        bool changed = !_snapshotTiles.SequenceEqual(tiles) || !_snapshotVectors.SequenceEqual(vectors);
        if (changed)
        {
            _snapshotTiles = tiles;
            _snapshotVectors = vectors;
            _elevationSnapshot = new(tiles, vectors);
        }
        var next = new TerrainOptions(_elevationSnapshot, IsTerrainEnabled, IsElevationLayerEnabled, TerrainExaggeration);
        if (_terrain == next) return false;
        _terrain = next;
        if (changed)
        {
            _worldContacts.Update(_vectors.Display, _elevationSnapshot);
            Journey.RefreshSurface();
            Camera = Camera.OnTerrain(_terrain, ActualHeight);
            if (!Journey.Active) _wasTracking = false;
            UpdateRideCamera();
            PositionMarker();
        }
        return true;
    }

    private void ClearElevation()
    {
        _elevations.Clear();
        _snapshotTiles = [];
        _snapshotVectors = [];
        _elevationSnapshot = ElevationSnapshot.Empty;
        _terrain = null;
    }

    private MapCamera PanCamera(MapCamera camera, double fromX, double fromY, double toX, double toY) =>
        _terrain is { Relief: true } terrain
            ? camera.PanFromTo(fromX, fromY, toX, toY, ActualWidth, ActualHeight, terrain)
            : camera.PanFromTo(fromX, fromY, toX, toY, ActualWidth, ActualHeight);

    private MapCamera TransformCamera(MapCamera camera, double zoom, double bearing, double pitch, double x, double y) =>
        _terrain is { Relief: true } terrain
            ? camera.TransformAt(zoom, bearing, pitch, x, y, ActualWidth, ActualHeight, terrain)
            : camera.TransformAt(zoom, bearing, pitch, x, y, ActualWidth, ActualHeight);
}
