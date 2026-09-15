using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ShinyGo60.Builder.Core.Keymaps;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;

namespace ShinyGo60.Builder.Core.Manifests;

public static class LayoutIdentity
{
    public const string Prefix = "sg60-v1-";

    private static readonly byte[] Domain = "ShinyGo60 layer contract identity v2\0"u8.ToArray();

    public static string Create(ProtocolVersion protocolVersion, ReadOnlySpan<byte> keymapBytes)
    {
        Span<byte> encodedVersion = stackalloc byte[sizeof(ushort) * 2];
        BinaryPrimitives.WriteUInt16BigEndian(encodedVersion, protocolVersion.Major);
        BinaryPrimitives.WriteUInt16BigEndian(encodedVersion[sizeof(ushort)..], protocolVersion.Minor);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Domain);
        hash.AppendData(encodedVersion);
        // Companion commands depend on layer IDs and names, not the keys bound within each layer.
        KeymapInspection inspection = Go60KeymapInspector.Inspect("layout.keymap", keymapBytes.ToArray());
        Span<byte> encodedLayer = stackalloc byte[sizeof(int) * 2];
        foreach (LayerDefinition layer in inspection.Layers)
        {
            byte[] name = Encoding.UTF8.GetBytes(layer.Name);
            BinaryPrimitives.WriteInt32BigEndian(encodedLayer, layer.Id);
            BinaryPrimitives.WriteInt32BigEndian(encodedLayer[sizeof(int)..], name.Length);
            hash.AppendData(encodedLayer);
            hash.AppendData(name);
        }

        byte[] digest = hash.GetHashAndReset();
        return Prefix + Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }
}
