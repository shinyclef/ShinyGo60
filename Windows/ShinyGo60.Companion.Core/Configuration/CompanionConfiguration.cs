namespace ShinyGo60.Companion.Core.Configuration;

public sealed record CompanionConfiguration(
    int SchemaVersion,
    TransportPreference TransportPreference,
    IReadOnlyList<ShortcutConfiguration> Shortcuts)
{
    public const int CurrentSchemaVersion = 1;

    public WidgetTaskbarSelection? WidgetTaskbar { get; init; }
}
