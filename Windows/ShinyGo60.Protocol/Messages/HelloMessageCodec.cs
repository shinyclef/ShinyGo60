using System.Buffers.Binary;

namespace ShinyGo60.Protocol.Messages;

public static class HelloMessageCodec
{
    public const int PacketSize = 16;

    public static readonly ProtocolVersion CurrentVersion = new(0, 1);

    private static ReadOnlySpan<byte> Magic => "SG60"u8;

    public static byte[] Encode(HelloMessage message)
    {
        if (message.Version != CurrentVersion)
        {
            throw new ArgumentException($"Protocol version {message.Version} is unsupported.", nameof(message));
        }

        if (message.Type is not HelloMessageType.Hello and not HelloMessageType.HelloResult)
        {
            throw new ArgumentException($"Message type {message.Type} is unsupported.", nameof(message));
        }

        byte[] packet = new byte[PacketSize];
        Magic.CopyTo(packet);
        packet[4] = checked((byte)message.Version.Major);
        packet[5] = checked((byte)message.Version.Minor);
        packet[6] = (byte)message.Type;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, sizeof(uint)), message.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, sizeof(uint)), message.Challenge);
        return packet;
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, out HelloMessage message)
    {
        message = default;
        if (packet.Length != PacketSize || !packet[..Magic.Length].SequenceEqual(Magic) || packet[7] != 0)
        {
            return false;
        }

        ProtocolVersion version = new(packet[4], packet[5]);
        if (version != CurrentVersion || !Enum.IsDefined((HelloMessageType)packet[6]))
        {
            return false;
        }

        message = new HelloMessage(
            version,
            (HelloMessageType)packet[6],
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8, sizeof(uint))),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(12, sizeof(uint))));
        return true;
    }
}
