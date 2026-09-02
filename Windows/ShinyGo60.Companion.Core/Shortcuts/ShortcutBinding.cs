namespace ShinyGo60.Companion.Core.Shortcuts;

public sealed record ShortcutBinding(
    ShortcutGesture Gesture,
    ShortcutActionKind Action,
    byte TargetLayerId,
    string TargetLayerName);
