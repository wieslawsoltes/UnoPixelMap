using System.Buffers;

namespace PixelMap.Data;

internal static class TilePayload
{
    public static Stream? OpenSeed(TileId id, string group, string extension)
    {
        if (id.Z != 0 && id is not { Z: 14, X: >= 4822 and <= 4824, Y: >= 6159 and <= 6161 }) return null;
        var name = FormattableString.Invariant($"PixelMap.{group}.{id.Z}-{id.X}-{id.Y}.{extension}");
        return typeof(TilePayload).Assembly.GetManifestResourceStream(name);
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream input, int maxBytes, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0) return output.ToArray();
                if (output.Length + read > maxBytes)
                    throw new InvalidDataException("Tile exceeds the byte budget.");
                output.Write(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
