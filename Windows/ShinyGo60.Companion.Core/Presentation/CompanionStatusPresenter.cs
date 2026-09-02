using ShinyGo60.Companion.Core.Sessions;
using ShinyGo60.Companion.Core.Telemetry;
using ShinyGo60.Protocol.Transport;

namespace ShinyGo60.Companion.Core.Presentation;

public static class CompanionStatusPresenter
{
    public static CompanionDisplayState Present(CompanionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        bool isCurrent = status.ConnectionState == CompanionConnectionState.Connected && status.LayerState is not null;
        CompanionDisplayConnectionState connectionState = isCurrent
            ? CompanionDisplayConnectionState.Current
            : status.LayerState is not null
                ? CompanionDisplayConnectionState.Stale
                : CompanionDisplayConnectionState.Disconnected;

        return new CompanionDisplayState(
            connectionState,
            status.LayerState?.EffectiveLayer.Name ?? "No keyboard",
            FormatConnectionLabel(status, connectionState),
            isCurrent ? FormatTransport(status.Transport) : string.Empty,
            FormatBattery(status.BatteryState?.Left, connectionState != CompanionDisplayConnectionState.Current),
            FormatBattery(status.BatteryState?.Right, connectionState != CompanionDisplayConnectionState.Current),
            status.Detail);
    }

    private static string FormatConnectionLabel(
        CompanionStatus status,
        CompanionDisplayConnectionState connectionState)
    {
        return connectionState switch
        {
            CompanionDisplayConnectionState.Current => "CURRENT",
            CompanionDisplayConnectionState.Stale => "STALE",
            CompanionDisplayConnectionState.Disconnected when status.ConnectionState == CompanionConnectionState.Connecting => "SEARCHING",
            CompanionDisplayConnectionState.Disconnected => "DISCONNECTED",
            _ => throw new ArgumentOutOfRangeException(nameof(connectionState), connectionState, "The display connection state is unsupported."),
        };
    }

    private static string FormatTransport(TransportKind? transport)
    {
        return transport switch
        {
            TransportKind.Usb => "USB",
            TransportKind.Bluetooth => "Bluetooth",
            null => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, "The keyboard transport is unsupported."),
        };
    }

    private static CompanionBatteryDisplay FormatBattery(BatteryReading? reading, bool connectionIsStale)
    {
        if (reading?.Level is not byte level || reading.Status == BatteryReadingStatus.Unavailable)
        {
            return new CompanionBatteryDisplay("—", false, false);
        }

        return new CompanionBatteryDisplay(
            $"{level}%",
            true,
            connectionIsStale || reading.Status == BatteryReadingStatus.Stale);
    }
}
