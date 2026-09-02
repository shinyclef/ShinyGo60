using ShinyGo60.Protocol.Manifests;

namespace ShinyGo60.Builder.Core.Manifests;

public sealed record LayoutArtifacts(
    string KeymapPath,
    string ManifestPath,
    string FirmwareHeaderPath,
    LayoutManifest Manifest);
