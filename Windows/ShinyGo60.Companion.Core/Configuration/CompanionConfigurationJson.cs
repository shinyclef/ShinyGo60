using System.Text.Json;
using System.Text.Json.Serialization;
using ShinyGo60.Companion.Core.Shortcuts;
using ShinyGo60.Protocol.Manifests;

namespace ShinyGo60.Companion.Core.Configuration;

public static class CompanionConfigurationJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static async ValueTask<ResolvedCompanionConfiguration> ReadAndResolveAsync(
        string path,
        LayoutManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] utf8Json = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return DeserializeAndResolve(utf8Json, manifest);
    }

    public static async ValueTask WriteAsync(
        string path,
        CompanionConfiguration configuration,
        LayoutManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _ = Resolve(configuration, manifest);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The companion configuration path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] utf8Json = JsonSerializer.SerializeToUtf8Bytes(configuration, SerializerOptions);
            await File.WriteAllBytesAsync(temporaryPath, utf8Json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static ResolvedCompanionConfiguration DeserializeAndResolve(
        ReadOnlySpan<byte> utf8Json,
        LayoutManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        LayoutManifestJson.Validate(manifest);

        CompanionConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<CompanionConfiguration>(utf8Json, SerializerOptions)
                ?? throw new InvalidDataException("The companion configuration is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The companion configuration is not valid JSON for the current schema.", exception);
        }

        return Resolve(configuration, manifest);
    }

    public static ResolvedCompanionConfiguration Resolve(
        CompanionConfiguration configuration,
        LayoutManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(manifest);
        LayoutManifestJson.Validate(manifest);

        if (configuration.SchemaVersion != CompanionConfiguration.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Companion configuration schema {configuration.SchemaVersion} is unsupported; " +
                $"expected {CompanionConfiguration.CurrentSchemaVersion}.");
        }

        if (!Enum.IsDefined(configuration.TransportPreference))
        {
            throw new InvalidDataException($"Transport preference '{configuration.TransportPreference}' is unsupported.");
        }

        WidgetTaskbarSelection widgetTaskbar = ResolveWidgetTaskbar(configuration.WidgetTaskbar);

        if (configuration.Shortcuts is null || configuration.Shortcuts.Count == 0)
        {
            throw new InvalidDataException("The companion configuration must contain at least one shortcut.");
        }

        List<ShortcutBinding> bindings = [];
        HashSet<ShortcutGesture> gestures = [];
        foreach (ShortcutConfiguration configuredShortcut in configuration.Shortcuts)
        {
            if (configuredShortcut is null)
            {
                throw new InvalidDataException("A companion shortcut entry is null.");
            }

            if (string.IsNullOrWhiteSpace(configuredShortcut.Shortcut))
            {
                throw new InvalidDataException("A companion shortcut entry has no shortcut key.");
            }

            if (string.IsNullOrWhiteSpace(configuredShortcut.TargetLayer))
            {
                throw new InvalidDataException($"Shortcut '{configuredShortcut.Shortcut}' has no target layer.");
            }

            ShortcutGesture gesture;
            try
            {
                gesture = ShortcutGesture.Parse(configuredShortcut.Shortcut);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    $"Shortcut '{configuredShortcut.Shortcut}' is invalid: {exception.Message}",
                    exception);
            }

            if (!gestures.Add(gesture))
            {
                throw new InvalidDataException($"Shortcut '{gesture}' is configured more than once.");
            }

            if (!Enum.IsDefined(configuredShortcut.Action))
            {
                throw new InvalidDataException(
                    $"Shortcut '{gesture}' uses unsupported action '{configuredShortcut.Action}'.");
            }

            LayerDefinition? layer = manifest.Layers.FirstOrDefault(
                candidate => string.Equals(candidate.Name, configuredShortcut.TargetLayer, StringComparison.Ordinal));
            if (layer is null)
            {
                throw new InvalidDataException(
                    $"Shortcut '{gesture}' targets layer '{configuredShortcut.TargetLayer}', which is not present in the manifest.");
            }

            bindings.Add(new ShortcutBinding(gesture, configuredShortcut.Action, checked((byte)layer.Id), layer.Name));
        }

        return new ResolvedCompanionConfiguration(configuration.TransportPreference, bindings)
        {
            WidgetTaskbar = widgetTaskbar,
        };
    }

    private static WidgetTaskbarSelection ResolveWidgetTaskbar(WidgetTaskbarSelection? configuredSelection)
    {
        WidgetTaskbarSelection selection = configuredSelection ?? WidgetTaskbarSelection.Primary;
        if (!Enum.IsDefined(selection.Mode))
        {
            throw new InvalidDataException($"Widget taskbar mode '{selection.Mode}' is unsupported.");
        }

        if (selection.Mode == WidgetTaskbarMode.SpecificMonitor)
        {
            if (string.IsNullOrWhiteSpace(selection.MonitorId))
            {
                throw new InvalidDataException("A specific widget monitor must include its monitor ID.");
            }

            return WidgetTaskbarSelection.ForMonitor(selection.MonitorId.Trim());
        }

        if (selection.MonitorId is not null)
        {
            throw new InvalidDataException($"Widget taskbar mode '{selection.Mode}' cannot include a monitor ID.");
        }

        return selection.Mode == WidgetTaskbarMode.All
            ? WidgetTaskbarSelection.All
            : WidgetTaskbarSelection.Primary;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
