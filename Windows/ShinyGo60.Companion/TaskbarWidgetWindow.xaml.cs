using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ShinyGo60.Companion.Core.Presentation;
using ShinyGo60.Platform.Windows.Shell;

namespace ShinyGo60.Companion;

public partial class TaskbarWidgetWindow : Window
{
    private const int MouseActivateMessage = 0x0021;
    private const int DoNotActivateResult = 3;
    private static readonly Brush CurrentBrush = CreateBrush(0x43, 0xD1, 0x8B);
    private static readonly Brush StaleBrush = CreateBrush(0xF2, 0xB8, 0x4B);
    private static readonly Brush DisconnectedBrush = CreateBrush(0x91, 0x9A, 0xA6);

    public TaskbarWidgetWindow()
    {
        this.InitializeComponent();
    }

    public event EventHandler? SettingsRequested;

    public IntPtr NativeHandle { get; private set; }

    public void UpdateDisplayState(CompanionDisplayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        this.LayerValue.Text = state.LayerName;
        this.ConnectionValue.Text = string.IsNullOrEmpty(state.TransportLabel)
            ? state.ConnectionLabel
            : $"{state.ConnectionLabel} · {state.TransportLabel.ToUpperInvariant()}";
        SetBatteryDisplay(
            this.LeftBatteryValue,
            this.LeftBatteryPercent,
            this.LeftBatteryStaleMarker,
            state.LeftBattery);
        SetBatteryDisplay(
            this.RightBatteryValue,
            this.RightBatteryPercent,
            this.RightBatteryStaleMarker,
            state.RightBattery);
        this.StateStripe.Background = state.ConnectionState switch
        {
            CompanionDisplayConnectionState.Current => CurrentBrush,
            CompanionDisplayConnectionState.Stale => StaleBrush,
            CompanionDisplayConnectionState.Disconnected => DisconnectedBrush,
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state.ConnectionState,
                "The display connection state is unsupported."),
        };
        this.ToolTip = $"{state.ConnectionLabel}: {state.LayerName}. {state.Detail} Click to open settings.";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        this.NativeHandle = new WindowInteropHelper(this).Handle;
        WidgetWindowNativeMethods.MakeNonActivating(this.NativeHandle);
        HwndSource source = HwndSource.FromHwnd(this.NativeHandle)
            ?? throw new InvalidOperationException("The taskbar widget has no native window source.");
        source.AddHook(this.ProcessWindowMessage);
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static void SetBatteryDisplay(
        TextBlock value,
        TextBlock percent,
        TextBlock staleMarker,
        CompanionBatteryDisplay battery)
    {
        value.Text = battery.IsAvailable ? battery.Text[..^1] : battery.Text;
        percent.Text = battery.IsAvailable ? "%" : string.Empty;
        staleMarker.Text = battery.IsStale ? "*" : string.Empty;
    }

    private IntPtr ProcessWindowMessage(
        IntPtr window,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        _ = window;
        _ = wordParameter;
        _ = longParameter;
        if (message == MouseActivateMessage)
        {
            handled = true;
            return new IntPtr(DoNotActivateResult);
        }

        return IntPtr.Zero;
    }

    private void OnWidgetMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        _ = e;
        this.SettingsRequested?.Invoke(this, EventArgs.Empty);
    }
}
