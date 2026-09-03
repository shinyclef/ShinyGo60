namespace ShinyGo60.Companion.Core.Configuration;

public sealed record WidgetTaskbarSelection(
    WidgetTaskbarMode Mode,
    string? MonitorId = null)
{
    public static WidgetTaskbarSelection Primary { get; } = new(WidgetTaskbarMode.Primary);

    public static WidgetTaskbarSelection All { get; } = new(WidgetTaskbarMode.All);

    public static WidgetTaskbarSelection ForMonitor(string monitorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        return new WidgetTaskbarSelection(WidgetTaskbarMode.SpecificMonitor, monitorId);
    }
}
