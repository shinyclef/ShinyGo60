namespace ShinyGo60.Companion.Core.Telemetry;

public sealed class LayerTelemetryStateChangedEventArgs : EventArgs
{
    public LayerTelemetryStateChangedEventArgs(LayerTelemetryState state)
    {
        this.State = state;
    }

    public LayerTelemetryState State { get; }
}
