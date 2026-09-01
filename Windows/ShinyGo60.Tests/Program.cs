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
            new("USB/Bluetooth transport contract", TransportContractTests.RunAsync),
            new("Builder process orchestration contract", ProcessContractTests.RunAsync),
            new("Companion shortcut contract", ShortcutContractTests.RunAsync),
            new("Structured diagnostic sink", DiagnosticSinkTests.RunAsync),
        ];

        return TestRunner.RunAsync(tests);
    }
}
