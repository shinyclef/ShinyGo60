using System.Text.Json;

namespace ShinyGo60.Diagnostics;

public sealed class JsonLineDiagnosticSink : IDiagnosticSink, IDisposable
{
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly TextWriter writer;

    public JsonLineDiagnosticSink(TextWriter writer)
    {
        this.writer = writer;
    }

    public async ValueTask WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);

        string json = JsonSerializer.Serialize(diagnosticEvent);

        await this.writeLock.WaitAsync(cancellationToken);
        try
        {
            await this.writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await this.writer.FlushAsync(cancellationToken);
        }
        finally
        {
            this.writeLock.Release();
        }
    }

    public void Dispose()
    {
        this.writeLock.Dispose();
    }
}
