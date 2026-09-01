namespace ShinyGo60.Companion.Core.Shortcuts;

public sealed record ShortcutBinding(
    string Shortcut,
    ShortcutActionKind Action,
    int TargetLayerId);
