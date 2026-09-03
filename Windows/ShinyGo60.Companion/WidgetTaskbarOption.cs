using ShinyGo60.Companion.Core.Configuration;

namespace ShinyGo60.Companion;

public sealed record WidgetTaskbarOption(
    string Label,
    WidgetTaskbarSelection Selection,
    bool IsPrimary = false);
