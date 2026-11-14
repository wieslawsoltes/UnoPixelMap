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
using PixelMap.Controls;
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
    private readonly SolidColorBrush _ink = new(ColorHelper.FromArgb(255, 28, 28, 30));
    private readonly SolidColorBrush _muted = new(ColorHelper.FromArgb(255, 99, 99, 102));
    private readonly SolidColorBrush _rowFill = new(ColorHelper.FromArgb(255, 241, 242, 246));
    private readonly DispatcherTimer _readoutClock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CancellationTokenSource? _request;
    private PanelKind _panel;
    private long _lastReadout;
    private bool _restored, _journeysOpen, _gameOpen;
    private MapPlace? _selectedPlace;
    private BrowserViewport? _browserViewport;
    private bool _narrow, _sidebarHidden, _restoreHiddenAfterDrawer, _sheetDragged, _layingOut;
    private int _sheetDetent;
    private uint? _sheetPointer;
    private double _sheetDragStart, _sheetStartHeight, _sheetDragHeight, _keyboardBottom;
    private double _safeTop, _safeRight, _safeBottom, _safeLeft;
    private long _ignoreSheetClickUntil;

    private enum PanelKind { Closed, Guide, Search, Help, Share, Camera }

    public MainPage()
    {
        InitializeComponent();
        if (OperatingSystem.IsBrowser()) Map.AttachAudioDevice(new PixelMap.Audio.BrowserAudioDevice());
        JourneyPanel.Attach(Map, _geocoder, new RoutePlanner(RoutingHttp, Map.VectorTiles));
        GamePanel.Attach(Map);
        GamePanel.CloseRequested += (_, _) => DisplayGame(false);
        JourneyPanel.CloseRequested += (_, _) => DisplayJourneys(false);
        JourneyPanel.PickingChanged += (_, _) => LayoutJourneys();
        JourneyPanel.SizeChanged += (_, _) => UpdateNavigationInsets();
        SidebarSurface.SizeChanged += (_, _) => UpdateNavigationInsets();
        SheetHandle.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSheetPressed), true);
        SheetHandle.AddHandler(PointerMovedEvent, new PointerEventHandler(OnSheetMoved), true);
        SheetHandle.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSheetReleased), true);
        SheetHandle.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnSheetCanceled), true);
        SheetHandle.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSheetCanceled), true);
        SizeChanged += (_, e) => LayoutChrome(e.NewSize.Width, e.NewSize.Height);
        Loaded += (_, _) =>
        {
            Map.SelectionChanged -= OnMapSelectionChanged;
            Map.SelectionChanged += OnMapSelectionChanged;
            LocateButton.IsEnabled = true;
            _browserViewport ??= BrowserViewport.TryCreate(() => LayoutChrome(ActualWidth, ActualHeight));
            LayoutChrome(ActualWidth, ActualHeight);
            RestoreSharedView();
            SyncReadout();
        };
        Unloaded += (_, _) =>
        {
            CancelRequest();
            _readoutClock.Stop();
            _sheetPointer = null;
            _sheetDragged = false;
            _ignoreSheetClickUntil = 0;
            SheetHandle.ReleasePointerCaptures();
            _browserViewport?.Dispose();
            _browserViewport = null;
            Map.SelectionChanged -= OnMapSelectionChanged;
        };
        _readoutClock.Tick += (_, _) =>
        {
            _readoutClock.Stop();
            if (!IsLoaded) return;
            _lastReadout = Environment.TickCount64;
            SyncReadout(cameraOnly: true);
        };
        Map.CameraChanged += (_, _) =>
        {
            if (!IsLoaded) return;
            var tick = Environment.TickCount64;
            if (tick - _lastReadout < 100 && (Map.Camera.Pitch < 1) == Equals(ProjectionShortcut.Content, "2D"))
            {
                if (!_readoutClock.IsEnabled) _readoutClock.Start();
                return;
            }
            _readoutClock.Stop();
            _lastReadout = tick;
            SyncReadout(cameraOnly: true);
        };
        Map.StatusChanged += (_, _) => SyncReadout();
    }

    private void LayoutChrome(double width, double height)
    {
        if (width <= 0 || height <= 0 || _layingOut) return;
        _layingOut = true;
        try
        {
            var viewport = MapChromeLayout.Measure(height, _browserViewport?.Read() ?? []);
            _safeTop = viewport.Top; _safeRight = viewport.Right;
            _safeBottom = viewport.Bottom; _safeLeft = viewport.Left;
            _keyboardBottom = viewport.KeyboardBottom;
            _narrow = width < 720 || width < 1000 && height < 500;
            double inset = _narrow ? 12 : 16;
            double usableHeight = viewport.AvailableHeight;
            bool picking = _journeysOpen && JourneyPanel.IsPicking;
            bool visible = !_gameOpen && !picking && !_sidebarHidden;
            var detents = SheetHeights(usableHeight);
            double sheetHeight = _sheetPointer.HasValue && _sheetDragged
                ? Math.Clamp(_sheetDragHeight, detents[0], detents[2]) : detents[_sheetDetent];
            double desktopHeight = _journeysOpen ? 660 : _panel == PanelKind.Closed ? 468 : 610;
            SidebarSurface.Width = _narrow ? Math.Max(180, width - _safeLeft - _safeRight) : 360;
            SidebarSurface.Height = _narrow ? sheetHeight : Math.Max(160, Math.Min(desktopHeight, usableHeight - _safeTop - 32));
            SidebarSurface.HorizontalAlignment = HorizontalAlignment.Left;
            SidebarSurface.VerticalAlignment = _narrow ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            SidebarSurface.Margin = _narrow
                ? new Thickness(_safeLeft, 0, _safeRight, _keyboardBottom)
                : new Thickness(16 + _safeLeft, 16 + _safeTop, 0, 16 + _keyboardBottom);
            SidebarSurface.CornerRadius = _narrow ? new CornerRadius(24, 24, 0, 0) : new CornerRadius(22);
            SidebarSurface.Padding = _narrow ? new Thickness(16, 0, 16, Math.Max(12, _safeBottom)) : new Thickness(16, 6, 16, 16);
            SidebarSurface.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            OpenSidebarButton.Visibility = !_gameOpen && !picking && _sidebarHidden ? Visibility.Visible : Visibility.Collapsed;
            OpenSidebarButton.Margin = new Thickness(inset + _safeLeft, inset + _safeTop, 0, 0);
            BrandPanel.Visibility = _narrow ? Visibility.Collapsed : Visibility.Visible;
            SheetHandle.Visibility = _narrow ? Visibility.Visible : Visibility.Collapsed;
            bool expanded = !_narrow || _sheetDetent > 0 || _sheetPointer.HasValue && _sheetDragged;
            SearchOverlay.Visibility = _journeysOpen ? Visibility.Collapsed : Visibility.Visible;
            SidebarContent.Visibility = expanded && !_journeysOpen ? Visibility.Visible : Visibility.Collapsed;
            HomeScroll.Visibility = _panel == PanelKind.Closed ? Visibility.Visible : Visibility.Collapsed;
            JourneyPanel.Visibility = _journeysOpen && !picking && expanded ? Visibility.Visible : Visibility.Collapsed;
            JourneyPeek.Visibility = _journeysOpen && !picking && !expanded ? Visibility.Visible : Visibility.Collapsed;
            JourneyPanel.MaxHeight = Math.Max(80, SidebarSurface.Height - 62 - _safeBottom);
            PickBanner.Visibility = picking ? Visibility.Visible : Visibility.Collapsed;
            PickBanner.Margin = new Thickness(inset + _safeLeft, inset + _safeTop, inset + _safeRight, 0);
            PickBanner.MaxWidth = Math.Max(160, width - _safeLeft - _safeRight - inset * 2);
            if (picking) PickText.Text = JourneyPanel.PickingPrompt;

            double coveredBottom = visible && _narrow ? SidebarSurface.Height + _keyboardBottom : _keyboardBottom;
            double mapHeight = height - coveredBottom - _safeTop;
            bool horizontalTools = _narrow && mapHeight < 375;
            SceneDock.Orientation = horizontalTools ? Orientation.Horizontal : Orientation.Vertical;
            LayerStack.Orientation = ToolStack.Orientation = horizontalTools ? Orientation.Horizontal : Orientation.Vertical;
            SceneDock.Margin = new Thickness(0, inset + _safeTop, inset + _safeRight, 0);
            MapTools.Visibility = _gameOpen ? Visibility.Collapsed : Visibility.Visible;
            LayerDivider.Visibility = ToolDivider.Visibility = horizontalTools ? Visibility.Collapsed : Visibility.Visible;
            bool zoomVisible = !horizontalTools || width - _safeLeft - _safeRight >= 360;
            ZoomInButton.Visibility = ZoomOutButton.Visibility = zoomVisible ? Visibility.Visible : Visibility.Collapsed;
            double footerLeft = !_narrow && visible ? SidebarSurface.Margin.Left + SidebarSurface.Width + 16 : inset + _safeLeft;
            Footer.Margin = new Thickness(footerLeft, 0, 64 + _safeRight, coveredBottom + Math.Max(6, _safeBottom));
            Footer.MaxWidth = Math.Max(120, Math.Min(610, width - footerLeft - 76 - _safeRight));
            Footer.Visibility = mapHeight >= 170 ? Visibility.Visible : Visibility.Collapsed;
            MapInfoButton.Margin = new Thickness(0, 0, inset + _safeRight, coveredBottom + Math.Max(6, _safeBottom));
            MapInfoButton.Visibility = mapHeight >= 170 && !_gameOpen ? Visibility.Visible : Visibility.Collapsed;
            ScaleBar.Margin = new Thickness(0, 0, inset + _safeRight, coveredBottom + 58 + _safeBottom);
            ScaleBar.Visibility = mapHeight >= 250 && !_gameOpen ? Visibility.Visible : Visibility.Collapsed;
            GamePanel.SetViewportInsets(new Thickness(_safeLeft, _safeTop, _safeRight, _safeBottom), _keyboardBottom);
            AutomationProperties.SetName(SheetHandle, _sheetDetent == 2 ? "Collapse search sheet" : "Expand search sheet");
            AutomationProperties.SetHelpText(SheetHandle, "Drag to resize. Up Arrow expands; Down Arrow collapses; Home shows the map; End expands fully.");
            UpdateNavigationInsets();
        }
        finally { _layingOut = false; }
        if (IsLoaded) SyncReadout();
    }

    private double[] SheetHeights(double availableHeight) =>
        MapChromeLayout.SheetHeights(availableHeight, _safeTop, _safeBottom);

    private void SetSheetDetent(int detent)
    {
        _sheetDetent = Math.Clamp(detent, 0, 2);
        if (_sheetDetent == 0 && SearchBox.FocusState != FocusState.Unfocused) Map.Focus(FocusState.Programmatic);
        LayoutChrome(ActualWidth, ActualHeight);
    }

    private void OnSheetToggle(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 < _ignoreSheetClickUntil) return;
        SetSheetDetent((_sheetDetent + 1) % 3);
    }

    private void OnSheetKey(object sender, KeyRoutedEventArgs e)
    {
        int? detent = e.Key switch
        {
            VirtualKey.Up => Math.Min(2, _sheetDetent + 1), VirtualKey.Down => Math.Max(0, _sheetDetent - 1),
            VirtualKey.Home or VirtualKey.Escape => 0, VirtualKey.End => 2, _ => null
        };
        if (detent is not int value) return;
        e.Handled = true;
        SetSheetDetent(value);
    }

    private void OnSheetPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_narrow || _sheetPointer.HasValue) return;
        _sheetPointer = e.Pointer.PointerId;
        _ignoreSheetClickUntil = 0;
        _sheetDragStart = e.GetCurrentPoint(this).Position.Y;
        _sheetStartHeight = _sheetDragHeight = SidebarSurface.ActualHeight;
        _sheetDragged = false;
        SheetHandle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSheetMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_sheetPointer != e.Pointer.PointerId) return;
        double delta = e.GetCurrentPoint(this).Position.Y - _sheetDragStart;
        if (Math.Abs(delta) > 6)
        {
            _sheetDragged = true;
            _ignoreSheetClickUntil = long.MaxValue;
        }
        if (_sheetDragged)
        {
            _sheetDragHeight = _sheetStartHeight - delta;
            LayoutChrome(ActualWidth, ActualHeight);
        }
        e.Handled = true;
    }

    private void OnSheetReleased(object sender, PointerRoutedEventArgs e) => FinishSheetDrag(e);
    private void OnSheetCanceled(object sender, PointerRoutedEventArgs e) => FinishSheetDrag(e);

    private void FinishSheetDrag(PointerRoutedEventArgs e)
    {
        if (_sheetPointer != e.Pointer.PointerId) return;
        _sheetPointer = null;
        SheetHandle.ReleasePointerCapture(e.Pointer);
        if (_sheetDragged)
        {
            // ButtonBase may release capture before our released handler. A tap is
            // owned by Click; only a real drag suppresses that accessible action.
            _ignoreSheetClickUntil = Environment.TickCount64 + 250;
            var heights = SheetHeights(Math.Max(0, ActualHeight - _keyboardBottom));
            _sheetDetent = Enumerable.Range(0, 3).MinBy(index => Math.Abs(heights[index] - _sheetDragHeight));
        }
        _sheetDragged = false;
        SetSheetDetent(_sheetDetent);
        e.Handled = true;
    }

    private void OnSearchFocused(object sender, RoutedEventArgs e)
    {
        if (SidebarSurface is not null && _narrow) SetSheetDetent(2);
    }

    private void OnHideSidebar(object sender, RoutedEventArgs e)
    {
        _sidebarHidden = true;
        Map.Focus(FocusState.Programmatic);
        LayoutChrome(ActualWidth, ActualHeight);
    }

    private void OnShowSidebar(object sender, RoutedEventArgs e)
    {
        _sidebarHidden = false;
        if (_narrow) _sheetDetent = _journeysOpen ? 1 : 2;
        LayoutChrome(ActualWidth, ActualHeight);
        if (!_journeysOpen) SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnChromeTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;
    private void OnChromeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;
    private void OnChromeWheel(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    private void OnFeaturedPlace(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int index) && index >= 0 && index < Destinations.Length)
            NavigateTo(Destinations[index]);
    }

    private void OnLocate(object sender, RoutedEventArgs e)
    {
        if (_browserViewport is null) return;
        LocateButton.IsEnabled = false;
        _browserViewport.Locate((latitude, longitude) =>
        {
            if (!IsLoaded) return;
            LocateButton.IsEnabled = true;
            NavigateTo(new MapPlace("My Location", "Your current location", latitude, longitude, 16.8));
        }, message =>
        {
            if (!IsLoaded) return;
            LocateButton.IsEnabled = true;
            ShowPanel(PanelKind.Help, "Location unavailable", message);
        });
    }

    private void SyncReadout(bool cameraOnly = false)
    {
        var camera = Map.Camera;
        PerspectiveItem.IsChecked = camera.Perspective;
        if (!cameraOnly)
        {
            BalancedQualityItem.IsChecked = Map.RenderingQuality == WebGpuQuality.Balanced;
            HighQualityItem.IsChecked = Map.RenderingQuality == WebGpuQuality.High;
            UltraQualityItem.IsChecked = Map.RenderingQuality == WebGpuQuality.Ultra;
            PixelStyleItem.IsChecked = Map.RenderStyle == MapRenderStyle.Pixel;
            NormalStyleItem.IsChecked = Map.RenderStyle == MapRenderStyle.Normal;
            MinecraftStyleItem.IsChecked = Map.RenderStyle == MapRenderStyle.Minecraft;
            RobloxStyleItem.IsChecked = Map.RenderStyle == MapRenderStyle.Roblox;
            TerrainReliefItem.IsChecked = Map.IsTerrainEnabled;
            MapLayerButton.IsChecked = !Map.IsEarthImageryEnabled && !Map.IsElevationLayerEnabled;
            EarthLayerButton.IsChecked = Map.IsEarthImageryEnabled;
            ElevationLayerButton.IsChecked = Map.IsElevationLayerEnabled;
            EarthCredits.Visibility = Map.IsEarthImageryEnabled ? Visibility.Visible : Visibility.Collapsed;
            EarthSourceLinks.Visibility = EarthCredits.Visibility;
            ElevationLegend.Visibility = Map.IsElevationLayerEnabled ? Visibility.Visible : Visibility.Collapsed;
            ElevationCredits.Visibility = Map.IsTerrainEnabled || Map.IsElevationLayerEnabled ? Visibility.Visible : Visibility.Collapsed;
            ElevationSourceLinks.Visibility = ElevationCredits.Visibility;
        }
        bool showElevation = (Map.IsTerrainEnabled || Map.IsElevationLayerEnabled) && Map.HasElevation;
        double elevation = showElevation && (Status.Visibility == Visibility.Visible || !_gameOpen && Map.IsElevationLayerEnabled)
            ? Map.CenterElevationMeters : 0;
        var status = Map.StatusText;
        if (Status.Visibility == Visibility.Visible)
        {
            var location = FormattableString.Invariant($"{camera.Latitude:0.0000}°, {camera.Longitude:0.0000}°");
            var text = FormattableString.Invariant($"{status}   ·   {Map.RenderStyle} rendering   ·   {location}   ·   z{camera.Zoom:0.0}   ·   {camera.Bearing:0}°");
            if (showElevation) text += FormattableString.Invariant($"   ·   {elevation:0} m elevation");
            SetReadout(Status, text);
        }
        if (!_gameOpen)
        {
            if (MobileStatus.Visibility == Visibility.Visible) SetReadout(MobileStatus, status);
            if (Map.IsElevationLayerEnabled)
                SetReadout(ElevationReadout, Map.HasElevation
                    ? FormattableString.Invariant($"{elevation:0} m elevation · {(Map.IsTerrainEnabled ? Map.TerrainExaggeration.ToString("0.##", CultureInfo.InvariantCulture) + "× relief" : "2D altitude")}")
                    : "Loading elevation");
            double bearing = camera.Bearing * Math.PI / 180;
            CompassTransform.Angle = Math.Atan2(-Math.Sin(bearing), Math.Cos(bearing) * Math.Cos(camera.Pitch * Math.PI / 180)) * 180 / Math.PI;
        }
        if (ScaleBar.Visibility == Visibility.Visible)
        {
            double metersPerPixel = 40075016.68557849 * Math.Cos(camera.Latitude * Math.PI / 180) / (512 * Math.Pow(2, camera.Zoom));
            double scaleY = Math.Clamp(Map.ActualHeight - ScaleBar.Margin.Bottom - 4, 0, Math.Max(0, Map.ActualHeight - 1));
            if ((camera.Perspective || Map.IsTerrainEnabled) && Map.ActualWidth > 80 && Map.ActualHeight > 32 &&
                Map.TryGetLocation(Map.ActualWidth / 2 - 40, scaleY, out var left) &&
                Map.TryGetLocation(Map.ActualWidth / 2 + 40, scaleY, out var right))
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
        }
        ProjectionShortcut.Content = camera.Pitch < 1 ? "2D" : "3D";
        var angledView = camera.Perspective ? "perspective" : "isometric";
        AutomationProperties.SetName(ProjectionShortcut, camera.Pitch < 1 ? $"Switch to 3D {angledView}" : "Switch to 2D overhead");
        ProjectionItem.Text = camera.Pitch < 1 ? $"Switch to {angledView} view" : "Switch to overhead view";
    }

    private static void SetReadout(TextBlock element, string text)
    {
        if (element.Text == text) return;
        element.Text = text;
        AutomationProperties.SetName(element, text);
    }

    private void OnNorth(object sender, RoutedEventArgs e) => Map.RotateBy(-Math.IEEERemainder(Map.Camera.Bearing, 360));
    private void OnZoomIn(object sender, RoutedEventArgs e) => Map.ZoomBy(1);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Map.ZoomBy(-1);
    private void OnRotateLeft(object sender, RoutedEventArgs e) => Map.RotateBy(-45);
    private void OnRotateRight(object sender, RoutedEventArgs e) => Map.RotateBy(45);
    private void OnProjection(object sender, RoutedEventArgs e) => Map.ToggleProjection();

    private void OnPerspective(object sender, RoutedEventArgs e) => ChooseCamera(Map.Camera with { Perspective = !Map.Camera.Perspective });

    private void OnRenderingQuality(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem item && Enum.TryParse<WebGpuQuality>(item.Tag?.ToString(), out var quality))
            Map.RenderingQuality = quality;
        SyncReadout();
    }

    private void OnRenderStyle(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem item && Enum.TryParse<MapRenderStyle>(item.Tag?.ToString(), out var style))
            Map.RenderStyle = style;
        SyncReadout();
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
        LayersButton.Flyout?.Hide();
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
        if (page is null || !MapViewLink.TryRead(page, out var camera, out var palette, out var earth, out var elevation,
            out var terrain, out var exaggeration, out var renderStyle)) return;
        SetPalette(palette);
        Map.RenderStyle = renderStyle;
        SetBaseLayer(earth, elevation);
        Map.IsTerrainEnabled = terrain;
        Map.TerrainExaggeration = exaggeration;
        Destination.Text = "Shared view";
        DestinationDetail.Text = FormattableString.Invariant($"{camera.Latitude:0.0000}°, {camera.Longitude:0.0000}°");
        PlaceEyebrow.Text = "Map link";
        Map.FlyTo(camera);
    }

    private void OnShare(object sender, RoutedEventArgs e)
    {
        var page = CurrentPageUri() ?? new Uri("https://wieslawsoltes.github.io/UnoPixelMap/");
        var url = MapViewLink.Create(page, Map.Camera, Map.Palette, Map.IsEarthImageryEnabled,
            Map.IsElevationLayerEnabled, Map.IsTerrainEnabled, Map.TerrainExaggeration, Map.RenderStyle).AbsoluteUri;
        ShowPanel(PanelKind.Share, "Share this view", "This link preserves the place, map style, layers, terrain, camera and lighting.");
        var field = new TextBox { Text = url, IsReadOnly = true, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
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

    private void OnGame(object sender, RoutedEventArgs e) => DisplayGame(!_gameOpen);

    private void DisplayGame(bool show)
    {
        if (_gameOpen == show) return;
        if (show)
        {
            DisplayJourneys(false);
            CloseDrawer();
            _gameOpen = true;
            GamePanel.Visibility = Visibility.Visible;
            GamePanel.Open();
        }
        else
        {
            _gameOpen = false;
            GamePanel.Hide();
            GamePanel.Visibility = Visibility.Collapsed;
            Map.StopFreeRoam();
        }
        LayoutJourneys();
        SyncReadout();
    }

    private void DisplayJourneys(bool show)
    {
        _journeysOpen = show;
        if (show)
        {
            DisplayGame(false);
            CloseDrawer();
            _sidebarHidden = false;
            if (_narrow) _sheetDetent = 1;
            JourneyPanel.Open(_selectedPlace);
        }
        else JourneyPanel.Hide();
        LayoutJourneys();
    }

    private void LayoutJourneys()
    {
        LayoutChrome(ActualWidth, ActualHeight);
    }

    private void UpdateNavigationInsets()
    {
        var insets = new Thickness(0);
        if (SidebarSurface.Visibility == Visibility.Visible && SidebarSurface.ActualWidth > 0 && SidebarSurface.ActualHeight > 0)
        {
            insets = _narrow
                ? new Thickness(0, 0, 0, SidebarSurface.Margin.Bottom + SidebarSurface.ActualHeight + 8)
                : new Thickness(SidebarSurface.Margin.Left + SidebarSurface.ActualWidth + 12, 0, 0, 0);
        }
        if (!Map.NavigationInsets.Equals(insets)) Map.NavigationInsets = insets;
    }

    private void OnCancelPick(object sender, RoutedEventArgs e) => JourneyPanel.StopPicking();

    private void OnCameraTools(object sender, RoutedEventArgs e)
    {
        if (_panel == PanelKind.Camera) { CloseDrawer(); return; }
        ShowPanel(PanelKind.Camera, "Camera", "Right-drag to orbit and tilt. Trackpad scroll pans, and pinch zooms around the pointer.");
        AddCameraAction("North-up plan", () => ChooseCamera(Map.Camera with { Bearing = 0, Pitch = 0, Perspective = false }));
        AddCameraAction("Isometric · 55°", () => ChooseCamera(Map.Camera with { Bearing = -35, Pitch = 55, Perspective = false }));
        AddCameraAction("Low skyline · 70°", () => ChooseCamera(Map.Camera with { Pitch = 70, Perspective = true }));
        AddCameraAction("Horizon · 88°", () => ChooseCamera(Map.Camera with { Pitch = 88, Perspective = true }));
        AddCameraAction("Bird’s-eye · 30°", () => ChooseCamera(Map.Camera with { Pitch = 30, Perspective = true }));
        var orbit = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (text, bearing, pitch) in new[] { ("Left", -15d, 0d), ("Right", 15d, 0d), ("Tilt -", 0d, -10d), ("Tilt +", 0d, 10d) })
        {
            var button = new Button { Content = text, MinHeight = 44, Padding = new Thickness(10, 8, 10, 8) };
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
        ShowPanel(PanelKind.Guide, "City guides", "Choose a city to explore its streets, skyline and terrain.");
        foreach (var place in Destinations) AddPlace(place);
    }

    private void OnHelp(object sender, RoutedEventArgs e)
    {
        ShowPanel(PanelKind.Help, "Map controls", "Place search by Photon / komoot with OpenStreetMap data.");
        AddHelp("Browse", "Drag or use a two-finger trackpad scroll to pan. Pinch over the map to zoom around your fingers or pointer. Mouse-wheel zoom stays on the map. Tap to inspect a location.");
        AddHelp("Change perspective", "Use the compass to face north, or switch between 2D and 3D on the map. Map appearance includes camera angles, orbit and lighting.");
        AddHelp("Keyboard", "Focus the map. Use arrows to pan, + and − to zoom, Q and E to rotate, P to switch projection, or Home to return to Manhattan.");
        AddHelp("Elevation", "Choose Elevation for altitude colors. Terrain relief works with Map and Earth too. Camera tools adjust vertical exaggeration from 1× to 4×. Elevation readouts remain in real metres above sea level.");
        AddHelp("Directions", "Choose Directions for a place. Add stops or try a vehicle demo. Preview, take control, or jump to a turn.");
        AddHelp("Free roam", "Choose Free roam to walk, drive, cycle, sail, pilot or fly from the map center. WASD moves, dragging looks around, Space jumps or brakes, and Page Up / Down changes flight altitude. Two independent touch sticks control movement and look.");
        AddHelp("Appearance", "Choose map layers switches Map, Earth and Elevation. Map appearance includes Pixel art, Normal, Minecraft and Roblox styles, plus lighting, terrain relief and Balanced, High or Ultra quality.");
        AddHelp("Search sheet", "On a phone, drag the handle or activate it to resize the sheet. Arrow keys, Home and End also change its height. Hide search panel leaves the map unobscured; Search brings it back.");
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

        ShowPanel(PanelKind.Search, "Search results", "Searching places…");
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
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 11, 12, 11),
            MinHeight = 60,
            Content = TextPair(place.Name, place.Detail),
            Tag = place
        };
        AutomationProperties.SetName(button, place.Name + ", " + place.Detail);
        button.Click += OnPick;
        Rows.Children.Add(button);
    }

    private StackPanel TextPair(string title, string detail)
    {
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = _ink, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 13, Foreground = _muted, TextWrapping = TextWrapping.Wrap });
        return text;
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MapPlace place }) NavigateTo(place);
    }

    private void OnMapSelectionChanged(object? sender, EventArgs e)
    {
        if (_gameOpen || _journeysOpen) return;
        if (Map.Selection is not { } selection)
        {
            _selectedPlace = null;
            PlaceEyebrow.Text = "Explore the map";
            Destination.Text = "Find your next stop";
            DestinationDetail.Text = "Search a place or explore a city.";
            return;
        }
        if (_selectedPlace is null || _selectedPlace.Latitude != selection.Latitude ||
            _selectedPlace.Longitude != selection.Longitude || _selectedPlace.Name != selection.Label)
            _selectedPlace = new MapPlace(selection.Label,
                FormattableString.Invariant($"{selection.Latitude:0.0000}°, {selection.Longitude:0.0000}°"),
                selection.Latitude, selection.Longitude, Map.Camera.Zoom);
        CloseDrawer();
        Destination.Text = _selectedPlace.Name;
        DestinationDetail.Text = _selectedPlace.Detail;
        PlaceEyebrow.Text = "Selected place";
        _sidebarHidden = false;
        if (_narrow) _sheetDetent = 1;
        LayoutChrome(ActualWidth, ActualHeight);
    }

    private void NavigateTo(MapPlace place)
    {
        CloseDrawer();
        _selectedPlace = place;
        Destination.Text = place.Name;
        DestinationDetail.Text = place.Detail;
        PlaceEyebrow.Text = "Selected place";
        _sidebarHidden = false;
        if (_narrow) _sheetDetent = 1;
        Map.SetMarker(place.Latitude, place.Longitude, place.Name);
        if (place == Destinations[^1])
        {
            SetBaseLayer(false, true);
            Map.FlyTo(new MapCamera(place.Latitude, place.Longitude, place.Zoom, -25, 58, true));
        }
        else Map.FlyTo(place.Latitude, place.Longitude, place.Zoom);
        LayoutChrome(ActualWidth, ActualHeight);
    }

    private void ShowPanel(PanelKind kind, string heading, string note)
    {
        MapInfoButton.Flyout?.Hide();
        LayersButton.Flyout?.Hide();
        bool wasHidden = _sidebarHidden;
        DisplayGame(false);
        DisplayJourneys(false);
        CancelRequest();
        if (_panel == PanelKind.Closed) _restoreHiddenAfterDrawer = wasHidden;
        _sidebarHidden = false;
        if (_narrow) _sheetDetent = 2;
        _panel = kind;
        Rows.Children.Clear();
        DrawerHeading.Text = heading;
        DrawerNote.Text = note;
        Drawer.Visibility = Visibility.Visible;
        LayoutChrome(ActualWidth, ActualHeight);
    }

    private void OnCloseDrawer(object sender, RoutedEventArgs e) => CloseDrawer();

    private void CloseDrawer()
    {
        CancelRequest();
        _panel = PanelKind.Closed;
        Drawer.Visibility = Visibility.Collapsed;
        Rows.Children.Clear();
        if (_restoreHiddenAfterDrawer) _sidebarHidden = true;
        _restoreHiddenAfterDrawer = false;
        LayoutChrome(ActualWidth, ActualHeight);
    }

    private void CancelRequest()
    {
        _request?.Cancel();
        _request = null;
    }
}
