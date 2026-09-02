namespace ShinyGo60.Protocol.Transport;

public sealed class KeyboardPacketReceivedEventArgs : EventArgs
{
    public KeyboardPacketReceivedEventArgs(ReadOnlyMemory<byte> packet)
    {
        this.Packet = packet.ToArray();
    }

    public ReadOnlyMemory<byte> Packet { get; }
}
