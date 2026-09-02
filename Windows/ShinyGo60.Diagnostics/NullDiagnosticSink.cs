namespace ShinyGo60.Diagnostics;

public sealed class NullDiagnosticSink : IDiagnosticSink
{
    private NullDiagnosticSink()
    {
    }

    public static NullDiagnosticSink Instance { get; } = new();

    public ValueTask WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
