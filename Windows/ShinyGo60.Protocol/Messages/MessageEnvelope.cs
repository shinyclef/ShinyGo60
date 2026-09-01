namespace ShinyGo60.Protocol.Messages;

public sealed record MessageEnvelope(
    ProtocolVersion Version,
    ushort MessageType,
    uint Sequence,
    ReadOnlyMemory<byte> Payload);
