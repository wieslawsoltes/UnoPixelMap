using System.Buffers.Binary;
using System.IO.Compression;

namespace PixelMap.Data;

/// <summary>Decodes raw Terrarium RGB channels without colour conversion. PNG payload,
/// dimensions and decompressed row counts are bounded before allocating sample storage.</summary>
public static class TerrariumDecoder
{
    internal const int MaxBytes = 2 * 1024 * 1024;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static ElevationTile Decode(TileId id, ReadOnlySpan<byte> encoded)
    {
        ElevationTile.ValidateId(id);
        if (encoded.Length > MaxBytes || !encoded.StartsWith(Signature))
            throw new InvalidDataException("Expected a bounded Terrarium PNG tile.");

        using var compressed = new MemoryStream();
        int width = 0, height = 0, channels = 0, gutter = 0;
        int? transparentRgb = null;
        bool header = false, pixels = false, dataEnded = false, end = false;
        int offset = Signature.Length;
        while (offset < encoded.Length)
        {
            if (encoded.Length - offset < 12) throw new InvalidDataException("Truncated PNG chunk.");
            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(encoded[offset..]);
            if (rawLength > MaxBytes || rawLength > encoded.Length - offset - 12)
                throw new InvalidDataException("Invalid PNG chunk length.");
            int length = (int)rawLength;
            var kind = encoded.Slice(offset + 4, 4);
            var data = encoded.Slice(offset + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(encoded.Slice(offset + 8 + length, 4));
            if (Crc(encoded.Slice(offset + 4, length + 4)) != crc)
                throw new InvalidDataException("PNG chunk checksum mismatch.");
            if (!header && !kind.SequenceEqual("IHDR"u8))
                throw new InvalidDataException("The PNG header must be the first chunk.");

            if (kind.SequenceEqual("IHDR"u8))
            {
                if (header || length != 13) throw new InvalidDataException("Invalid PNG header.");
                uint w = BinaryPrimitives.ReadUInt32BigEndian(data), h = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                if (w != h || w is not (256 or 260 or 512 or 516) || data[8] != 8 ||
                    data[9] is not (2 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0)
                    throw new InvalidDataException("Terrarium requires a non-interlaced 256 or 512 square RGB/RGBA8 PNG, with optional two-pixel gutters.");
                width = height = (int)w;
                channels = data[9] == 2 ? 3 : 4;
                gutter = width is 260 or 516 ? 2 : 0;
                header = true;
            }
            else if (kind.SequenceEqual("IDAT"u8))
            {
                if (dataEnded) throw new InvalidDataException("PNG image data chunks must be consecutive.");
                compressed.Write(data);
                pixels = true;
            }
            else if (kind.SequenceEqual("IEND"u8))
            {
                if (!pixels || length != 0 || offset + 12 != encoded.Length)
                    throw new InvalidDataException("Expected a complete PNG image and final end chunk.");
                end = true;
            }
            else
            {
                if (pixels) dataEnded = true;
                if (kind.SequenceEqual("tRNS"u8))
                {
                    if (pixels || channels != 3 || transparentRgb.HasValue || length != 6 || data[0] != 0 || data[2] != 0 || data[4] != 0)
                        throw new InvalidDataException("Invalid Terrarium PNG transparency key.");
                    transparentRgb = (data[1] << 16) | (data[3] << 8) | data[5];
                }
                else if (kind.SequenceEqual("PLTE"u8))
                {
                    if (pixels || length is 0 or > 768 || length % 3 != 0)
                        throw new InvalidDataException("Invalid PNG suggested palette.");
                }
                else if ((kind[0] & 32) == 0)
                    throw new InvalidDataException("Unsupported critical PNG chunk.");
            }
            offset += length + 12;
        }
        if (!end) throw new InvalidDataException("PNG end chunk is missing.");

        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        int size = width - 2 * gutter, stride = width * channels;
        var heights = new float[size * size];
        byte[] row = new byte[stride], previous = new byte[stride];
        try
        {
            for (int y = 0; y < height; y++)
            {
                int filter = inflater.ReadByte();
                if (filter is < 0 or > 4) throw new InvalidDataException("Invalid or truncated PNG row filter.");
                inflater.ReadExactly(row);
                Unfilter(row, previous, channels, filter);
                if (y >= gutter && y < height - gutter)
                {
                    int target = (y - gutter) * size;
                    for (int x = gutter; x < width - gutter; x++)
                    {
                        int index = x * channels;
                        int rgb = (row[index] << 16) | (row[index + 1] << 8) | row[index + 2];
                        // Black is the signed -32768 no-data sentinel. Partial transparency
                        // is also unavailable data, never a premultiplied height value.
                        bool missing = rgb == 0 || rgb == transparentRgb || channels == 4 && row[index + 3] != 255;
                        heights[target++] = missing ? float.NaN : row[index] * 256 + row[index + 1] + row[index + 2] / 256f - 32768;
                    }
                }
                (row, previous) = (previous, row);
            }
            if (inflater.ReadByte() != -1) throw new InvalidDataException("PNG expands beyond its declared dimensions.");
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("Truncated PNG pixel data.", error);
        }
        return ElevationTile.FromOwnedSamples(id, size, heights);
    }

    private static void Unfilter(Span<byte> row, ReadOnlySpan<byte> previous, int channels, int filter)
    {
        switch (filter)
        {
            case 0:
                return;
            case 1:
                for (int i = channels; i < row.Length; i++) row[i] = unchecked((byte)(row[i] + row[i - channels]));
                return;
            case 2:
                for (int i = 0; i < row.Length; i++) row[i] = unchecked((byte)(row[i] + previous[i]));
                return;
            case 3:
                for (int i = 0; i < row.Length; i++)
                    row[i] = unchecked((byte)(row[i] + ((i < channels ? 0 : row[i - channels]) + previous[i]) / 2));
                return;
            case 4:
                for (int i = 0; i < row.Length; i++)
                    row[i] = unchecked((byte)(row[i] + Paeth(i < channels ? 0 : row[i - channels], previous[i], i < channels ? 0 : previous[i - channels])));
                return;
        }
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        int prediction = left + above - upperLeft;
        int a = Math.Abs(prediction - left), b = Math.Abs(prediction - above), c = Math.Abs(prediction - upperLeft);
        return a <= b && a <= c ? left : b <= c ? above : upperLeft;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320u;
            table[i] = value;
        }
        return table;
    }
}
