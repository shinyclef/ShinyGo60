using ShinyGo60.Builder.Core.Processes;

namespace ShinyGo60.Tests.Fakes;

internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessInvocation> Invocations { get; } = [];

    public ProcessInvocation? LastInvocation { get; private set; }

    public ProcessResult Result { get; init; } = new(0, string.Empty, string.Empty);

    public Func<ProcessInvocation, CancellationToken, ValueTask<ProcessResult>>? Handler { get; init; }

    public ValueTask<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default)
    {
        this.Invocations.Add(invocation);
        this.LastInvocation = invocation;
        return this.Handler is null
            ? ValueTask.FromResult(this.Result)
            : this.Handler(invocation, cancellationToken);
    }
}
