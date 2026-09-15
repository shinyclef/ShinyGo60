using System.Globalization;

namespace ShinyGo60.Companion.Core.Diagnostics;

public sealed record ConnectionIssue(DateTimeOffset StartedUtc, bool Estimated)
{
    public DateTimeOffset? RecoveredUtc { get; set; }
    public DateTimeOffset LastEvidenceUtc { get; set; } = StartedUtc;
    public int? FirstDisconnectCode { get; set; }
    public int FailedReconnects { get; set; }
    public int SecurityFailures { get; set; }
    public long MissingEvents { get; set; }
    public int WindowsDriverErrors { get; set; }
    public string? BootId { get; set; }
    internal DateTimeOffset? ObservationEndedUtc { get; set; }

    public string Summary => this.FirstDisconnectCode switch
    {
        40 => "Bluetooth timing failure (Instant Passed, 0x28)",
        8 or 34 => "Bluetooth connection timed out",
        19 or 22 => "Bluetooth connection ended",
        62 => "Bluetooth connection could not be established",
        int code => $"Bluetooth disconnected (code 0x{code:X2})",
        _ => "Bluetooth connection lost; firmware cause unavailable",
    };

    public string DisplayText
    {
        get
        {
            string time = this.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            string recovery = this.RecoveredUtc is DateTimeOffset restored
                ? $"Companion connection restored after {(restored - this.StartedUtc).TotalSeconds.ToString("F0", CultureInfo.CurrentCulture)} seconds."
                : "Recovery timing has not been recorded.";
            string counts = $"Failed reconnects: {this.FailedReconnects}. Security failures: {this.SecurityFailures}.";
            string driver = this.WindowsDriverErrors > 0
                ? $" Windows also recorded {this.WindowsDriverErrors} Bluetooth driver error(s)." : string.Empty;
            string gaps = this.MissingEvents > 0 ? $" {this.MissingEvents} firmware events are missing; the initial cause may be lost." : string.Empty;
            string grouping = this.Estimated ? "\nNearby firmware failures are grouped; the exact outage duration is unavailable." : string.Empty;
            return $"{time}{(this.Estimated ? " (estimated)" : string.Empty)} — {this.Summary}\n{recovery}\n{counts}{driver}{gaps}" +
                grouping + "\nReported evidence does not identify which device or driver caused the failure.";
        }
    }
}
