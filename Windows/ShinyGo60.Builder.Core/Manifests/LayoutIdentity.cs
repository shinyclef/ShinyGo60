using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ShinyGo60.Protocol;

namespace ShinyGo60.Builder.Core.Manifests;

public static class LayoutIdentity
{
    public const string Prefix = "sg60-v1-";

    private static readonly byte[] Domain = "ShinyGo60 layout identity v1\0"u8.ToArray();

    public static string Create(ProtocolVersion protocolVersion, ReadOnlySpan<byte> keymapBytes)
    {
        Span<byte> encodedVersion = stackalloc byte[sizeof(ushort) * 2];
        BinaryPrimitives.WriteUInt16BigEndian(encodedVersion, protocolVersion.Major);
        BinaryPrimitives.WriteUInt16BigEndian(encodedVersion[sizeof(ushort)..], protocolVersion.Minor);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Domain);
        hash.AppendData(encodedVersion);
        hash.AppendData(keymapBytes);

        byte[] digest = hash.GetHashAndReset();
        return Prefix + Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }
}
