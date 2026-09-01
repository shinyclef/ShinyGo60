using ShinyGo60.Builder.Core.Processes;
using ShinyGo60.Tests.Fakes;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Builder;

internal static class ProcessContractTests
{
    public static async ValueTask RunAsync()
    {
        FakeProcessRunner runner = new()
        {
            Result = new ProcessResult(0, "firmware built", string.Empty),
        };
        ProcessInvocation invocation = new(
            "docker",
            ["run", "--network", "none", "shinygo60-builder:v25.11"],
            "C:\\fixture");

        ProcessResult result = await runner.RunAsync(invocation);

        AssertEx.Equal(0, result.ExitCode);
        AssertEx.Equal(invocation, runner.LastInvocation);
    }
}
