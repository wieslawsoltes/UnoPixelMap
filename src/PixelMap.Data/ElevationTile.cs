namespace PixelMap.Data;

/// <summary>Immutable, row-major metre samples at the pixel centres of one Web Mercator XYZ tile.
/// NaN marks a missing sample. Signed elevations retain below-sea-level terrain and bathymetry.</summary>
public sealed class ElevationTile
{
    private readonly float[] _heights;

    public TileId Id { get; }
    public int Size { get; }
    public int ValidSampleCount { get; }
    public double MinimumMeters { get; }
    public double MaximumMeters { get; }
    public ReadOnlySpan<float> Heights => _heights;

    public ElevationTile(TileId id, int size, ReadOnlySpan<float> heights)
        : this(id, size, heights.ToArray()) { }

    private ElevationTile(TileId id, int size, float[] heights)
    {
        ValidateId(id);
        if (size is < 2 or > 512 || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Use a power-of-two grid from 2 to 512 samples per side.");
        if (heights.Length != size * size)
            throw new ArgumentException("The elevation grid must contain size squared samples.", nameof(heights));

        double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
        int valid = 0;
        foreach (float value in heights)
        {
            if (float.IsNaN(value)) continue;
            if (!float.IsFinite(value) || value < -32768 || value >= 32768)
                throw new ArgumentOutOfRangeException(nameof(heights), "Metre samples must fit the Terrarium range or be NaN.");
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            valid++;
        }

        Id = id;
        Size = size;
        _heights = heights;
        ValidSampleCount = valid;
        MinimumMeters = valid == 0 ? 0 : minimum;
        MaximumMeters = valid == 0 ? 0 : maximum;
    }

    public float this[int x, int y]
    {
        get
        {
            if ((uint)x >= Size) throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)y >= Size) throw new ArgumentOutOfRangeException(nameof(y));
            return _heights[y * Size + x];
        }
    }

    internal static ElevationTile FromOwnedSamples(TileId id, int size, float[] heights) => new(id, size, heights);

    internal static void ValidateId(TileId id)
    {
        if (id.Z is < 0 or > 24 || (uint)id.X >= 1u << id.Z || (uint)id.Y >= 1u << id.Z)
            throw new ArgumentOutOfRangeException(nameof(id), "Supply a canonical Web Mercator XYZ tile.");
    }
}
