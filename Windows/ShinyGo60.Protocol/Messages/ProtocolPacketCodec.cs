using System.Buffers.Binary;

namespace ShinyGo60.Protocol.Messages;

public static class ProtocolPacketCodec
{
    public const int PacketSize = 20;
    public const byte NoLayer = byte.MaxValue;
    public const byte MaximumLeaseUnits = 50;
    public const int LeaseUnitMilliseconds = 100;

    private const int PayloadOffset = 4;
    private const ProtocolCapability KnownCapabilities =
        ProtocolCapability.StateTelemetry |
        ProtocolCapability.PersistentLayer |
        ProtocolCapability.MomentaryLayer |
        ProtocolCapability.BatteryTelemetry;
    private static ReadOnlySpan<byte> Magic => "SG"u8;

    public static byte[] Encode(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        byte[] packet = new byte[PacketSize];
        Magic.CopyTo(packet);
        packet[2] = PackVersion(ProtocolVersion.Current);
        packet[3] = (byte)message.Type;
        Span<byte> payload = packet.AsSpan(PayloadOffset);

        switch (message)
        {
            case ProtocolMessage.HelloRequest hello:
                RequireNonZero(hello.ClientNonce, nameof(hello.ClientNonce));
                RequireNonZero(hello.ExpectedLayout.Value, nameof(hello.ExpectedLayout));
                ValidateCapabilities(hello.RequestedCapabilities, nameof(hello));
                BinaryPrimitives.WriteUInt16LittleEndian(payload, hello.ClientNonce);
                payload[2] = (byte)hello.RequestedCapabilities;
                BinaryPrimitives.WriteUInt64BigEndian(payload[4..], hello.ExpectedLayout.Value);
                break;
            case ProtocolMessage.HelloResult helloResult:
                ValidateHelloResult(helloResult);
                BinaryPrimitives.WriteUInt16LittleEndian(payload, helloResult.ClientNonce);
                payload[2] = (byte)helloResult.Status;
                payload[3] = (byte)helloResult.SelectedCapabilities;
                BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], helloResult.SessionId);
                BinaryPrimitives.WriteUInt64BigEndian(payload[8..], helloResult.Layout.Value);
                break;
            case ProtocolMessage.GetStateRequest getState:
                RequireSessionAndId(getState.SessionId, getState.RequestId);
                BinaryPrimitives.WriteUInt32LittleEndian(payload, getState.SessionId);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], getState.RequestId);
                break;
            case ProtocolMessage.StateSnapshot snapshot:
                RequireNonZero(snapshot.SessionId, nameof(snapshot.SessionId));
                WriteLayerState(payload, snapshot.SessionId, snapshot.RequestId, snapshot.State);
                break;
            case ProtocolMessage.LayerChanged changed:
                RequireNonZero(changed.SessionId, nameof(changed.SessionId));
                WriteLayerState(payload, changed.SessionId, changed.SourceCommandId, changed.State);
                break;
            case ProtocolMessage.SetPersistentLayerCommand command:
                ValidateLayerCommand(command.SessionId, command.CommandId, command.ExpectedStateRevision, command.LayerId);
                WriteLayerCommand(payload, command.SessionId, command.CommandId, command.ExpectedStateRevision, command.LayerId);
                break;
            case ProtocolMessage.PressMomentaryLayerCommand command:
                ValidateLayerCommand(command.SessionId, command.CommandId, command.ExpectedStateRevision, command.LayerId);
                ValidateLease(command.LeaseUnits);
                WriteLayerCommand(payload, command.SessionId, command.CommandId, command.ExpectedStateRevision, command.LayerId);
                payload[13] = command.LeaseUnits;
                break;
            case ProtocolMessage.RenewMomentaryLayerCommand command:
                RequireSessionAndId(command.SessionId, command.CommandId);
                RequireNonZero(command.ActivationId, nameof(command.ActivationId));
                ValidateLease(command.LeaseUnits);
                BinaryPrimitives.WriteUInt32LittleEndian(payload, command.SessionId);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], command.CommandId);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], command.ActivationId);
                payload[12] = command.LeaseUnits;
                break;
            case ProtocolMessage.ReleaseMomentaryLayerCommand command:
                RequireSessionAndId(command.SessionId, command.CommandId);
                RequireNonZero(command.ActivationId, nameof(command.ActivationId));
                BinaryPrimitives.WriteUInt32LittleEndian(payload, command.SessionId);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], command.CommandId);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], command.ActivationId);
                break;
            case ProtocolMessage.CommandResult result:
                RequireSessionAndId(result.SessionId, result.CommandId);
                ValidateCommandStatus(result.Status);
                ValidateLayerState(result.State);
                BinaryPrimitives.WriteUInt32LittleEndian(payload, result.SessionId);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], result.CommandId);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], result.State.Revision);
                payload[12] = (byte)result.Status;
                payload[13] = result.State.EffectiveLayerId;
                payload[14] = result.State.PersistentLayerId ?? NoLayer;
                payload[15] = result.State.MomentaryLayerCount;
                break;
            case ProtocolMessage.ErrorMessage error:
                ValidateErrorCode(error.Code);
                BinaryPrimitives.WriteUInt32LittleEndian(payload, error.SessionId);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], error.RelatedId);
                BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], error.StateRevision);
                payload[12] = (byte)error.Code;
                payload[13] = error.OffendingMessageType;
                BinaryPrimitives.WriteUInt16LittleEndian(payload[14..], error.Detail);
                break;
            default:
                throw new ArgumentException($"Message type {message.GetType().Name} is unsupported.", nameof(message));
        }

        return packet;
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, out ProtocolMessage? message)
    {
        message = null;
        if (!TryReadHeader(packet, out ProtocolVersion version, out ProtocolMessageType type) || version != ProtocolVersion.Current)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = packet[PayloadOffset..];
        return type switch
        {
            ProtocolMessageType.Hello => TryDecodeHello(payload, out message),
            ProtocolMessageType.HelloResult => TryDecodeHelloResult(payload, out message),
            ProtocolMessageType.GetState => TryDecodeGetState(payload, out message),
            ProtocolMessageType.StateSnapshot => TryDecodeState(payload, isEvent: false, out message),
            ProtocolMessageType.LayerChanged => TryDecodeState(payload, isEvent: true, out message),
            ProtocolMessageType.SetPersistentLayer => TryDecodeSetPersistent(payload, out message),
            ProtocolMessageType.PressMomentaryLayer => TryDecodePressMomentary(payload, out message),
            ProtocolMessageType.RenewMomentaryLayer => TryDecodeRenewMomentary(payload, out message),
            ProtocolMessageType.ReleaseMomentaryLayer => TryDecodeReleaseMomentary(payload, out message),
            ProtocolMessageType.CommandResult => TryDecodeCommandResult(payload, out message),
            ProtocolMessageType.Error => TryDecodeError(payload, out message),
            _ => false,
        };
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> packet, out ProtocolVersion version, out ProtocolMessageType type)
    {
        version = default;
        type = default;
        if (packet.Length != PacketSize || !packet[..Magic.Length].SequenceEqual(Magic))
        {
            return false;
        }

        version = UnpackVersion(packet[2]);
        type = (ProtocolMessageType)packet[3];
        return true;
    }

    private static bool TryDecodeHello(ReadOnlySpan<byte> payload, out ProtocolMessage? message)
    {
        ushort nonce = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        ulong fingerprint = BinaryPrimitives.ReadUInt64BigEndian(payload[4..]);
        ProtocolCapability capabilities = (ProtocolCapability)payload[2];
        if (nonce == 0 || !HasOnlyKnownCapabilities(capabilities) || payload[3] != 0 ||
            !IsZero(payload[12..]) || fingerprint == 0)
        {
            message = null;
            return false;
        }

        message = new ProtocolMessage.HelloRequest(nonce, capabilities, new LayoutFingerprint(fingerprint));
        return true;
    }

    private static bool TryDecodeHelloResult(ReadOnlySpan<byte> payload, out ProtocolMessage? message)
    {
        ushort nonce = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        HelloStatus status = (HelloStatus)payload[2];
        ProtocolCapability capabilities = (ProtocolCapability)payload[3];
        uint sessionId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        ulong fingerprint = BinaryPrimitives.ReadUInt64BigEndian(payload[8..]);
        if (nonce == 0 || !Enum.IsDefined(status) || !HasOnlyKnownCapabilities(capabilities) || fingerprint == 0 ||
            (status == HelloStatus.Success ? sessionId == 0 : sessionId != 0 || capabilities != ProtocolCapability.None))
        {
            message = null;
            return false;
        }

        message = new ProtocolMessage.HelloResult(nonce, status, capabilities, sessionId, new LayoutFingerprint(fingerprint));
        return true;
    }

    private static bool TryDecodeGetState(ReadOnlySpan<byte> payload, out ProtocolMessage? message)
    {
        uint sessionId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint requestId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (sessionId == 0 || requestId == 0 || !IsZero(payload[8..]))
        {
            message = null;
            return false;
        }

        message = new ProtocolMessage.GetStateRequest(sessionId, requestId);
        return true;
    }

    private static bool TryDecodeState(ReadOnlySpan<byte> payload, bool isEvent, out ProtocolMessage? message)
    {
        uint sessionId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint revision = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        uint relatedId = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        ProtocolMessage.LayerState state = new(
            revision,
            payload[12],
            DecodeOptionalLayer(payload[13]),
            payload[14],
            (LayerStateIndicators)payload[15]);
        if (sessionId == 0 || !IsValidLayerState(state))
        {
            message = null;
            return false;
        }

        message = isEvent
            ? new ProtocolMessage.LayerChanged(sessionId, relatedId, state)
            : new ProtocolMessage.StateSnapshot(sessionId, relatedId, state);
        return true;
    }

    private static bool TryDecodeSetPersistent(ReadOnlySpan<byte> payload, out ProtocolMessage? message)
    {
        if (!TryDecodeLayerCommand(payload, requireLease: false, out uint sessionId, out uint commandId, out uint revision, out byte layerId, out _))
        {
            message = null;
            return false;
        }

        message = new ProtocolMessage.SetPersistentLayerCommand(sessionId, commandId, revision, layerId);
        return true;
    }

    private static bool TryDecodePressMomentary(ReadOnlySpan<byte> payload, out ProtocolMessage? message)
    {
        if (!TryDecodeLayerCommand(
                payload,
                requireLease: true,
                out uint sessionId,
                out uint commandId,
                out uint revision,
                out byte layerId,
                out byte leaseUnits))
        {
            message = null;
            return false;
        }

        message = new ProtocolMessage.PressMomentaryLayerCommand(sessionId, commandId, revision, layerId, leaseUnits);
        return true;
    }

    private static bool TryDecodeLayerCommand(
        ReadOnlySpan<byte> payload,
        bool requireLease,
        out uint sessionId,
        out uint commandId,
        out uint revision,
        out byte layerId,
        out byte leaseUnits)
    {
        sessionId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        commandId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        revision = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        layerId = payload[12];
        leaseUnits = payload[13];
        return sessionId != 0 && commandId != 0 && revision != 0 && layerId != NoLayer && IsZero(payload[14..]) &&
            (requireLease ? IsValidLease(leaseUnits) : leaseUnits == 0);
    }

    private static bool TryDecodeRenewMomentary(ReadOnlySpan<byte> payload, out ProtocolMessage? message)
    {
        uint sessionId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint commandId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        uint activationId = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        byte leaseUnits = payload[12];
        if (sessionId == 0 || commandId == 0 || activationId == 0 || !IsValidLease(leaseUnits) || !IsZero(payload[13..]))
        {
            message = null;
            return false;
        }

        message = new ProtocolMessage.RenewMomentaryLayerCommand(sessionId, commandId, activationId, leaseUnits);
        return true;
    }

    private static bool TryDecodeReleaseMomentary(ReadOnlySpan<byte> payload, out ProtocolMessage? message)
    {
        uint sessionId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint commandId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        uint activationId = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        if (sessionId == 0 || commandId == 0 || activationId == 0 || !IsZero(payload[12..]))
        {
            message = null;
            return false;
        }

        message = new ProtocolMessage.ReleaseMomentaryLayerCommand(sessionId, commandId, activationId);
        return true;
    }

    private static bool TryDecodeCommandResult(ReadOnlySpan<byte> payload, out ProtocolMessage? message)
    {
        uint sessionId = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        uint commandId = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        CommandStatus status = (CommandStatus)payload[12];
        byte? persistentLayer = DecodeOptionalLayer(payload[14]);
        LayerStateIndicators indicators =
            (persistentLayer is null ? LayerStateIndicators.None : LayerStateIndicators.PersistentLayerActive) |
            (payload[15] == 0 ? LayerStateIndicators.None : LayerStateIndicators.MomentaryLayerActive);
        ProtocolMessage.LayerState state = new(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
            payload[13],
            persistentLayer,
            payload[15],
            indicators);
        if (sessionId == 0 || commandId == 0 || !Enum.IsDefined(status) || !IsValidLayerState(state))
        {
            message = null;
            return false;
        }

        message = new ProtocolMessage.CommandResult(sessionId, commandId, status, state);
        return true;
    }

    private static bool TryDecodeError(ReadOnlySpan<byte> payload, out ProtocolMessage? message)
    {
        ProtocolErrorCode code = (ProtocolErrorCode)payload[12];
        if (!Enum.IsDefined(code))
        {
            message = null;
            return false;
        }

        message = new ProtocolMessage.ErrorMessage(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
            code,
            payload[13],
            BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]));
        return true;
    }

    private static void WriteLayerState(Span<byte> payload, uint sessionId, uint relatedId, ProtocolMessage.LayerState state)
    {
        ValidateLayerState(state);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], state.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], relatedId);
        payload[12] = state.EffectiveLayerId;
        payload[13] = state.PersistentLayerId ?? NoLayer;
        payload[14] = state.MomentaryLayerCount;
        payload[15] = (byte)state.Indicators;
    }

    private static void WriteLayerCommand(Span<byte> payload, uint sessionId, uint commandId, uint revision, byte layerId)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(payload, sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], commandId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], revision);
        payload[12] = layerId;
    }

    private static void ValidateHelloResult(ProtocolMessage.HelloResult result)
    {
        RequireNonZero(result.ClientNonce, nameof(result.ClientNonce));
        RequireNonZero(result.Layout.Value, nameof(result.Layout));
        ValidateCapabilities(result.SelectedCapabilities, nameof(result));
        if (!Enum.IsDefined(result.Status))
        {
            throw new ArgumentOutOfRangeException(nameof(result));
        }

        bool validSession = result.Status == HelloStatus.Success
            ? result.SessionId != 0
            : result.SessionId == 0 && result.SelectedCapabilities == ProtocolCapability.None;
        if (!validSession)
        {
            throw new ArgumentException("The Hello result status, capabilities, and session are inconsistent.", nameof(result));
        }
    }

    private static void ValidateLayerCommand(uint sessionId, uint commandId, uint revision, byte layerId)
    {
        RequireSessionAndId(sessionId, commandId);
        RequireNonZero(revision, nameof(revision));
        if (layerId == NoLayer)
        {
            throw new ArgumentOutOfRangeException(nameof(layerId));
        }
    }

    private static void ValidateLayerState(ProtocolMessage.LayerState state)
    {
        if (!IsValidLayerState(state))
        {
            throw new ArgumentException("The layer state contains an invalid revision, layer, count, or indicator combination.", nameof(state));
        }
    }

    private static bool IsValidLayerState(ProtocolMessage.LayerState state)
    {
        LayerStateIndicators knownIndicators =
            LayerStateIndicators.PersistentLayerActive | LayerStateIndicators.MomentaryLayerActive;
        return state.Revision != 0 && state.EffectiveLayerId != NoLayer &&
            (state.Indicators & ~knownIndicators) == 0 &&
            state.PersistentLayerId.HasValue == state.Indicators.HasFlag(LayerStateIndicators.PersistentLayerActive) &&
            (state.MomentaryLayerCount > 0) == state.Indicators.HasFlag(LayerStateIndicators.MomentaryLayerActive);
    }

    private static void ValidateLease(byte leaseUnits)
    {
        if (!IsValidLease(leaseUnits))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseUnits), $"Lease units must be between 1 and {MaximumLeaseUnits}.");
        }
    }

    private static bool IsValidLease(byte leaseUnits)
    {
        return leaseUnits is > 0 and <= MaximumLeaseUnits;
    }

    private static void ValidateCommandStatus(CommandStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
    }

    private static void ValidateErrorCode(ProtocolErrorCode code)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }
    }

    private static void ValidateCapabilities(ProtocolCapability capabilities, string parameterName)
    {
        if (!HasOnlyKnownCapabilities(capabilities))
        {
            throw new ArgumentOutOfRangeException(parameterName, "The capability mask contains an unknown bit.");
        }
    }

    private static bool HasOnlyKnownCapabilities(ProtocolCapability capabilities)
    {
        return (capabilities & ~KnownCapabilities) == 0;
    }

    private static void RequireSessionAndId(uint sessionId, uint id)
    {
        RequireNonZero(sessionId, nameof(sessionId));
        RequireNonZero(id, nameof(id));
    }

    private static void RequireNonZero(ulong value, string parameterName)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Zero is reserved.");
        }
    }

    private static byte? DecodeOptionalLayer(byte value)
    {
        return value == NoLayer ? null : value;
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static byte PackVersion(ProtocolVersion version)
    {
        if (version.Major > 0x0F || version.Minor > 0x0F)
        {
            throw new InvalidOperationException("Wire protocol versions must fit in four bits each.");
        }

        return (byte)((version.Major << 4) | version.Minor);
    }

    private static ProtocolVersion UnpackVersion(byte packed)
    {
        return new ProtocolVersion((ushort)(packed >> 4), (ushort)(packed & 0x0F));
    }
}
