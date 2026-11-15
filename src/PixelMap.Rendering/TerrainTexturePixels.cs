using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PixelMap.Rendering;

/// <summary>Copy native BGRA texels into the GPU's RGB payload while retaining the
/// exact all-opaque decision. Source and destination are separate texture buffers.</summary>
internal static class TerrainTexturePixels
{
    internal static bool CopyRgb(ReadOnlySpan<uint> source, Span<uint> destination)
    {
        if (destination.Length < source.Length)
            throw new ArgumentException("The destination must contain every source texel.", nameof(destination));

        int index = 0;
        bool opaque = true;
        if (Vector128.IsHardwareAccelerated && source.Length >= Vector128<uint>.Count)
        {
            ref uint input = ref MemoryMarshal.GetReference(source);
            ref uint output = ref MemoryMarshal.GetReference(destination);
            var rgb = Vector128.Create(0x00ffffffu);
            var alpha = Vector128.Create(0xff000000u);
            var allAlpha = alpha;
            for (; index <= source.Length - Vector128<uint>.Count; index += Vector128<uint>.Count)
            {
                // Unaligned loads/stores support native bitmap rows and managed
                // array slices without assuming a platform-specific alignment.
                var pixels = Vector128.LoadUnsafe(ref input, (nuint)index);
                (pixels & rgb).StoreUnsafe(ref output, (nuint)index);
                allAlpha &= pixels;
            }
            opaque = Vector128.EqualsAll(allAlpha, alpha);
        }
        // The same exact scalar operation handles a short tail and platforms
        // without accelerated Vector128 support, including an empty source.
        for (; index < source.Length; index++)
        {
            uint pixel = source[index];
            destination[index] = pixel & 0x00ffffff;
            opaque &= (pixel & 0xff000000) == 0xff000000;
        }
        return opaque;
    }
}
