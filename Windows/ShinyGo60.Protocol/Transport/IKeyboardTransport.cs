namespace ShinyGo60.Protocol.Transport;

public interface IKeyboardTransport : IAsyncDisposable
{
    event EventHandler<KeyboardPacketReceivedEventArgs>? PacketReceived;

    TransportKind Kind { get; }

    bool IsConnected { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}
