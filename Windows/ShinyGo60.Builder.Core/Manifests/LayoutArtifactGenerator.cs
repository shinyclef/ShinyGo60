using System.Text;
using ShinyGo60.Builder.Core.Keymaps;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;

namespace ShinyGo60.Builder.Core.Manifests;

public static class LayoutArtifactGenerator
{
    public const string ManifestFileName = "layout-manifest.json";
    public const string FirmwareHeaderFileName = "shinygo60_layout.h";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async ValueTask<LayoutArtifacts> GenerateAsync(
        string keymapPath,
        string destinationDirectory,
        ProtocolVersion protocolVersion,
        string firmwareRevision,
        DateTimeOffset builtAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(firmwareRevision);

        KeymapInspection inspection = await Go60KeymapInspector.InspectAsync(keymapPath, cancellationToken).ConfigureAwait(false);
        return await GenerateAsync(
            inspection,
            destinationDirectory,
            protocolVersion,
            firmwareRevision,
            builtAtUtc,
            cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<LayoutArtifacts> GenerateAsync(
        KeymapInspection inspection,
        string destinationDirectory,
        ProtocolVersion protocolVersion,
        string firmwareRevision,
        DateTimeOffset builtAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(firmwareRevision);

        string destination = Path.GetFullPath(destinationDirectory);
        string copiedKeymapPath = Path.Combine(destination, Path.GetFileName(inspection.SourcePath));
        if (string.Equals(inspection.SourcePath, copiedKeymapPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The generated keymap destination must differ from the source path.");
        }

        string layoutIdentifier = LayoutIdentity.Create(protocolVersion, inspection.SourceBytes.Span);
        LayoutManifest manifest = new(
            SchemaVersion: LayoutManifest.CurrentSchemaVersion,
            ProtocolVersion: protocolVersion,
            LayoutIdentifier: layoutIdentifier,
            KeymapSha256: inspection.KeymapSha256,
            FirmwareRevision: firmwareRevision,
            Layers: inspection.Layers,
            BuiltAtUtc: builtAtUtc.ToUniversalTime());

        byte[] manifestBytes = LayoutManifestJson.Serialize(manifest);
        string firmwareHeader = CreateFirmwareHeader(layoutIdentifier, inspection.KeymapSha256);
        string manifestPath = Path.Combine(destination, ManifestFileName);
        string firmwareHeaderPath = Path.Combine(destination, FirmwareHeaderFileName);

        Directory.CreateDirectory(destination);
        await File.WriteAllBytesAsync(copiedKeymapPath, inspection.SourceBytes, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(firmwareHeaderPath, firmwareHeader, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);

        return new LayoutArtifacts(copiedKeymapPath, manifestPath, firmwareHeaderPath, manifest);
    }

    private static string CreateFirmwareHeader(string layoutIdentifier, string keymapSha256)
    {
        return $$"""
            #ifndef SHINYGO60_GENERATED_LAYOUT_H_
            #define SHINYGO60_GENERATED_LAYOUT_H_

            #define SHINYGO60_LAYOUT_IDENTIFIER "{{layoutIdentifier}}"
            #define SHINYGO60_KEYMAP_SHA256 "{{keymapSha256}}"

            #endif /* SHINYGO60_GENERATED_LAYOUT_H_ */
            """;
    }
}
