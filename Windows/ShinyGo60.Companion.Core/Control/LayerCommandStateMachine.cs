using ShinyGo60.Companion.Core.Telemetry;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;

namespace ShinyGo60.Companion.Core.Control;

public sealed class LayerCommandStateMachine
{
    private readonly object syncRoot = new();
    private readonly Queue<QueuedCommand> queuedCommands = new();
    private readonly HashSet<uint> momentaryActivations = [];
    private readonly int layerCount;
    private uint sessionId;
    private uint nextCommandId;
    private ProtocolCapability selectedCapabilities;
    private PendingCommandState? pendingCommand;

    public LayerCommandStateMachine(LayoutManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        LayoutManifestJson.Validate(manifest);
        this.layerCount = manifest.Layers.Count;
        this.StateTracker = new LayerStateTracker(manifest);
    }

    public LayerStateTracker StateTracker { get; }

    public bool HasSession
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.sessionId != 0;
            }
        }
    }

    public int QueuedCommandCount
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.queuedCommands.Count;
            }
        }
    }

    public int MomentaryActivationCount
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.momentaryActivations.Count;
            }
        }
    }

    public ProtocolMessage? PendingCommand
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.pendingCommand?.Message;
            }
        }
    }

    public void BeginSession(ProtocolMessage.HelloResult hello)
    {
        ArgumentNullException.ThrowIfNull(hello);

        lock (this.syncRoot)
        {
            this.StateTracker.BeginSession(hello);
            this.sessionId = hello.SessionId;
            this.selectedCapabilities = hello.SelectedCapabilities;
            this.nextCommandId = 1;
            this.pendingCommand = null;
            this.queuedCommands.Clear();
            this.momentaryActivations.Clear();
        }
    }

    public void EndSession()
    {
        lock (this.syncRoot)
        {
            this.sessionId = 0;
            this.selectedCapabilities = ProtocolCapability.None;
            this.nextCommandId = 0;
            this.pendingCommand = null;
            this.queuedCommands.Clear();
            this.momentaryActivations.Clear();
            this.StateTracker.EndSession();
        }
    }

    public uint QueuePersistentLayer(byte layerId)
    {
        lock (this.syncRoot)
        {
            this.RequireSessionCapability(ProtocolCapability.PersistentLayer);
            this.ValidateLayer(layerId);
            uint commandId = this.AllocateCommandId();
            this.queuedCommands.Enqueue(new QueuedCommand(CommandKind.SetPersistent, commandId, layerId, 0, 0));
            return commandId;
        }
    }

    public uint QueueMomentaryPress(byte layerId, byte leaseUnits)
    {
        lock (this.syncRoot)
        {
            this.RequireSessionCapability(ProtocolCapability.MomentaryLayer);
            this.ValidateLayer(layerId);
            ValidateLease(leaseUnits);
            uint commandId = this.AllocateCommandId();
            this.momentaryActivations.Add(commandId);
            this.queuedCommands.Enqueue(
                new QueuedCommand(CommandKind.PressMomentary, commandId, layerId, commandId, leaseUnits));
            return commandId;
        }
    }

    public uint QueueMomentaryRenewal(uint activationId, byte leaseUnits)
    {
        lock (this.syncRoot)
        {
            this.RequireSessionCapability(ProtocolCapability.MomentaryLayer);
            this.RequireKnownActivation(activationId);
            ValidateLease(leaseUnits);
            uint commandId = this.AllocateCommandId();
            this.queuedCommands.Enqueue(
                new QueuedCommand(CommandKind.RenewMomentary, commandId, 0, activationId, leaseUnits));
            return commandId;
        }
    }

    public uint QueueMomentaryRelease(uint activationId)
    {
        lock (this.syncRoot)
        {
            this.RequireSessionCapability(ProtocolCapability.MomentaryLayer);
            this.RequireKnownActivation(activationId);

            if (this.pendingCommand?.Command.Kind == CommandKind.ReleaseMomentary &&
                this.pendingCommand.Command.ActivationId == activationId)
            {
                return this.pendingCommand.Command.CommandId;
            }

            foreach (QueuedCommand queued in this.queuedCommands)
            {
                if (queued.Kind == CommandKind.ReleaseMomentary && queued.ActivationId == activationId)
                {
                    return queued.CommandId;
                }
            }

            uint commandId = this.AllocateCommandId();
            this.queuedCommands.Enqueue(
                new QueuedCommand(CommandKind.ReleaseMomentary, commandId, 0, activationId, 0));
            return commandId;
        }
    }

    public ProtocolMessage? TryStartNextCommand()
    {
        lock (this.syncRoot)
        {
            if (this.sessionId == 0 || this.pendingCommand is not null || this.queuedCommands.Count == 0)
            {
                return null;
            }

            LayerTelemetryState? state = this.StateTracker.CurrentState;
            if (state is null)
            {
                return null;
            }

            QueuedCommand command = this.queuedCommands.Dequeue();
            ProtocolMessage message = command.Kind switch
            {
                CommandKind.SetPersistent => new ProtocolMessage.SetPersistentLayerCommand(
                    this.sessionId, command.CommandId, state.Revision, command.LayerId),
                CommandKind.PressMomentary => new ProtocolMessage.PressMomentaryLayerCommand(
                    this.sessionId, command.CommandId, state.Revision, command.LayerId, command.LeaseUnits),
                CommandKind.RenewMomentary => new ProtocolMessage.RenewMomentaryLayerCommand(
                    this.sessionId, command.CommandId, command.ActivationId, command.LeaseUnits),
                CommandKind.ReleaseMomentary => new ProtocolMessage.ReleaseMomentaryLayerCommand(
                    this.sessionId, command.CommandId, command.ActivationId),
                _ => throw new InvalidOperationException($"Unsupported queued command {command.Kind}."),
            };
            this.pendingCommand = new PendingCommandState(command, message);
            return message;
        }
    }

    public ProtocolMessage? RetryPendingCommand()
    {
        lock (this.syncRoot)
        {
            return this.pendingCommand?.Message;
        }
    }

    public LayerCommandResponseResult ApplyResponse(ProtocolMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        lock (this.syncRoot)
        {
            if (this.pendingCommand is null)
            {
                return LayerCommandResponseResult.NoPendingCommand;
            }

            if (!TryReadResponseIdentity(response, out uint responseSessionId, out uint responseCommandId))
            {
                return LayerCommandResponseResult.UnexpectedMessage;
            }

            if (responseSessionId != this.sessionId)
            {
                return LayerCommandResponseResult.WrongSession;
            }

            if (responseCommandId != this.pendingCommand.Command.CommandId)
            {
                return LayerCommandResponseResult.WrongCommand;
            }

            if (response is ProtocolMessage.CommandResult result)
            {
                LayerTelemetryApplyResult applyResult = this.StateTracker.Apply(result);
                if (!StateWasAccepted(applyResult))
                {
                    return LayerCommandResponseResult.InvalidLayerState;
                }

                this.CompleteActivation(result.Status, wasRejected: false);
                this.pendingCommand = null;
                return LayerCommandResponseResult.CommandAccepted;
            }

            this.CompleteActivation(null, wasRejected: true);
            this.pendingCommand = null;
            return LayerCommandResponseResult.CommandRejected;
        }
    }

    private static bool TryReadResponseIdentity(
        ProtocolMessage response, out uint sessionId, out uint commandId)
    {
        switch (response)
        {
            case ProtocolMessage.CommandResult result:
                sessionId = result.SessionId;
                commandId = result.CommandId;
                return true;
            case ProtocolMessage.ErrorMessage error:
                sessionId = error.SessionId;
                commandId = error.RelatedId;
                return true;
            default:
                sessionId = 0;
                commandId = 0;
                return false;
        }
    }

    private static bool StateWasAccepted(LayerTelemetryApplyResult result)
    {
        return result is LayerTelemetryApplyResult.Applied or
            LayerTelemetryApplyResult.AppliedAfterGap or
            LayerTelemetryApplyResult.Duplicate or
            LayerTelemetryApplyResult.StaleRevision;
    }

    private static void ValidateLease(byte leaseUnits)
    {
        if (leaseUnits is 0 or > ProtocolPacketCodec.MaximumLeaseUnits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseUnits),
                $"Lease units must be between 1 and {ProtocolPacketCodec.MaximumLeaseUnits}.");
        }
    }

    private void CompleteActivation(CommandStatus? status, bool wasRejected)
    {
        QueuedCommand command = this.pendingCommand!.Command;
        if (command.Kind == CommandKind.PressMomentary &&
            (wasRejected || status == CommandStatus.AlreadyReleased))
        {
            this.momentaryActivations.Remove(command.ActivationId);
        }
        else if (command.Kind == CommandKind.RenewMomentary && status == CommandStatus.AlreadyReleased)
        {
            this.momentaryActivations.Remove(command.ActivationId);
        }
        else if (command.Kind == CommandKind.ReleaseMomentary)
        {
            this.momentaryActivations.Remove(command.ActivationId);
        }
    }

    private void RequireSessionCapability(ProtocolCapability capability)
    {
        if (this.sessionId == 0)
        {
            throw new InvalidOperationException("A successful keyboard session is required.");
        }

        if ((this.selectedCapabilities & capability) == 0)
        {
            throw new InvalidOperationException($"The keyboard session did not select {capability} support.");
        }
    }

    private void ValidateLayer(byte layerId)
    {
        if (layerId >= this.layerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(layerId), $"Layer {layerId} is not present in the manifest.");
        }
    }

    private void RequireKnownActivation(uint activationId)
    {
        if (!this.momentaryActivations.Contains(activationId))
        {
            throw new ArgumentException(
                $"Momentary activation {activationId} does not belong to the current session.",
                nameof(activationId));
        }
    }

    private uint AllocateCommandId()
    {
        if (this.nextCommandId == 0)
        {
            throw new InvalidOperationException("The command ID range is exhausted; start a new keyboard session.");
        }

        uint commandId = this.nextCommandId;
        this.nextCommandId = commandId == uint.MaxValue ? 0 : commandId + 1;
        return commandId;
    }

    private enum CommandKind
    {
        SetPersistent,
        PressMomentary,
        RenewMomentary,
        ReleaseMomentary,
    }

    private sealed record QueuedCommand(
        CommandKind Kind,
        uint CommandId,
        byte LayerId,
        uint ActivationId,
        byte LeaseUnits);

    private sealed record PendingCommandState(QueuedCommand Command, ProtocolMessage Message);
}
