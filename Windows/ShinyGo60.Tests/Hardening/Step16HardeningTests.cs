using System.Globalization;
using ShinyGo60.Companion.Core.Control;
using ShinyGo60.Companion.Core.Telemetry;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Tests.Companion;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Hardening;

internal static class Step16HardeningTests
{
    private const int BurstEventCount = 20_000;
    private const int RandomPacketCount = 50_000;
    private const int SessionCycleCount = 5_000;
    private static readonly LayoutFingerprint Layout = new(0x0123456789ABCDEF);

    public static async ValueTask RunAsync()
    {
        VerifyProtocolDecoderUnderCorruption();
        VerifyTelemetryBurstConvergence();
        VerifyRepeatedLayerSessionCleanup();
        await CompanionServiceTests.RunStressAsync();
    }

    private static void VerifyProtocolDecoderUnderCorruption()
    {
        int acceptedPacketCount = 0;
        string vectorDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Protocol", "Vectors");
        string[] vectorPaths = Directory.GetFiles(vectorDirectory, "*.bytes").Order(StringComparer.Ordinal).ToArray();
        AssertEx.Equal(15, vectorPaths.Length);

        foreach (string vectorPath in vectorPaths)
        {
            byte[] vector = ReadVector(vectorPath);
            AssertEx.True(VerifyStableDecode(vector), $"Golden vector '{Path.GetFileName(vectorPath)}' should decode.");
            acceptedPacketCount++;

            for (int byteIndex = 0; byteIndex < vector.Length; byteIndex++)
            {
                for (int bitIndex = 0; bitIndex < 8; bitIndex++)
                {
                    byte[] mutation = (byte[])vector.Clone();
                    mutation[byteIndex] ^= checked((byte)(1 << bitIndex));
                    if (VerifyStableDecode(mutation))
                    {
                        acceptedPacketCount++;
                    }
                }
            }
        }

        Random random = new(0x5A17E516);
        ProtocolMessageType[] messageTypes = Enum.GetValues<ProtocolMessageType>();
        byte packedVersion = checked((byte)((ProtocolVersion.Current.Major << 4) | ProtocolVersion.Current.Minor));
        byte[] packet = new byte[ProtocolPacketCodec.PacketSize];
        for (int sample = 0; sample < RandomPacketCount; sample++)
        {
            random.NextBytes(packet);
            packet[0] = (byte)'S';
            packet[1] = (byte)'G';
            packet[2] = packedVersion;
            packet[3] = (byte)messageTypes[sample % messageTypes.Length];
            if (VerifyStableDecode(packet))
            {
                acceptedPacketCount++;
            }
        }

        for (int length = 0; length <= ProtocolPacketCodec.PacketSize * 2; length++)
        {
            byte[] variableLengthPacket = new byte[length];
            random.NextBytes(variableLengthPacket);
            bool decoded = ProtocolPacketCodec.TryDecode(variableLengthPacket, out _);
            if (length != ProtocolPacketCodec.PacketSize)
            {
                AssertEx.True(!decoded, $"A packet with length {length} should be rejected.");
            }
        }

        AssertEx.True(
            acceptedPacketCount > vectorPaths.Length,
            "The mutation run should exercise accepted packets as well as rejected corruption.");
    }

    private static bool VerifyStableDecode(ReadOnlySpan<byte> packet)
    {
        if (!ProtocolPacketCodec.TryDecode(packet, out ProtocolMessage? message))
        {
            return false;
        }

        AssertEx.True(message is not null, "A successful decode must return a message.");
        byte[] encoded = ProtocolPacketCodec.Encode(message!);
        AssertEx.SequenceEqual(packet, encoded);
        AssertEx.True(
            ProtocolPacketCodec.TryDecode(encoded, out ProtocolMessage? decodedAgain),
            "A decoded packet should remain valid after encoding.");
        AssertEx.Equal(message, decodedAgain);
        return true;
    }

    private static void VerifyTelemetryBurstConvergence()
    {
        LayoutManifest manifest = CreateManifest();
        ProtocolMessage.HelloResult hello = SuccessfulHello(1);
        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = 2,
        };

        LayerStateTracker layerTracker = new(manifest);
        layerTracker.BeginSession(hello);
        AssertEx.Equal(
            LayerTelemetryApplyResult.AppliedSnapshot,
            layerTracker.Apply(new ProtocolMessage.StateSnapshot(1, 1, LayerState(1, 0, null, 0))));
        int unexpectedLayerResults = 0;
        Parallel.For(2, BurstEventCount + 2, options, revision =>
        {
            byte layerId = checked((byte)(revision % manifest.Layers.Count));
            LayerTelemetryApplyResult result = layerTracker.Apply(
                new ProtocolMessage.LayerChanged(1, 0, LayerState(checked((uint)revision), layerId, null, 0)));
            if (result is not LayerTelemetryApplyResult.Applied and
                not LayerTelemetryApplyResult.AppliedAfterGap and
                not LayerTelemetryApplyResult.StaleRevision)
            {
                Interlocked.Increment(ref unexpectedLayerResults);
            }
        });

        uint finalRevision = BurstEventCount + 1;
        byte finalLayerId = checked((byte)(finalRevision % manifest.Layers.Count));
        AssertEx.Equal(0, unexpectedLayerResults);
        AssertEx.Equal(finalRevision, layerTracker.CurrentState?.Revision);
        AssertEx.Equal(finalLayerId, layerTracker.CurrentState?.EffectiveLayer.Id);

        BatteryStateIndicators bothFresh =
            BatteryStateIndicators.LeftAvailable | BatteryStateIndicators.RightAvailable;
        BatteryStateTracker batteryTracker = new(manifest);
        batteryTracker.BeginSession(hello);
        AssertEx.Equal(
            BatteryTelemetryApplyResult.AppliedSnapshot,
            batteryTracker.Apply(new ProtocolMessage.BatterySnapshot(
                1,
                1,
                new ProtocolMessage.BatteryState(1, 50, 50, bothFresh))));
        int unexpectedBatteryResults = 0;
        Parallel.For(2, BurstEventCount + 2, options, revision =>
        {
            ProtocolMessage.BatteryState state = new(
                checked((uint)revision),
                checked((byte)(revision % 101)),
                checked((byte)((revision * 3) % 101)),
                bothFresh);
            BatteryTelemetryApplyResult result = batteryTracker.Apply(new ProtocolMessage.BatteryChanged(1, state));
            if (result is not BatteryTelemetryApplyResult.Applied and
                not BatteryTelemetryApplyResult.AppliedAfterGap and
                not BatteryTelemetryApplyResult.StaleRevision)
            {
                Interlocked.Increment(ref unexpectedBatteryResults);
            }
        });

        AssertEx.Equal(0, unexpectedBatteryResults);
        AssertEx.Equal(finalRevision, batteryTracker.CurrentState?.Revision);
        AssertEx.Equal((byte?)(finalRevision % 101), batteryTracker.CurrentState?.Left.Level);
        AssertEx.Equal((byte?)((finalRevision * 3) % 101), batteryTracker.CurrentState?.Right.Level);
    }

    private static void VerifyRepeatedLayerSessionCleanup()
    {
        LayerCommandStateMachine machine = new(CreateManifest());
        for (uint cycle = 1; cycle <= SessionCycleCount; cycle++)
        {
            machine.BeginSession(SuccessfulHello(cycle));
            AssertEx.Equal(
                LayerTelemetryApplyResult.AppliedSnapshot,
                machine.StateTracker.Apply(new ProtocolMessage.StateSnapshot(
                    cycle,
                    1,
                    LayerState(1, 0, null, 0))));

            uint activationId = machine.QueueMomentaryPress(1, 20);
            ProtocolMessage.PressMomentaryLayerCommand press =
                (ProtocolMessage.PressMomentaryLayerCommand)machine.TryStartNextCommand()!;
            if (cycle % 3 == 0)
            {
                machine.EndSession();
                AssertSessionCleared(machine);
                continue;
            }

            uint? releaseCommandId = cycle % 3 == 1
                ? machine.QueueMomentaryRelease(activationId)
                : null;
            AssertEx.Equal(
                LayerCommandResponseResult.CommandAccepted,
                machine.ApplyResponse(new ProtocolMessage.CommandResult(
                    cycle,
                    press.CommandId,
                    CommandStatus.Applied,
                    LayerState(2, 1, null, 1))));

            if (releaseCommandId.HasValue)
            {
                ProtocolMessage.ReleaseMomentaryLayerCommand release =
                    (ProtocolMessage.ReleaseMomentaryLayerCommand)machine.TryStartNextCommand()!;
                AssertEx.Equal(releaseCommandId.Value, release.CommandId);
                AssertEx.Equal(
                    LayerCommandResponseResult.CommandAccepted,
                    machine.ApplyResponse(new ProtocolMessage.CommandResult(
                        cycle,
                        release.CommandId,
                        CommandStatus.Applied,
                        LayerState(3, 0, null, 0))));

                uint persistentCommandId = machine.QueuePersistentLayer(2);
                ProtocolMessage.SetPersistentLayerCommand persistent =
                    (ProtocolMessage.SetPersistentLayerCommand)machine.TryStartNextCommand()!;
                AssertEx.Equal(persistentCommandId, persistent.CommandId);
                AssertEx.Equal(
                    LayerCommandResponseResult.CommandAccepted,
                    machine.ApplyResponse(new ProtocolMessage.CommandResult(
                        cycle,
                        persistent.CommandId,
                        CommandStatus.Applied,
                        LayerState(4, 2, 2, 0))));
            }

            machine.EndSession();
            AssertSessionCleared(machine);
        }
    }

    private static void AssertSessionCleared(LayerCommandStateMachine machine)
    {
        AssertEx.True(!machine.HasSession, "A completed transport session should be cleared.");
        AssertEx.Equal(0, machine.MomentaryActivationCount);
        AssertEx.Equal(0, machine.QueuedCommandCount);
        AssertEx.Equal<ProtocolMessage?>(null, machine.PendingCommand);
        AssertEx.Equal<LayerTelemetryState?>(null, machine.StateTracker.CurrentState);
    }

    private static byte[] ReadVector(string path)
    {
        string[] tokens = File.ReadAllText(path).Split([',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return tokens
            .Select(token => byte.Parse(token.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static ProtocolMessage.HelloResult SuccessfulHello(uint sessionId)
    {
        return new ProtocolMessage.HelloResult(
            1,
            HelloStatus.Success,
            ProtocolCapability.StateTelemetry |
                ProtocolCapability.PersistentLayer |
                ProtocolCapability.MomentaryLayer |
                ProtocolCapability.BatteryTelemetry |
                ProtocolCapability.AdaptiveBluetoothLatency,
            sessionId,
            Layout);
    }

    private static ProtocolMessage.LayerState LayerState(
        uint revision,
        byte effectiveLayer,
        byte? persistentLayer,
        byte momentaryCount)
    {
        LayerStateIndicators indicators =
            (persistentLayer.HasValue ? LayerStateIndicators.PersistentLayerActive : LayerStateIndicators.None) |
            (momentaryCount > 0 ? LayerStateIndicators.MomentaryLayerActive : LayerStateIndicators.None);
        return new ProtocolMessage.LayerState(
            revision,
            effectiveLayer,
            persistentLayer,
            momentaryCount,
            indicators);
    }

    private static LayoutManifest CreateManifest()
    {
        return new LayoutManifest(
            LayoutManifest.CurrentSchemaVersion,
            ProtocolVersion.Current,
            "sg60-v1-0123456789abcdef0123456789abcdef",
            new string('a', 64),
            "fixture-revision",
            [
                new LayerDefinition(0, "Home"),
                new LayerDefinition(1, "Navigation"),
                new LayerDefinition(2, "Keypad"),
            ],
            DateTimeOffset.UnixEpoch);
    }
}
