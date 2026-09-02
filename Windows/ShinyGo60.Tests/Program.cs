using ShinyGo60.Tests.Builder;
using ShinyGo60.Tests.Companion;
using ShinyGo60.Tests.Diagnostics;
using ShinyGo60.Tests.Protocol;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests;

internal static class Program
{
    public static Task<int> Main()
    {
        TestCase[] tests =
        [
            new("Protocol manifest contracts", ProtocolContractTests.RunAsync),
            new("Shared protocol-v1 byte vectors", ProtocolCodecTests.RunAsync),
            new("USB/Bluetooth transport contract", TransportContractTests.RunAsync),
            new("Go60 keymap inspection and layout artifacts", KeymapInspectionTests.RunAsync),
            new("Atomic keymap-to-UF2 pipeline", FirmwareBuildPipelineTests.RunAsync),
            new("Builder process orchestration contract", ProcessContractTests.RunAsync),
            new("Companion shortcut contract", ShortcutContractTests.RunAsync),
            new("Effective-layer state convergence", LayerStateTrackerTests.RunAsync),
            new("Structured diagnostic sink", DiagnosticSinkTests.RunAsync),
        ];

        return TestRunner.RunAsync(tests);
    }
}
