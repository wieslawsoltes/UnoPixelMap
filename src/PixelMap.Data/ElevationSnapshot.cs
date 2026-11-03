using System.Numerics;

namespace PixelMap.Data;

/// <summary>An immutable elevation field shared by terrain, camera, picking and simulation.
/// Object identity can key retained geometry. Samples blend across tile edges and use coarser
/// loaded levels when a neighbour or individual height sample is unavailable.</summary>
public sealed class ElevationSnapshot
{
    private const double MaxLatitude = 85.0511287798066;
    private readonly Dictionary<TileId, ElevationTile> _tiles = [];
    private readonly Dictionary<TileId, ElevationSurfaceIndex> _surfaces = [];
    private readonly Level[] _levels;
    private readonly int[] _surfaceZooms;
    private readonly ElevationSurfaceCoverage _surfaceCoverage;

    private readonly record struct Level(int Zoom, int Size, int Shift, long PixelCount);

    public static ElevationSnapshot Empty { get; } = new([]);
    public IReadOnlyList<ElevationTile> Tiles { get; }
    public int Count => _tiles.Count;
    public int MaximumZoom => _levels.Length == 0 ? 0 : _levels[0].Zoom;
    public double MinimumMeters { get; }
    public double MaximumMeters { get; }

    public ElevationSnapshot(IEnumerable<ElevationTile> tiles, IEnumerable<MapTile>? surfaceTiles = null)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        foreach (var tile in tiles)
        {
            ArgumentNullException.ThrowIfNull(tile);
            _tiles[tile.Id] = tile;
        }
        var ordered = _tiles.Values.OrderBy(tile => tile.Id.Z).ThenBy(tile => tile.Id.X).ThenBy(tile => tile.Id.Y).ToArray();
        Tiles = Array.AsReadOnly(ordered);
        var groups = ordered.GroupBy(tile => tile.Id.Z).OrderByDescending(group => group.Key).ToArray();
        _levels = new Level[groups.Length];
        for (int i = 0; i < groups.Length; i++)
        {
            int size = groups[i].First().Size;
            if (groups[i].Any(tile => tile.Size != size))
                throw new ArgumentException("Tiles at the same zoom must use the same sample grid.", nameof(tiles));
            _levels[i] = new(groups[i].Key, size, BitOperations.Log2((uint)size), (1L << groups[i].Key) * size);
        }
        var populated = ordered.Where(tile => tile.ValidSampleCount > 0).ToArray();
        MinimumMeters = populated.Length == 0 ? 0 : populated.Min(tile => tile.MinimumMeters);
        MaximumMeters = populated.Length == 0 ? 0 : populated.Max(tile => tile.MaximumMeters);

        if (surfaceTiles is not null)
        {
            foreach (var tile in surfaceTiles)
            {
                ArgumentNullException.ThrowIfNull(tile);
                ElevationTile.ValidateId(tile.Id);
                _surfaces[tile.Id] = ElevationSurfaceIndex.For(tile);
            }
        }
        _surfaceZooms = _surfaces.Keys.Select(id => id.Z).Distinct().OrderDescending().ToArray();
        _surfaceCoverage = new(_surfaces);
    }

    public double SampleMeters(double latitude, double longitude) =>
        TrySampleMeters(latitude, longitude, out double meters) ? meters : 0;

    public bool TrySampleMeters(double latitude, double longitude, out double meters)
    {
        meters = 0;
        return Geographic(latitude, longitude, out double x, out double y) && TrySampleMercator(x, y, out meters);
    }

    public double SampleMercator(double x, double y) => TrySampleMercator(x, y, out double meters) ? meters : 0;

    public bool TrySampleMercator(double x, double y, out double meters)
    {
        meters = 0;
        return Normalize(ref x, ref y) && TrySampleNormalized(x, y, 0, true, out meters);
    }

    /// <summary>Mapped ocean surfaces are at zero metres. Land and inland water retain signed
    /// elevations, including lakes and valleys below sea level.</summary>
    public double SampleSurfaceMeters(double latitude, double longitude) =>
        Geographic(latitude, longitude, out double x, out double y) ? SampleSurfaceMercator(x, y) : 0;

    public double SampleSurfaceMercator(double x, double y)
    {
        if (!Normalize(ref x, ref y) || IsOcean(x, y)) return 0;
        return TrySampleNormalized(x, y, 0, true, out double meters) ? meters : 0;
    }

    /// <summary>Conservatively tests whether mapped ocean geometry may intersect a tile.
    /// Finer vector coverage replaces its parent. Polygon bounds may include coastal holes.</summary>
    public bool MayContainOcean(TileId bounds)
    {
        ElevationTile.ValidateId(bounds);
        return _surfaceCoverage.MayContainOcean(bounds);
    }

    private bool TrySampleNormalized(double x, double y, int startLevel, bool useParents, out double meters)
    {
        Span<double> values = stackalloc double[4];
        for (int i = startLevel; i < _levels.Length; i++)
        {
            var level = _levels[i];
            double pixelX = x * level.PixelCount - .5, pixelY = y * level.PixelCount - .5;
            long column = (long)Math.Floor(pixelX), row = (long)Math.Floor(pixelY);
            int mask = ReadFour(level, column, row, values);
            if (mask == 0) continue;
            if (mask != 15)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    if ((mask & (1 << corner)) != 0) continue;
                    double sampleX = (column + (corner & 1) + .5) / level.PixelCount;
                    double sampleY = (row + (corner >> 1) + .5) / level.PixelCount;
                    Normalize(ref sampleX, ref sampleY);
                    // A coarser interpolation fills its own voids from a valid corner. This
                    // bounds parent lookup to the number of loaded levels, even for sparse grids.
                    values[corner] = useParents && TrySampleNormalized(sampleX, sampleY, i + 1, false, out double parent)
                        ? parent : NearestValid(values, mask, corner);
                }
            }
            double fractionX = pixelX - column, fractionY = pixelY - row;
            double top = values[0] + (values[1] - values[0]) * fractionX;
            double bottom = values[2] + (values[3] - values[2]) * fractionX;
            meters = top + (bottom - top) * fractionY;
            return true;
        }
        meters = 0;
        return false;
    }

    private int ReadFour(Level level, long column, long row, Span<double> values)
    {
        long wrappedColumn = column & (level.PixelCount - 1);
        int localX = (int)(wrappedColumn & (level.Size - 1));
        int localY = (int)(row & (level.Size - 1));
        if (localX < level.Size - 1 && localY < level.Size - 1 && row >= 0 && row < level.PixelCount - 1 &&
            _tiles.TryGetValue(new(level.Zoom, (int)(wrappedColumn >> level.Shift), (int)(row >> level.Shift)), out var tile))
        {
            var heights = tile.Heights;
            int offset = localY * level.Size + localX;
            values[0] = heights[offset];
            values[1] = heights[offset + 1];
            values[2] = heights[offset + level.Size];
            values[3] = heights[offset + level.Size + 1];
        }
        else
        {
            for (int corner = 0; corner < 4; corner++)
                values[corner] = ReadPixel(level, column + (corner & 1), row + (corner >> 1));
        }
        int mask = 0;
        for (int corner = 0; corner < 4; corner++)
            if (double.IsFinite(values[corner])) mask |= 1 << corner;
        return mask;
    }

    private double ReadPixel(Level level, long column, long row)
    {
        column &= level.PixelCount - 1;
        row = Math.Clamp(row, 0, level.PixelCount - 1);
        if (!_tiles.TryGetValue(new(level.Zoom, (int)(column >> level.Shift), (int)(row >> level.Shift)), out var tile))
            return double.NaN;
        int x = (int)(column & (level.Size - 1)), y = (int)(row & (level.Size - 1));
        return tile.Heights[y * level.Size + x];
    }

    private static double NearestValid(ReadOnlySpan<double> values, int mask, int corner)
    {
        int sameRow = corner ^ 1, sameColumn = corner ^ 2;
        if ((mask & (1 << sameRow)) != 0) return values[sameRow];
        if ((mask & (1 << sameColumn)) != 0) return values[sameColumn];
        return values[corner ^ 3];
    }

    private bool IsOcean(double x, double y)
    {
        // The southern world edge belongs to the last tile's interior. Polygon ray tests
        // use half-open Y intervals, so query the immediately adjacent representable point.
        y = Math.Min(y, Math.BitDecrement(1d));
        foreach (int zoom in _surfaceZooms)
        {
            int count = 1 << zoom;
            double tileX = x * count, tileY = y * count;
            int column = (int)tileX, row = Math.Min(count - 1, (int)tileY);
            if (_surfaces.TryGetValue(new(zoom, column, row), out var surface))
                return surface.IsOcean(tileX - column, tileY - row);
        }
        return false;
    }

    private static bool Geographic(double latitude, double longitude, out double x, out double y)
    {
        x = y = 0;
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)) return false;
        x = (longitude + 180) / 360;
        y = (1 - Math.Asinh(Math.Tan(Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180)) / Math.PI) * .5;
        return true;
    }

    private static bool Normalize(ref double x, ref double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        x -= Math.Floor(x);
        y = Math.Clamp(y, 0, 1);
        return true;
    }
}
