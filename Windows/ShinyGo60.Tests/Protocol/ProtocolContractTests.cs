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
            SchemaVersion: 1,
            ProtocolVersion: new ProtocolVersion(0, 1),
            LayoutIdentifier: "fixture-layout",
            KeymapSha256: new string('A', 64),
            FirmwareRevision: "fixture-revision",
            Layers:
            [
                new LayerDefinition(0, "Base"),
                new LayerDefinition(1, "Navigation"),
            ],
            BuiltAtUtc: DateTimeOffset.UnixEpoch);

        AssertEx.Equal("0.1", manifest.ProtocolVersion.ToString());
        AssertEx.Equal(2, manifest.Layers.Count);
        AssertEx.Equal("Navigation", manifest.Layers[1].Name);

        HelloMessage request = new(HelloMessageCodec.CurrentVersion, HelloMessageType.Hello, 0x01020304, 0xA0B0C0D0);
        byte[] requestBytes = HelloMessageCodec.Encode(request);
        byte[] expectedBytes =
        [
            0x53, 0x47, 0x36, 0x30,
            0x00, 0x01, 0x01, 0x00,
            0x04, 0x03, 0x02, 0x01,
            0xD0, 0xC0, 0xB0, 0xA0,
        ];

        AssertEx.SequenceEqual(expectedBytes, requestBytes);
        AssertEx.True(HelloMessageCodec.TryDecode(requestBytes, out HelloMessage decoded), "The Hello packet should decode.");
        AssertEx.Equal(request, decoded);

        requestBytes[0] = 0;
        AssertEx.True(!HelloMessageCodec.TryDecode(requestBytes, out _), "A packet with the wrong magic should be rejected.");
        return ValueTask.CompletedTask;
    }
}
