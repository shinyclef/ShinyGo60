using ShinyGo60.Protocol.Manifests;

namespace ShinyGo60.Builder.Core.Keymaps;

public sealed class KeymapInspection
{
    private readonly byte[] sourceBytes;

    internal KeymapInspection(
        string sourcePath,
        string keymapSha256,
        IReadOnlyList<LayerDefinition> layers,
        byte[] sourceBytes)
    {
        this.SourcePath = sourcePath;
        this.KeymapSha256 = keymapSha256;
        this.Layers = layers;
        this.sourceBytes = sourceBytes;
    }

    public string SourcePath { get; }

    public string KeymapSha256 { get; }

    public IReadOnlyList<LayerDefinition> Layers { get; }

    public ReadOnlyMemory<byte> SourceBytes => this.sourceBytes;
}
