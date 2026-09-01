namespace ShinyGo60.Diagnostics;

public interface IDiagnosticSink
{
    ValueTask WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default);
}
