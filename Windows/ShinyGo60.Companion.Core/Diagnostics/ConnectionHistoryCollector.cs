using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ShinyGo60.Diagnostics;
using ShinyGo60.Protocol.Transport;

namespace ShinyGo60.Companion.Core.Diagnostics;

public sealed class ConnectionHistoryCollector : IAsyncDisposable
{
    private readonly IDiagnosticSink sink;
    private readonly string? checkpointPath;
    private readonly object sync = new();
    private ConnectionHistoryPosition position;
    private CancellationTokenSource? cancellation;
    private Task collection = Task.CompletedTask;
    private bool checkpointLoaded;
    private IKeyboardTransport? unsupportedTransport;
    private long lastControlActivity;

    public ConnectionHistoryCollector(IDiagnosticSink sink, string? checkpointPath = null)
    {
        this.sink = sink;
        this.checkpointPath = checkpointPath;
    }

    // Called before dispatching control work. Never wait for a diagnostic read here.
    public void Pause()
    {
        lock (this.sync)
        {
            this.lastControlActivity = Stopwatch.GetTimestamp();
            this.cancellation?.Cancel();
        }
    }

    public void RequestCollection(IKeyboardTransport transport)
    {
        if (transport is not IKeyboardConnectionHistory reader)
        {
            return;
        }

        lock (this.sync)
        {
            if (!this.collection.IsCompleted || ReferenceEquals(this.unsupportedTransport, transport))
            {
                return;
            }

            this.cancellation?.Dispose();
            this.cancellation = new CancellationTokenSource();
            CancellationToken token = this.cancellation.Token;
            this.collection = Task.Run(() => this.CollectAsync(transport, reader, token), CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        Task pending;
        lock (this.sync)
        {
            this.cancellation?.Cancel();
            pending = this.collection;
        }

        await pending.ConfigureAwait(false);
        lock (this.sync)
        {
            this.cancellation?.Dispose();
            this.cancellation = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task CollectAsync(IKeyboardTransport transport, IKeyboardConnectionHistory reader, CancellationToken cancellationToken)
    {
        try
        {
            TimeSpan quietPeriod;
            lock (this.sync)
            {
                quietPeriod = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(this.lastControlActivity);
            }

            if (quietPeriod > TimeSpan.Zero)
            {
                await Task.Delay(quietPeriod, cancellationToken).ConfigureAwait(false);
            }

            await this.LoadCheckpointAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset started = DateTimeOffset.UtcNow;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            ReadOnlyMemory<byte> bytes;
            try
            {
                bytes = await reader.ReadConnectionHistoryAsync(this.position, timeout.Token).ConfigureAwait(false);
            }
            catch (NotSupportedException exception)
            {
                this.unsupportedTransport = transport;
                await this.WriteAsync("firmware_history_unsupported", exception.Message, DiagnosticLevel.Warning).ConfigureAwait(false);
                return;
            }

            DateTimeOffset received = DateTimeOffset.UtcNow;
            if (bytes.IsEmpty)
            {
                this.unsupportedTransport = transport;
                await this.WriteAsync("firmware_history_unavailable", "This firmware does not expose production connection history.")
                    .ConfigureAwait(false);
                return;
            }

            ConnectionHistory history = ConnectionHistory.Decode(bytes.Span);
            await this.RecordAsync(history, started, received).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Control traffic and session shutdown preempt this optional work.
        }
        catch (Exception exception)
        {
            // Contain failures at the optional external GATT/file boundary, outside the control loop.
            await this.WriteAsync("firmware_history_failed", "Connection history collection failed.", DiagnosticLevel.Warning,
                new Dictionary<string, string>
                {
                    ["exceptionType"] = exception.GetType().Name,
                    ["hresult"] = $"0x{exception.HResult:X8}",
                }).ConfigureAwait(false);
        }
    }

    private async Task LoadCheckpointAsync(CancellationToken cancellationToken)
    {
        if (this.checkpointLoaded)
        {
            return;
        }

        if (this.checkpointPath is not null && File.Exists(this.checkpointPath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(this.checkpointPath, cancellationToken).ConfigureAwait(false);
                this.position = JsonSerializer.Deserialize<ConnectionHistoryPosition>(json);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                await this.WriteAsync("firmware_history_checkpoint_failed", "Saved collection progress could not be read; retained events will be replayed.",
                    DiagnosticLevel.Warning).ConfigureAwait(false);
            }
        }

        this.checkpointLoaded = true;
    }

    private async Task RecordAsync(ConnectionHistory history, DateTimeOffset started, DateTimeOffset received)
    {
        string boot = history.BootId.ToString("X8", CultureInfo.InvariantCulture);
        bool changedBoot = this.position.BootId != history.BootId;
        if (changedBoot)
        {
            await this.WriteAsync("firmware_boot", this.position.BootId == 0
                ? "Observed the keyboard's current boot. Earlier history may be unavailable."
                : "The keyboard restarted. RAM history from its previous boot is no longer available.", DiagnosticLevel.Information,
                new Dictionary<string, string>
                {
                    ["bootId"] = boot,
                    ["previousBootId"] = this.position.BootId.ToString("X8", CultureInfo.InvariantCulture),
                    ["uptimeMs"] = history.UptimeMs.ToString(CultureInfo.InvariantCulture),
                    ["resetCause"] = $"0x{history.ResetCause:X8}",
                    ["resetResult"] = history.ResetResult.ToString(CultureInfo.InvariantCulture),
                }).ConfigureAwait(false);
            this.position = new ConnectionHistoryPosition(history.BootId, 0, 0);
        }

        foreach (ConnectionHistory.Entry entry in history.Entries)
        {
            uint previous = entry.Critical ? this.position.CriticalSequence : this.position.RoutineSequence;
            uint distance = unchecked(entry.Sequence - previous);
            if (distance == 0 || distance > int.MaxValue)
            {
                continue;
            }

            uint ageMs = unchecked(history.UptimeMs - entry.UptimeMs);
            Dictionary<string, string> properties = ConnectionHistoryDescription.Describe(entry);
            properties["bootId"] = boot;
            properties["firmwareVersion"] = history.FirmwareVersion;
            properties["formatVersion"] = ConnectionHistory.FormatVersion.ToString(CultureInfo.InvariantCulture);
            properties["sequence"] = entry.Sequence.ToString(CultureInfo.InvariantCulture);
            properties["stream"] = entry.Critical ? "critical" : "routine";
            properties["missedEvents"] = (distance - 1).ToString(CultureInfo.InvariantCulture);
            properties["uptimeMs"] = entry.UptimeMs.ToString(CultureInfo.InvariantCulture);
            properties["downloadStartedUtc"] = started.ToString("O");
            properties["downloadedUtc"] = received.ToString("O");
            properties["eventTimeEarliestUtc"] = started.AddMilliseconds(-ageMs).ToString("O");
            properties["eventTimeLatestUtc"] = received.AddMilliseconds(-ageMs).ToString("O");
            properties["eventTimeBasis"] = "snapshot_uptime_modulo_2^32_ms; assumes event age below 49.7 days";
            if (distance > 1)
            {
                await this.WriteAsync("firmware_history_overwritten", "Retained history has a gap; these events were not collected.",
                    DiagnosticLevel.Warning, new Dictionary<string, string>
                    {
                        ["bootId"] = boot, ["stream"] = properties["stream"], ["missedEvents"] = properties["missedEvents"],
                    }).ConfigureAwait(false);
            }

            await this.WriteAsync("firmware_connection_event", properties["description"], DiagnosticLevel.Information, properties).ConfigureAwait(false);
            this.position = entry.Critical ? this.position with { CriticalSequence = entry.Sequence } : this.position with { RoutineSequence = entry.Sequence };
        }

        if (this.checkpointPath is not null)
        {
            string temporary = this.checkpointPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this.position)).ConfigureAwait(false);
            File.Move(temporary, this.checkpointPath, overwrite: true);
        }

        await this.WriteAsync("firmware_history_collected", "Downloaded retained connection history.", DiagnosticLevel.Information,
            new Dictionary<string, string>
            {
                ["bootId"] = boot, ["firmwareVersion"] = history.FirmwareVersion,
                ["formatVersion"] = ConnectionHistory.FormatVersion.ToString(CultureInfo.InvariantCulture),
                ["criticalCapacity"] = history.CriticalCapacity.ToString(CultureInfo.InvariantCulture),
                ["routineCapacity"] = history.RoutineCapacity.ToString(CultureInfo.InvariantCulture),
                ["criticalTotal"] = history.CriticalTotal.ToString(CultureInfo.InvariantCulture),
                ["routineTotal"] = history.RoutineTotal.ToString(CultureInfo.InvariantCulture),
                ["collectionComplete"] = (this.position.CriticalSequence == history.CriticalTotal &&
                    this.position.RoutineSequence == history.RoutineTotal).ToString(),
                ["downloadedUtc"] = received.ToString("O"),
            }).ConfigureAwait(false);

    }

    private ValueTask WriteAsync(string name, string message, DiagnosticLevel level = DiagnosticLevel.Information,
        IReadOnlyDictionary<string, string>? properties = null) =>
        this.sink.WriteAsync(new DiagnosticEvent(DateTimeOffset.UtcNow, level, "companion.history", name, message, properties));
}
