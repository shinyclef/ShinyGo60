namespace ShinyGo60.Protocol.Transport;

public sealed class KeyboardTransportConnectionLostEventArgs : EventArgs
{
    public KeyboardTransportConnectionLostEventArgs(Exception cause)
    {
        this.Cause = cause;
    }

    public Exception Cause { get; }
}
