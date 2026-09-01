namespace ShinyGo60.Diagnostics;

public sealed record DiagnosticEvent(
    DateTimeOffset TimestampUtc,
    DiagnosticLevel Level,
    string Component,
    string EventName,
    string Message,
    IReadOnlyDictionary<string, string>? Properties = null);
