using System.Globalization;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PixelMap.Controls;
using PixelMap.Data;
using PixelMap.Routing;
using PixelMap.Simulation;
using Windows.System;

namespace PixelMap.App;

public sealed partial class JourneyPanel : UserControl
{
    private sealed class Waypoint(RouteStop? place = null)
    {
        public RouteStop? Place { get; set; } = place;
        public string Query { get; set; } = "";
    }

    private readonly List<Waypoint> _stops = [new(), new()];
    private readonly List<RouteStep> _directions = [];
    private readonly SolidColorBrush _selected = new(ColorHelper.FromArgb(255, 215, 235, 215));
    private readonly SolidColorBrush _neutral = new(ColorHelper.FromArgb(255, 241, 242, 229));
    private readonly Dictionary<uint, PadAction> _pressed = [];
    // Uno's generated named-element references are weak. Retain the inactive pane.
    private readonly StackPanel _planningPane;
    private readonly StackPanel _playbackPane;
    private PixelMapControl _map = null!;
    private PhotonGeocoder _geocoder = null!;
    private RoutePlanner _planner = null!;
    private CancellationTokenSource? _lookup, _planning;
    private int _index = 1, _visibleSteps;
    private PixelMap.Routing.RouteTrack? _guidance;
    private IReadOnlyList<RoutePlan> _choices = Array.Empty<RoutePlan>();
    private bool _writingChoices;
    private bool _linked, _writingProgress, _settingMode, _driving, _trip;
    private long _lastTelemetry;

    private enum PadAction { Left, Throttle, Brake, Right, Climb, Descend }

    public bool IsPicking { get; private set; }
    public string PickingPrompt => $"Tap the map to set {Role(_index).ToLowerInvariant()}";
    public event EventHandler? PickingChanged;
    public event EventHandler? CloseRequested;

    public JourneyPanel()
    {
        InitializeComponent();
        _planningPane = PlanPane;
        _playbackPane = TripPane;
        JourneyBody.Children.Remove(_playbackPane);
        ModeBox.SelectedIndex = 0;
        Rate.SelectedIndex = 0;
        BindPad(SteerLeft, PadAction.Left);
        BindPad(SteerRight, PadAction.Right);
        BindPad(Gas, PadAction.Throttle);
        BindPad(Brake, PadAction.Brake);
        BindPad(Ascend, PadAction.Climb);
        BindPad(Descend, PadAction.Descend);
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) =>
        {
            Cancel(ref _lookup);
            Cancel(ref _planning);
            StopPicking();
            ReleasePad();
            if (!_linked) return;
            _map.MapPointTapped -= OnMapPoint;
            _map.Journey.Changed -= OnJourneyChanged;
            _linked = false;
        };
    }

    public void Attach(PixelMapControl map, PhotonGeocoder geocoder, RoutePlanner planner)
    {
        _map = map;
        _geocoder = geocoder;
        _planner = planner;
        Subscribe();
        RefreshStops();
        SetText(Note, "Choose a destination, or try a ready-to-play route with Demo.");
    }

    private void Subscribe()
    {
        if (_linked || _map is null) return;
        _map.MapPointTapped += OnMapPoint;
        _map.Journey.Changed += OnJourneyChanged;
        _linked = true;
    }

    public void Open(MapPlace? destination = null)
    {
        Subscribe();
        if (!_trip && _stops[^1].Place is null && destination is not null)
        {
            _stops[^1].Place = PlaceStop(destination);
            if (_stops[0].Place is null) _index = 0;
        }
        if (!_trip && _stops[0].Place is null && _stops[^1].Place is null)
            _stops[0].Place = new RouteStop("Map center", new RoutePoint(_map.Camera.Latitude, _map.Camera.Longitude));
        RefreshStops();
        SyncTrip(true);
    }

    public void Hide()
    {
        StopPicking();
        Cancel(ref _lookup);
        Cancel(ref _planning);
        ReleasePad();
    }

    public void LoadDemo(TravelMode mode)
    {
        Cancel(ref _planning);
        StopPicking();
        Cancel(ref _lookup);
        _settingMode = true;
        try { ModeBox.SelectedItem = ModeBox.Items.OfType<ComboBoxItem>().First(i => string.Equals(i.Tag?.ToString(), mode.ToString(), StringComparison.Ordinal)); }
        finally { _settingMode = false; }
        var route = RouteDemos.ForMode(mode);
        _stops.Clear();
        foreach (var stop in route.Stops) _stops.Add(new Waypoint(stop));
        _index = _stops.Count - 1;
        Query.Text = "";
        FoundPlaces.Children.Clear();
        ShowChoices([route]);
    }

    private TravelMode Mode => ModeBox.SelectedItem is ComboBoxItem item && Enum.TryParse<TravelMode>(item.Tag?.ToString(), out var mode) ? mode : TravelMode.Drive;
    private string Role(int i) => i == 0 ? "Start" : i == _stops.Count - 1 ? "Destination" : $"Via {i}";

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void OnDemo(object sender, RoutedEventArgs e) => LoadDemo(Mode);

    private void OnMode(object sender, SelectionChangedEventArgs e)
    {
        if (_map is null || _settingMode) return;
        InvalidateRoute();
        RefreshStops();
        SetText(Note, "Choose stops and find a route, or load a demo for this travel mode.");
    }

    private void RefreshStops()
    {
        SetText(ModeNote, Mode switch
        {
            TravelMode.Boat => "Routes follow mapped ferry and water alignments where connected. Times are estimates.",
            TravelMode.Train => "Routes trace mapped railway tracks. Service schedules and passenger access vary.",
            TravelMode.Flight => "Direct great-circle course between stops with estimated cruise time.",
            _ => "Street routing follows the selected mode and visits stops in order."
        });
        ExistingTripActions.Visibility = _map.Journey.Route is null ? Visibility.Collapsed : Visibility.Visible;
        StopRows.Children.Clear();
        for (var i = 0; i < _stops.Count; i++)
        {
            var role = Role(i);
            var title = _stops[i].Query.Length > 0 ? _stops[i].Query : _stops[i].Place?.Name ?? "Choose a place";
            var button = new Button
            {
                Tag = i,
                Content = new TextBlock { Text = $"{role}   ·   {title}", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 },
                Background = i == _index ? _selected : _neutral,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MinHeight = 34, Padding = new Thickness(9, 4, 9, 4), CornerRadius = new CornerRadius(4)
            };
            AutomationProperties.SetName(button, $"Set {role}: {title}");
            button.Click += OnSelectStop;
            StopRows.Children.Add(button);
        }
        RemoveStop.Visibility = _index > 0 && _index < _stops.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        SetText(StopTitle, "SET " + Role(_index).ToUpperInvariant());
        UpdatePlanEnabled();
    }

    private void OnSelectStop(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;
        SelectStop(index);
    }

    private void SelectStop(int i)
    {
        Cancel(ref _lookup);
        _index = i;
        Query.Text = _stops[i].Query;
        FoundPlaces.Children.Clear();
        RefreshStops();
        SetText(Note, $"Search or pick {Role(i).ToLowerInvariant()} on the map.");
    }

    private void OnAddStop(object sender, RoutedEventArgs e)
    {
        if (_stops.Count >= 24) { SetText(Note, "A journey can have up to twenty-four stops."); return; }
        InvalidateRoute();
        _stops.Insert(_stops.Count - 1, new Waypoint());
        SelectStop(_stops.Count - 2);
    }

    private void OnRemoveStop(object sender, RoutedEventArgs e)
    {
        if (_index <= 0 || _index >= _stops.Count - 1) return;
        InvalidateRoute();
        _stops.RemoveAt(_index);
        SelectStop(Math.Min(_index, _stops.Count - 1));
    }

    private void OnReverse(object sender, RoutedEventArgs e)
    {
        InvalidateRoute();
        _stops.Reverse();
        SelectStop(_stops.Count - 1 - _index);
    }

    private void Assign(RouteStop stop)
    {
        InvalidateRoute();
        Cancel(ref _lookup);
        _stops[_index].Place = stop;
        _stops[_index].Query = "";
        Query.Text = "";
        FoundPlaces.Children.Clear();
        SetText(Note, $"{Role(_index)} set to {stop.Name}.");
        var missing = _stops.FindIndex(s => s.Place is null);
        if (missing >= 0) { _index = missing; Query.Text = _stops[missing].Query; }
        RefreshStops();
    }

    private void OnUseCenter(object sender, RoutedEventArgs e) =>
        Assign(new RouteStop("Map center", new RoutePoint(_map.Camera.Latitude, _map.Camera.Longitude)));

    private void OnPickMap(object sender, RoutedEventArgs e)
    {
        Cancel(ref _lookup);
        Cancel(ref _planning);
        IsPicking = _map.IsMapPointSelectionEnabled = true;
        PickingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StopPicking()
    {
        if (!IsPicking) return;
        IsPicking = _map.IsMapPointSelectionEnabled = false;
        PickingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMapPoint(object? sender, MapPointEventArgs e)
    {
        if (!IsPicking) return;
        Assign(new RouteStop(FormattableString.Invariant($"{e.Latitude:0.0000}°, {e.Longitude:0.0000}°"), new RoutePoint(e.Latitude, e.Longitude)));
        StopPicking();
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void OnSearchKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private void OnSearchEdited(object sender, TextChangedEventArgs e)
    {
        Cancel(ref _lookup);
        FoundPlaces?.Children.Clear();
        if (_index < _stops.Count && _stops[_index].Query != Query.Text.Trim())
        {
            if (_planning is not null) SetText(Note, "Stops changed. Find a route to apply your edits.");
            Cancel(ref _planning);
            _stops[_index].Query = Query.Text.Trim();
        }
        if (PlanAction is not null) UpdatePlanEnabled();
    }

    private void UpdatePlanEnabled() => SetEnabled(PlanAction,
        _planning is null && _stops.All(p => p.Place is not null || p.Query.Length > 0));

    private async Task SearchAsync()
    {
        string query = Query.Text.Trim();
        if (query.Length == 0) { SetText(Note, "Enter a place name or latitude, longitude."); return; }
        if (PhotonGeocoder.TryParseCoordinates(query, out var pin)) { Assign(PlaceStop(pin)); return; }
        Cancel(ref _lookup);
        var request = _lookup = new CancellationTokenSource();
        FoundPlaces.Children.Clear();
        SetText(Note, "Searching places…");
        try
        {
            var places = await _geocoder.SearchAsync(query, request.Token);
            if (request.IsCancellationRequested) return;
            foreach (var place in places)
            {
                string label = $"{place.Name} · {place.Detail}";
                var button = new Button
                {
                    Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                    Tag = place, Padding = new Thickness(7, 4, 7, 4), HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left, Background = _neutral
                };
                AutomationProperties.SetName(button, label);
                button.Click += (_, _) => Assign(PlaceStop(place));
                FoundPlaces.Children.Add(button);
            }
            SetText(Note, places.Count > 0 ? "Select a search result." : "No matches. Try another name or pick on the map.");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            if (!request.IsCancellationRequested) SetText(Note, "Live search is unavailable. Enter coordinates or pick on the map.");
        }
        finally { if (ReferenceEquals(_lookup, request)) _lookup = null; request.Dispose(); }
    }

    private static RouteStop PlaceStop(MapPlace place) => new(place.Name, new RoutePoint(place.Latitude, place.Longitude));

    private async void OnPlan(object sender, RoutedEventArgs e)
    {
        if (_planning is not null) return;
        for (int i = 0; i < _stops.Count; i++)
        {
            var draft = _stops[i];
            if (draft.Query.Length == 0) continue;
            if (PhotonGeocoder.TryParseCoordinates(draft.Query, out var place))
            {
                draft.Place = PlaceStop(place);
                draft.Query = "";
                if (i == _index) Query.Text = "";
            }
            else
            {
                SelectStop(i);
                await SearchAsync();
                return;
            }
        }
        if (_stops.Any(s => s.Place is null)) return;
        Cancel(ref _lookup);
        var request = _planning = new CancellationTokenSource();
        SetEnabled(PlanAction, false);
        SetText(Note, "Finding a route…");
        try
        {
            var stops = _stops.Select(s => s.Place!).ToArray();
            var plans = await _planner.PlanChoicesAsync(Mode, stops, request.Token);
            if (!request.IsCancellationRequested) ShowChoices(plans);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is RouteUnavailableException or HttpRequestException or OperationCanceledException or ArgumentException or InvalidDataException)
        {
            if (!request.IsCancellationRequested)
                SetText(Note, ex is RouteUnavailableException ? ex.Message : "Couldn't find this route. Adjust the stops, retry or try a demo.");
        }
        finally
        {
            if (ReferenceEquals(_planning, request)) _planning = null;
            request.Dispose();
            RefreshStops();
        }
    }

    private void ShowChoices(IReadOnlyList<RoutePlan> plans)
    {
        if (plans.Count == 0) { SetText(Note, "No route was found between these stops."); return; }
        _choices = plans;
        SetText(SingleChoiceText, $"{Duration(plans[0].Duration)} · {Distance(plans[0].DistanceMeters)}");
        SingleChoice.Visibility = plans.Count > 1 ? Visibility.Collapsed : Visibility.Visible;
        _writingChoices = true;
        try
        {
            Alternatives.Items.Clear();
            if (plans.Count > 1)
            {
                for (int i = 0; i < plans.Count; i++)
                {
                    var plan = plans[i];
                    var label = i == 0 ? "Recommended" : $"Option {i + 1}";
                    Alternatives.Items.Add($"{label} · {Duration(plan.Duration)} · {Distance(plan.DistanceMeters)}");
                }
                Alternatives.SelectedIndex = 0;
            }
            Alternatives.Visibility = plans.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _writingChoices = false; }
        ShowPlan(plans[0]);
    }

    private void OnChoice(object sender, SelectionChangedEventArgs e)
    {
        if (!Alternatives.IsDropDownOpen) ApplyChoice();
    }

    private void OnChoicesClosed(object sender, object e) => ApplyChoice();

    private void ApplyChoice()
    {
        int index = Alternatives.SelectedIndex;
        if (!_writingChoices && (uint)index < _choices.Count && !ReferenceEquals(_choices[index], _map.Journey.Route))
            ShowPlan(_choices[index]);
    }

    private void ShowPlan(RoutePlan route)
    {
        StopPicking();
        ReleasePad();
        _trip = true;
        _driving = false;
        _guidance = new PixelMap.Routing.RouteTrack(route);
        _directions.Clear();
        _visibleSteps = 0;
        foreach (var leg in route.Legs) _directions.AddRange(leg.Steps);
        StepRows.Children.Clear();
        StepRows.Visibility = Visibility.Collapsed;
        StepButton.Content = $"Directions ({_directions.Count})";
        SetText(RouteSource, $"{(route.IsEstimate ? "Estimated journey · " : "")}{route.Source}");
        FixMap.Visibility = route.Source.Contains("OSRM", StringComparison.OrdinalIgnoreCase) ||
            route.Source.Contains("FOSSGIS", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        SetText(RouteSummary, $"{route.Stops[0].Name} to {route.Stops[^1].Name}");
        RouteSummary.MaxLines = 2;
        SetText(ModeBadge, route.Mode switch
        {
            TravelMode.Walk => "WALKING", TravelMode.Bike => "BICYCLE", TravelMode.Drive => "CAR",
            TravelMode.Boat => "BOAT", TravelMode.Train => "TRAIN", _ => "AIRCRAFT"
        });
        SetText(TripCaveat, route.Mode switch
        {
            TravelMode.Boat => "Mapped water route. Times and service availability vary.",
            TravelMode.Train => "Mapped track route. Check passenger services and schedules separately.",
            TravelMode.Flight => "Great-circle sightseeing estimate between stops.",
            _ => ""
        });
        TripCaveatGroup.Visibility = TripCaveat.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SetText(Note, $"{Distance(route.DistanceMeters)} · {Duration(route.Duration)} · {route.Legs.Count} leg{(route.Legs.Count == 1 ? "" : "s")}");
        _map.ShowRoute(route);
        ShowTrip(true);
        SyncTrip(true);
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        _map.Journey.Pause();
        ShowTrip(false);
        RefreshStops();
        SetText(Note, "Change stops or travel mode, then find a route.");
    }

    private void OnBackToTrip(object sender, RoutedEventArgs e)
    {
        if (_map.Journey.Route is null) return;
        ShowTrip(true);
        SyncTrip(true);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        InvalidateRoute();
        _stops.Clear();
        _stops.Add(new Waypoint(new RouteStop("Map center", new RoutePoint(_map.Camera.Latitude, _map.Camera.Longitude))));
        _stops.Add(new Waypoint());
        ShowTrip(false);
        SelectStop(1);
    }

    private void ShowTrip(bool show)
    {
        _trip = show;
        var pane = show ? _playbackPane : _planningPane;
        if (!ReferenceEquals(JourneyBody.Children.FirstOrDefault(), pane))
        {
            JourneyBody.Children.Clear();
            JourneyBody.Children.Add(pane);
        }
        if (!show) ManualPad.Visibility = Visibility.Collapsed;
        SetText(PanelTitle, show ? "Travel controls" : "Route planner");
        ScrollBody.ChangeView(null, 0, null, true);
    }

    private void InvalidateRoute()
    {
        Cancel(ref _planning);
        if (_map.Journey.Route is not null) _map.ShowRoute(null, false);
        if (_trip) ShowTrip(false);
        _directions.Clear();
        _choices = Array.Empty<RoutePlan>();
        _guidance = null;
        _driving = false;
    }

    private void OnSteps(object sender, RoutedEventArgs e)
    {
        StepRows.Visibility = StepRows.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (StepRows.Visibility == Visibility.Visible && _visibleSteps == 0) AddDirections();
    }

    private void AddDirections()
    {
        if (StepRows.Children.LastOrDefault() is Button { Tag: "more" } last) StepRows.Children.Remove(last);
        int end = Math.Min(_visibleSteps + 16, _directions.Count);
        for (; _visibleSteps < end; _visibleSteps++)
        {
            var step = _directions[_visibleSteps];
            var button = new Button
            {
                Content = new TextBlock { Text = $"{_visibleSteps + 1}. {step.Instruction} · {Distance(step.DistanceMeters)}", TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                Padding = new Thickness(6, 3, 6, 3), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Background = _neutral
            };
            button.Click += (_, _) => GoTo(step.Point);
            StepRows.Children.Add(button);
        }
        if (_visibleSteps == _directions.Count) return;
        var more = new Button { Tag = "more", Content = "More directions…" };
        more.Click += (_, _) => AddDirections();
        StepRows.Children.Add(more);
    }

    private void OnStart(object sender, RoutedEventArgs e) { if (_map.Journey.Route is { } r) GoTo(r.Stops[0].Point); }
    private void OnEnd(object sender, RoutedEventArgs e) { if (_map.Journey.Route is { } r) GoTo(r.Stops[^1].Point); }
    private void GoTo(RoutePoint point)
    {
        _map.Journey.Follow = false;
        _map.FlyTo(point.Latitude, point.Longitude, Math.Max(15, _map.Camera.Zoom));
        SyncTrip(true);
    }

    private void OnFit(object sender, RoutedEventArgs e) { _map.Journey.Follow = false; _map.FitRoute(); SyncTrip(true); }
    private void OnPreview(object sender, RoutedEventArgs e) { ReleasePad(); _driving = false; _map.Journey.Preview(); SyncTrip(true); }
    private void OnDrive(object sender, RoutedEventArgs e) { _driving = true; _map.Journey.Drive(); _map.Focus(FocusState.Programmatic); SyncTrip(true); }
    private void OnPause(object sender, RoutedEventArgs e)
    {
        if (_map.Journey.State == JourneyState.Paused) _map.Journey.Resume(); else { ReleasePad(); _map.Journey.Pause(); }
        SyncTrip(true);
    }
    private void OnReset(object sender, RoutedEventArgs e) { ReleasePad(); _driving = false; _map.Journey.Reset(); SyncTrip(true); }
    private void OnRig(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || !Enum.TryParse<JourneyCameraRig>(item.Tag?.ToString(), out var rig)) return;
        _map.CameraRig = rig;
        _map.Journey.Follow = true;
        ToolTipService.SetToolTip(CameraButton, item.Text + " camera");
        SyncTrip(true);
    }

    private void OnRecover(object sender, RoutedEventArgs e) { ReleasePad(); _map.Journey.Respawn(); SyncTrip(true); }
    private void OnSignal(object sender, RoutedEventArgs e) { _map.Journey.Muted = false; _map.SoundCue(); SyncTrip(true); }
    private void OnCruise(object sender, RoutedEventArgs e) { _map.Journey.SetCruise(Cruise.IsChecked == true); SyncTrip(true); }
    private void OnFollow(object sender, RoutedEventArgs e) { _map.Journey.Follow = Follow.IsChecked == true; SyncTrip(true); }
    private void OnSound(object sender, RoutedEventArgs e) { _map.Journey.Muted = Sound.IsChecked != true; SyncTrip(true); }

    private void OnSeek(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_writingProgress || _map?.Journey.Route is null) return;
        _driving = false;
        _map.Journey.Seek(e.NewValue / 1000);
        SyncTrip(true);
    }

    private void OnRate(object sender, SelectionChangedEventArgs e)
    {
        if (_map is not null && Rate.SelectedItem is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out var rate))
            _map.Journey.PlaybackRate = rate;
    }

    private void OnJourneyChanged(object? sender, EventArgs e) => SyncTrip(false);

    private void SyncTrip(bool force)
    {
        if (!_trip || (!force && Visibility != Visibility.Visible) || _map?.Journey.Route is not { } route) return;
        var now = Environment.TickCount64;
        if (!force && now - _lastTelemetry < 100) return;
        _lastTelemetry = now;
        var j = _map.Journey;
        _writingProgress = true;
        try { ProgressSlider.Value = Math.Clamp(j.Progress, 0, 1) * 1000; }
        finally { _writingProgress = false; }
        Follow.IsChecked = j.Follow;
        Sound.IsChecked = !j.Muted;
        Cruise.IsChecked = j.Cruise;
        SetText(PositionText, $"{Math.Clamp(j.Progress, 0, 1):P0}");
        bool manual = _driving && j.State is JourneyState.Driving or JourneyState.Paused;
        SetText(PanelTitle, j.State switch
        {
            JourneyState.Preview => "Route preview", JourneyState.Driving => "Manual control",
            JourneyState.Paused => "Journey paused", JourneyState.Finished => "Destination reached", _ => "Trip ready"
        });
        foreach (var control in new Control[] { Gas, Brake, Ascend, Descend, Cruise }) SetEnabled(control, j.State == JourneyState.Driving);
        SetEnabled(SteerLeft, j.State == JourneyState.Driving && route.Mode != TravelMode.Train);
        SetEnabled(SteerRight, j.State == JourneyState.Driving && route.Mode != TravelMode.Train);
        ManualPad.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        SetEnabled(PauseButton, j.State is JourneyState.Driving or JourneyState.Preview or JourneyState.Paused);
        PauseButton.Content = j.State == JourneyState.Paused ? "Resume" : "Pause";
        DriveButton.Content = route.Mode switch { TravelMode.Flight => "Pilot", TravelMode.Boat => "Helm", TravelMode.Train => "Operate", TravelMode.Walk => "Walk", TravelMode.Bike => "Ride", _ => "Drive" };
        var altitude = route.Mode == TravelMode.Flight
            ? $" · {j.AltitudeMeters:0} m elevation · {j.AltitudeAboveGroundMeters:0} m clearance"
            : _map is { IsTerrainEnabled: true } ? $" · {j.GroundElevationMeters:0} m elevation" : "";
        var motion = !j.IsScoredRun && j.State is JourneyState.Preview or JourneyState.Paused ? "Tour" : $"{j.SpeedMetersPerSecond * 3.6:0} km/h";
        var telemetry = $"{motion} · {Distance(j.DistanceMeters)} / {Distance(route.DistanceMeters)}{altitude}";
        if (manual || j.State == JourneyState.Finished)
            telemetry += $" · {j.CheckpointsPassed}/{j.CheckpointsTotal} gates · {j.Score:0} pts · {(int)(j.ElapsedSeconds / 60)}:{(int)(j.ElapsedSeconds % 60):00}";
        if (manual) telemetry += $" · next gate {Distance(j.NextCheckpointMeters)}";
        if (manual && j.OffRouteMeters > 30) telemetry += $" · {Distance(j.OffRouteMeters)} off route";
        SetText(Telemetry, telemetry);
        if (j.State == JourneyState.Finished) SetText(TripInstruction, "Destination reached");
        else if (_guidance is { } guide)
        {
            var turn = guide.GuidanceAt(j.DistanceMeters);
            var prefix = route.Legs.Count > 1 ? $"Leg {turn.LegIndex + 1} of {route.Legs.Count} · " : "";
            var instruction = prefix + (turn.Current?.Instruction ?? "Follow the highlighted route");
            if (turn.Next is { } next)
                instruction += $"\nIn {Distance(turn.MetersToNext)} · {next.Instruction}";
            SetText(TripInstruction, instruction);
        }
        Ascend.Visibility = Descend.Visibility = route.Mode == TravelMode.Flight ? Visibility.Visible : Visibility.Collapsed;
        SetText(DrivingTip, route.Mode == TravelMode.Flight ? "W/S speed · A/D turn · use Climb and Descend." :
            route.Mode == TravelMode.Train ? "Regulate speed along the track. Space brakes." : "W/S speed · A/D steer · Space brakes. Hold the buttons below on touch.");
    }

    private void BindPad(Button button, PadAction action)
    {
        button.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            _pressed[e.Pointer.PointerId] = action;
            button.CapturePointer(e.Pointer);
            SendPad();
            e.Handled = true;
        }), true);
        void End(object sender, PointerRoutedEventArgs e)
        {
            if (!_pressed.Remove(e.Pointer.PointerId)) return;
            button.ReleasePointerCapture(e.Pointer);
            SendPad();
            e.Handled = true;
        }
        button.AddHandler(PointerReleasedEvent, new PointerEventHandler(End), true);
        button.AddHandler(PointerCanceledEvent, new PointerEventHandler(End), true);
        button.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(End), true);
    }

    private void ReleasePad()
    {
        if (_pressed.Count == 0) return;
        _pressed.Clear();
        SendPad();
    }

    private void SendPad()
    {
        if (_map is null) return;
        double Press(PadAction a) => _pressed.ContainsValue(a) ? 1 : 0;
        _map.Journey.SetInput(Press(PadAction.Throttle), Press(PadAction.Right) - Press(PadAction.Left), Press(PadAction.Brake));
        _map.Journey.SetLift(Press(PadAction.Climb) - Press(PadAction.Descend));
    }

    private static void SetText(TextBlock element, string text)
    {
        if (element.Text == text) return;
        element.Text = text;
        if (element.Parent is Border readout && AutomationProperties.GetAccessibilityView(element) == AccessibilityView.Raw)
        {
            AutomationProperties.SetName(readout, text);
            return;
        }
        FrameworkElementAutomationPeer.FromElement(element)?.InvalidatePeer();
    }

    private static void SetEnabled(Control element, bool enabled)
    {
        if (element.IsEnabled == enabled) return;
        element.IsEnabled = enabled;
        FrameworkElementAutomationPeer.FromElement(element)?.InvalidatePeer();
    }

    private static string Distance(double meters) => meters < 1000 ? $"{meters:0} m" : $"{meters / 1000:0.##} km";
    private static string Duration(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min" : $"{Math.Max(1, (int)Math.Ceiling(time.TotalMinutes))} min";
    private static void Cancel(ref CancellationTokenSource? source) { var old = source; source = null; old?.Cancel(); }
}
