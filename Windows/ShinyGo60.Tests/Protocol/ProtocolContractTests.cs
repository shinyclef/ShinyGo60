using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
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
        return ValueTask.CompletedTask;
    }
}
