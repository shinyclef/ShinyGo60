namespace ShinyGo60.Builder.Core.Processes;

public interface IProcessRunner
{
    ValueTask<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default);
}
