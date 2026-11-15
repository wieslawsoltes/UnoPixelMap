namespace PixelMap.App;

/// <summary>The visible browser region available to overlay controls, in XAML coordinates.</summary>
internal readonly record struct MapChromeViewport(
    double Top, double Right, double Bottom, double Left, double KeyboardBottom, double AvailableHeight);

internal static class MapChromeLayout
{
    internal static MapChromeViewport Measure(double layoutHeight, ReadOnlySpan<double> browser)
    {
        double visibleTop = Math.Min(layoutHeight, Read(browser, 2));
        double visibleBottom = Math.Min(layoutHeight, visibleTop + Read(browser, 1, layoutHeight));
        double keyboardBottom = Math.Max(0, layoutHeight - visibleBottom);
        // The visual viewport can pan as well as shrink when a keyboard opens.
        // Include its origin in the top inset, while an already resized XAML root
        // must not lose the keyboard height a second time.
        return new(
            Math.Min(visibleBottom, visibleTop + Read(browser, 3)),
            Read(browser, 4), keyboardBottom > 0 ? 0 : Read(browser, 5), Read(browser, 6),
            keyboardBottom, visibleBottom);
    }

    internal static double[] SheetHeights(double availableHeight, double topInset, double bottomInset)
    {
        double visibleHeight = Math.Max(0, availableHeight - topInset);
        double compact = Math.Min(136 + bottomInset, Math.Max(96, visibleHeight - 52));
        double expanded = Math.Max(compact, visibleHeight - 76);
        double medium = Math.Clamp(visibleHeight * .49, compact, expanded);
        return [compact, medium, expanded];
    }

    private static double Read(ReadOnlySpan<double> values, int index, double fallback = 0) =>
        index < values.Length && double.IsFinite(values[index]) ? Math.Max(0, values[index]) : fallback;
}
