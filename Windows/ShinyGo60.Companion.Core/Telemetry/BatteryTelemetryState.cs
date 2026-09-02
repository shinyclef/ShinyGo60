namespace ShinyGo60.Companion.Core.Telemetry;

public sealed record BatteryTelemetryState(
    uint SessionId,
    uint Revision,
    BatteryReading Left,
    BatteryReading Right);
