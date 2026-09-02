namespace ShinyGo60.Companion.Core.Shortcuts;

public sealed class ShortcutRouter
{
    private readonly Dictionary<ShortcutGesture, ShortcutBinding> bindings;
    private readonly HashSet<string> pressedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShortcutBinding> activeBindings = new(StringComparer.Ordinal);
    private readonly object syncRoot = new();

    public ShortcutRouter(IEnumerable<ShortcutBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        this.bindings = [];
        foreach (ShortcutBinding binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            if (!this.bindings.TryAdd(binding.Gesture, binding))
            {
                throw new ArgumentException($"Shortcut '{binding.Gesture}' is configured more than once.", nameof(bindings));
            }
        }

        if (this.bindings.Count == 0)
        {
            throw new ArgumentException("At least one shortcut binding is required.", nameof(bindings));
        }
    }

    public ShortcutRoute Route(ShortcutKeyEvent keyEvent)
    {
        ArgumentNullException.ThrowIfNull(keyEvent);
        ShortcutGesture gesture = ShortcutGesture.FromInput(keyEvent.Key, keyEvent.Modifiers);

        lock (this.syncRoot)
        {
            if (keyEvent.State == ShortcutKeyState.Down)
            {
                if (!this.pressedKeys.Add(gesture.Key))
                {
                    return ShortcutRoute.RepeatSuppressed;
                }

                if (!this.bindings.TryGetValue(gesture, out ShortcutBinding? binding))
                {
                    return ShortcutRoute.Ignored;
                }

                this.activeBindings.Add(gesture.Key, binding);
                return new ShortcutRoute(ShortcutRouteKind.Pressed, binding);
            }

            if (!this.pressedKeys.Remove(gesture.Key))
            {
                return ShortcutRoute.Ignored;
            }

            return this.activeBindings.Remove(gesture.Key, out ShortcutBinding? releasedBinding)
                ? new ShortcutRoute(ShortcutRouteKind.Released, releasedBinding)
                : ShortcutRoute.Ignored;
        }
    }

    public void SeedPressedKeys(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (this.syncRoot)
        {
            foreach (string key in keys)
            {
                this.pressedKeys.Add(ShortcutGesture.NormalizeKey(key));
            }
        }
    }

    public void ForgetActiveBindings()
    {
        lock (this.syncRoot)
        {
            this.activeBindings.Clear();
        }
    }
}
