using ShinyGo60.Companion.Core.Control;
using ShinyGo60.Companion.Core.Telemetry;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Companion;

internal static class LayerCommandStateMachineTests
{
    private const uint FirstSession = 0x10203040;
    private const uint SecondSession = 0x50607080;
    private static readonly LayoutFingerprint Layout = new(0x0123456789ABCDEF);

    public static ValueTask RunAsync()
    {
        VerifyQuickReleaseAndResponseReordering();
        VerifyIndependentMomentaryOwnership();
        VerifySessionReplacementAndTransportLoss();
        VerifyTimeoutRetryAndRejectedPress();
        VerifyLeaseExpiryBeforeRelease();
        return ValueTask.CompletedTask;
    }

    private static void VerifyIndependentMomentaryOwnership()
    {
        LayerCommandStateMachine machine = CreateReadyMachine(FirstSession, revision: 4, persistentLayer: 2);

        uint firstActivation = machine.QueueMomentaryPress(1, 20);
        _ = machine.TryStartNextCommand();
        AssertEx.Equal(
            LayerCommandResponseResult.CommandAccepted,
            machine.ApplyResponse(new ProtocolMessage.CommandResult(
                FirstSession,
                firstActivation,
                CommandStatus.Applied,
                State(5, 2, 2, 1))));

        uint secondActivation = machine.QueueMomentaryPress(1, 20);
        _ = machine.TryStartNextCommand();
        AssertEx.Equal(
            LayerCommandResponseResult.CommandAccepted,
            machine.ApplyResponse(new ProtocolMessage.CommandResult(
                FirstSession,
                secondActivation,
                CommandStatus.Applied,
                State(6, 2, 2, 2))));
        AssertEx.Equal(2, machine.MomentaryActivationCount);

        uint firstRelease = machine.QueueMomentaryRelease(firstActivation);
        _ = machine.TryStartNextCommand();
        AssertEx.Equal(
            LayerCommandResponseResult.CommandAccepted,
            machine.ApplyResponse(new ProtocolMessage.CommandResult(
                FirstSession,
                firstRelease,
                CommandStatus.Applied,
                State(7, 2, 2, 1))));
        AssertEx.Equal(1, machine.MomentaryActivationCount);

        uint secondRelease = machine.QueueMomentaryRelease(secondActivation);
        _ = machine.TryStartNextCommand();
        AssertEx.Equal(
            LayerCommandResponseResult.CommandAccepted,
            machine.ApplyResponse(new ProtocolMessage.CommandResult(
                FirstSession,
                secondRelease,
                CommandStatus.Applied,
                State(8, 2, 2, 0))));
        AssertEx.Equal(0, machine.MomentaryActivationCount);
        AssertEx.Equal(2, machine.StateTracker.CurrentState!.EffectiveLayer.Id);
        AssertEx.Equal(2, machine.StateTracker.CurrentState!.PersistentLayer!.Id);
    }

    private static void VerifyQuickReleaseAndResponseReordering()
    {
        LayerCommandStateMachine machine = CreateReadyMachine(FirstSession, revision: 1);
        uint activationId = machine.QueueMomentaryPress(layerId: 1, leaseUnits: 20);
        ProtocolMessage.PressMomentaryLayerCommand press =
            (ProtocolMessage.PressMomentaryLayerCommand)machine.TryStartNextCommand()!;
        AssertEx.Equal(activationId, press.CommandId);
        AssertEx.Equal(1, press.LayerId);
        AssertEx.Equal(1U, press.ExpectedStateRevision);

        uint releaseCommandId = machine.QueueMomentaryRelease(activationId);
        AssertEx.Equal(2U, releaseCommandId);
        AssertEx.Equal(releaseCommandId, machine.QueueMomentaryRelease(activationId));
        AssertEx.Equal(1, machine.QueuedCommandCount);

        ProtocolMessage retry = machine.RetryPendingCommand()!;
        AssertEx.SequenceEqual(ProtocolPacketCodec.Encode(press), ProtocolPacketCodec.Encode(retry));

        ProtocolMessage.CommandResult futureResponse = new(
            FirstSession,
            releaseCommandId,
            CommandStatus.Applied,
            State(2, 1, null, 1));
        AssertEx.Equal(LayerCommandResponseResult.WrongCommand, machine.ApplyResponse(futureResponse));
        AssertEx.Equal(press, machine.PendingCommand);

        ProtocolMessage.LayerChanged earlyEvent = new(
            FirstSession,
            activationId,
            State(2, 1, null, 1));
        AssertEx.Equal(LayerTelemetryApplyResult.Applied, machine.StateTracker.Apply(earlyEvent));
        ProtocolMessage.CommandResult pressResult = new(
            FirstSession,
            activationId,
            CommandStatus.Applied,
            earlyEvent.State);
        AssertEx.Equal(LayerCommandResponseResult.CommandAccepted, machine.ApplyResponse(pressResult));

        ProtocolMessage.ReleaseMomentaryLayerCommand release =
            (ProtocolMessage.ReleaseMomentaryLayerCommand)machine.TryStartNextCommand()!;
        AssertEx.Equal(activationId, release.ActivationId);
        AssertEx.Equal(releaseCommandId, release.CommandId);
        ProtocolMessage.CommandResult releaseResult = new(
            FirstSession,
            releaseCommandId,
            CommandStatus.Applied,
            State(3, 0, null, 0));
        AssertEx.Equal(LayerCommandResponseResult.CommandAccepted, machine.ApplyResponse(releaseResult));
        AssertEx.Equal(0, machine.MomentaryActivationCount);
        AssertEx.Equal(LayerCommandResponseResult.NoPendingCommand, machine.ApplyResponse(releaseResult));

        uint persistentCommandId = machine.QueuePersistentLayer(2);
        ProtocolMessage.SetPersistentLayerCommand persistent =
            (ProtocolMessage.SetPersistentLayerCommand)machine.TryStartNextCommand()!;
        AssertEx.Equal(3U, persistent.ExpectedStateRevision);
        AssertEx.Equal(persistentCommandId, persistent.CommandId);
        ProtocolMessage.CommandResult persistentResult = new(
            FirstSession,
            persistentCommandId,
            CommandStatus.Applied,
            State(4, 2, 2, 0));
        AssertEx.Equal(LayerCommandResponseResult.CommandAccepted, machine.ApplyResponse(persistentResult));
    }

    private static void VerifySessionReplacementAndTransportLoss()
    {
        LayerCommandStateMachine machine = CreateReadyMachine(FirstSession, revision: 4, persistentLayer: 2);
        uint oldActivationId = machine.QueueMomentaryPress(1, 20);
        ProtocolMessage oldPress = machine.TryStartNextCommand()!;
        machine.BeginSession(SuccessfulHello(SecondSession));

        AssertEx.True(machine.HasSession, "The replacement session should be active.");
        AssertEx.Equal(0, machine.MomentaryActivationCount);
        AssertEx.Equal(0, machine.QueuedCommandCount);
        AssertEx.Equal<ProtocolMessage?>(null, machine.PendingCommand);
        AssertEx.Equal<ProtocolMessage?>(null, machine.TryStartNextCommand());

        ProtocolMessage.PressMomentaryLayerCommand oldCommand =
            (ProtocolMessage.PressMomentaryLayerCommand)oldPress;
        ProtocolMessage.CommandResult oldResponse = new(
            FirstSession,
            oldCommand.CommandId,
            CommandStatus.Applied,
            State(5, 2, 2, 1));
        AssertEx.Equal(LayerCommandResponseResult.NoPendingCommand, machine.ApplyResponse(oldResponse));

        ApplySnapshot(machine, SecondSession, revision: 4, persistentLayer: 2);
        uint newActivationId = machine.QueueMomentaryPress(1, 20);
        ProtocolMessage.PressMomentaryLayerCommand newPress =
            (ProtocolMessage.PressMomentaryLayerCommand)machine.TryStartNextCommand()!;
        AssertEx.Equal(1U, newActivationId);
        AssertEx.Equal(1U, newPress.CommandId);
        AssertEx.Equal(LayerCommandResponseResult.WrongSession, machine.ApplyResponse(oldResponse));
        AssertEx.Equal(newPress, machine.PendingCommand);

        machine.EndSession();
        AssertEx.True(!machine.HasSession, "Transport loss should end the local command session.");
        AssertEx.Equal(0, machine.MomentaryActivationCount);
        AssertEx.Equal<ProtocolMessage?>(null, machine.PendingCommand);
        AssertEx.Throws<InvalidOperationException>(() => machine.QueueMomentaryRelease(oldActivationId));
    }

    private static void VerifyTimeoutRetryAndRejectedPress()
    {
        LayerCommandStateMachine retriedMachine = CreateReadyMachine(FirstSession, revision: 1);
        uint retriedActivationId = retriedMachine.QueueMomentaryPress(1, 20);
        ProtocolMessage original = retriedMachine.TryStartNextCommand()!;
        AssertEx.Equal(original, retriedMachine.RetryPendingCommand());
        ProtocolMessage.CommandResult duplicate = new(
            FirstSession,
            retriedActivationId,
            CommandStatus.Duplicate,
            State(2, 1, null, 1));
        AssertEx.Equal(LayerCommandResponseResult.CommandAccepted, retriedMachine.ApplyResponse(duplicate));
        AssertEx.Equal(1, retriedMachine.MomentaryActivationCount);

        LayerCommandStateMachine machine = CreateReadyMachine(FirstSession, revision: 1);
        uint activationId = machine.QueueMomentaryPress(1, 20);
        ProtocolMessage command = machine.TryStartNextCommand()!;
        AssertEx.Equal(command, machine.RetryPendingCommand());

        ProtocolMessage.ErrorMessage busy = new(
            FirstSession,
            activationId,
            1,
            ProtocolErrorCode.Busy,
            (byte)ProtocolMessageType.PressMomentaryLayer,
            8);
        AssertEx.Equal(LayerCommandResponseResult.CommandRejected, machine.ApplyResponse(busy));
        AssertEx.Equal(0, machine.MomentaryActivationCount);

        uint nextActivationId = machine.QueueMomentaryPress(1, 20);
        ProtocolMessage.PressMomentaryLayerCommand nextPress =
            (ProtocolMessage.PressMomentaryLayerCommand)machine.TryStartNextCommand()!;
        AssertEx.Equal(2U, nextActivationId);
        AssertEx.Equal(2U, nextPress.CommandId);
        machine.EndSession();
        AssertEx.Equal<ProtocolMessage?>(null, machine.RetryPendingCommand());
    }

    private static void VerifyLeaseExpiryBeforeRelease()
    {
        LayerCommandStateMachine machine = CreateReadyMachine(FirstSession, revision: 1);
        uint activationId = machine.QueueMomentaryPress(1, 1);
        _ = machine.TryStartNextCommand();
        ProtocolMessage.CommandResult pressResult = new(
            FirstSession,
            activationId,
            CommandStatus.Applied,
            State(2, 1, null, 1));
        AssertEx.Equal(LayerCommandResponseResult.CommandAccepted, machine.ApplyResponse(pressResult));

        ProtocolMessage.LayerChanged expiry = new(
            FirstSession,
            0,
            State(3, 0, null, 0));
        AssertEx.Equal(LayerTelemetryApplyResult.Applied, machine.StateTracker.Apply(expiry));
        uint releaseCommandId = machine.QueueMomentaryRelease(activationId);
        _ = machine.TryStartNextCommand();
        ProtocolMessage.CommandResult alreadyReleased = new(
            FirstSession,
            releaseCommandId,
            CommandStatus.AlreadyReleased,
            expiry.State);
        AssertEx.Equal(LayerCommandResponseResult.CommandAccepted, machine.ApplyResponse(alreadyReleased));
        AssertEx.Equal(0, machine.MomentaryActivationCount);
    }

    private static LayerCommandStateMachine CreateReadyMachine(
        uint sessionId,
        uint revision,
        byte? persistentLayer = null)
    {
        LayerCommandStateMachine machine = new(CreateManifest());
        machine.BeginSession(SuccessfulHello(sessionId));
        ApplySnapshot(machine, sessionId, revision, persistentLayer);
        return machine;
    }

    private static void ApplySnapshot(
        LayerCommandStateMachine machine,
        uint sessionId,
        uint revision,
        byte? persistentLayer = null)
    {
        ProtocolMessage.StateSnapshot snapshot = new(
            sessionId,
            1,
            State(revision, persistentLayer ?? 0, persistentLayer, 0));
        AssertEx.Equal(LayerTelemetryApplyResult.AppliedSnapshot, machine.StateTracker.Apply(snapshot));
    }

    private static ProtocolMessage.HelloResult SuccessfulHello(uint sessionId)
    {
        return new ProtocolMessage.HelloResult(
            1,
            HelloStatus.Success,
            ProtocolCapability.StateTelemetry |
                ProtocolCapability.PersistentLayer |
                ProtocolCapability.MomentaryLayer,
            sessionId,
            Layout);
    }

    private static ProtocolMessage.LayerState State(
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
