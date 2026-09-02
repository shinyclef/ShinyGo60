namespace ShinyGo60.Companion;

internal sealed class CompanionInstanceCoordinator : IDisposable
{
    private const string InstanceMutexName = @"Local\ShinyGo60.Companion.Instance";
    private const string ShowSettingsEventName = @"Local\ShinyGo60.Companion.ShowSettings";
    private readonly Mutex instanceMutex;
    private readonly EventWaitHandle showSettingsEvent;
    private readonly RegisteredWaitHandle? showSettingsRegistration;
    private bool disposed;

    public CompanionInstanceCoordinator()
    {
        this.showSettingsEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ShowSettingsEventName);
        this.instanceMutex = new Mutex(initiallyOwned: false, InstanceMutexName);
        try
        {
            this.IsPrimary = this.instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            this.IsPrimary = true;
        }

        if (this.IsPrimary)
        {
            this.showSettingsRegistration = ThreadPool.RegisterWaitForSingleObject(
                this.showSettingsEvent,
                this.OnShowSettingsSignaled,
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
    }

    public event EventHandler? ShowSettingsRequested;

    public bool IsPrimary { get; }

    public void SignalShowSettings()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        this.showSettingsEvent.Set();
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.showSettingsRegistration?.Unregister(null);
        if (this.IsPrimary)
        {
            this.instanceMutex.ReleaseMutex();
        }

        this.instanceMutex.Dispose();
        this.showSettingsEvent.Dispose();
        this.disposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnShowSettingsSignaled(object? state, bool timedOut)
    {
        _ = state;
        if (!timedOut)
        {
            this.ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
