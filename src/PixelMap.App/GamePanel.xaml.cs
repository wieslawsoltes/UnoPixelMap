using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PixelMap.Controls;
using PixelMap.Rendering;
using PixelMap.Routing;
using PixelMap.Simulation;

namespace PixelMap.App;

public sealed partial class GamePanel : UserControl
{
    private readonly Dictionary<uint, PadAction> _pressed = [];
    private readonly DispatcherTimer _lookClock = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _pulseClock = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private PixelMapControl? _map;
    private bool _shown, _syncing;
    private long _lookTimestamp, _lastTelemetry, _lastPadPointer;
    private SessionViewState? _displayedSession;
    private readonly record struct SessionViewState(JourneyState State, TravelMode Mode, JourneyCameraRig Rig,
        bool FreeRoam, bool FreeFlight, bool Muted);
    private enum PadAction { JumpBrake, Boost, Climb, Descend }

    public event EventHandler? CloseRequested;

    public GamePanel()
    {
        InitializeComponent();
        MoveStick.ValueChanged += (_, _) => SendInput();
        LookStick.ValueChanged += (_, _) => UpdateLookClock();
        _lookClock.Tick += (_, _) => AdvanceLook();
        _pulseClock.Tick += (_, _) =>
        {
            _pulseClock.Stop();
            _pressed.Remove(uint.MaxValue);
            SendInput();
        };
        BindPad(JumpBrake, PadAction.JumpBrake);
        BindPad(Boost, PadAction.Boost);
        BindPad(Climb, PadAction.Climb);
        BindPad(Descend, PadAction.Descend);
        SizeChanged += (_, _) => LayoutControls();
        Unloaded += (_, _) => ReleaseInput();
    }

    public void Attach(PixelMapControl map)
    {
        if (_map is not null)
        {
            _map.Journey.Changed -= OnJourneyChanged;
            _map.StatusChanged -= OnMapChanged;
            _map.CameraChanged -= OnCameraUpdated;
        }
        _map = map;
        map.Journey.Changed += OnJourneyChanged;
        map.StatusChanged += OnMapChanged;
        map.CameraChanged += OnCameraUpdated;
    }

    public void Open()
    {
        if (_map is null) return;
        _shown = true;
        if (!_map.Journey.IsFreeRoaming) _map.IsTerrainEnabled = true;
        SyncChoices();
        SetExpanded(!_map.Journey.IsFreeRoaming);
        SyncSession(true);
        LayoutControls();
    }

    public void Hide()
    {
        _shown = false;
        ReleaseInput();
    }

    private void LayoutControls()
    {
        double inset = ActualWidth < 600 ? 8 : 16;
        bool compact = ActualWidth >= 600 && ActualHeight < 360;
        bool compactHud = compact && Setup.Visibility != Visibility.Visible;
        SettingsCard.HorizontalAlignment = compact && !compactHud ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        SettingsCard.Margin = new Thickness(inset, compactHud ? 6 : inset, inset, inset);
        SettingsCard.Padding = new Thickness(12, compactHud ? 3 : 9, 12, compactHud ? 3 : 9);
        SettingsContent.Spacing = compactHud ? 3 : 7;
        SettingsCard.Width = Math.Max(180, Math.Min(326, ActualWidth - 2 * inset));
        SettingsCard.MaxHeight = Math.Max(120, ActualHeight - 2 * inset - (compact ? 64 : 0));
        TouchPad.Margin = new Thickness(inset, 0, inset, 8);
        PadActions.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
        FlightActions.Orientation = ActualWidth < 370 ? Orientation.Vertical : Orientation.Horizontal;
    }

    private string SelectedMode => (ModeChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Walk";
    private bool SelectedFreeFlight => SelectedMode == "FreeFlight";

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || StartButton is null) return;
        StartButton.Content = SelectedMode switch
        {
            "Walk" => "Start walking here", "Drive" => "Drive from here", "Bike" => "Ride from here",
            "Boat" => "Sail from here", "Flight" => "Pilot from here", "Train" => "Join nearby tracks", _ => "Fly freely from here"
        };
        ControlsHint.Text = SelectedMode switch
        {
            "Walk" => "WASD moves · drag to look · Space jumps · Shift runs. Use both sticks on touch.",
            "FreeFlight" => "WASD moves · drag to look · Page Up / Down changes height · Shift boosts.",
            "Flight" => "W/S throttle · A/D turn · Page Up / Down climbs or descends · Space brakes.",
            "Train" => "W/S forward and reverse · Space brakes. The train follows nearby mapped tracks as they load.",
            _ => "W/S forward and reverse · A/D steers · Space brakes · drag to look around."
        };
        CameraChoice.SelectedIndex = SelectedFreeFlight || SelectedMode == "Walk" ? 1 : 0;
        CameraChoice.IsEnabled = !SelectedFreeFlight;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (_map is null) return;
        ReleaseInput();
        var selectedRig = JourneyCameraRig.FirstPerson;
        if (CameraChoice.SelectedItem is ComboBoxItem cameraItem &&
            Enum.TryParse<JourneyCameraRig>(cameraItem.Tag?.ToString(), out var chosenRig)) selectedRig = chosenRig;
        _map.IsTerrainEnabled = Relief.IsOn;
        if (SelectedFreeFlight) _map.StartFreeFlight();
        else if (Enum.TryParse<TravelMode>(SelectedMode, out var mode)) _map.StartFreeRoam(mode);
        _map.CameraRig = selectedRig;
        _map.Journey.Follow = true;
        SetExpanded(false);
        _map.Focus(FocusState.Programmatic);
        SyncSession(true);
    }

    private void OnCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) ApplyCamera();
    }

    private void ApplyCamera()
    {
        if (_map?.Journey.IsFreeRoaming == true && CameraChoice.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<JourneyCameraRig>(item.Tag?.ToString(), out var rig))
        {
            _map.CameraRig = rig;
            _map.Journey.Follow = true;
            SyncSession(true);
        }
    }

    private void OnStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && _map is not null && StyleChoice.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<MapRenderStyle>(item.Tag?.ToString(), out var style)) _map.RenderStyle = style;
    }

    private void OnLayerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _map is null || LayerChoice.SelectedItem is not ComboBoxItem item) return;
        _map.IsElevationLayerEnabled = item.Tag?.ToString() == "Elevation";
        _map.IsEarthImageryEnabled = item.Tag?.ToString() == "Earth";
    }

    private void OnReliefChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing && _shown && _map is not null) _map.IsTerrainEnabled = Relief.IsOn;
    }

    private void OnMapChanged(object? sender, EventArgs e)
    {
        if (_shown) SyncChoices();
    }

    private void OnCameraUpdated(object? sender, EventArgs e)
    {
        if (_shown && _map?.CameraRig != _displayedSession?.Rig) SyncSession(true);
    }

    private void SyncChoices()
    {
        if (_map is null) return;
        _syncing = true;
        try
        {
            StyleChoice.SelectedIndex = (int)_map.RenderStyle;
            LayerChoice.SelectedIndex = _map.IsElevationLayerEnabled ? 2 : _map.IsEarthImageryEnabled ? 1 : 0;
            Relief.IsOn = _map.IsTerrainEnabled;
        }
        finally { _syncing = false; }
    }

    private void OnToggleSettings(object sender, RoutedEventArgs e) => SetExpanded(Setup.Visibility != Visibility.Visible);

    private void SetExpanded(bool expanded)
    {
        Setup.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SettingsToggle.Content = expanded ? "−" : "Settings";
        SettingsToggle.Width = expanded ? 34 : 76;
        AutomationProperties.SetName(SettingsToggle, expanded ? "Collapse game settings" : "Expand game settings");
        LayoutControls();
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        if (_map?.Journey.IsFreeRoaming != true) return;
        ReleaseInput();
        if (_map.Journey.State == JourneyState.Paused) _map.Journey.Resume();
        else _map.Journey.Pause();
        _map.Focus(FocusState.Programmatic);
        SyncSession(true);
    }

    private void OnRespawn(object sender, RoutedEventArgs e)
    {
        if (_map?.Journey.IsFreeRoaming != true) return;
        ReleaseInput();
        _map.Journey.Reset();
        _map.Focus(FocusState.Programmatic);
        SyncSession(true);
    }

    private void OnSound(object sender, RoutedEventArgs e)
    {
        if (_map is not null) _map.Journey.Muted = Sound.IsChecked != true;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        ReleaseInput();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnJourneyChanged(object? sender, EventArgs e) => SyncSession(false);

    private void SyncSession(bool force)
    {
        if (!_shown || _map is null) return;
        var session = _map.Journey;
        bool active = session.IsFreeRoaming;
        bool paused = session.State == JourneyState.Paused;
        var view = new SessionViewState(session.State, session.Vehicle.Mode, _map.CameraRig,
            active, session.IsFreeFlying, session.Muted);
        bool transition = _displayedSession != view;
        // A pause can stop the simulation before the next telemetry tick arrives.
        // Release captured controls immediately, including keyboard/blur transitions.
        if (!active || paused) ReleaseInput();
        long now = Environment.TickCount64;
        if (!force && !transition && now - _lastTelemetry < 100) return;
        _lastTelemetry = now;
        _displayedSession = view;
        if (active && transition)
        {
            _syncing = true;
            try
            {
                foreach (ComboBoxItem item in CameraChoice.Items)
                    if (item.Tag?.ToString() == _map.CameraRig.ToString()) { CameraChoice.SelectedItem = item; break; }
            }
            finally { _syncing = false; }
        }
        if (force || transition)
        {
            TouchPad.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            Transport.Visibility = TelemetryBox.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            TouchPad.IsHitTestVisible = active && !paused;
            TouchPad.Opacity = paused ? .45 : 1;
            string title = !active ? "Free roam" : paused ? "Game paused" : session.IsFreeFlying ? "Free flight"
                : session.Vehicle.Mode switch { TravelMode.Walk => "Walking", TravelMode.Drive => "Driving", TravelMode.Bike => "Cycling", TravelMode.Boat => "Sailing", TravelMode.Train => "Train", _ => "Flying" };
            SetText(Title, title);
            PauseButton.Content = paused ? "Resume" : "Pause";
            AutomationProperties.SetName(PauseButton, paused ? "Resume free roam" : "Pause free roam");
            Sound.IsChecked = !session.Muted;
            bool walking = session.Vehicle.Mode == TravelMode.Walk && !session.IsFreeFlying;
            JumpBrake.Content = walking ? "Jump" : "Brake";
            FlightActions.Visibility = session.IsFreeFlying || session.Vehicle.Mode == TravelMode.Flight ? Visibility.Visible : Visibility.Collapsed;
            Reticle.Visibility = active && (session.IsFreeFlying || _map.CameraRig is JourneyCameraRig.FirstPerson or JourneyCameraRig.Cockpit)
                ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!active) return;
        var telemetry = FormattableString.Invariant($"{session.SpeedMetersPerSecond * 3.6:0.0} km/h · {session.AltitudeMeters:0.0} m elevation · {session.AltitudeAboveGroundMeters:0.0} m clearance\n{session.Latitude:0.00000}°, {session.Longitude:0.00000}° · {session.RunDistanceMeters:0} m travelled");
        if (session.IsWaitingForTrack) telemetry += " · waiting for nearby mapped tracks";
        if (session.IsWaitingForWater) telemetry += " · waiting for navigable water";
        if (Telemetry.Text == telemetry) return;
        Telemetry.Text = telemetry;
        AutomationProperties.SetName(TelemetryBox, "Free roam telemetry · " + telemetry.Replace('\n', ' '));
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

    private void BindPad(Button button, PadAction action)
    {
        button.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (_map?.Journey.IsFreeRoaming != true || !_map.Journey.Active) return;
            _lastPadPointer = Environment.TickCount64;
            _pressed[e.Pointer.PointerId] = action;
            button.CapturePointer(e.Pointer);
            SendInput();
            e.Handled = true;
        }), true);
        void Release(object sender, PointerRoutedEventArgs e)
        {
            _lastPadPointer = Environment.TickCount64;
            if (!_pressed.Remove(e.Pointer.PointerId)) return;
            button.ReleasePointerCapture(e.Pointer);
            SendInput();
            e.Handled = true;
        }
        button.AddHandler(PointerReleasedEvent, new PointerEventHandler(Release), true);
        button.AddHandler(PointerCanceledEvent, new PointerEventHandler(Release), true);
        button.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Release), true);
        button.Click += (_, _) =>
        {
            // Keyboard and assistive activation provide a finite input pulse.
            if (_map?.Journey.IsFreeRoaming != true || !_map.Journey.Active || Environment.TickCount64 - _lastPadPointer < 250) return;
            _pressed[uint.MaxValue] = action;
            SendInput();
            _pulseClock.Stop();
            _pulseClock.Start();
        };
    }

    private void SendInput()
    {
        if (_map is null) return;
        bool held(PadAction action) => _pressed.ContainsValue(action);
        bool walking = _map.Journey.Vehicle.Mode == TravelMode.Walk && !_map.Journey.IsFreeFlying;
        bool strafe = walking || _map.Journey.IsFreeFlying;
        _map.SetFreeRoamInput(new FreeRoamInput(
            Forward: -MoveStick.Value.Y,
            Strafe: strafe ? MoveStick.Value.X : 0,
            Turn: strafe ? 0 : MoveStick.Value.X,
            Vertical: (held(PadAction.Climb) ? 1 : 0) - (held(PadAction.Descend) ? 1 : 0),
            Brake: !walking && held(PadAction.JumpBrake) ? 1 : 0,
            Jump: walking && held(PadAction.JumpBrake),
            Boost: held(PadAction.Boost)));
    }

    private void UpdateLookClock()
    {
        bool moving = Math.Abs(LookStick.Value.X) + Math.Abs(LookStick.Value.Y) > 0;
        if (moving && !_lookClock.IsEnabled)
        {
            _lookTimestamp = Stopwatch.GetTimestamp();
            _lookClock.Start();
        }
        else if (!moving) _lookClock.Stop();
    }

    private void AdvanceLook()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Clamp(Stopwatch.GetElapsedTime(_lookTimestamp, now).TotalSeconds, 0, .1);
        _lookTimestamp = now;
        if (_map?.Journey.IsFreeRoaming == true && _map.Journey.Active)
            _map.LookFreeRoam(LookStick.Value.X * 120 * dt, -LookStick.Value.Y * 90 * dt);
        else ReleaseInput();
    }

    private void ReleaseInput()
    {
        _lookClock.Stop();
        _pulseClock.Stop();
        MoveStick.Reset();
        LookStick.Reset();
        _pressed.Clear();
        _map?.SetFreeRoamInput(default);
    }
}
