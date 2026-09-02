namespace ShinyGo60.Protocol.Messages;

public enum HelloStatus : byte
{
    Success = 0,
    LayoutMismatch = 1,
    UnsupportedVersion = 2,
}
