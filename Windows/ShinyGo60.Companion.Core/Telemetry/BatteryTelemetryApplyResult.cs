namespace ShinyGo60.Companion.Core.Telemetry;

public enum BatteryTelemetryApplyResult
{
    NoSession,
    AwaitingSnapshot,
    AppliedSnapshot,
    Applied,
    AppliedAfterGap,
    Duplicate,
    WrongSession,
    StaleRevision,
    ConflictingRevision,
    InvalidState,
}
