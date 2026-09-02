using ShinyGo60.Companion.Core.Shortcuts;

namespace ShinyGo60.Companion.Core.Configuration;

public sealed record ShortcutConfiguration(
    string Shortcut,
    ShortcutActionKind Action,
    string TargetLayer);
