using System.Buffers.Binary;

namespace PixelMap.Data;

/// <summary>Forward-only protobuf reader. Slices refer to the one backing buffer for this tile.</summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> bytes)
{
    private ReadOnlySpan<byte> _rest = bytes;
    public bool End => _rest.IsEmpty;
    public int Remaining => _rest.Length;

    public ulong Varint()
    {
        ulong result = 0;
        for (int shift = 0; shift <= 63 && !_rest.IsEmpty; shift += 7)
        {
            byte next = _rest[0];
            _rest = _rest[1..];
            if (shift == 63 && next > 1) break;
            result |= (ulong)(next & 127) << shift;
            if (next < 128) return result;
        }
        throw new InvalidDataException("Malformed protobuf varint.");
    }

    public uint Tag()
    {
        uint tag = checked((uint)Varint());
        if (tag < 8) throw new InvalidDataException("Invalid protobuf field number.");
        return tag;
    }

    public ReadOnlySpan<byte> Take(int count)
    {
        if ((uint)count > (uint)_rest.Length) throw new InvalidDataException("Truncated protobuf field.");
        var result = _rest[..count];
        _rest = _rest[count..];
        return result;
    }

    public ReadOnlySpan<byte> Bytes() => Take(checked((int)Varint()));

    public float Float() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double Double() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public void Skip(uint tag)
    {
        switch (tag & 7)
        {
            case 0: Varint(); break;
            case 1: Take(8); break;
            case 2: Bytes(); break;
            case 5: Take(4); break;
            default: throw new InvalidDataException("Unsupported protobuf wire type.");
        }
    }
}
