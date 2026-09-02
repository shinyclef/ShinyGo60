using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Protocol;

internal static class ProtocolContractTests
{
    public static ValueTask RunAsync()
    {
        LayoutManifest manifest = new(
            SchemaVersion: LayoutManifest.CurrentSchemaVersion,
            ProtocolVersion: ProtocolVersion.Current,
            LayoutIdentifier: "sg60-v1-0123456789abcdef0123456789abcdef",
            KeymapSha256: new string('a', 64),
            FirmwareRevision: "fixture-revision",
            Layers:
            [
                new LayerDefinition(0, "Base"),
                new LayerDefinition(1, "Navigation"),
            ],
            BuiltAtUtc: DateTimeOffset.UnixEpoch);

        AssertEx.Equal("1.0", manifest.ProtocolVersion.ToString());
        AssertEx.Equal(2, manifest.Layers.Count);
        AssertEx.Equal("Navigation", manifest.Layers[1].Name);

        byte[] manifestJson = LayoutManifestJson.Serialize(manifest);
        LayoutManifest decodedManifest = LayoutManifestJson.Deserialize(manifestJson);
        AssertEx.Equal(manifest.SchemaVersion, decodedManifest.SchemaVersion);
        AssertEx.Equal(manifest.LayoutIdentifier, decodedManifest.LayoutIdentifier);
        AssertEx.Equal(manifest.KeymapSha256, decodedManifest.KeymapSha256);
        AssertEx.Equal(manifest.Layers.Count, decodedManifest.Layers.Count);

        LayoutFingerprint fingerprint = LayoutFingerprint.FromLayoutIdentifier(manifest.LayoutIdentifier);
        AssertEx.Equal(new LayoutFingerprint(0x0123456789ABCDEF), fingerprint);
        return ValueTask.CompletedTask;
    }
}
