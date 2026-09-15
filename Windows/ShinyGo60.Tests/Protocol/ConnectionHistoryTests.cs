using System.Buffers.Binary;
using ShinyGo60.Protocol.Transport;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Protocol;

internal static class ConnectionHistoryTests
{
    public static ValueTask RunAsync()
    {
        byte[] bytes = CreateSnapshot(42, 18, 19);
        ConnectionHistory history = ConnectionHistory.Decode(bytes);
        AssertEx.Equal(42U, history.BootId);
        AssertEx.Equal(2, history.Entries.Count);
        AssertEx.Equal(18U, history.Entries[0].Sequence);
        AssertEx.Equal(8, history.Entries[0].Result);
        AssertEx.Equal((byte)2, history.Entries[0].Event);
        AssertEx.Equal((ushort)400, history.Entries[0].C);
        AssertEx.Throws<InvalidDataException>(() => ConnectionHistory.Decode(bytes.AsSpan(0, bytes.Length - 1)));
        bytes[0] = 0;
        AssertEx.Throws<InvalidDataException>(() => ConnectionHistory.Decode(bytes));
        AssertEx.Throws<InvalidDataException>(() => ConnectionHistory.Decode(CreateSnapshot(42, 18, 20)));
        AssertEx.Equal(0U, ConnectionHistory.Decode(CreateSnapshot(42, uint.MaxValue, 0)).Entries[1].Sequence);
        AssertEx.Equal(0, ConnectionHistory.Decode(CreateSnapshot(42)).Entries.Count);
        byte[] full = CreateSnapshot(42, Enumerable.Range(1, 64).Select(value => (uint)value).ToArray());
        AssertEx.Equal(1320, full.Length);
        AssertEx.Equal(64, ConnectionHistory.Decode(full).Entries.Count);
        full[2] = 1; full[3] = 0;
        AssertEx.Throws<InvalidDataException>(() => ConnectionHistory.Decode(full));
        return ValueTask.CompletedTask;
    }

    internal static byte[] CreateSnapshot(uint boot, params uint[] sequences)
    {
        byte[] bytes = new byte[40 + sequences.Length * 20];
        bytes[0] = 2;
        bytes[1] = 20;
        bytes[2] = 64;
        bytes[3] = 32;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), boot);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 123456);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), sequences.LastOrDefault());
        "0.9.0"u8.CopyTo(bytes.AsSpan(28));
        for (int index = 0; index < sequences.Length; index++)
        {
            Span<byte> record = bytes.AsSpan(40 + index * 20, 20);
            BinaryPrimitives.WriteUInt32LittleEndian(record, sequences[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], 123000);
            record[8] = 0x82;
            record[9] = 0x40;
            BinaryPrimitives.WriteInt16LittleEndian(record[10..], 8);
            BinaryPrimitives.WriteUInt16LittleEndian(record[16..], 400);
        }
        return bytes;
    }
}
