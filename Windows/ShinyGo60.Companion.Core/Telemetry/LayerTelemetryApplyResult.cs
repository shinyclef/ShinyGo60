namespace ShinyGo60.Companion.Core.Telemetry;

public enum LayerTelemetryApplyResult
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
