using System.Globalization;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Protocol;

internal static class ProtocolCodecTests
{
    private static readonly LayoutFingerprint Layout = new(0xB4C690CEDFC730F3);

    public static ValueTask RunAsync()
    {
        VerifyGoldenVectors();
        VerifyMalformedPackets();
        VerifyEncodeBounds();
        return ValueTask.CompletedTask;
    }

    private static void VerifyGoldenVectors()
    {
        ProtocolMessage.LayerState snapshotState = new(42, 3, null, 0, LayerStateIndicators.None);
        ProtocolMessage.LayerState changedState = new(
            43,
            4,
            4,
            1,
            LayerStateIndicators.PersistentLayerActive | LayerStateIndicators.MomentaryLayerActive);
        ProtocolMessage.LayerState resultState = new(43, 4, 4, 0, LayerStateIndicators.PersistentLayerActive);
        ProtocolMessage.BatteryState batterySnapshotState = new(
            44,
            87,
            63,
            BatteryStateIndicators.LeftAvailable | BatteryStateIndicators.RightAvailable);
        ProtocolMessage.BatteryState batteryChangedState = new(
            45,
            86,
            63,
            BatteryStateIndicators.LeftAvailable |
            BatteryStateIndicators.RightAvailable |
            BatteryStateIndicators.RightStale);

        (string FileName, ProtocolMessage Message)[] vectors =
        [
            ("hello-request.bytes", new ProtocolMessage.HelloRequest(0x1234, (ProtocolCapability)0x1F, Layout)),
            ("hello-result.bytes", new ProtocolMessage.HelloResult(0x1234, HelloStatus.Success, (ProtocolCapability)0x1F, 0x89ABCDEF, Layout)),
            ("get-state.bytes", new ProtocolMessage.GetStateRequest(0x89ABCDEF, 0x01020304)),
            ("state-snapshot.bytes", new ProtocolMessage.StateSnapshot(0x89ABCDEF, 0x01020304, snapshotState)),
            ("layer-changed.bytes", new ProtocolMessage.LayerChanged(0x89ABCDEF, 0x11223344, changedState)),
            ("get-battery.bytes", new ProtocolMessage.GetBatteryRequest(0x89ABCDEF, 0x01020305)),
            ("battery-snapshot.bytes", new ProtocolMessage.BatterySnapshot(0x89ABCDEF, 0x01020305, batterySnapshotState)),
            ("battery-changed.bytes", new ProtocolMessage.BatteryChanged(0x89ABCDEF, batteryChangedState)),
            ("set-persistent-layer.bytes", new ProtocolMessage.SetPersistentLayerCommand(0x89ABCDEF, 0x11223344, 42, 4)),
            ("press-momentary-layer.bytes", new ProtocolMessage.PressMomentaryLayerCommand(0x89ABCDEF, 0x11223345, 43, 3, 20)),
            ("renew-momentary-layer.bytes", new ProtocolMessage.RenewMomentaryLayerCommand(0x89ABCDEF, 0x11223346, 0x11223345, 20)),
            ("release-momentary-layer.bytes", new ProtocolMessage.ReleaseMomentaryLayerCommand(0x89ABCDEF, 0x11223347, 0x11223345)),
            ("set-bluetooth-connection-mode.bytes", new ProtocolMessage.SetBluetoothConnectionModeCommand(
                0x89ABCDEF,
                0x11223348,
                BluetoothConnectionMode.Interactive)),
            ("command-result.bytes", new ProtocolMessage.CommandResult(0x89ABCDEF, 0x11223344, CommandStatus.Applied, resultState)),
            ("error.bytes", new ProtocolMessage.ErrorMessage(0x89ABCDEF, 0x11223344, 43, ProtocolErrorCode.StaleState, 0x10, 42)),
        ];

        foreach ((string fileName, ProtocolMessage expectedMessage) in vectors)
        {
            byte[] expectedPacket = ReadVector(fileName);
            AssertEx.Equal(ProtocolPacketCodec.PacketSize, expectedPacket.Length);
            AssertEx.SequenceEqual(expectedPacket, ProtocolPacketCodec.Encode(expectedMessage));
            AssertEx.True(
                ProtocolPacketCodec.TryDecode(expectedPacket, out ProtocolMessage? decodedMessage),
                $"Golden vector '{fileName}' should decode.");
            AssertEx.Equal(expectedMessage, decodedMessage);
        }
    }

    private static void VerifyMalformedPackets()
    {
        byte[] hello = ReadVector("hello-request.bytes");
        AssertRejected(hello.AsSpan(0, hello.Length - 1), "A truncated packet should be rejected.");

        byte[] wrongMagic = (byte[])hello.Clone();
        wrongMagic[0] = 0;
        AssertRejected(wrongMagic, "A packet with incorrect magic should be rejected.");

        byte[] wrongVersion = (byte[])hello.Clone();
        wrongVersion[2] = 0x10;
        AssertRejected(wrongVersion, "An unsupported version should be rejected.");

        byte[] unknownType = (byte[])hello.Clone();
        unknownType[3] = 0x7E;
        AssertRejected(unknownType, "An unknown message type should be rejected.");

        byte[] unknownCapability = (byte[])hello.Clone();
        unknownCapability[6] = 0x80;
        AssertRejected(unknownCapability, "An unknown capability bit should be rejected.");

        byte[] nonZeroReserved = (byte[])hello.Clone();
        nonZeroReserved[7] = 1;
        AssertRejected(nonZeroReserved, "A nonzero reserved field should be rejected.");

        byte[] invalidLayer = ReadVector("set-persistent-layer.bytes");
        invalidLayer[16] = ProtocolPacketCodec.NoLayer;
        AssertRejected(invalidLayer, "The reserved no-layer value should be rejected as a command target.");

        byte[] invalidLease = ReadVector("press-momentary-layer.bytes");
        invalidLease[17] = 0;
        AssertRejected(invalidLease, "A zero momentary lease should be rejected.");
        invalidLease[17] = ProtocolPacketCodec.MaximumLeaseUnits + 1;
        AssertRejected(invalidLease, "An over-limit momentary lease should be rejected.");

        byte[] invalidBluetoothMode = ReadVector("set-bluetooth-connection-mode.bytes");
        invalidBluetoothMode[12] = 2;
        AssertRejected(invalidBluetoothMode, "An unknown Bluetooth connection mode should be rejected.");

        byte[] nonZeroBluetoothReserved = ReadVector("set-bluetooth-connection-mode.bytes");
        nonZeroBluetoothReserved[13] = 1;
        AssertRejected(nonZeroBluetoothReserved, "A nonzero Bluetooth mode reserved field should be rejected.");

        byte[] inconsistentState = ReadVector("state-snapshot.bytes");
        inconsistentState[19] = (byte)LayerStateIndicators.PersistentLayerActive;
        AssertRejected(inconsistentState, "State presence indicators must match their fields.");

        byte[] unknownBatteryIndicator = ReadVector("battery-snapshot.bytes");
        unknownBatteryIndicator[18] = 0x10;
        AssertRejected(unknownBatteryIndicator, "An unknown battery indicator should be rejected.");

        byte[] invalidBatteryLevel = ReadVector("battery-snapshot.bytes");
        invalidBatteryLevel[16] = 101;
        AssertRejected(invalidBatteryLevel, "A battery level above 100 should be rejected.");

        byte[] staleUnavailableBattery = ReadVector("battery-snapshot.bytes");
        staleUnavailableBattery[18] = (byte)(BatteryStateIndicators.LeftAvailable | BatteryStateIndicators.RightStale);
        AssertRejected(staleUnavailableBattery, "A stale battery must also be available.");

        byte[] nonZeroBatteryReserved = ReadVector("battery-snapshot.bytes");
        nonZeroBatteryReserved[19] = 1;
        AssertRejected(nonZeroBatteryReserved, "A nonzero battery reserved field should be rejected.");
    }

    private static void VerifyEncodeBounds()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => ProtocolPacketCodec.Encode(new ProtocolMessage.HelloRequest(1, (ProtocolCapability)0x80, Layout)));
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => ProtocolPacketCodec.Encode(new ProtocolMessage.PressMomentaryLayerCommand(1, 1, 1, 1, 0)));
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => ProtocolPacketCodec.Encode(new ProtocolMessage.SetPersistentLayerCommand(1, 1, 1, ProtocolPacketCodec.NoLayer)));
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => ProtocolPacketCodec.Encode(
                new ProtocolMessage.SetBluetoothConnectionModeCommand(1, 1, (BluetoothConnectionMode)2)));
        AssertEx.Throws<ArgumentException>(
            () => ProtocolPacketCodec.Encode(
                new ProtocolMessage.BatteryChanged(
                    1,
                    new ProtocolMessage.BatteryState(1, 101, 0, BatteryStateIndicators.LeftAvailable))));
    }

    private static void AssertRejected(ReadOnlySpan<byte> packet, string message)
    {
        AssertEx.True(!ProtocolPacketCodec.TryDecode(packet, out _), message);
    }

    private static byte[] ReadVector(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Protocol", "Vectors", fileName);
        string[] tokens = File.ReadAllText(path).Split([',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return tokens
            .Select(token => byte.Parse(token.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))
            .ToArray();
    }
}
