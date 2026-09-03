using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShinyGo60.Companion.Core.Configuration;
using ShinyGo60.Companion.Core.Presentation;
using ShinyGo60.Companion.Core.Sessions;
using ShinyGo60.Companion.Core.Shortcuts;
using ShinyGo60.Protocol.Manifests;

namespace ShinyGo60.Companion;

public partial class MainWindow : Window
{
    private static readonly Brush CurrentBackground = CreateBrush(0xD9, 0xF3, 0xE8);
    private static readonly Brush CurrentForeground = CreateBrush(0x12, 0x65, 0x43);
    private static readonly Brush StaleBackground = CreateBrush(0xFF, 0xED, 0xC7);
    private static readonly Brush StaleForeground = CreateBrush(0x8A, 0x55, 0x12);
    private static readonly Brush DisconnectedBackground = CreateBrush(0xE7, 0xEA, 0xEE);
    private static readonly Brush DisconnectedForeground = CreateBrush(0x45, 0x4D, 0x58);
    private bool allowClose;

    public MainWindow(
        LayoutManifest manifest,
        ResolvedCompanionConfiguration configuration,
        bool startWithWindows,
        string diagnosticPath)
        : this(
            manifest,
            configuration,
            [new WidgetTaskbarOption("Primary taskbar", WidgetTaskbarSelection.Primary, IsPrimary: true)],
            startWithWindows,
            diagnosticPath)
    {
    }

    public MainWindow(
        LayoutManifest manifest,
        ResolvedCompanionConfiguration configuration,
        IReadOnlyList<WidgetTaskbarOption> widgetTaskbarOptions,
        bool startWithWindows,
        string diagnosticPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(widgetTaskbarOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticPath);

        this.AvailableActions =
        [
            new ShortcutActionOption("Go to layer", ShortcutActionKind.GoToLayer),
            new ShortcutActionOption("Hold while pressed", ShortcutActionKind.MomentaryLayer),
        ];
        this.AvailableLayers = manifest.Layers.Select(layer => layer.Name).ToArray();
        this.ShortcutRows = [];
        this.WidgetTaskbarOptions = new ObservableCollection<WidgetTaskbarOption>(widgetTaskbarOptions);

        this.InitializeComponent();
        this.DataContext = this;
        this.TransportPreferenceValue.ItemsSource = Enum.GetValues<TransportPreference>();
        this.LogPathValue.Text = $"Diagnostic log: {diagnosticPath}";
        this.ApplySavedConfiguration(configuration, startWithWindows);
    }

    public event EventHandler? ReconnectRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler<SettingsSaveRequestedEventArgs>? SettingsSaveRequested;

    public IReadOnlyList<ShortcutActionOption> AvailableActions { get; }

    public IReadOnlyList<string> AvailableLayers { get; }

    public ObservableCollection<ShortcutEditorRow> ShortcutRows { get; }

    public ObservableCollection<WidgetTaskbarOption> WidgetTaskbarOptions { get; }

    public void ApplySavedConfiguration(ResolvedCompanionConfiguration configuration, bool startWithWindows)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        this.TransportPreferenceValue.SelectedItem = configuration.TransportPreference;
        this.SelectWidgetTaskbar(configuration.WidgetTaskbar);
        this.StartWithWindowsValue.IsChecked = startWithWindows;
        this.ShortcutRows.Clear();
        foreach (ShortcutBinding binding in configuration.Shortcuts)
        {
            this.ShortcutRows.Add(new ShortcutEditorRow(binding.Gesture.ToString(), binding.Action, binding.TargetLayerName));
        }
    }

    public void UpdateWidgetTaskbarOptions(IReadOnlyList<WidgetTaskbarOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        WidgetTaskbarSelection selection = this.WidgetTaskbarValue.SelectedItem is WidgetTaskbarOption selected
            ? selected.Selection
            : WidgetTaskbarSelection.Primary;
        this.WidgetTaskbarOptions.Clear();
        foreach (WidgetTaskbarOption option in options)
        {
            this.WidgetTaskbarOptions.Add(option);
        }

        this.SelectWidgetTaskbar(selection);
    }

    public void UpdateStatus(CompanionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        CompanionDisplayState display = CompanionStatusPresenter.Present(status);
        this.ConnectionBadgeValue.Text = display.ConnectionLabel;
        this.LayerValue.Text = display.LayerName;
        this.TransportValue.Text = string.IsNullOrEmpty(display.TransportLabel)
            ? display.Detail
            : $"Connected over {display.TransportLabel}";
        this.LeftBatteryValue.Text = FormatBattery(display.LeftBattery);
        this.RightBatteryValue.Text = FormatBattery(display.RightBattery);
        this.DetailValue.Text = display.Detail;
        this.OwnershipValue.Text = status.LayerState is null
            ? "Layer ownership: unknown"
            : $"Layer ownership: persistent {status.LayerState.PersistentLayer?.Name ?? "none"}; " +
              $"momentary {status.LayerState.MomentaryLayerCount.ToString(CultureInfo.InvariantCulture)}";
        this.ApplyConnectionColors(display.ConnectionState);
    }

    public void UpdateShortcutActivity(ShortcutKeyEvent keyEvent, ShortcutRouteKind route)
    {
        ArgumentNullException.ThrowIfNull(keyEvent);
        string origin = keyEvent.IsInjected ? "Windows-injected input" : "hardware input";
        this.ShortcutActivityValue.Text =
            $"{keyEvent.Key} {keyEvent.State.ToString().ToLowerInvariant()}: {route} ({origin})";
    }

    public void UpdateShortcutFailure(string message)
    {
        this.ShortcutActivityValue.Text = $"Shortcut capture error: {message}";
    }

    public void SetSaving(bool isSaving)
    {
        this.SaveButton.IsEnabled = !isSaving;
        this.SaveStatusValue.Text = isSaving ? "Saving settings and restarting the connection…" : string.Empty;
    }

    public void ShowSaveResult(string message, bool succeeded)
    {
        this.SaveButton.IsEnabled = true;
        this.SaveStatusValue.Foreground = succeeded ? CurrentForeground : StaleForeground;
        this.SaveStatusValue.Text = message;
    }

    public void PrepareForExit()
    {
        this.allowClose = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!this.allowClose)
        {
            e.Cancel = true;
            this.Hide();
        }

        base.OnClosing(e);
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static string FormatBattery(CompanionBatteryDisplay battery)
    {
        return battery.IsStale ? $"{battery.Text} stale" : battery.Text;
    }

    private static bool TryGetShortcutKey(Key key, out string shortcutKey)
    {
        if (key is >= Key.A and <= Key.Z || key is >= Key.F1 and <= Key.F24)
        {
            shortcutKey = key.ToString();
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            shortcutKey = key.ToString()[1..];
            return true;
        }

        shortcutKey = key switch
        {
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.End => "End",
            Key.Enter or Key.Return => "Enter",
            Key.Escape => "Escape",
            Key.Home => "Home",
            Key.Insert => "Insert",
            Key.PageDown => "PageDown",
            Key.PageUp => "PageUp",
            Key.Space => "Space",
            Key.Tab => "Tab",
            _ => string.Empty,
        };
        return shortcutKey.Length > 0;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    }

    private static ShortcutModifiers GetShortcutModifiers()
    {
        ModifierKeys current = Keyboard.Modifiers;
        ShortcutModifiers modifiers = ShortcutModifiers.None;
        if ((current & ModifierKeys.Control) != 0)
        {
            modifiers |= ShortcutModifiers.Control;
        }

        if ((current & ModifierKeys.Alt) != 0)
        {
            modifiers |= ShortcutModifiers.Alt;
        }

        if ((current & ModifierKeys.Shift) != 0)
        {
            modifiers |= ShortcutModifiers.Shift;
        }

        if ((current & ModifierKeys.Windows) != 0)
        {
            modifiers |= ShortcutModifiers.Windows;
        }

        return modifiers;
    }

    private void SelectWidgetTaskbar(WidgetTaskbarSelection selection)
    {
        WidgetTaskbarOption? option = selection.Mode switch
        {
            WidgetTaskbarMode.All => this.WidgetTaskbarOptions.FirstOrDefault(
                candidate => candidate.Selection.Mode == WidgetTaskbarMode.All),
            WidgetTaskbarMode.SpecificMonitor => this.WidgetTaskbarOptions.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Selection.MonitorId,
                    selection.MonitorId,
                    StringComparison.OrdinalIgnoreCase)),
            WidgetTaskbarMode.Primary => this.WidgetTaskbarOptions.FirstOrDefault(candidate => candidate.IsPrimary),
            _ => null,
        };
        if (option is null)
        {
            string label = selection.Mode == WidgetTaskbarMode.SpecificMonitor
                ? $"{selection.MonitorId} (currently unavailable)"
                : "Primary taskbar (currently unavailable)";
            option = new WidgetTaskbarOption(label, selection, selection.Mode == WidgetTaskbarMode.Primary);
            this.WidgetTaskbarOptions.Add(option);
        }

        this.WidgetTaskbarValue.SelectedItem = option;
    }

    private void ApplyConnectionColors(CompanionDisplayConnectionState state)
    {
        (Brush background, Brush foreground) = state switch
        {
            CompanionDisplayConnectionState.Current => (CurrentBackground, CurrentForeground),
            CompanionDisplayConnectionState.Stale => (StaleBackground, StaleForeground),
            CompanionDisplayConnectionState.Disconnected => (DisconnectedBackground, DisconnectedForeground),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "The display connection state is unsupported."),
        };
        this.ConnectionBadge.Background = background;
        this.ConnectionBadgeValue.Foreground = foreground;
    }

    private void OnShortcutPreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key) || !TryGetShortcutKey(key, out string shortcutKey))
        {
            return;
        }

        if (sender is TextBox { DataContext: ShortcutEditorRow row })
        {
            row.Shortcut = ShortcutGesture.FromInput(shortcutKey, GetShortcutModifiers()).ToString();
            e.Handled = true;
        }
    }

    private void OnAddShortcutClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        HashSet<string> configured = this.ShortcutRows
            .Select(row => row.Shortcut)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string shortcut = Enumerable.Range(1, 24)
            .Reverse()
            .Select(number => $"F{number.ToString(CultureInfo.InvariantCulture)}")
            .FirstOrDefault(candidate => !configured.Contains(candidate)) ?? string.Empty;
        string layer = this.AvailableLayers.FirstOrDefault(name => !string.Equals(name, "Home", StringComparison.Ordinal))
            ?? this.AvailableLayers[0];
        this.ShortcutRows.Add(new ShortcutEditorRow(shortcut, ShortcutActionKind.MomentaryLayer, layer));
        this.ShortcutList.ScrollIntoView(this.ShortcutRows[^1]);
    }

    private void OnRemoveShortcutClick(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Button { Tag: ShortcutEditorRow row })
        {
            this.ShortcutRows.Remove(row);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        TransportPreference transport = this.TransportPreferenceValue.SelectedItem is TransportPreference selected
            ? selected
            : TransportPreference.Automatic;
        WidgetTaskbarSelection widgetTaskbar = this.WidgetTaskbarValue.SelectedItem is WidgetTaskbarOption taskbarOption
            ? taskbarOption.Selection
            : WidgetTaskbarSelection.Primary;
        CompanionConfiguration configuration = new(
            CompanionConfiguration.CurrentSchemaVersion,
            transport,
            this.ShortcutRows
                .Select(row => new ShortcutConfiguration(row.Shortcut.Trim(), row.Action, row.TargetLayer))
                .ToArray())
        {
            WidgetTaskbar = widgetTaskbar,
        };
        this.SetSaving(true);
        if (this.SettingsSaveRequested is null)
        {
            this.ShowSaveResult("The settings service is unavailable.", succeeded: false);
            return;
        }

        this.SettingsSaveRequested.Invoke(
            this,
            new SettingsSaveRequestedEventArgs(configuration, this.StartWithWindowsValue.IsChecked == true));
    }

    private void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        this.ReconnectRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        this.Hide();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        this.ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
