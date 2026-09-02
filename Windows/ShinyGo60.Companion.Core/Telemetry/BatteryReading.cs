namespace ShinyGo60.Companion.Core.Telemetry;

public sealed record BatteryReading(byte? Level, BatteryReadingStatus Status);
