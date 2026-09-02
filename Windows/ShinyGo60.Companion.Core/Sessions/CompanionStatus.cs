using ShinyGo60.Companion.Core.Telemetry;
using ShinyGo60.Protocol.Transport;

namespace ShinyGo60.Companion.Core.Sessions;

public sealed record CompanionStatus(
    CompanionConnectionState ConnectionState,
    TransportKind? Transport,
    LayerTelemetryState? LayerState,
    BatteryTelemetryState? BatteryState,
    string Detail,
    int ReconnectAttempt)
{
    public static CompanionStatus Stopped { get; } = new(
        CompanionConnectionState.Stopped,
        null,
        null,
        null,
        "Stopped",
        0);
}
