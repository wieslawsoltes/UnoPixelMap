using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PixelMap.Data;

/// <summary>Decodes the cartographic OpenMapTiles layers directly from Mapbox Vector Tile v2 protobuf.</summary>
public static class VectorTileDecoder
{
    internal const int MaxBytes = 8 * 1024 * 1024;

    public static MapTile Decode(TileId id, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Vector tile exceeds the byte budget.");
        var features = new List<MapFeature>();
        var reader = new ProtoReader(bytes);
        while (!reader.End)
        {
            uint tag = reader.Tag();
            if (tag == 26) ReadLayer(reader.Bytes(), features);
            else reader.Skip(tag);
        }
        return new(id, features.ToArray());
    }

    private static void ReadLayer(ReadOnlySpan<byte> bytes, List<MapFeature> features)
    {
        var reader = new ProtoReader(bytes);
        string layer = "";
        int extent = 4096;
        while (!reader.End)
        {
            uint tag = reader.Tag();
            if (tag == 10) layer = Encoding.UTF8.GetString(reader.Bytes());
            else if (tag == 40) extent = checked((int)reader.Varint());
            else reader.Skip(tag);
        }
        if (layer is not ("building" or "landcover" or "landuse" or "park" or "water" or "waterway"
            or "transportation" or "boundary" or "aeroway" or "place" or "transportation_name"
            or "mountain_peak" or "water_name" or "poi")) return;
        if (extent <= 0) throw new InvalidDataException("Invalid vector tile extent.");

        // The protobuf spec does not require tables to precede feature messages. Index value
        // slices first and decode only referenced style/label values, ignoring other translations.
        var keys = new List<Key>();
        var values = new List<ValueEntry>();
        reader = new(bytes);
        while (!reader.End)
        {
            uint tag = reader.Tag();
            if (tag == 26) keys.Add(ParseKey(reader.Bytes()));
            else if (tag == 34)
            {
                var value = reader.Bytes();
                values.Add(new(bytes.Length - reader.Remaining - value.Length, value.Length));
            }
            else reader.Skip(tag);
        }
        var points = new List<TilePoint>();
        var paths = new List<TilePoint[]>();
        reader = new(bytes);
        while (!reader.End)
        {
            uint tag = reader.Tag();
            if (tag == 18)
            {
                var feature = ReadFeature(reader.Bytes(), layer, extent, keys, values, bytes, points, paths);
                if (feature is not null) features.Add(feature);
            }
            else reader.Skip(tag);
        }
    }

    private enum Key : byte { Ignore, Class, Subclass, Name, NameEn, Rank, Height, Base, Hide, Level, Brunnel }

    private static Key ParseKey(ReadOnlySpan<byte> key)
    {
        if (key.SequenceEqual("class"u8)) return Key.Class;
        if (key.SequenceEqual("subclass"u8)) return Key.Subclass;
        if (key.SequenceEqual("name"u8)) return Key.Name;
        if (key.SequenceEqual("name:en"u8)) return Key.NameEn;
        if (key.SequenceEqual("rank"u8)) return Key.Rank;
        if (key.SequenceEqual("render_height"u8)) return Key.Height;
        if (key.SequenceEqual("render_min_height"u8)) return Key.Base;
        if (key.SequenceEqual("hide_3d"u8)) return Key.Hide;
        if (key.SequenceEqual("layer"u8)) return Key.Level;
        if (key.SequenceEqual("brunnel"u8)) return Key.Brunnel;
        return Key.Ignore;
    }

    private struct ValueEntry(int offset, int length)
    {
        public readonly int Offset = offset;
        public readonly int Length = length;
        public bool Decoded;
        public string? Text;
        public double? Number;

        public void Read(ReadOnlySpan<byte> bytes)
        {
            if (Decoded) return;
            var reader = new ProtoReader(bytes.Slice(Offset, Length));
            while (!reader.End)
            {
                uint tag = reader.Tag();
                switch (tag)
                {
                    case 10:
                        Text = Encoding.UTF8.GetString(reader.Bytes());
                        if (double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                            Number = n;
                        break;
                    case 21: Number = reader.Float(); break;
                    case 25: Number = reader.Double(); break;
                    case 32: Number = unchecked((long)reader.Varint()); break;
                    case 40: case 56: Number = reader.Varint(); break;
                    case 48:
                        ulong v = reader.Varint();
                        Number = (long)(v >> 1) ^ -(long)(v & 1);
                        break;
                    default: reader.Skip(tag); break;
                }
            }
            if (Number.HasValue && !double.IsFinite(Number.Value)) Number = null;
            Decoded = true;
        }
    }

    private static MapFeature? ReadFeature(ReadOnlySpan<byte> bytes, string layer, int extent,
        List<Key> keys, List<ValueEntry> values, ReadOnlySpan<byte> layerBytes, List<TilePoint> points,
        List<TilePoint[]> paths)
    {
        ulong id = 0;
        GeometryType type = 0;
        string @class = "", subclass = "", name = "", nameEn = "", brunnel = "";
        double? height = null, minHeight = null;
        bool hide = false;
        int rank = 0, level = 0;
        ReadOnlySpan<byte> geometry = default;
        var reader = new ProtoReader(bytes);
        while (!reader.End)
        {
            uint tag = reader.Tag();
            switch (tag)
            {
                case 8: id = reader.Varint(); break;
                case 18:
                    var tags = new ProtoReader(reader.Bytes());
                    while (!tags.End)
                    {
                        int ki = checked((int)tags.Varint()), vi = checked((int)tags.Varint());
                        if ((uint)ki >= keys.Count || (uint)vi >= values.Count)
                            throw new InvalidDataException("Vector tile tag index is outside its table.");
                        Key key = keys[ki];
                        if (key == Key.Ignore) continue;
                        ref var value = ref CollectionsMarshal.AsSpan(values)[vi];
                        value.Read(layerBytes);
                        switch (key)
                        {
                            case Key.Class: @class = value.Text ?? ""; break;
                            case Key.Subclass: subclass = value.Text ?? ""; break;
                            case Key.Name: name = value.Text ?? ""; break;
                            case Key.NameEn: nameEn = value.Text ?? ""; break;
                            case Key.Rank: rank = (int)(value.Number ?? 0); break;
                            case Key.Height: height = value.Number; break;
                            case Key.Base: minHeight = value.Number; break;
                            case Key.Hide: hide = value.Number == 1 || value.Text == "true"; break;
                            case Key.Level: level = (int)(value.Number ?? 0); break;
                            case Key.Brunnel: brunnel = value.Text ?? ""; break;
                        }
                    }
                    break;
                case 24: type = (GeometryType)reader.Varint(); break;
                case 34: geometry = reader.Bytes(); break;
                default: reader.Skip(tag); break;
            }
        }
        if (layer == "poi" && name.Length == 0 && nameEn.Length == 0) return null;
        if (type is < GeometryType.Point or > GeometryType.Polygon || geometry.IsEmpty) return null;
        ReadGeometry(geometry, type, points, paths);
        if (paths.Count == 0) return null;
        return new(layer, @class, subclass, name.Length != 0 ? name : nameEn, id, extent, type,
            paths.ToArray(), height, minHeight, hide, rank, level, brunnel);
    }

    private static void ReadGeometry(ReadOnlySpan<byte> bytes, GeometryType type,
        List<TilePoint> points, List<TilePoint[]> paths)
    {
        var reader = new ProtoReader(bytes);
        int x = 0, y = 0;
        points.Clear();
        paths.Clear();
        while (!reader.End)
        {
            uint command = checked((uint)reader.Varint());
            int op = (int)(command & 7), count = (int)(command >> 3);
            if (count == 0) throw new InvalidDataException("Empty MVT command.");
            if (op == 7)
            {
                if (type != GeometryType.Polygon || count != 1 || points.Count < 3)
                    throw new InvalidDataException("Invalid polygon ClosePath.");
                paths.Add(points.ToArray());
                points.Clear();
                continue;
            }
            if ((op != 1 && op != 2) || count > reader.Remaining / 2)
                throw new InvalidDataException("Invalid vector tile geometry command.");
            if (op == 1 && points.Count > 0 && type != GeometryType.Point)
            {
                if (type == GeometryType.Polygon || points.Count < 2)
                    throw new InvalidDataException("Incomplete vector tile path.");
                paths.Add(points.ToArray());
                points.Clear();
            }
            if ((op == 1 && type != GeometryType.Point && count != 1) ||
                (op == 2 && (points.Count == 0 || type == GeometryType.Point)))
                throw new InvalidDataException("Invalid vector tile path.");
            for (int i = 0; i < count; i++)
            {
                x = checked(x + Delta(reader.Varint()));
                y = checked(y + Delta(reader.Varint()));
                points.Add(new(x, y));
            }
        }
        if (points.Count == 0) return;
        if (type == GeometryType.Polygon || (type == GeometryType.LineString && points.Count < 2))
            throw new InvalidDataException("Unterminated or incomplete vector tile path.");
        paths.Add(points.ToArray());
    }

    private static int Delta(ulong varint)
    {
        uint value = checked((uint)varint);
        return (int)(value >> 1) ^ -(int)(value & 1);
    }
}
