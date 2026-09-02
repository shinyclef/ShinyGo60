using ShinyGo60.Protocol.Transport;
using ShinyGo60.Tests.Fakes;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Protocol;

internal static class TransportContractTests
{
    public static async ValueTask RunAsync()
    {
        await using FakeKeyboardTransport transport = new() { Kind = TransportKind.Bluetooth };
        byte[] request = [0x01, 0x02, 0x03];
        byte[] receivedPacket = [];
        transport.PacketReceived += (_, args) => receivedPacket = args.Packet.ToArray();

        await transport.ConnectAsync();
        ReadOnlyMemory<byte> response = await transport.ExchangeAsync(request);

        AssertEx.Equal(TransportKind.Bluetooth, transport.Kind);
        AssertEx.True(transport.IsConnected, "The fake transport should be connected.");
        AssertEx.SequenceEqual(request, response.Span);

        transport.RaisePacket(request);
        AssertEx.SequenceEqual(request, receivedPacket);

        await transport.DisconnectAsync();
        AssertEx.True(!transport.IsConnected, "The fake transport should be disconnected.");
    }
}
