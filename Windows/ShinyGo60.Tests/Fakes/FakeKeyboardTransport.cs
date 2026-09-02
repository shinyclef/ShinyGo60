using ShinyGo60.Protocol.Transport;

namespace ShinyGo60.Tests.Fakes;

internal sealed class FakeKeyboardTransport : IKeyboardTransport
{
    public event EventHandler<KeyboardPacketReceivedEventArgs>? PacketReceived;

    public TransportKind Kind { get; init; } = TransportKind.Usb;

    public bool IsConnected { get; private set; }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        this.IsConnected = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        if (!this.IsConnected)
        {
            throw new InvalidOperationException("The fake transport is disconnected.");
        }

        ReadOnlyMemory<byte> response = request.ToArray();
        return ValueTask.FromResult(response);
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        this.IsConnected = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        this.IsConnected = false;
        return ValueTask.CompletedTask;
    }

    public void RaisePacket(ReadOnlyMemory<byte> packet)
    {
        this.PacketReceived?.Invoke(this, new KeyboardPacketReceivedEventArgs(packet));
    }
}
