namespace ShinyGo60.Companion.Core.Shortcuts;

public sealed record ShortcutRoute(ShortcutRouteKind Kind, ShortcutBinding? Binding)
{
    public static ShortcutRoute Ignored { get; } = new(ShortcutRouteKind.Ignored, null);

    public static ShortcutRoute RepeatSuppressed { get; } = new(ShortcutRouteKind.RepeatSuppressed, null);
}
