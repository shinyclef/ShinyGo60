namespace ShinyGo60.Protocol.Messages;

public sealed record BluetoothLatencyParameters(ushort ActiveLatency, ushort IdleLatency, ushort MinimumSwitchSeconds)
{
    public static BluetoothLatencyParameters Default { get; } = new(4, 30, 30);

    // Bounds also satisfy the pinned firmware's 15 ms maximum interval and 4 second supervision timeout.
    public bool IsValid => ActiveLatency <= IdleLatency && IdleLatency <= 99 && MinimumSwitchSeconds is >= 5 and <= 300;
}
