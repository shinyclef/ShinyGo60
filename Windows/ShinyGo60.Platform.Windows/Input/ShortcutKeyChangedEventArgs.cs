using ShinyGo60.Companion.Core.Shortcuts;

namespace ShinyGo60.Platform.Windows.Input;

public sealed class ShortcutKeyChangedEventArgs : EventArgs
{
    public ShortcutKeyChangedEventArgs(ShortcutKeyEvent keyEvent)
    {
        this.KeyEvent = keyEvent;
    }

    public ShortcutKeyEvent KeyEvent { get; }
}
