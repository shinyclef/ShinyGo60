namespace ShinyGo60.Protocol.Messages;

public abstract record ProtocolMessage(ProtocolMessageType Type)
{
    public sealed record HelloRequest(
        ushort ClientNonce,
        ProtocolCapability RequestedCapabilities,
        LayoutFingerprint ExpectedLayout)
        : ProtocolMessage(ProtocolMessageType.Hello);

    public sealed record HelloResult(
        ushort ClientNonce,
        HelloStatus Status,
        ProtocolCapability SelectedCapabilities,
        uint SessionId,
        LayoutFingerprint Layout)
        : ProtocolMessage(ProtocolMessageType.HelloResult);

    public sealed record GetStateRequest(uint SessionId, uint RequestId)
        : ProtocolMessage(ProtocolMessageType.GetState);

    public sealed record StateSnapshot(uint SessionId, uint RequestId, LayerState State)
        : ProtocolMessage(ProtocolMessageType.StateSnapshot);

    public sealed record LayerChanged(uint SessionId, uint SourceCommandId, LayerState State)
        : ProtocolMessage(ProtocolMessageType.LayerChanged);

    public sealed record GetBatteryRequest(uint SessionId, uint RequestId)
        : ProtocolMessage(ProtocolMessageType.GetBattery);

    public sealed record BatterySnapshot(uint SessionId, uint RequestId, BatteryState State)
        : ProtocolMessage(ProtocolMessageType.BatterySnapshot);

    public sealed record BatteryChanged(uint SessionId, BatteryState State)
        : ProtocolMessage(ProtocolMessageType.BatteryChanged);

    public sealed record SetPersistentLayerCommand(
        uint SessionId,
        uint CommandId,
        uint ExpectedStateRevision,
        byte LayerId)
        : ProtocolMessage(ProtocolMessageType.SetPersistentLayer);

    public sealed record PressMomentaryLayerCommand(
        uint SessionId,
        uint CommandId,
        uint ExpectedStateRevision,
        byte LayerId,
        byte LeaseUnits)
        : ProtocolMessage(ProtocolMessageType.PressMomentaryLayer);

    public sealed record RenewMomentaryLayerCommand(
        uint SessionId,
        uint CommandId,
        uint ActivationId,
        byte LeaseUnits)
        : ProtocolMessage(ProtocolMessageType.RenewMomentaryLayer);

    public sealed record ReleaseMomentaryLayerCommand(uint SessionId, uint CommandId, uint ActivationId)
        : ProtocolMessage(ProtocolMessageType.ReleaseMomentaryLayer);

    public sealed record CommandResult(
        uint SessionId,
        uint CommandId,
        CommandStatus Status,
        LayerState State)
        : ProtocolMessage(ProtocolMessageType.CommandResult);

    public sealed record ErrorMessage(
        uint SessionId,
        uint RelatedId,
        uint StateRevision,
        ProtocolErrorCode Code,
        byte OffendingMessageType,
        ushort Detail)
        : ProtocolMessage(ProtocolMessageType.Error);

    public readonly record struct LayerState(
        uint Revision,
        byte EffectiveLayerId,
        byte? PersistentLayerId,
        byte MomentaryLayerCount,
        LayerStateIndicators Indicators);

    public readonly record struct BatteryState(
        uint Revision,
        byte LeftLevel,
        byte RightLevel,
        BatteryStateIndicators Indicators);
}
