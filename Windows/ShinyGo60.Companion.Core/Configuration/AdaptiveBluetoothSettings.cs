using ShinyGo60.Protocol.Messages;

namespace ShinyGo60.Companion.Core.Configuration;

public sealed record AdaptiveBluetoothSettings
{
    public bool Enabled { get; init; } = true;
    public ushort ActiveLatency { get; init; } = 4;
    public ushort IdleLatency { get; init; } = 30;
    public int IdleAfterSeconds { get; init; } = 60;
    public ushort MinimumSwitchSeconds { get; init; } = 30;
    public bool UseIdleWhenLocked { get; init; } = true;

    public BluetoothLatencyParameters ToParameters() => new(this.ActiveLatency, this.IdleLatency, this.MinimumSwitchSeconds);

    public void Validate()
    {
        if (!this.ToParameters().IsValid)
        {
            throw new InvalidDataException("Bluetooth latency must be 0–99, active must not exceed idle, and minimum switch time must be 5–300 seconds.");
        }

        if (this.IdleAfterSeconds is < 15 or > 3600)
        {
            throw new InvalidDataException("The Bluetooth idle timeout must be 15–3600 seconds.");
        }
    }
}
