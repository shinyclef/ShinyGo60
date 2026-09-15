using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace ShinyGo60.Companion;

internal sealed class CompanionInstanceCoordinator : IDisposable
{
    private const string InstanceMutexName = @"Local\ShinyGo60.Companion.Instance";
    private const string ShowSettingsEventName = @"Local\ShinyGo60.Companion.ShowSettings";
    private const string ExitForUpdateEventName = @"Local\ShinyGo60.Companion.ExitForUpdate";
    private const string IdentityName = @"Local\ShinyGo60.Companion.ActiveBuild";
    private readonly Mutex instanceMutex = new(initiallyOwned: false, InstanceMutexName);
    private readonly EventWaitHandle showSettingsEvent = new(false, EventResetMode.AutoReset, ShowSettingsEventName);
    private readonly EventWaitHandle exitForUpdateEvent = new(false, EventResetMode.AutoReset, ExitForUpdateEventName);
    private RegisteredWaitHandle? showSettingsRegistration;
    private RegisteredWaitHandle? exitRegistration;
    private MemoryMappedFile? identity;
    private bool disposed;

    public CompanionInstanceCoordinator()
    {
        this.TryBecomePrimary();
    }

    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? ExitForUpdateRequested;
    public bool IsPrimary { get; private set; }

    // Mutex ownership is acquired/released on the WPF dispatcher thread.
    public bool TryBecomePrimary()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.IsPrimary)
        {
            return true;
        }

        try
        {
            this.IsPrimary = this.instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            this.IsPrimary = true;
        }

        if (!this.IsPrimary)
        {
            return false;
        }

        this.identity = MemoryMappedFile.CreateOrOpen(IdentityName, 1024);
        using (StreamWriter writer = new(this.identity.CreateViewStream(), new UTF8Encoding(false)))
        {
            writer.WriteLine(typeof(App).Assembly.GetName().Version!.ToString());
            writer.WriteLine(typeof(App).Assembly.ManifestModule.ModuleVersionId.ToString());
        }

        this.showSettingsRegistration = ThreadPool.RegisterWaitForSingleObject(this.showSettingsEvent,
            (_, timedOut) => { if (!timedOut) { this.ShowSettingsRequested?.Invoke(this, EventArgs.Empty); } },
            null, Timeout.Infinite, executeOnlyOnce: false);
        this.exitRegistration = ThreadPool.RegisterWaitForSingleObject(this.exitForUpdateEvent,
            (_, timedOut) => { if (!timedOut) { this.ExitForUpdateRequested?.Invoke(this, EventArgs.Empty); } },
            null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    public static (Version Version, Guid Build)? ReadActiveBuild()
    {
        try
        {
            using MemoryMappedFile active = MemoryMappedFile.OpenExisting(IdentityName, MemoryMappedFileRights.Read);
            using StreamReader reader = new(active.CreateViewStream(0, 1024, MemoryMappedFileAccess.Read));
            return Version.TryParse(reader.ReadLine(), out Version? version) && Guid.TryParse(reader.ReadLine(), out Guid build)
                ? (version, build) : null;
        }
        catch (FileNotFoundException)
        {
            // Older releases own the mutex but do not publish their identity.
            return null;
        }
    }

    public void SignalShowSettings() => this.showSettingsEvent.Set();
    public void RequestExitForUpdate() => this.exitForUpdateEvent.Set();

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.showSettingsRegistration?.Unregister(null);
        this.exitRegistration?.Unregister(null);
        this.identity?.Dispose();
        if (this.IsPrimary)
        {
            this.instanceMutex.ReleaseMutex();
        }

        this.instanceMutex.Dispose();
        this.showSettingsEvent.Dispose();
        this.exitForUpdateEvent.Dispose();
        this.disposed = true;
        GC.SuppressFinalize(this);
    }
}
