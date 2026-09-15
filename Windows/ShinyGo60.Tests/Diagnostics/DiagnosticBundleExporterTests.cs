using System.IO.Compression;
using System.Text.Json;
using ShinyGo60.Companion.Core.Diagnostics;
using ShinyGo60.Diagnostics;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Diagnostics;

internal static class DiagnosticBundleExporterTests
{
    public static async ValueTask RunAsync()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "Build Records", "Tests", "Export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string active = Path.Combine(directory, "companion-20000101.jsonl");
        string destination = Path.Combine(directory, "diagnostics.zip");
        DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(-1);
        DiagnosticEvent retained = new(now, DiagnosticLevel.Information, "companion.history", "firmware_connection_event", "Disconnected.",
            new Dictionary<string, string> { ["bootId"] = "1234", ["stream"] = "critical", ["sequence"] = "8", ["firmwareVersion"] = "0.9.0" });
        DiagnosticEvent old = retained with { TimestampUtc = now.AddDays(-8), Message = "OLD EVENT MUST BE EXCLUDED" };
        await File.WriteAllLinesAsync(active,
            [JsonSerializer.Serialize(old), JsonSerializer.Serialize(retained), JsonSerializer.Serialize(retained), "incomplete JSON"]);
        DiagnosticBundleEnvironment environment = new("1.1.0", "Test OS", "Test runtime", "1.2", "test-layout", "Collected",
            [new BluetoothAdapterDiagnostic("Test adapter", "Test vendor", "Test provider", "1.2.3", "2026-09-08")]);
        await DiagnosticBundleExporter.ExportAsync(directory, active, destination, environment);
        using (ZipArchive archive = ZipFile.OpenRead(destination))
        {
            AssertEx.Equal(4, archive.Entries.Count);
            using StreamReader log = new(archive.GetEntry("companion.jsonl")!.Open());
            string logs = await log.ReadToEndAsync();
            AssertEx.True(!logs.Contains("OLD EVENT", StringComparison.Ordinal), "Old events leaked into the recent window.");
            using StreamReader history = new(archive.GetEntry("firmware-history.jsonl")!.Open());
            string text = await history.ReadToEndAsync();
            AssertEx.Equal(1, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            using JsonDocument metadata = await JsonDocument.ParseAsync(archive.GetEntry("environment.json")!.Open());
            AssertEx.Equal("1.1.0", metadata.RootElement.GetProperty("Environment").GetProperty("CompanionVersion").GetString());
            JsonElement adapter = metadata.RootElement.GetProperty("Environment").GetProperty("BluetoothAdapters")[0];
            AssertEx.Equal("1.2.3", adapter.GetProperty("DriverVersion").GetString());
            AssertEx.Equal("0.9.0", metadata.RootElement.GetProperty("ObservedFirmwareVersions")[0].GetString());
            AssertEx.True(metadata.RootElement.GetProperty("Notes").GetArrayLength() > 0, "Incomplete log data was not reported.");
        }

        string issuePath = Path.Combine(directory, "connection-issues.json");
        await File.WriteAllTextAsync(issuePath, "{\"Issues\":[],\"CollectionStatus\":\"Windows events unavailable\"}");
        await DiagnosticBundleExporter.ExportAsync(directory, active, destination, environment);
        using (ZipArchive archive = ZipFile.OpenRead(destination))
        {
            using StreamReader summary = new(archive.GetEntry("connection-issues.json")!.Open());
            AssertEx.Equal(await File.ReadAllTextAsync(issuePath), await summary.ReadToEndAsync());
        }

        File.Delete(issuePath);
        File.Delete(active);
        File.Delete(destination);
        Directory.Delete(directory);
    }
}
