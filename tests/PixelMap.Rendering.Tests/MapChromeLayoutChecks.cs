using PixelMap.App;

internal static class MapChromeLayoutChecks
{
    internal static void Run()
    {
        var desktop = MapChromeLayout.Measure(900, []);
        Equal(0, desktop.Top, "Desktop top");
        Equal(0, desktop.KeyboardBottom, "Desktop keyboard");
        Equal(900, desktop.AvailableHeight, "Desktop bounds");
        Heights(desktop, 136, 441, 824);

        var mobile = MapChromeLayout.Measure(844, [844, 844, 0, 47, 0, 34, 0]);
        Equal(47, mobile.Top, "Notch inset");
        Equal(34, mobile.Bottom, "Home indicator inset");
        Heights(mobile, 170, 390.53, 721);

        var keyboard = MapChromeLayout.Measure(844, [844, 510, 0, 47, 0, 34, 0]);
        Equal(334, keyboard.KeyboardBottom, "Overlay keyboard occlusion");
        Equal(0, keyboard.Bottom, "Keyboard replaces home indicator padding");
        Heights(keyboard, 136, 226.87, 387);
        Visible(keyboard, 0, 510);

        // Safari can pan the visual viewport to the focused native input. The
        // expanded sheet, including its handle/search, must remain below its top.
        var panned = MapChromeLayout.Measure(844, [844, 400, 250, 47, 0, 34, 0]);
        Equal(297, panned.Top, "Panned viewport origin plus notch");
        Equal(194, panned.KeyboardBottom, "Panned keyboard occlusion");
        Heights(panned, 136, 172.97, 277);
        Visible(panned, 250, 650);

        // Chrome's interactive-widget=resizes-content can already resize XAML.
        // It must not have the keyboard height subtracted for a second time.
        var resized = MapChromeLayout.Measure(510, [844, 510, 0, 0, 0, 0, 0]);
        Equal(0, resized.KeyboardBottom, "Already resized root");
        Heights(resized, 136, 249.9, 434);
        Visible(resized, 0, 510);

        var landscape = MapChromeLayout.Measure(390, [390, 390, 0, 0, 59, 21, 59]);
        Equal(59, landscape.Left, "Landscape left inset");
        Equal(59, landscape.Right, "Landscape right inset");
        Heights(landscape, 157, 191.1, 314);

        var restored = MapChromeLayout.Measure(844, [844, 844, 0, 47, 0, 34, 0]);
        if (restored != mobile) throw new InvalidOperationException("Closing the keyboard did not restore chrome bounds.");
        Console.WriteLine("Passed map chrome desktop, safe-area, keyboard, panned viewport, resized-root and restoration checks.");
    }

    private static void Heights(MapChromeViewport viewport, double compact, double medium, double expanded)
    {
        var heights = MapChromeLayout.SheetHeights(viewport.AvailableHeight, viewport.Top, viewport.Bottom);
        Equal(compact, heights[0], "Compact detent");
        Equal(medium, heights[1], "Medium detent");
        Equal(expanded, heights[2], "Expanded detent");
    }

    private static void Visible(MapChromeViewport viewport, double visibleTop, double visibleBottom)
    {
        var heights = MapChromeLayout.SheetHeights(viewport.AvailableHeight, viewport.Top, viewport.Bottom);
        foreach (double height in heights)
        {
            double sheetTop = viewport.AvailableHeight - height;
            if (sheetTop < visibleTop || viewport.AvailableHeight > visibleBottom || height < 120)
                throw new InvalidOperationException("A keyboard detent clipped the handle/search outside the visible viewport.");
        }
    }

    private static void Equal(double expected, double actual, string name)
    {
        if (Math.Abs(expected - actual) > .000001)
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}
