using System.Windows;
using System.Windows.Threading;
using ShinyGo60.Companion.Core.Presentation;
using ShinyGo60.Platform.Windows.Shell;

namespace ShinyGo60.Companion;

public sealed class TaskbarWidgetController : IDisposable
{
    private readonly TaskbarWidgetWindow window;
    private readonly ITaskbarWindowProvider taskbarProvider;
    private readonly DispatcherTimer maintenanceTimer;
    private string? lastFailure;
    private bool disposed;

    public TaskbarWidgetController(
        TaskbarWidgetWindow window,
        ITaskbarWindowProvider taskbarProvider)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(taskbarProvider);
        this.window = window;
        this.taskbarProvider = taskbarProvider;
        this.maintenanceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        this.maintenanceTimer.Tick += this.OnMaintenanceTimerTick;
    }

    public event EventHandler<WidgetPlacementFailedEventArgs>? PlacementFailed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (!this.window.IsVisible)
        {
            this.window.Show();
        }

        this.Reposition();
        this.maintenanceTimer.Start();
    }

    public void Reposition()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        try
        {
            TaskbarWindowInfo? taskbar = this.taskbarProvider.GetCurrent();
            if (taskbar is null)
            {
                this.window.Visibility = Visibility.Hidden;
                return;
            }

            TaskbarWidgetPlacement placement = TaskbarWidgetPlacementCalculator.Calculate(taskbar.Geometry);
            if (!placement.IsVisible)
            {
                this.window.Visibility = Visibility.Hidden;
                return;
            }

            if (!WidgetWindowNativeMethods.IsAttachedToTaskbar(this.window.NativeHandle, taskbar.Handle))
            {
                WidgetWindowNativeMethods.AttachToTaskbar(this.window.NativeHandle, taskbar.Handle);
            }

            this.window.Visibility = Visibility.Visible;
            WidgetWindowNativeMethods.SetBounds(this.window.NativeHandle, placement.Bounds);
            this.lastFailure = null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            this.window.Visibility = Visibility.Hidden;

            if (!string.Equals(this.lastFailure, exception.Message, StringComparison.Ordinal))
            {
                this.lastFailure = exception.Message;
                this.PlacementFailed?.Invoke(this, new WidgetPlacementFailedEventArgs(exception));
            }
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.maintenanceTimer.Stop();
        this.maintenanceTimer.Tick -= this.OnMaintenanceTimerTick;
        this.disposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnMaintenanceTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        this.Reposition();
    }
}
