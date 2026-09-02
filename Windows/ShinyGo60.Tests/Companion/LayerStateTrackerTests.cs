using ShinyGo60.Companion.Core.Telemetry;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Companion;

internal static class LayerStateTrackerTests
{
    private const uint UsbSession = 0x10203040;
    private const uint BluetoothSession = 0x50607080;
    private static readonly LayoutFingerprint Layout = new(0x0123456789ABCDEF);

    public static ValueTask RunAsync()
    {
        LayerStateTracker tracker = new(CreateManifest());
        int changeCount = 0;
        tracker.StateChanged += (_, _) => changeCount++;

        ProtocolMessage.HelloResult usbHello = SuccessfulHello(UsbSession);
        tracker.BeginSession(usbHello);

        AssertEx.Equal(
            LayerTelemetryApplyResult.AwaitingSnapshot,
            tracker.Apply(LayerChanged(UsbSession, 1, 1)));
        AssertEx.Equal(
            LayerTelemetryApplyResult.AppliedSnapshot,
            tracker.Apply(Snapshot(UsbSession, 1, 0)));
        AssertEx.Equal("Home", tracker.CurrentState?.EffectiveLayer.Name);

        AssertEx.Equal(LayerTelemetryApplyResult.Applied, tracker.Apply(LayerChanged(UsbSession, 2, 1)));
        AssertEx.Equal("Navigation", tracker.CurrentState?.EffectiveLayer.Name);
        AssertEx.Equal(LayerTelemetryApplyResult.Duplicate, tracker.Apply(LayerChanged(UsbSession, 2, 1)));
        AssertEx.Equal(
            LayerTelemetryApplyResult.ConflictingRevision,
            tracker.Apply(LayerChanged(UsbSession, 2, 2)));
        AssertEx.Equal(
            LayerTelemetryApplyResult.StaleRevision,
            tracker.Apply(LayerChanged(UsbSession, 1, 0)));

        AssertEx.Equal(
            LayerTelemetryApplyResult.AppliedAfterGap,
            tracker.Apply(LayerChanged(UsbSession, 4, 2)));
        AssertEx.Equal("Keypad", tracker.CurrentState?.EffectiveLayer.Name);
        AssertEx.Equal(LayerTelemetryApplyResult.Applied, tracker.Apply(LayerChanged(UsbSession, 5, 4)));
        AssertEx.Equal("Gaming", tracker.CurrentState?.EffectiveLayer.Name);
        AssertEx.Equal(LayerTelemetryApplyResult.Applied, tracker.Apply(LayerChanged(UsbSession, 6, 3)));
        AssertEx.Equal("Conditional", tracker.CurrentState?.EffectiveLayer.Name);
        AssertEx.Equal(LayerTelemetryApplyResult.Applied, tracker.Apply(LayerChanged(UsbSession, 7, 1)));

        tracker.BeginSession(SuccessfulHello(BluetoothSession));
        AssertEx.Equal(
            LayerTelemetryApplyResult.WrongSession,
            tracker.Apply(LayerChanged(UsbSession, 8, 2)));
        AssertEx.Equal(
            LayerTelemetryApplyResult.AppliedSnapshot,
            tracker.Apply(Snapshot(BluetoothSession, 8, 2)));
        AssertEx.Equal("Keypad", tracker.CurrentState?.EffectiveLayer.Name);
        AssertEx.Equal(
            LayerTelemetryApplyResult.InvalidState,
            tracker.Apply(LayerChanged(BluetoothSession, 9, 30)));

        AssertEx.Equal(7, changeCount);
        VerifyManifestAndCapabilityFailures(tracker);
        return ValueTask.CompletedTask;
    }

    private static void VerifyManifestAndCapabilityFailures(LayerStateTracker tracker)
    {
        InvalidDataException layoutException = AssertEx.Throws<InvalidDataException>(
            () => tracker.BeginSession(SuccessfulHello(UsbSession) with { Layout = new LayoutFingerprint(1) }));
        AssertEx.True(
            layoutException.Message.Contains("does not match", StringComparison.Ordinal),
            "A mismatched firmware layout should identify the manifest mismatch.");

        InvalidDataException capabilityException = AssertEx.Throws<InvalidDataException>(
            () => tracker.BeginSession(SuccessfulHello(UsbSession) with { SelectedCapabilities = ProtocolCapability.None }));
        AssertEx.True(
            capabilityException.Message.Contains("telemetry", StringComparison.OrdinalIgnoreCase),
            "A firmware without telemetry should identify the missing capability.");
    }

    private static ProtocolMessage.HelloResult SuccessfulHello(uint sessionId)
    {
        return new ProtocolMessage.HelloResult(
            1,
            HelloStatus.Success,
            ProtocolCapability.StateTelemetry,
            sessionId,
            Layout);
    }

    private static ProtocolMessage.StateSnapshot Snapshot(uint sessionId, uint revision, byte layerId)
    {
        return new ProtocolMessage.StateSnapshot(sessionId, 1, State(revision, layerId));
    }

    private static ProtocolMessage.LayerChanged LayerChanged(uint sessionId, uint revision, byte layerId)
    {
        return new ProtocolMessage.LayerChanged(sessionId, 0, State(revision, layerId));
    }

    private static ProtocolMessage.LayerState State(uint revision, byte layerId)
    {
        return new ProtocolMessage.LayerState(revision, layerId, null, 0, LayerStateIndicators.None);
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
                new LayerDefinition(3, "Conditional"),
                new LayerDefinition(4, "Gaming"),
            ],
            DateTimeOffset.UnixEpoch);
    }
}
