namespace ShinyGo60.Companion.Core.Diagnostics;

public sealed record BluetoothSystemEvent(DateTimeOffset TimestampUtc, int Id, string Provider);
