using ShinyGo60.Companion.Core.Telemetry;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Companion;

internal static class BatteryStateTrackerTests
{
    private const uint UsbSession = 0x10203040;
    private const uint BluetoothSession = 0x50607080;
    private static readonly LayoutFingerprint Layout = new(0x0123456789ABCDEF);

    public static ValueTask RunAsync()
    {
        BatteryStateTracker tracker = new(CreateManifest());
        int changeCount = 0;
        tracker.StateChanged += (_, _) => changeCount++;
        tracker.BeginSession(SuccessfulHello(UsbSession));

        AssertEx.Equal(
            BatteryTelemetryApplyResult.AwaitingSnapshot,
            tracker.Apply(Changed(UsbSession, 1, 87, 0, BatteryStateIndicators.LeftAvailable)));
        AssertEx.Equal(
            BatteryTelemetryApplyResult.AppliedSnapshot,
            tracker.Apply(Snapshot(UsbSession, 1, 87, 0, BatteryStateIndicators.LeftAvailable)));
        AssertEx.Equal((byte?)87, tracker.CurrentState?.Left.Level);
        AssertEx.Equal(BatteryReadingStatus.Unavailable, tracker.CurrentState?.Right.Status);

        BatteryStateIndicators bothFresh = BatteryStateIndicators.LeftAvailable | BatteryStateIndicators.RightAvailable;
        AssertEx.Equal(
            BatteryTelemetryApplyResult.Applied,
            tracker.Apply(Changed(UsbSession, 2, 87, 63, bothFresh)));
        AssertEx.Equal((byte?)63, tracker.CurrentState?.Right.Level);
        AssertEx.Equal(BatteryReadingStatus.Fresh, tracker.CurrentState?.Right.Status);
        AssertEx.Equal(
            BatteryTelemetryApplyResult.Duplicate,
            tracker.Apply(Changed(UsbSession, 2, 87, 63, bothFresh)));
        AssertEx.Equal(
            BatteryTelemetryApplyResult.ConflictingRevision,
            tracker.Apply(Changed(UsbSession, 2, 86, 63, bothFresh)));
        AssertEx.Equal(
            BatteryTelemetryApplyResult.StaleRevision,
            tracker.Apply(Changed(UsbSession, 1, 87, 0, BatteryStateIndicators.LeftAvailable)));

        BatteryStateIndicators rightStale = bothFresh | BatteryStateIndicators.RightStale;
        AssertEx.Equal(
            BatteryTelemetryApplyResult.AppliedAfterGap,
            tracker.Apply(Changed(UsbSession, 4, 86, 63, rightStale)));
        AssertEx.Equal(BatteryReadingStatus.Stale, tracker.CurrentState?.Right.Status);
        AssertEx.Equal(
            BatteryTelemetryApplyResult.Applied,
            tracker.Apply(Changed(UsbSession, 5, 86, 0, BatteryStateIndicators.LeftAvailable)));
        AssertEx.Equal(BatteryReadingStatus.Unavailable, tracker.CurrentState?.Right.Status);

        tracker.BeginSession(SuccessfulHello(BluetoothSession));
        AssertEx.Equal(
            BatteryTelemetryApplyResult.WrongSession,
            tracker.Apply(Changed(UsbSession, 6, 85, 62, bothFresh)));
        AssertEx.Equal(
            BatteryTelemetryApplyResult.AppliedSnapshot,
            tracker.Apply(Snapshot(BluetoothSession, 6, 85, 62, bothFresh)));
        AssertEx.Equal(
            BatteryTelemetryApplyResult.InvalidState,
            tracker.Apply(Changed(BluetoothSession, 7, 85, 0, BatteryStateIndicators.RightStale)));

        AssertEx.Equal(5, changeCount);
        VerifyManifestAndCapabilityFailures(tracker);
        return ValueTask.CompletedTask;
    }

    private static void VerifyManifestAndCapabilityFailures(BatteryStateTracker tracker)
    {
        InvalidDataException layoutException = AssertEx.Throws<InvalidDataException>(
            () => tracker.BeginSession(SuccessfulHello(UsbSession) with { Layout = new LayoutFingerprint(1) }));
        AssertEx.True(
            layoutException.Message.Contains("does not match", StringComparison.Ordinal),
            "A mismatched firmware layout should identify the manifest mismatch.");

        InvalidDataException capabilityException = AssertEx.Throws<InvalidDataException>(
            () => tracker.BeginSession(SuccessfulHello(UsbSession) with { SelectedCapabilities = ProtocolCapability.StateTelemetry }));
        AssertEx.True(
            capabilityException.Message.Contains("battery", StringComparison.OrdinalIgnoreCase),
            "A firmware without battery telemetry should identify the missing capability.");
    }

    private static ProtocolMessage.HelloResult SuccessfulHello(uint sessionId)
    {
        return new ProtocolMessage.HelloResult(
            1,
            HelloStatus.Success,
            ProtocolCapability.StateTelemetry | ProtocolCapability.BatteryTelemetry,
            sessionId,
            Layout);
    }

    private static ProtocolMessage.BatterySnapshot Snapshot(
        uint sessionId,
        uint revision,
        byte left,
        byte right,
        BatteryStateIndicators indicators)
    {
        return new ProtocolMessage.BatterySnapshot(sessionId, 1, State(revision, left, right, indicators));
    }

    private static ProtocolMessage.BatteryChanged Changed(
        uint sessionId,
        uint revision,
        byte left,
        byte right,
        BatteryStateIndicators indicators)
    {
        return new ProtocolMessage.BatteryChanged(sessionId, State(revision, left, right, indicators));
    }

    private static ProtocolMessage.BatteryState State(
        uint revision,
        byte left,
        byte right,
        BatteryStateIndicators indicators)
    {
        return new ProtocolMessage.BatteryState(revision, left, right, indicators);
    }

    private static LayoutManifest CreateManifest()
    {
        return new LayoutManifest(
            LayoutManifest.CurrentSchemaVersion,
            ProtocolVersion.Current,
            "sg60-v1-0123456789abcdef0123456789abcdef",
            new string('a', 64),
            "fixture-revision",
            [new LayerDefinition(0, "Home")],
            DateTimeOffset.UnixEpoch);
    }
}
