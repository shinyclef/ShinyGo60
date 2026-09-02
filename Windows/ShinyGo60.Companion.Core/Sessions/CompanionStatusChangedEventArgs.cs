namespace ShinyGo60.Companion.Core.Sessions;

public sealed class CompanionStatusChangedEventArgs : EventArgs
{
    public CompanionStatusChangedEventArgs(CompanionStatus status)
    {
        this.Status = status;
    }

    public CompanionStatus Status { get; }
}
