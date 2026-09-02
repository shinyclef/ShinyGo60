namespace ShinyGo60.Companion;

public sealed class WidgetPlacementFailedEventArgs : EventArgs
{
    public WidgetPlacementFailedEventArgs(Exception exception)
    {
        this.Exception = exception;
    }

    public Exception Exception { get; }
}
