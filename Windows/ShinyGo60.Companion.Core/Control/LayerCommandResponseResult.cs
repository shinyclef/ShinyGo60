namespace ShinyGo60.Companion.Core.Control;

public enum LayerCommandResponseResult
{
    CommandAccepted,
    CommandRejected,
    NoPendingCommand,
    WrongSession,
    WrongCommand,
    InvalidLayerState,
    UnexpectedMessage,
}
