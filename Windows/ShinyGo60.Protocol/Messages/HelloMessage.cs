namespace ShinyGo60.Protocol.Messages;

public readonly record struct HelloMessage(
    ProtocolVersion Version,
    HelloMessageType Type,
    uint Sequence,
    uint Challenge);
