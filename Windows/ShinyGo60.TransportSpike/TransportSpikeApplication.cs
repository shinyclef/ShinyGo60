using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using ShinyGo60.Companion.Core.Telemetry;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Protocol.Transport;

namespace ShinyGo60.TransportSpike;

internal static class TransportSpikeApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            SpikeOptions options = SpikeOptions.Parse(args);
            LayoutManifest manifest = await LayoutManifestJson.ReadAsync(options.ManifestPath).ConfigureAwait(false);
            if (manifest.ProtocolVersion != ProtocolVersion.Current)
            {
                throw new InvalidDataException(
                    $"Manifest protocol {manifest.ProtocolVersion} is unsupported; expected {ProtocolVersion.Current}.");
            }

            foreach (TransportKind kind in options.GetTransportSequence())
            {
                await RunTransportAsync(kind, options, manifest).ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return 1;
        }
    }

    private static async Task RunTransportAsync(
        TransportKind kind,
        SpikeOptions options,
        LayoutManifest manifest)
    {
        await using IKeyboardTransport transport = CreateTransport(kind);
        LayerStateTracker tracker = new(manifest);
        using CancellationTokenSource connectTimeout = new(options.Timeout);

        Console.WriteLine($"Connecting over {kind}...");
        await transport.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        Console.WriteLine($"Connected over {kind}.");

        if (options.IsWatch)
        {
            await WatchLayerChangesAsync(transport, tracker, manifest, options).ConfigureAwait(false);
            await transport.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        TimeSpan[] durations = new TimeSpan[options.ExchangeCount];
        for (int exchange = 1; exchange <= options.ExchangeCount; exchange++)
        {
            using CancellationTokenSource exchangeTimeout = new(options.Timeout);
            SessionSnapshot result = await OpenTelemetrySessionAsync(
                    transport,
                    tracker,
                    manifest,
                    exchangeTimeout.Token)
                .ConfigureAwait(false);
            durations[exchange - 1] = result.Duration;

            Console.WriteLine(
                $"  Snapshot {exchange}: {result.Duration.TotalMilliseconds:F2} ms, session {result.SessionId:X8}, " +
                $"revision {result.State.Revision}, layer {result.State.EffectiveLayer.Id} ({result.State.EffectiveLayer.Name})");
        }

        await transport.DisconnectAsync().ConfigureAwait(false);
        double average = durations.Average(duration => duration.TotalMilliseconds);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{kind} Hello+GetState summary: count={durations.Length}, min={durations.Min().TotalMilliseconds:F2} ms, " +
                $"mean={average:F2} ms, max={durations.Max().TotalMilliseconds:F2} ms"));
    }

    private static async Task WatchLayerChangesAsync(
        IKeyboardTransport transport,
        LayerStateTracker tracker,
        LayoutManifest manifest,
        SpikeOptions options)
    {
        EventHandler<KeyboardPacketReceivedEventArgs> packetHandler = (_, args) => HandleLayerEvent(tracker, args.Packet);
        transport.PacketReceived += packetHandler;
        try
        {
            using CancellationTokenSource exchangeTimeout = new(options.Timeout);
            SessionSnapshot result = await OpenTelemetrySessionAsync(
                    transport,
                    tracker,
                    manifest,
                    exchangeTimeout.Token)
                .ConfigureAwait(false);
            Console.WriteLine(
                $"Initial state: revision {result.State.Revision}, layer {result.State.EffectiveLayer.Id} " +
                $"({result.State.EffectiveLayer.Name}).");
            Console.WriteLine(
                $"Watching {transport.Kind} for {options.WatchDuration.TotalSeconds:F0} seconds. Use layer keys on both halves now...");
            await Task.Delay(options.WatchDuration).ConfigureAwait(false);
            Console.WriteLine("Layer watch complete.");
        }
        finally
        {
            transport.PacketReceived -= packetHandler;
        }
    }

    private static void HandleLayerEvent(LayerStateTracker tracker, ReadOnlyMemory<byte> packet)
    {
        if (!ProtocolPacketCodec.TryDecode(packet.Span, out ProtocolMessage? decoded) ||
            decoded is not ProtocolMessage.LayerChanged changed)
        {
            Console.Error.WriteLine("WARNING: An unsolicited transport packet was not a valid LayerChanged event.");
            return;
        }

        LayerTelemetryApplyResult result = tracker.Apply(changed);
        if (result is LayerTelemetryApplyResult.Applied or LayerTelemetryApplyResult.AppliedAfterGap)
        {
            LayerTelemetryState state = tracker.CurrentState!;
            string gap = result == LayerTelemetryApplyResult.AppliedAfterGap ? " (converged after a missed revision)" : string.Empty;
            Console.WriteLine(
                $"  Layer changed: revision {state.Revision}, layer {state.EffectiveLayer.Id} ({state.EffectiveLayer.Name}){gap}");
        }
        else if (result is not LayerTelemetryApplyResult.Duplicate and not LayerTelemetryApplyResult.StaleRevision)
        {
            Console.Error.WriteLine($"WARNING: Layer event was not applied: {result}.");
        }
    }

    private static async Task<SessionSnapshot> OpenTelemetrySessionAsync(
        IKeyboardTransport transport,
        LayerStateTracker tracker,
        LayoutManifest manifest,
        CancellationToken cancellationToken)
    {
        LayoutFingerprint expectedLayout = LayoutFingerprint.FromLayoutIdentifier(manifest.LayoutIdentifier);
        ushort nonce = CreateNonce();
        ProtocolMessage.HelloRequest hello = new(nonce, ProtocolCapability.StateTelemetry, expectedLayout);
        long started = Stopwatch.GetTimestamp();
        ReadOnlyMemory<byte> helloBytes = await transport
            .ExchangeAsync(ProtocolPacketCodec.Encode(hello), cancellationToken)
            .ConfigureAwait(false);
        if (!ProtocolPacketCodec.TryDecode(helloBytes.Span, out ProtocolMessage? decodedHello) ||
            decodedHello is not ProtocolMessage.HelloResult helloResult)
        {
            throw new InvalidDataException($"The {transport.Kind} response was not a valid protocol-v1 HelloResult.");
        }

        if (helloResult.ClientNonce != nonce)
        {
            throw new InvalidDataException($"The {transport.Kind} HelloResult did not match the request nonce.");
        }

        tracker.BeginSession(helloResult);
        if (helloResult.SelectedCapabilities != ProtocolCapability.StateTelemetry)
        {
            throw new InvalidDataException($"The {transport.Kind} session selected unexpected capabilities.");
        }

        uint requestId = CreateRequestId();
        ProtocolMessage.GetStateRequest getState = new(helloResult.SessionId, requestId);
        ReadOnlyMemory<byte> snapshotBytes = await transport
            .ExchangeAsync(ProtocolPacketCodec.Encode(getState), cancellationToken)
            .ConfigureAwait(false);
        TimeSpan duration = Stopwatch.GetElapsedTime(started);
        if (!ProtocolPacketCodec.TryDecode(snapshotBytes.Span, out ProtocolMessage? decodedSnapshot) ||
            decodedSnapshot is not ProtocolMessage.StateSnapshot snapshot)
        {
            throw new InvalidDataException($"The {transport.Kind} response to GetState was not a valid StateSnapshot.");
        }

        if (snapshot.SessionId != helloResult.SessionId || snapshot.RequestId != requestId)
        {
            throw new InvalidDataException($"The {transport.Kind} StateSnapshot did not match its session and request.");
        }

        LayerTelemetryApplyResult applyResult = tracker.Apply(snapshot);
        if (applyResult != LayerTelemetryApplyResult.AppliedSnapshot)
        {
            throw new InvalidDataException($"The {transport.Kind} StateSnapshot could not initialize layer state: {applyResult}.");
        }

        return new SessionSnapshot(helloResult.SessionId, tracker.CurrentState!, duration);
    }

    private static IKeyboardTransport CreateTransport(TransportKind kind)
    {
        return kind switch
        {
            TransportKind.Usb => new UsbSerialKeyboardTransport(),
            TransportKind.Bluetooth => new BluetoothGattKeyboardTransport(),
            _ => throw new InvalidOperationException($"Unsupported transport kind: {kind}."),
        };
    }

    private static ushort CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        ushort nonce;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            nonce = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        }
        while (nonce == 0);

        return nonce;
    }

    private static uint CreateRequestId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        uint requestId;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            requestId = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        while (requestId == 0);

        return requestId;
    }

    private sealed record SessionSnapshot(uint SessionId, LayerTelemetryState State, TimeSpan Duration);
}
