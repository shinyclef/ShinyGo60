namespace ShinyGo60.Companion.Core.Shortcuts;

public sealed record ShortcutKeyEvent(
    string Key,
    ShortcutModifiers Modifiers,
    ShortcutKeyState State,
    bool IsInjected = false);
