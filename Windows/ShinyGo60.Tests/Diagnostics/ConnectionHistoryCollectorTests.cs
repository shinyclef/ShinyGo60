using System.Buffers.Binary;
using System.Collections.Concurrent;
using ShinyGo60.Companion.Core.Diagnostics;
using ShinyGo60.Diagnostics;
using ShinyGo60.Protocol.Transport;
using ShinyGo60.Tests.Protocol;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Diagnostics;

internal static class ConnectionHistoryCollectorTests
{
    public static async ValueTask RunAsync()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "Output", "Tests", "History-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string checkpoint = Path.Combine(directory, "position.json");
        RecordingSink sink = new();
        FakeHistoryTransport transport = new();
        await using (ConnectionHistoryCollector collector = new(sink, checkpoint))
        {
            collector.RequestCollection(transport);
            await sink.Collected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await collector.StopAsync();
        }

        AssertEx.True(File.Exists(checkpoint), "Collection progress was not persisted.");
        AssertEx.Equal(2, sink.Events.Count(value => value.EventName == "firmware_connection_event"));
        DiagnosticEvent first = sink.Events.First(value => value.EventName == "firmware_connection_event");
        AssertEx.Equal("critical", first.Properties!["stream"]);
        AssertEx.Equal("connection timeout", first.Properties["resultDescription"]);
        AssertEx.True(first.Properties.ContainsKey("eventTimeEarliestUtc"), "Event time was not distinguished from receipt.");

        RecordingSink restartedSink = new();
        await using (ConnectionHistoryCollector restarted = new(restartedSink, checkpoint))
        {
            restarted.RequestCollection(transport);
            await restartedSink.Collected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await restarted.StopAsync();
        }

        AssertEx.Equal(new ConnectionHistoryPosition(42, 19, 0), transport.RequestedAfter);
        AssertEx.Equal(0, restartedSink.Events.Count(value => value.EventName == "firmware_connection_event"));
        File.Delete(checkpoint);
        RecordingSink interruptedSink = new();
        FakeHistoryTransport interruptedTransport = new() { WaitForCancellation = true };
        await using (ConnectionHistoryCollector interrupted = new(interruptedSink, checkpoint))
        {
            interrupted.RequestCollection(interruptedTransport);
            await interruptedTransport.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            interrupted.Pause();
            await interrupted.StopAsync();
        }

        AssertEx.True(File.Exists(checkpoint), "Complete records from an interrupted download were not saved.");
        AssertEx.Equal(2, interruptedSink.Events.Count(value => value.EventName == "firmware_connection_event"));
        DiagnosticEvent partial = interruptedSink.Events.Single(value => value.EventName == "firmware_history_collected");
        AssertEx.Equal(bool.FalseString, partial.Properties!["collectionComplete"]);
        File.Delete(checkpoint);
        Directory.Delete(directory);
    }

    private sealed class RecordingSink : IDiagnosticSink
    {
        public ConcurrentQueue<DiagnosticEvent> Events { get; } = new();
        public TaskCompletionSource Collected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default)
        {
            this.Events.Enqueue(diagnosticEvent);
            if (diagnosticEvent.EventName == "firmware_history_collected")
            {
                this.Collected.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHistoryTransport : IKeyboardTransport, IKeyboardConnectionHistory
    {
        public ConnectionHistoryPosition RequestedAfter { get; private set; }
        public bool WaitForCancellation { get; init; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<KeyboardPacketReceivedEventArgs>? PacketReceived { add { } remove { } }
        public TransportKind Kind => TransportKind.Bluetooth;
        public bool IsConnected => true;
        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask<ReadOnlyMemory<byte>> ReadConnectionHistoryAsync(ConnectionHistoryPosition after, CancellationToken cancellationToken = default)
        {
            this.RequestedAfter = after;
            this.ReadStarted.TrySetResult();
            if (this.WaitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The transport returns complete records received before control activity interrupted the read.
                }
            }

            byte[] snapshot = ConnectionHistoryTests.CreateSnapshot(42, 18, 19);
            if (this.WaitForCancellation)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(snapshot.AsSpan(12), 20);
            }

            return snapshot;
        }
    }
}
