namespace PixelMap.Rendering;

/// <summary>Exact nominal allocation plan for the HDR scene and its bloom pyramid.
/// Depth24Plus is budgeted at four bytes per sample; driver alignment is not included.</summary>
internal readonly record struct WebGpuAttachmentPlan(int Width, int Height, int Samples,
    bool Photograph, int BloomLevels, long SceneBytes, long BloomBytes)
{
    internal long TotalBytes => checked(SceneBytes + BloomBytes);
}

internal static class WebGpuBloomLayout
{
    internal const int MaximumLevels = 6;

    internal static (int Width, int Height) Next(int width, int height) =>
        (width / 2 + width % 2, height / 2 + height % 2);

    internal static WebGpuAttachmentPlan Plan(int width, int height, int samples, bool photograph, int maximumLevels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (samples is not (1 or 4)) throw new ArgumentOutOfRangeException(nameof(samples));
        if (maximumLevels is < 0 or > MaximumLevels) throw new ArgumentOutOfRangeException(nameof(maximumLevels));
        long sceneBytes = checked((long)width * height *
            ((samples > 1 ? 8 + samples * 12 : 12) + (photograph ? 8 : 0)));
        long downBytes = 0, lastBytes = 0;
        int count = 0, levelWidth = width, levelHeight = height;
        while (count < maximumLevels && (levelWidth > 1 || levelHeight > 1))
        {
            (levelWidth, levelHeight) = Next(levelWidth, levelHeight);
            lastBytes = checked((long)levelWidth * levelHeight * 8);
            downBytes = checked(downBytes + lastBytes);
            count++;
        }
        // The coarsest downsample is already the initial reconstruction. Every
        // finer level has a distinct output, so no render pass samples its target.
        return new(width, height, samples, photograph, count, sceneBytes,
            checked(downBytes * 2 - lastBytes));
    }

    internal static WebGpuAttachmentPlan Fit(uint width, uint height, int maximumDimension,
        long maximumPixels, long maximumBytes, int samples, bool photograph, int maximumLevels)
    {
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPixels);
        var minimum = Plan(1, 1, samples, photograph, maximumLevels);
        if (minimum.TotalBytes > maximumBytes)
            throw new NotSupportedException("The HDR scene cannot fit within the map attachment budget.");
        double scale = Math.Min(1, Math.Sqrt(maximumPixels / ((double)width * height)));
        scale = Math.Min(scale, (double)maximumDimension / Math.Max(width, height));
        WebGpuAttachmentPlan At(double value) => Plan(
            Math.Max(1, (int)Math.Floor(width * value)), Math.Max(1, (int)Math.Floor(height * value)),
            samples, photograph, maximumLevels);
        bool Fits(WebGpuAttachmentPlan value) => value.TotalBytes <= maximumBytes &&
            (long)value.Width * value.Height <= maximumPixels;
        var candidate = At(scale);
        if (Fits(candidate)) return candidate;

        // Ceil-halving odd dimensions makes bytes discontinuous. Search the exact
        // monotone allocation function instead of assuming a fractional pixel cost.
        double low = 0, high = scale;
        var best = minimum;
        for (int i = 0; i < 48; i++)
        {
            double middle = (low + high) * .5;
            candidate = At(middle);
            if (Fits(candidate)) { low = middle; best = candidate; }
            else high = middle;
        }
        return best;
    }
}
