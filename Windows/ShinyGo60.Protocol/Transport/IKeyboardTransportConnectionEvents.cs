namespace ShinyGo60.Protocol.Transport;

public interface IKeyboardTransportConnectionEvents
{
    event EventHandler<KeyboardTransportConnectionLostEventArgs>? ConnectionLost;
}
