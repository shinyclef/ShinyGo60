using System.Globalization;
using ShinyGo60.Diagnostics;

namespace ShinyGo60.Companion.Core.Diagnostics;

public static class ConnectionIssueAnalyzer
{
    public static IReadOnlyList<ConnectionIssue> Analyze(IEnumerable<DiagnosticEvent> events, IEnumerable<BluetoothSystemEvent>? windowsEvents = null)
    {
        DiagnosticEvent[] ordered = events.OrderBy(entry => entry.TimestampUtc).ToArray();
        List<ConnectionIssue> issues = [];
        ConnectionIssue? pending = null;
        foreach (DiagnosticEvent entry in ordered)
        {
            if (entry.EventName == "application_started")
            {
                if (pending is not null)
                {
                    pending.ObservationEndedUtc = entry.TimestampUtc;
                }

                pending = null;
            }
            else if (Value(entry, "transport") == "Bluetooth")
            {
                bool loss = entry.EventName == "transport_connection_lost" ||
                    (entry.EventName == "connection_failed" && Value(entry, "phase") == "connected");
                if (loss && pending is null)
                {
                    pending = new ConnectionIssue(entry.TimestampUtc, false);
                    issues.Add(pending);
                }
                else if (entry.EventName == "connected" && pending is not null)
                {
                    pending.RecoveredUtc = entry.TimestampUtc;
                    pending.LastEvidenceUtc = entry.TimestampUtc;
                    pending = null;
                }
            }
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        var firmware = ordered.Where(entry => entry.EventName == "firmware_connection_event" &&
                Value(entry, "role") == "peripheral (host link)" &&
                !string.IsNullOrEmpty(Value(entry, "bootId")) && !string.IsNullOrEmpty(Value(entry, "sequence")))
            .Where(entry => seen.Add($"{Value(entry, "bootId")}/{Value(entry, "stream")}/{Value(entry, "sequence")}"))
            .OrderBy(entry => Time(entry, "eventTimeEarliestUtc"));
        foreach (DiagnosticEvent entry in firmware)
        {
            string kind = Value(entry, "event");
            if (!int.TryParse(Value(entry, "result"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            {
                continue;
            }
            if (kind != "disconnected" && !(kind == "security_changed" && result != 0))
            {
                continue;
            }

            DateTimeOffset earliest = Time(entry, "eventTimeEarliestUtc");
            DateTimeOffset latest = Time(entry, "eventTimeLatestUtc");
            string boot = Value(entry, "bootId");
            ConnectionIssue? issue = issues.Where(item => !item.Estimated && latest >= item.StartedUtc.AddSeconds(-2) &&
                    earliest.AddSeconds(-2) <= (item.RecoveredUtc ?? item.ObservationEndedUtc ?? DateTimeOffset.MaxValue))
                .MinBy(item => Math.Abs((earliest - item.StartedUtc).TotalSeconds));
            issue ??= issues.LastOrDefault(item => item.Estimated && item.BootId == boot &&
                earliest >= item.StartedUtc && earliest <= item.LastEvidenceUtc.AddSeconds(10));
            if (issue is null)
            {
                issue = new ConnectionIssue(earliest, true) { BootId = boot };
                issues.Add(issue);
            }

            issue.BootId ??= boot;
            if (latest > issue.LastEvidenceUtc)
            {
                issue.LastEvidenceUtc = latest;
            }

            if (kind == "disconnected")
            {
                issue.FirstDisconnectCode ??= result;
                if (result == 62)
                {
                    issue.FailedReconnects++;
                }
            }
            else
            {
                issue.SecurityFailures++;
            }

            if (long.TryParse(Value(entry, "missedEvents"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long missing))
            {
                issue.MissingEvents += missing;
            }
        }

        BluetoothSystemEvent[] systemEvents = windowsEvents?.Distinct().ToArray() ?? [];
        foreach (ConnectionIssue issue in issues)
        {
            issue.WindowsDriverErrors = systemEvents.Count(entry => entry.Id == 5 && entry.Provider == "BTHUSB" &&
                entry.TimestampUtc >= issue.StartedUtc.AddSeconds(-5) && entry.TimestampUtc <= issue.LastEvidenceUtc.AddSeconds(5));
        }

        return issues.OrderByDescending(issue => issue.StartedUtc).Take(50).ToArray();
    }

    private static string Value(DiagnosticEvent entry, string name) => entry.Properties?.GetValueOrDefault(name) ?? string.Empty;

    private static DateTimeOffset Time(DiagnosticEvent entry, string name) =>
        DateTimeOffset.TryParse(Value(entry, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset value)
            ? value : entry.TimestampUtc;
}
