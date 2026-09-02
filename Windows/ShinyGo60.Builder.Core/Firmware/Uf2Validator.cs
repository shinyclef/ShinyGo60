using System.Buffers.Binary;
using System.Text;

namespace ShinyGo60.Builder.Core.Firmware;

public static class Uf2Validator
{
    private const int BlockSize = 512;
    private const int PayloadOffset = 32;
    private const int MaximumPayloadSize = 476;
    private const uint FirstMagic = 0x0A324655;
    private const uint SecondMagic = 0x9E5D5157;
    private const uint FinalMagic = 0x0AB16F30;

    public static Uf2ValidationResult Validate(ReadOnlySpan<byte> uf2Bytes, IReadOnlyList<string> requiredPayloadText)
    {
        if (uf2Bytes.Length == 0 || uf2Bytes.Length % BlockSize != 0)
        {
            throw new InvalidDataException("The firmware output is empty or is not made of complete UF2 blocks.");
        }

        List<byte[]> segmentPayloads = [];
        using MemoryStream currentPayload = new();
        uint expectedBlockNumber = 0;
        uint currentTotalBlocks = 0;

        int blockCount = uf2Bytes.Length / BlockSize;
        for (int blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            ReadOnlySpan<byte> block = uf2Bytes.Slice(blockIndex * BlockSize, BlockSize);
            ValidateMagic(block, blockIndex);

            uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(block[16..]);
            uint blockNumber = BinaryPrimitives.ReadUInt32LittleEndian(block[20..]);
            uint totalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(block[24..]);
            if (payloadSize is 0 or > MaximumPayloadSize)
            {
                throw new InvalidDataException($"UF2 block {blockIndex} has invalid payload size {payloadSize}.");
            }

            if (blockNumber == 0)
            {
                if (blockIndex > 0)
                {
                    CompleteSegment(segmentPayloads, currentPayload, expectedBlockNumber, currentTotalBlocks);
                }

                expectedBlockNumber = 0;
                currentTotalBlocks = totalBlocks;
            }

            if (totalBlocks == 0 || totalBlocks != currentTotalBlocks || blockNumber != expectedBlockNumber)
            {
                throw new InvalidDataException($"UF2 block {blockIndex} has an inconsistent segment number or block count.");
            }

            currentPayload.Write(block.Slice(PayloadOffset, checked((int)payloadSize)));
            expectedBlockNumber++;
        }

        CompleteSegment(segmentPayloads, currentPayload, expectedBlockNumber, currentTotalBlocks);
        if (segmentPayloads.Count != 2)
        {
            throw new InvalidDataException($"The combined Go60 UF2 must contain two complete firmware segments; found {segmentPayloads.Count}.");
        }

        foreach (string requiredText in requiredPayloadText)
        {
            byte[] needle = Encoding.UTF8.GetBytes(requiredText);
            if (!segmentPayloads.Any(payload => payload.AsSpan().IndexOf(needle) >= 0))
            {
                throw new InvalidDataException($"The firmware does not contain the generated identity value '{requiredText}'.");
            }
        }

        return new Uf2ValidationResult(uf2Bytes.Length, blockCount, segmentPayloads.Count);
    }

    private static void ValidateMagic(ReadOnlySpan<byte> block, int blockIndex)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(block) != FirstMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(block[4..]) != SecondMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(block[508..]) != FinalMagic)
        {
            throw new InvalidDataException($"UF2 block {blockIndex} has invalid magic values.");
        }
    }

    private static void CompleteSegment(
        List<byte[]> segmentPayloads,
        MemoryStream currentPayload,
        uint completedBlocks,
        uint expectedBlocks)
    {
        if (completedBlocks == 0 || completedBlocks != expectedBlocks)
        {
            throw new InvalidDataException(
                $"A UF2 segment ended after {completedBlocks} blocks but declared {expectedBlocks}.");
        }

        segmentPayloads.Add(currentPayload.ToArray());
        currentPayload.SetLength(0);
    }
}
