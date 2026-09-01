using ShinyGo60.Builder.Core.Processes;

namespace ShinyGo60.Tests.Fakes;

internal sealed class FakeProcessRunner : IProcessRunner
{
    public ProcessInvocation? LastInvocation { get; private set; }

    public ProcessResult Result { get; init; } = new(0, string.Empty, string.Empty);

    public ValueTask<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default)
    {
        this.LastInvocation = invocation;
        return ValueTask.FromResult(this.Result);
    }
}
