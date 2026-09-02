namespace ShinyGo60.Platform.Windows.Input;

public sealed class GlobalShortcutErrorEventArgs : EventArgs
{
    public GlobalShortcutErrorEventArgs(Exception exception)
    {
        this.Exception = exception;
    }

    public Exception Exception { get; }
}
