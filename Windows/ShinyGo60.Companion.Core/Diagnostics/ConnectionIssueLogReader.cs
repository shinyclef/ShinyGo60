using System.Text.Json;
using ShinyGo60.Diagnostics;

namespace ShinyGo60.Companion.Core.Diagnostics;

public static class ConnectionIssueLogReader
{
    public static async Task<IReadOnlyList<DiagnosticEvent>> ReadAsync(string logDirectory, string activeLogPath,
        CancellationToken cancellationToken = default)
    {
        List<DiagnosticEvent> events = [];
        DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-7);
        string[] paths = Directory.EnumerateFiles(logDirectory, "companion-*.jsonl")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(7)
            .Append(activeLogPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string path in paths)
        {
            await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(file);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
            {
                DiagnosticEvent? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<DiagnosticEvent>(line);
                }
                catch (JsonException)
                {
                    // An active writer may not have completed its final line. The next scan retries it.
                    continue;
                }

                if (entry is not null && entry.TimestampUtc >= since &&
                    entry.EventName is "application_started" or "transport_connection_lost" or "connection_failed" or
                        "connected" or "firmware_connection_event")
                {
                    events.Add(entry);
                }
            }
        }

        return events;
    }
}
