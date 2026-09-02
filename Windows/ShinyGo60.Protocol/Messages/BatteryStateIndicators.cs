namespace ShinyGo60.Protocol.Messages;

[Flags]
public enum BatteryStateIndicators : byte
{
    None = 0,
    LeftAvailable = 1 << 0,
    LeftStale = 1 << 1,
    RightAvailable = 1 << 2,
    RightStale = 1 << 3,
}
