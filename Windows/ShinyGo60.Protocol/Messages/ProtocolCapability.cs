namespace ShinyGo60.Protocol.Messages;

[Flags]
public enum ProtocolCapability : byte
{
    None = 0,
    StateTelemetry = 1 << 0,
    PersistentLayer = 1 << 1,
    MomentaryLayer = 1 << 2,
    BatteryTelemetry = 1 << 3,
    AdaptiveBluetoothLatency = 1 << 4,
}
