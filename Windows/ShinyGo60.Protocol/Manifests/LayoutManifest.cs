namespace ShinyGo60.Protocol.Manifests;

public sealed record LayoutManifest(
    int SchemaVersion,
    ProtocolVersion ProtocolVersion,
    string LayoutIdentifier,
    string KeymapSha256,
    string FirmwareRevision,
    IReadOnlyList<LayerDefinition> Layers,
    DateTimeOffset BuiltAtUtc);
