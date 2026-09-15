namespace ShinyGo60.Companion.Core.Diagnostics;

public sealed record ConnectionIssueReport(DateTimeOffset UpdatedUtc, IReadOnlyList<ConnectionIssue> Issues, string CollectionStatus,
    IReadOnlyList<BluetoothSystemEvent> WindowsEvents);
