using ShinyGo60.Protocol.Transport;

namespace ShinyGo60.Companion.Core.Connections;

public interface IKeyboardTransportFactory
{
    IKeyboardTransport Create(TransportKind kind);
}
