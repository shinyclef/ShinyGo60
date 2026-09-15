using System.Globalization;
using ShinyGo60.Companion.Core.Diagnostics;
using ShinyGo60.Diagnostics;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Diagnostics;

internal static class ConnectionIssueAnalyzerTests
{
    public static ValueTask RunAsync()
    {
        DateTimeOffset start = new(2026, 9, 11, 6, 29, 9, TimeSpan.Zero);
        DiagnosticEvent loss = Event(start, "transport_connection_lost", new() { ["transport"] = "Bluetooth" });
        DiagnosticEvent recovery = Event(start.AddSeconds(93), "connected", new() { ["transport"] = "Bluetooth" });
        List<DiagnosticEvent> events = [loss, recovery];
        events.Add(Firmware(start.AddSeconds(100), start, 1, "disconnected", 40));
        for (int index = 0; index < 31; index++)
        {
            events.Add(Firmware(start.AddSeconds(100), start.AddSeconds(2 + index), (uint)(2 + index * 2), "security_changed", 9));
            events.Add(Firmware(start.AddSeconds(100), start.AddSeconds(2 + index), (uint)(3 + index * 2), "disconnected", 62));
        }

        events.Add(events[2]); // A checkpoint replay must not create or count another incident.
        BluetoothSystemEvent driver = new(start.AddSeconds(4), 5, "BTHUSB");
        IReadOnlyList<ConnectionIssue> issues = ConnectionIssueAnalyzer.Analyze(events, [driver, driver, new(start.AddHours(-1), 5, "BTHUSB")]);
        AssertEx.Equal(1, issues.Count);
        AssertEx.Equal(40, issues[0].FirstDisconnectCode!.Value);
        AssertEx.Equal(31, issues[0].FailedReconnects);
        AssertEx.Equal(31, issues[0].SecurityFailures);
        AssertEx.Equal(1, issues[0].WindowsDriverErrors);
        AssertEx.Equal(93d, (issues[0].RecoveredUtc!.Value - issues[0].StartedUtc).TotalSeconds);
        AssertEx.True(issues[0].Summary.Contains("Instant Passed", StringComparison.Ordinal), "Raw code 40 must have a useful explanation.");

        DiagnosticEvent split = Firmware(start.AddSeconds(101), start, 99, "disconnected", 8) with
        {
            Properties = new Dictionary<string, string>(Firmware(start, start, 99, "disconnected", 8).Properties!)
            {
                ["role"] = "central (split link)",
            },
        };
        AssertEx.Equal(0, ConnectionIssueAnalyzer.Analyze([split]).Count);
        AssertEx.Equal(0, ConnectionIssueAnalyzer.Analyze([recovery]).Count);

        ConnectionIssue standalone = ConnectionIssueAnalyzer.Analyze([events[2], events[3], events[4]])[0];
        AssertEx.True(standalone.Estimated && standalone.RecoveredUtc is null, "Offline history must not invent a recovery time.");
        DiagnosticEvent nextLoss = loss with { TimestampUtc = start.AddHours(1) };
        AssertEx.Equal(2, ConnectionIssueAnalyzer.Analyze([.. events, nextLoss]).Count);
        DiagnosticEvent gap = events[2] with
        {
            Properties = new Dictionary<string, string>(events[2].Properties!) { ["missedEvents"] = "95" },
        };
        AssertEx.Equal(95L, ConnectionIssueAnalyzer.Analyze([loss, recovery, gap])[0].MissingEvents);
        DiagnosticEvent lateFailure = Firmware(start.AddHours(1), start.AddMinutes(20), 100, "disconnected", 62);
        AssertEx.Equal(1, ConnectionIssueAnalyzer.Analyze([loss, lateFailure])[0].FailedReconnects);
        DiagnosticEvent restart = Event(start.AddMinutes(2), "application_started", []);
        IReadOnlyList<ConnectionIssue> interrupted = ConnectionIssueAnalyzer.Analyze([loss, restart, recovery with { TimestampUtc = start.AddMinutes(3) }]);
        AssertEx.True(interrupted[0].RecoveredUtc is null, "Restarting the app must not invent continuous observation of an outage.");
        return ValueTask.CompletedTask;
    }

    private static DiagnosticEvent Event(DateTimeOffset time, string name, Dictionary<string, string> properties) =>
        new(time, DiagnosticLevel.Information, "test", name, name, properties);

    private static DiagnosticEvent Firmware(DateTimeOffset receipt, DateTimeOffset occurrence, uint sequence, string name, int code) =>
        Event(receipt, "firmware_connection_event", new()
        {
            ["event"] = name, ["result"] = code.ToString(CultureInfo.InvariantCulture), ["bootId"] = "24EBF1AF", ["stream"] = "critical",
            ["sequence"] = sequence.ToString(CultureInfo.InvariantCulture), ["role"] = "peripheral (host link)",
            ["eventTimeEarliestUtc"] = occurrence.AddSeconds(-2).ToString("O", CultureInfo.InvariantCulture),
            ["eventTimeLatestUtc"] = occurrence.AddSeconds(2).ToString("O", CultureInfo.InvariantCulture), ["missedEvents"] = "0",
        });
}
