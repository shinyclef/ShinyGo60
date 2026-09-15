using System.Buffers.Binary;
using System.Text;

namespace ShinyGo60.Protocol.Transport;

public sealed record ConnectionHistory(
    uint BootId, uint UptimeMs, uint ResetCause, int ResetResult, string FirmwareVersion,
    uint CriticalTotal, uint RoutineTotal, byte CriticalCapacity, byte RoutineCapacity, IReadOnlyList<ConnectionHistory.Entry> Entries)
{
    public const byte FormatVersion = 2;
    public const int RecordSize = 20;
    public const int HeaderSize = 40;

    public sealed record Entry(
        uint Sequence, uint UptimeMs, byte Event, byte Connection, byte Role, bool Critical, int Result, ushort A, ushort B, ushort C, ushort D);

    // Transport joins the two 20-byte headers and subsequent 20-byte records.
    public static ConnectionHistory Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || bytes[0] != FormatVersion || bytes[1] != RecordSize)
        {
            throw new InvalidDataException("The connection history format is unsupported or incomplete.");
        }

        int capacity = bytes[2] + bytes[3];
        if (capacity == 0 || bytes.Length > HeaderSize + capacity * RecordSize || (bytes.Length - HeaderSize) % RecordSize != 0)
        {
            throw new InvalidDataException("The connection history length is invalid.");
        }

        List<Entry> entries = new((bytes.Length - HeaderSize) / RecordSize);
        uint? previousCritical = null;
        uint? previousRoutine = null;
        for (int offset = HeaderSize; offset < bytes.Length; offset += RecordSize)
        {
            ReadOnlySpan<byte> record = bytes.Slice(offset, RecordSize);
            uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(record);
            bool critical = (record[8] & 0x80) != 0;
            uint? previous = critical ? previousCritical : previousRoutine;
            if (previous.HasValue && sequence != unchecked(previous.Value + 1))
            {
                throw new InvalidDataException("The connection history contains a torn or out-of-order stream.");
            }

            if (critical)
            {
                previousCritical = sequence;
            }
            else
            {
                previousRoutine = sequence;
            }

            entries.Add(new Entry(sequence, BinaryPrimitives.ReadUInt32LittleEndian(record[4..]), (byte)(record[8] & 0x7f),
                (byte)(record[9] & 0x3f), (byte)(record[9] >> 6), critical, BinaryPrimitives.ReadInt16LittleEndian(record[10..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[12..]), BinaryPrimitives.ReadUInt16LittleEndian(record[14..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[16..]), BinaryPrimitives.ReadUInt16LittleEndian(record[18..])));
        }

        return new ConnectionHistory(BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]),
            Encoding.ASCII.GetString(bytes.Slice(28, 12)).TrimEnd('\0'),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]), bytes[2], bytes[3], entries);
    }
}
