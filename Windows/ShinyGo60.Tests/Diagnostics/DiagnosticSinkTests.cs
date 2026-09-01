using System.Globalization;
using System.Text.Json;
using ShinyGo60.Diagnostics;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Diagnostics;

internal static class DiagnosticSinkTests
{
    public static async ValueTask RunAsync()
    {
        using StringWriter writer = new(CultureInfo.InvariantCulture);
        using JsonLineDiagnosticSink sink = new(writer);
        DiagnosticEvent diagnosticEvent = new(
            DateTimeOffset.UnixEpoch,
            DiagnosticLevel.Information,
            "Builder",
            "ScaffoldCheck",
            "A metadata-only diagnostic event.",
            new Dictionary<string, string> { ["Revision"] = "fixture" });

        await sink.WriteAsync(diagnosticEvent);

        using JsonDocument document = JsonDocument.Parse(writer.ToString());
        JsonElement root = document.RootElement;
        AssertEx.Equal("ScaffoldCheck", root.GetProperty("EventName").GetString());
        AssertEx.Equal("fixture", root.GetProperty("Properties").GetProperty("Revision").GetString());
    }
}
