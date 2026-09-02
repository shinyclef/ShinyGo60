using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShinyGo60.Protocol.Manifests;

public static partial class LayoutManifestJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static byte[] Serialize(LayoutManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
    }

    public static LayoutManifest Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            LayoutManifest manifest = JsonSerializer.Deserialize<LayoutManifest>(utf8Json, SerializerOptions)
                ?? throw new InvalidDataException("The layout manifest is empty.");

            Validate(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The layout manifest is not valid JSON for the current schema.", exception);
        }
    }

    public static async ValueTask WriteAsync(
        string path,
        LayoutManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await File.WriteAllBytesAsync(path, Serialize(manifest), cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<LayoutManifest> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] utf8Json = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize(utf8Json);
    }

    public static void Validate(LayoutManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != LayoutManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Layout manifest schema {manifest.SchemaVersion} is unsupported; expected {LayoutManifest.CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.LayoutIdentifier) || !LayoutIdentifierRegex().IsMatch(manifest.LayoutIdentifier))
        {
            throw new InvalidDataException("The layout identifier is missing or has an unsupported format.");
        }

        if (string.IsNullOrWhiteSpace(manifest.KeymapSha256) || !Sha256Regex().IsMatch(manifest.KeymapSha256))
        {
            throw new InvalidDataException("The keymap SHA-256 must contain 64 lowercase hexadecimal characters.");
        }

        if (string.IsNullOrWhiteSpace(manifest.FirmwareRevision))
        {
            throw new InvalidDataException("The firmware source revision is missing.");
        }

        if (manifest.BuiltAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The layout manifest timestamp must be UTC.");
        }

        if (manifest.Layers is null || manifest.Layers.Count == 0)
        {
            throw new InvalidDataException("The layout manifest must contain at least one layer.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        for (int index = 0; index < manifest.Layers.Count; index++)
        {
            LayerDefinition layer = manifest.Layers[index];
            if (layer.Id != index)
            {
                throw new InvalidDataException($"Layer '{layer.Name}' has ID {layer.Id}; expected {index}.");
            }

            if (string.IsNullOrWhiteSpace(layer.Name) || !names.Add(layer.Name))
            {
                throw new InvalidDataException($"Layer name '{layer.Name}' is empty or duplicated.");
            }
        }
    }

    [GeneratedRegex("^sg60-v1-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex LayoutIdentifierRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
