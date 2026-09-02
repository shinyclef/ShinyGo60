namespace ShinyGo60.Companion.Core.Telemetry;

public sealed class BatteryTelemetryStateChangedEventArgs : EventArgs
{
    public BatteryTelemetryStateChangedEventArgs(BatteryTelemetryState state)
    {
        this.State = state;
    }

    public BatteryTelemetryState State { get; }
}
