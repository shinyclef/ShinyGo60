using ShinyGo60.Companion.Core.Shortcuts;

namespace ShinyGo60.Companion.Core.Configuration;

public sealed record ResolvedCompanionConfiguration(
    TransportPreference TransportPreference,
    IReadOnlyList<ShortcutBinding> Shortcuts)
{
    public WidgetTaskbarSelection WidgetTaskbar { get; init; } = WidgetTaskbarSelection.Primary;
}
