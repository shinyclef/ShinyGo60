namespace ShinyGo60.Protocol.Messages;

public enum ProtocolErrorCode : byte
{
    MalformedPacket = 1,
    UnsupportedVersion = 2,
    UnsupportedMessage = 3,
    NoSession = 4,
    WrongSession = 5,
    LayoutMismatch = 6,
    CapabilityUnavailable = 7,
    InvalidLayer = 8,
    StaleState = 9,
    StaleCommand = 10,
    DuplicateConflict = 11,
    LeaseOutOfRange = 12,
    Busy = 13,
    Internal = 14,
}
