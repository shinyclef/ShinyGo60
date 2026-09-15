using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ShinyGo60.Diagnostics;

namespace ShinyGo60.Companion.Core.Diagnostics;

public static class DiagnosticBundleExporter
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new() { WriteIndented = true };
    public static async Task ExportAsync(string logDirectory, string activeLogPath, string destination,
        DiagnosticBundleEnvironment environment, CancellationToken cancellationToken = default)
    {
        DateTimeOffset exported = DateTimeOffset.UtcNow;
        DateTimeOffset since = exported.AddDays(-7);
        List<string> notes = [];
        HashSet<string> firmwareVersions = new(StringComparer.Ordinal);
        HashSet<string> firmwareEvents = new(StringComparer.Ordinal);
        int malformedLines = 0;
        int eventCount = 0;
        string[] paths = Directory.EnumerateFiles(logDirectory, "companion-*.jsonl")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(7)
            .Append(activeLogPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true);
                // ZIP entries must be closed before opening the next. Buffer filtered history separately.
                using MemoryStream history = new();
                await using (StreamWriter historyWriter = new(history, new UTF8Encoding(false), leaveOpen: true))
                await using (StreamWriter logWriter = new(archive.CreateEntry("companion.jsonl").Open(), new UTF8Encoding(false)))
                {
                    foreach (string path in paths)
                    {
                        try
                        {
                            await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            using StreamReader reader = new(file);
                            // Fix the window by event time; a running app can still be writing yesterday's filename.
                            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
                            {
                                DiagnosticEvent? entry;
                                try
                                {
                                    entry = JsonSerializer.Deserialize<DiagnosticEvent>(line);
                                }
                                catch (JsonException)
                                {
                                    malformedLines++;
                                    continue;
                                }

                                if (entry is null || entry.TimestampUtc < since || entry.TimestampUtc > exported)
                                {
                                    continue;
                                }

                                eventCount++;
                                await logWriter.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                                if (entry.EventName.StartsWith("firmware_", StringComparison.Ordinal))
                                {
                                    if (entry.Properties?.TryGetValue("firmwareVersion", out string? version) == true)
                                    {
                                        firmwareVersions.Add(version);
                                    }

                                    if (entry.EventName == "firmware_connection_event" && entry.Properties is not null &&
                                        entry.Properties.TryGetValue("bootId", out string? boot) &&
                                        entry.Properties.TryGetValue("sequence", out string? sequence))
                                    {
                                        string stream = entry.Properties.GetValueOrDefault("stream", "legacy");
                                        if (!firmwareEvents.Add($"{boot}:{stream}:{sequence}"))
                                        {
                                            continue;
                                        }
                                    }

                                    await historyWriter.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                                }
                            }
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            notes.Add($"Could not completely read {Path.GetFileName(path)}: {exception.GetType().Name}.");
                        }
                    }
                }

                history.Position = 0;
                await using (Stream target = archive.CreateEntry("firmware-history.jsonl").Open())
                {
                    await history.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }

                if (firmwareVersions.Count == 0)
                {
                    notes.Add("No firmware version was collected in this log window. Check firmware_history_unavailable/unsupported/failed events.");
                }

                string issuePath = Path.Combine(logDirectory, "connection-issues.json");
                if (File.Exists(issuePath))
                {
                    try
                    {
                        byte[] report = await File.ReadAllBytesAsync(issuePath, cancellationToken).ConfigureAwait(false);
                        await using Stream target = archive.CreateEntry("connection-issues.json").Open();
                        await target.WriteAsync(report, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        notes.Add("The saved issue summary could not be included: " + exception.GetType().Name);
                    }
                }

                if (malformedLines > 0)
                {
                    notes.Add($"Skipped {malformedLines} incomplete or malformed log lines.");
                }

                await using (Stream metadata = archive.CreateEntry("environment.json").Open())
                {
                    await JsonSerializer.SerializeAsync(metadata, new
                    {
                        BundleVersion = 1, ExportedUtc = exported, EventsSinceUtc = since, EventCount = eventCount,
                        Environment = environment, ObservedFirmwareVersions = firmwareVersions.Order(StringComparer.Ordinal).ToArray(),
                        Notes = notes,
                    }, MetadataJsonOptions, cancellationToken).ConfigureAwait(false);
                }

                await using StreamWriter readme = new(archive.CreateEntry("README.txt").Open(), new UTF8Encoding(false));
                await readme.WriteAsync("""
                    ShinyGo60 local diagnostic export

                    companion.jsonl: events from the last seven days in the seven newest log files plus the active file.
                    firmware-history.jsonl: collected firmware events; repeated boot/stream/sequence entries are omitted.
                    environment.json: software versions, local Bluetooth adapter/driver details, and collection notes.
                    connection-issues.json (when available): latest saved incident summaries and Windows Bluetooth event IDs/times.

                    Event timestamps in the outer log are host receipt times. Firmware uptime and estimated event-time
                    bounds describe occurrence. Earlier experimental logs may contain raw codes without descriptions.
                    A boot change clears on-device RAM history. Missing-event counts are events, not disconnect counts.
                    This export uses already collected history; it does not disturb the keyboard to obtain a new snapshot.

                    Logs may contain configured layer names, shortcut names, and error paths from older software.
                    Adapter identifiers, Bluetooth addresses and pairing keys are not intentionally added by the export.
                    Review the files before sharing. Saving this ZIP does not upload it anywhere.
                    """.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}
