namespace ShinyGo60.Protocol.Messages;

public enum CommandStatus : byte
{
    Applied = 0,
    NoChange = 1,
    Duplicate = 2,
    AlreadyReleased = 3,
}
