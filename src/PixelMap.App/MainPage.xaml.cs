using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;

namespace PixelMap.App;

public sealed partial class MainPage : Page
{
    private static readonly HttpClient SearchHttp = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly HttpClient RoutingHttp = new() { Timeout = TimeSpan.FromSeconds(35) };
    private static readonly MapPlace[] Destinations =
    [
        new("Lower Manhattan", "New York City, United States", 40.7087, -74.0104, 16.5),
        new("Rynek Główny", "Old Town, Kraków, Poland", 50.0617, 19.9373, 16.8),
        new("Shibuya Crossing", "Tokyo, Japan", 35.6595, 139.7005, 16.9),
        new("Eixample", "Barcelona, Spain", 41.3924, 2.1648, 16.4),
        new("Île de la Cité", "Paris, France", 48.8548, 2.3470, 16.2),
        new("Marina Bay", "Singapore", 1.2831, 103.8586, 16.2),
        new("Lake Garda mountains", "Riva del Garda, Italy · elevation demo", 45.875, 10.85, 12.3)
    ];
    private readonly PhotonGeocoder _geocoder = new(SearchHttp);
    private readonly SolidColorBrush _ink = new(ColorHelper.FromArgb(255, 28, 59, 54));
    private readonly SolidColorBrush _muted = new(ColorHelper.FromArgb(255, 75, 101, 92));
    private readonly SolidColorBrush _rowFill = new(ColorHelper.FromArgb(255, 240, 241, 226));
    private CancellationTokenSource? _request;
    private PanelKind _panel;
    private long _lastReadout;
    private bool _restored, _journeysOpen;
    private MapPlace? _selectedPlace;

    private enum PanelKind { Closed, Guide, Search, Help, Share, Camera }

    public MainPage()
    {
        InitializeComponent();
        if (OperatingSystem.IsBrowser()) Map.AttachAudioDevice(new PixelMap.Audio.BrowserAudioDevice());
        JourneyPanel.Attach(Map, _geocoder, new RoutePlanner(RoutingHttp, Map.VectorTiles));
        JourneyPanel.CloseRequested += (_, _) => DisplayJourneys(false);
        JourneyPanel.PickingChanged += (_, _) => LayoutJourneys();
        JourneyPanel.SizeChanged += (_, _) => UpdateNavigationInsets();
        SizeChanged += (_, e) => LayoutChrome(e.NewSize.Width, e.NewSize.Height);
        Loaded += (_, _) =>
        {
            LayoutChrome(ActualWidth, ActualHeight);
            RestoreSharedView();
            SyncReadout();
        };
        Unloaded += (_, _) => CancelRequest();
        Map.CameraChanged += (_, _) =>
        {
            var tick = Environment.TickCount64;
            if (tick - _lastReadout < 100 && (Map.Camera.Pitch < 1) == Equals(ProjectionShortcut.Content, "2D")) return;
            _lastReadout = tick;
            SyncReadout();
        };
        Map.StatusChanged += (_, _) => SyncReadout();
    }

    private void LayoutChrome(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        var narrow = width < 600;
        var inset = narrow ? 8 : 16;
        SearchOverlay.Margin = MapTools.Margin = SceneDock.Margin = new Thickness(inset);
        JourneyPanel.Margin = PickBanner.Margin = new Thickness(inset);
        JourneyPanel.Width = Math.Max(140, Math.Min(350, width - 2 * inset));
        JourneyPanel.MaxHeight = narrow && height > 420 ? Math.Clamp(height * .54, 230, 470) : Math.Max(150, height - 115);
        PickBanner.MaxWidth = Math.Max(160, width - 2 * inset);
        SearchOverlay.Width = Math.Max(120, Math.Min(344, width - 2 * inset));
        Header.Padding = new Thickness(narrow ? 8 : 16, narrow ? 6 : 9, narrow ? 8 : 16, narrow ? 6 : 9);
        BrandCaption.Visibility = width < 360 ? Visibility.Collapsed : Visibility.Visible;
        Status.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        ScaleBar.Visibility = width < 790 ? Visibility.Collapsed : Visibility.Visible;
        MobileStatus.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        PlaceCard.MaxWidth = Math.Max(110, Math.Min(335, width - 85 - 2 * inset));
        ResultScroll.MaxHeight = Math.Clamp(height - 225, 55, 330);
        PlaceCard.Visibility = height < 345 ? Visibility.Collapsed : Visibility.Visible;
        // Keep controls usable on landscape phones by laying out the strip horizontally.
        ToolStack.Orientation = height < 420 ? Orientation.Horizontal : Orientation.Vertical;
        ToolDivider.Visibility = height < 420 ? Visibility.Collapsed : Visibility.Visible;
        if (narrow && height < 420) PlaceCard.Visibility = Visibility.Collapsed;
        UpdateNavigationInsets();
    }

    private void SyncReadout()
    {
        var camera = Map.Camera;
        PerspectiveItem.IsChecked = camera.Perspective;
        GpuRenderingItem.IsChecked = Map.IsGpuRenderingEnabled;
        TerrainReliefItem.IsChecked = Map.IsTerrainEnabled;
        ElevationLegend.Visibility = Map.IsElevationLayerEnabled ? Visibility.Visible : Visibility.Collapsed;
        ElevationCredits.Visibility = Map.IsTerrainEnabled || Map.IsElevationLayerEnabled ? Visibility.Visible : Visibility.Collapsed;
        ElevationReadout.Text = Map.HasElevation
            ? FormattableString.Invariant($"{Map.CenterElevationMeters:0} m elevation · {(Map.IsTerrainEnabled ? Map.TerrainExaggeration.ToString("0.##", CultureInfo.InvariantCulture) + "× relief" : "2D altitude")}")
            : "Loading elevation";
        AutomationProperties.SetName(ElevationReadout, ElevationReadout.Text);
        var status = Map.StatusText;
        var location = FormattableString.Invariant($"{camera.Latitude:0.0000}°, {camera.Longitude:0.0000}°");
        Status.Text = FormattableString.Invariant($"{status}   ·   {location}   ·   z{camera.Zoom:0.0}   ·   {camera.Bearing:0}°");
        if ((Map.IsTerrainEnabled || Map.IsElevationLayerEnabled) && Map.HasElevation)
            Status.Text += FormattableString.Invariant($"   ·   {Map.CenterElevationMeters:0} m elevation");
        MobileStatus.Text = status;
        AutomationProperties.SetName(Status, Status.Text);
        AutomationProperties.SetName(MobileStatus, status);
        double bearing = camera.Bearing * Math.PI / 180;
        CompassTransform.Angle = Math.Atan2(-Math.Sin(bearing), Math.Cos(bearing) * Math.Cos(camera.Pitch * Math.PI / 180)) * 180 / Math.PI;
        double metersPerPixel = 40075016.68557849 * Math.Cos(camera.Latitude * Math.PI / 180) / (512 * Math.Pow(2, camera.Zoom));
        if ((camera.Perspective || Map.IsTerrainEnabled) && Map.ActualWidth > 80 && Map.ActualHeight > 32 &&
            Map.TryGetLocation(Map.ActualWidth / 2 - 40, Map.ActualHeight - 32, out var left) &&
            Map.TryGetLocation(Map.ActualWidth / 2 + 40, Map.ActualHeight - 32, out var right))
        {
            double groundScale = new RoutePoint(left.Latitude, left.Longitude).DistanceTo(new RoutePoint(right.Latitude, right.Longitude)) / 80;
            if (double.IsFinite(groundScale) && groundScale > 0) metersPerPixel = groundScale;
        }
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(80 * metersPerPixel)));
        var steps = 80 * metersPerPixel / magnitude;
        var distance = magnitude * (steps >= 5 ? 5 : steps >= 2 ? 2 : 1);
        ScaleRule.Width = distance / metersPerPixel;
        ScaleLabel.Text = distance >= 1000
            ? FormattableString.Invariant($"{distance / 1000:0.#} km")
            : FormattableString.Invariant($"{distance:0.#} m");
        ProjectionShortcut.Content = camera.Pitch < 1 ? "2D" : "3D";
        var angledView = camera.Perspective ? "perspective" : "isometric";
        AutomationProperties.SetName(ProjectionShortcut, camera.Pitch < 1 ? $"Switch to 3D {angledView}" : "Switch to 2D overhead");
        ProjectionItem.Text = camera.Pitch < 1 ? $"Switch to {angledView} view" : "Switch to overhead view";
    }

    private void OnNorth(object sender, RoutedEventArgs e) => Map.RotateBy(-Math.IEEERemainder(Map.Camera.Bearing, 360));
    private void OnZoomIn(object sender, RoutedEventArgs e) => Map.ZoomBy(1);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Map.ZoomBy(-1);
    private void OnRotateLeft(object sender, RoutedEventArgs e) => Map.RotateBy(-45);
    private void OnRotateRight(object sender, RoutedEventArgs e) => Map.RotateBy(45);
    private void OnProjection(object sender, RoutedEventArgs e) => Map.ToggleProjection();

    private void OnPerspective(object sender, RoutedEventArgs e) => ChooseCamera(Map.Camera with { Perspective = !Map.Camera.Perspective });

    private void OnGpuRendering(object sender, RoutedEventArgs e)
    {
        Map.IsGpuRenderingEnabled = !Map.IsGpuRenderingEnabled;
        GpuRenderingItem.IsChecked = Map.IsGpuRenderingEnabled;
    }

    private void OnTerrainRelief(object sender, RoutedEventArgs e)
    {
        Map.IsTerrainEnabled = !Map.IsTerrainEnabled;
        TerrainReliefItem.IsChecked = Map.IsTerrainEnabled;
        SyncReadout();
    }

    private void ChooseCamera(MapCamera camera)
    {
        Map.Journey.Follow = false;
        Map.FlyTo(camera);
    }

    private void OnPalette(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item || !Enum.TryParse<PixelStyle>(item.Tag?.ToString(), out var style)) return;
        SetPalette(style);
    }

    private void SetPalette(PixelStyle style)
    {
        Map.Palette = style;
        DayItem.IsChecked = style == PixelStyle.Day;
        DuskItem.IsChecked = style == PixelStyle.Dusk;
        NightItem.IsChecked = style == PixelStyle.Night;
    }

    private void OnMapLayer(object sender, RoutedEventArgs e) => SetBaseLayer(false);
    private void OnEarthLayer(object sender, RoutedEventArgs e) => SetBaseLayer(true);

    private void OnElevationLayer(object sender, RoutedEventArgs e) => SetBaseLayer(false, true);

    private void SetBaseLayer(bool earth, bool elevation = false)
    {
        Map.IsElevationLayerEnabled = elevation;
        Map.IsEarthImageryEnabled = earth;
        MapLayerButton.IsChecked = !earth && !elevation;
        EarthLayerButton.IsChecked = earth;
        ElevationLayerButton.IsChecked = elevation;
        EarthCredits.Visibility = earth ? Visibility.Visible : Visibility.Collapsed;
        SyncReadout();
    }

    private static Uri? CurrentPageUri()
    {
        if (!OperatingSystem.IsBrowser()) return null;
        using var window = JSHost.GlobalThis;
        using var location = window.GetPropertyAsJSObject("location");
        return Uri.TryCreate(location?.GetPropertyAsString("href"), UriKind.Absolute, out var page) ? page : null;
    }

    private void RestoreSharedView()
    {
        if (_restored) return;
        _restored = true;
        var page = CurrentPageUri();
        if (page?.Fragment.Split('/') is ["#journey", var name] && Enum.TryParse<TravelMode>(name, true, out var mode) &&
            mode.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            DisplayJourneys(true);
            JourneyPanel.LoadDemo(mode);
            return;
        }
        if (page is null || !MapViewLink.TryRead(page, out var camera, out var palette, out var earth, out var elevation, out var terrain, out var exaggeration)) return;
        SetPalette(palette);
        SetBaseLayer(earth, elevation);
        Map.IsTerrainEnabled = terrain;
        Map.TerrainExaggeration = exaggeration;
        Destination.Text = "Shared view";
        Map.FlyTo(camera);
    }

    private void OnShare(object sender, RoutedEventArgs e)
    {
        var page = CurrentPageUri() ?? new Uri("https://wieslawsoltes.github.io/UnoPixelMap/");
        var url = MapViewLink.Create(page, Map.Camera, Map.Palette, Map.IsEarthImageryEnabled,
            Map.IsElevationLayerEnabled, Map.IsTerrainEnabled, Map.TerrainExaggeration).AbsoluteUri;
        ShowPanel(PanelKind.Share, "SHARE THIS VIEW", "This link preserves the map position, base layer, terrain relief, vertical exaggeration, camera and lighting.");
        var field = new TextBox { Text = url, IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(field, "Shareable map link");
        field.GotFocus += (_, _) => field.SelectAll();
        Rows.Children.Add(field);
        var copy = new Button { Content = "Copy link", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText(url);
                Clipboard.SetContent(data);
                DrawerNote.Text = "Map link copied.";
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or NotSupportedException or JSException)
            {
                field.Focus(FocusState.Programmatic);
                field.SelectAll();
                DrawerNote.Text = "Use your device’s Copy command on the selected link.";
            }
        };
        Rows.Children.Add(copy);
    }

    private void OnJourneys(object sender, RoutedEventArgs e) => DisplayJourneys(true);

    private void DisplayJourneys(bool show)
    {
        _journeysOpen = show;
        if (show)
        {
            CloseDrawer();
            JourneyPanel.Open(_selectedPlace);
        }
        else JourneyPanel.Hide();
        LayoutJourneys();
    }

    private void LayoutJourneys()
    {
        bool pick = _journeysOpen && JourneyPanel.IsPicking;
        JourneyPanel.Visibility = _journeysOpen && !pick ? Visibility.Visible : Visibility.Collapsed;
        SearchOverlay.Visibility = _journeysOpen ? Visibility.Collapsed : Visibility.Visible;
        PickBanner.Visibility = pick ? Visibility.Visible : Visibility.Collapsed;
        if (pick) PickText.Text = JourneyPanel.PickingPrompt;
        UpdateNavigationInsets();
    }

    private void UpdateNavigationInsets()
    {
        var insets = new Thickness(0);
        if (_journeysOpen && JourneyPanel.Visibility == Visibility.Visible && JourneyPanel.ActualWidth > 0 && JourneyPanel.ActualHeight > 0)
        {
            insets = ActualWidth < 600
                ? new Thickness(0, JourneyPanel.Margin.Top + JourneyPanel.ActualHeight + 8, 0, 0)
                : new Thickness(JourneyPanel.Margin.Left + JourneyPanel.ActualWidth + 8, 0, 0, 0);
        }
        if (!Map.NavigationInsets.Equals(insets)) Map.NavigationInsets = insets;
    }

    private void OnCancelPick(object sender, RoutedEventArgs e) => JourneyPanel.StopPicking();

    private void OnCameraTools(object sender, RoutedEventArgs e)
    {
        if (_panel == PanelKind.Camera) { CloseDrawer(); return; }
        ShowPanel(PanelKind.Camera, "CAMERA", "Right-drag to orbit and tilt around the map.");
        AddCameraAction("North-up plan", () => ChooseCamera(Map.Camera with { Bearing = 0, Pitch = 0, Perspective = false }));
        AddCameraAction("Isometric · 55°", () => ChooseCamera(Map.Camera with { Bearing = -35, Pitch = 55, Perspective = false }));
        AddCameraAction("Low skyline · 70°", () => ChooseCamera(Map.Camera with { Pitch = 70, Perspective = true }));
        AddCameraAction("Bird’s-eye · 30°", () => ChooseCamera(Map.Camera with { Pitch = 30, Perspective = true }));
        var orbit = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (text, bearing, pitch) in new[] { ("Left", -15d, 0d), ("Right", 15d, 0d), ("Tilt -", 0d, -10d), ("Tilt +", 0d, 10d) })
        {
            var button = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4) };
            AutomationProperties.SetName(button, "Camera " + text);
            button.Click += (_, _) => Map.OrbitBy(bearing, pitch);
            orbit.Children.Add(button);
        }
        Rows.Children.Add(orbit);
        var terrain = new ToggleSwitch { Header = "Terrain relief", IsOn = Map.IsTerrainEnabled };
        terrain.Toggled += (_, _) => { Map.IsTerrainEnabled = terrain.IsOn; SyncReadout(); };
        Rows.Children.Add(terrain);
        var heightLabel = new TextBlock { Text = $"Vertical exaggeration · {Map.TerrainExaggeration:0.##}×", Foreground = _ink };
        var exaggeration = new Slider { Minimum = 1, Maximum = 4, StepFrequency = .25, SmallChange = .25, LargeChange = 1, Value = Map.TerrainExaggeration };
        AutomationProperties.SetName(exaggeration, "Terrain vertical exaggeration");
        exaggeration.ValueChanged += (_, args) =>
        {
            Map.TerrainExaggeration = args.NewValue;
            heightLabel.Text = $"Vertical exaggeration · {Map.TerrainExaggeration:0.##}×";
        };
        Rows.Children.Add(heightLabel);
        Rows.Children.Add(exaggeration);
    }

    private void AddCameraAction(string label, Action apply)
    {
        var button = new Button { Content = label, Background = _rowFill, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => apply();
        Rows.Children.Add(button);
    }

    private void OnGuide(object sender, RoutedEventArgs e)
    {
        if (_panel == PanelKind.Guide) { CloseDrawer(); return; }
        ShowPanel(PanelKind.Guide, "CITY GUIDE", "Choose a district to fly there.");
        foreach (var place in Destinations) AddPlace(place);
    }

    private void OnHelp(object sender, RoutedEventArgs e)
    {
        ShowPanel(PanelKind.Help, "MAP CONTROLS", "Place search by Photon / komoot with OpenStreetMap data.");
        AddHelp("Browse", "Drag to pan, scroll or pinch to zoom. Tap the map to inspect a location.");
        AddHelp("Change perspective", "Use the curved arrows to rotate. Switch projection or lighting in View.");
        AddHelp("Keyboard", "Focus the map. Use arrows to pan, + and − to zoom, Q and E to rotate, P to switch projection, or Home to return to Manhattan.");
        AddHelp("Elevation", "Choose Elevation for altitude colors. Terrain relief works with Map and Earth too. Camera tools adjust vertical exaggeration from 1× to 4×. Elevation readouts remain in real metres above sea level.");
        AddHelp("Journeys", "Use Route beside search. Choose stops or try a vehicle demo. Preview, take control, or jump to a turn.");
        AddHelp("Drop a pin", "Enter latitude, longitude in the search field and press Enter.");
    }

    private void AddHelp(string title, string detail)
    {
        var text = TextPair(title, detail);
        text.Margin = new Thickness(4, 4, 4, 8);
        Rows.Children.Add(text);
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => await SubmitSearchAsync();

    private async void OnSearchKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await SubmitSearchAsync();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseDrawer();
        }
    }

    private void OnQueryEdited(object sender, TextChangedEventArgs e)
    {
        if (_panel == PanelKind.Search) CloseDrawer();
    }

    private async Task SubmitSearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) { OnGuide(this, new RoutedEventArgs()); return; }
        if (PhotonGeocoder.TryParseCoordinates(query, out var coordinate))
        {
            NavigateTo(coordinate);
            return;
        }

        ShowPanel(PanelKind.Search, "SEARCH RESULTS", "Searching places…");
        var request = new CancellationTokenSource();
        _request = request;
        try
        {
            var matches = await _geocoder.SearchAsync(query, request.Token);
            if (request.IsCancellationRequested) return;
            var local = LocalMatches(query);
            foreach (var place in matches) AddPlace(place);
            foreach (var place in local.Where(p => !matches.Any(m =>
                Math.Abs(m.Latitude - p.Latitude) < .005 && Math.Abs(m.Longitude - p.Longitude) < .005)))
                AddPlace(place);
            DrawerNote.Text = Rows.Children.Count == 0
                ? "No matches. Try a nearby city, a street address, or latitude, longitude."
                : "Select a place to fly there. Search by Photon / komoot.";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            if (request.IsCancellationRequested) return;
            foreach (var place in LocalMatches(query)) AddPlace(place);
            DrawerNote.Text = Rows.Children.Count > 0
                ? "Live search is unavailable. Showing matches from the city guide."
                : "Live search is unavailable. Try again, browse the city guide, or enter coordinates.";
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
        }
    }

    private static IEnumerable<MapPlace> LocalMatches(string query) => Destinations.Where(p =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(p.Name + " " + p.Detail, query,
            CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreCase) >= 0);

    private void AddPlace(MapPlace place)
    {
        var button = new Button
        {
            Background = _rowFill,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(9, 5, 9, 5),
            Content = TextPair(place.Name, place.Detail),
            Tag = place
        };
        AutomationProperties.SetName(button, place.Name + ", " + place.Detail);
        button.Click += OnPick;
        Rows.Children.Add(button);
    }

    private StackPanel TextPair(string title, string detail)
    {
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, Foreground = _ink, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = _muted, TextWrapping = TextWrapping.Wrap });
        return text;
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MapPlace place }) NavigateTo(place);
    }

    private void NavigateTo(MapPlace place)
    {
        CloseDrawer();
        _selectedPlace = place;
        Destination.Text = place.Name;
        Map.SetMarker(place.Latitude, place.Longitude, place.Name);
        if (place == Destinations[^1])
        {
            SetBaseLayer(false, true);
            Map.FlyTo(new MapCamera(place.Latitude, place.Longitude, place.Zoom, -25, 58, true));
        }
        else Map.FlyTo(place.Latitude, place.Longitude, place.Zoom);
    }

    private void ShowPanel(PanelKind kind, string heading, string note)
    {
        DisplayJourneys(false);
        CancelRequest();
        _panel = kind;
        Rows.Children.Clear();
        DrawerHeading.Text = heading;
        DrawerNote.Text = note;
        Drawer.Visibility = Visibility.Visible;
    }

    private void OnCloseDrawer(object sender, RoutedEventArgs e) => CloseDrawer();

    private void CloseDrawer()
    {
        CancelRequest();
        _panel = PanelKind.Closed;
        Drawer.Visibility = Visibility.Collapsed;
        Rows.Children.Clear();
    }

    private void CancelRequest()
    {
        _request?.Cancel();
        _request = null;
    }
}
