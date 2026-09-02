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
        LayerStateTracker layerTracker = new(manifest);
        BatteryStateTracker batteryTracker = new(manifest);
        using CancellationTokenSource connectTimeout = new(options.Timeout);

        Console.WriteLine($"Connecting over {kind}...");
        await transport.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        Console.WriteLine($"Connected over {kind}.");

        if (options.IsWatch)
        {
            await WatchTelemetryAsync(transport, layerTracker, batteryTracker, manifest, options).ConfigureAwait(false);
            await transport.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        TimeSpan[] durations = new TimeSpan[options.ExchangeCount];
        for (int exchange = 1; exchange <= options.ExchangeCount; exchange++)
        {
            using CancellationTokenSource exchangeTimeout = new(options.Timeout);
            SessionSnapshot result = await OpenTelemetrySessionAsync(
                    transport,
                    layerTracker,
                    batteryTracker,
                    manifest,
                    exchangeTimeout.Token)
                .ConfigureAwait(false);
            durations[exchange - 1] = result.Duration;

            Console.WriteLine(
                $"  Snapshot {exchange}: {result.Duration.TotalMilliseconds:F2} ms, session {result.SessionId:X8}, " +
                $"layer revision {result.LayerState.Revision}, layer {result.LayerState.EffectiveLayer.Id} " +
                $"({result.LayerState.EffectiveLayer.Name}), battery revision {result.BatteryState.Revision}, " +
                $"left {FormatBattery(result.BatteryState.Left)}, right {FormatBattery(result.BatteryState.Right)}");
        }

        await transport.DisconnectAsync().ConfigureAwait(false);
        double average = durations.Average(duration => duration.TotalMilliseconds);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{kind} Hello+GetState+GetBattery summary: count={durations.Length}, " +
                $"min={durations.Min().TotalMilliseconds:F2} ms, " +
                $"mean={average:F2} ms, max={durations.Max().TotalMilliseconds:F2} ms"));
    }

    private static async Task WatchTelemetryAsync(
        IKeyboardTransport transport,
        LayerStateTracker layerTracker,
        BatteryStateTracker batteryTracker,
        LayoutManifest manifest,
        SpikeOptions options)
    {
        EventHandler<KeyboardPacketReceivedEventArgs> packetHandler =
            (_, args) => HandleTelemetryEvent(layerTracker, batteryTracker, args.Packet);
        transport.PacketReceived += packetHandler;
        try
        {
            using CancellationTokenSource exchangeTimeout = new(options.Timeout);
            SessionSnapshot result = await OpenTelemetrySessionAsync(
                    transport,
                    layerTracker,
                    batteryTracker,
                    manifest,
                    exchangeTimeout.Token)
                .ConfigureAwait(false);
            Console.WriteLine(
                $"Initial state: layer revision {result.LayerState.Revision}, layer {result.LayerState.EffectiveLayer.Id} " +
                $"({result.LayerState.EffectiveLayer.Name}); battery revision {result.BatteryState.Revision}, " +
                $"left {FormatBattery(result.BatteryState.Left)}, right {FormatBattery(result.BatteryState.Right)}.");
            Console.WriteLine(
                $"Watching {transport.Kind} for {options.WatchDuration.TotalSeconds:F0} seconds. " +
                "Battery heartbeats and layer changes will appear below...");
            await Task.Delay(options.WatchDuration).ConfigureAwait(false);
            Console.WriteLine("Telemetry watch complete.");
        }
        finally
        {
            transport.PacketReceived -= packetHandler;
        }
    }

    private static void HandleTelemetryEvent(
        LayerStateTracker layerTracker,
        BatteryStateTracker batteryTracker,
        ReadOnlyMemory<byte> packet)
    {
        if (!ProtocolPacketCodec.TryDecode(packet.Span, out ProtocolMessage? decoded) || decoded is null)
        {
            Console.Error.WriteLine("WARNING: An unsolicited transport packet was not a valid telemetry event.");
            return;
        }

        switch (decoded)
        {
            case ProtocolMessage.LayerChanged changed:
                HandleLayerEvent(layerTracker, changed);
                break;
            case ProtocolMessage.BatteryChanged changed:
                HandleBatteryEvent(batteryTracker, changed);
                break;
            default:
                Console.Error.WriteLine($"WARNING: Unexpected unsolicited {decoded.Type} packet.");
                break;
        }
    }

    private static void HandleLayerEvent(LayerStateTracker tracker, ProtocolMessage.LayerChanged changed)
    {
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

    private static void HandleBatteryEvent(BatteryStateTracker tracker, ProtocolMessage.BatteryChanged changed)
    {
        BatteryTelemetryApplyResult result = tracker.Apply(changed);
        if (result is BatteryTelemetryApplyResult.Applied or BatteryTelemetryApplyResult.AppliedAfterGap)
        {
            BatteryTelemetryState state = tracker.CurrentState!;
            string gap = result == BatteryTelemetryApplyResult.AppliedAfterGap
                ? " (converged after a missed revision)"
                : string.Empty;
            Console.WriteLine(
                $"  Battery changed: revision {state.Revision}, left {FormatBattery(state.Left)}, " +
                $"right {FormatBattery(state.Right)}{gap}");
        }
        else if (result is not BatteryTelemetryApplyResult.Duplicate and
                 not BatteryTelemetryApplyResult.StaleRevision and
                 not BatteryTelemetryApplyResult.AwaitingSnapshot)
        {
            Console.Error.WriteLine($"WARNING: Battery event was not applied: {result}.");
        }
    }

    private static async Task<SessionSnapshot> OpenTelemetrySessionAsync(
        IKeyboardTransport transport,
        LayerStateTracker layerTracker,
        BatteryStateTracker batteryTracker,
        LayoutManifest manifest,
        CancellationToken cancellationToken)
    {
        LayoutFingerprint expectedLayout = LayoutFingerprint.FromLayoutIdentifier(manifest.LayoutIdentifier);
        ushort nonce = CreateNonce();
        ProtocolCapability requestedCapabilities =
            ProtocolCapability.StateTelemetry | ProtocolCapability.BatteryTelemetry;
        ProtocolMessage.HelloRequest hello = new(nonce, requestedCapabilities, expectedLayout);
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

        layerTracker.BeginSession(helloResult);
        batteryTracker.BeginSession(helloResult);
        if (helloResult.SelectedCapabilities != requestedCapabilities)
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

        LayerTelemetryApplyResult layerApplyResult = layerTracker.Apply(snapshot);
        if (layerApplyResult != LayerTelemetryApplyResult.AppliedSnapshot)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} StateSnapshot could not initialize layer state: {layerApplyResult}.");
        }

        uint batteryRequestId = CreateRequestId();
        ProtocolMessage.GetBatteryRequest getBattery = new(helloResult.SessionId, batteryRequestId);
        ReadOnlyMemory<byte> batteryBytes = await transport
            .ExchangeAsync(ProtocolPacketCodec.Encode(getBattery), cancellationToken)
            .ConfigureAwait(false);
        duration = Stopwatch.GetElapsedTime(started);
        if (!ProtocolPacketCodec.TryDecode(batteryBytes.Span, out ProtocolMessage? decodedBattery) ||
            decodedBattery is not ProtocolMessage.BatterySnapshot batterySnapshot)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} response to GetBattery was not a valid BatterySnapshot.");
        }

        if (batterySnapshot.SessionId != helloResult.SessionId || batterySnapshot.RequestId != batteryRequestId)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} BatterySnapshot did not match its session and request.");
        }

        BatteryTelemetryApplyResult batteryApplyResult = batteryTracker.Apply(batterySnapshot);
        if (batteryApplyResult != BatteryTelemetryApplyResult.AppliedSnapshot)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} BatterySnapshot could not initialize battery state: {batteryApplyResult}.");
        }

        return new SessionSnapshot(
            helloResult.SessionId,
            layerTracker.CurrentState!,
            batteryTracker.CurrentState!,
            duration);
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

    private static string FormatBattery(BatteryReading reading)
    {
        return reading.Status switch
        {
            BatteryReadingStatus.Unavailable => "unavailable",
            BatteryReadingStatus.Fresh => $"{reading.Level}%",
            BatteryReadingStatus.Stale => $"{reading.Level}% (stale)",
            _ => throw new InvalidOperationException($"Unsupported battery status: {reading.Status}."),
        };
    }

    private sealed record SessionSnapshot(
        uint SessionId,
        LayerTelemetryState LayerState,
        BatteryTelemetryState BatteryState,
        TimeSpan Duration);
}
