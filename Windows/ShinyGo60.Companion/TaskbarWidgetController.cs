using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ShinyGo60.Companion.Core.Configuration;
using ShinyGo60.Companion.Core.Presentation;
using ShinyGo60.Companion.Core.Sessions;
using ShinyGo60.Platform.Windows.Shell;

namespace ShinyGo60.Companion;

public sealed class TaskbarWidgetController : IDisposable
{
    private readonly ITaskbarWindowProvider taskbarProvider;
    private readonly DispatcherTimer maintenanceTimer;
    private readonly Dictionary<string, TaskbarWidgetWindow> windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<TaskbarWidgetWindow> ownedWindows = [];
    private readonly HashSet<string> activeFailures = new(StringComparer.Ordinal);
    private readonly TaskbarWidgetWindow? suppliedWindow;
    private CompanionDisplayState displayState = CompanionStatusPresenter.Present(CompanionStatus.Stopped);
    private WidgetTaskbarSelection selection;
    private bool suppliedWindowUsed;
    private bool started;
    private bool disposed;

    public TaskbarWidgetController(
        ITaskbarWindowProvider taskbarProvider,
        WidgetTaskbarSelection selection)
        : this(taskbarProvider, selection, null)
    {
    }

    public TaskbarWidgetController(
        TaskbarWidgetWindow window,
        ITaskbarWindowProvider taskbarProvider)
        : this(taskbarProvider, WidgetTaskbarSelection.Primary, window)
    {
    }

    private TaskbarWidgetController(
        ITaskbarWindowProvider taskbarProvider,
        WidgetTaskbarSelection selection,
        TaskbarWidgetWindow? suppliedWindow)
    {
        ArgumentNullException.ThrowIfNull(taskbarProvider);
        ArgumentNullException.ThrowIfNull(selection);
        this.taskbarProvider = taskbarProvider;
        this.selection = selection;
        this.suppliedWindow = suppliedWindow;
        this.maintenanceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        this.maintenanceTimer.Tick += this.OnMaintenanceTimerTick;
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler<WidgetPlacementFailedEventArgs>? PlacementFailed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.started)
        {
            return;
        }

        this.started = true;
        this.Reposition();
        this.maintenanceTimer.Start();
    }

    public void SetSelection(WidgetTaskbarSelection selection)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentNullException.ThrowIfNull(selection);
        this.selection = selection;
        if (this.started)
        {
            this.Reposition();
        }
    }

    public void UpdateDisplayState(CompanionDisplayState state)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        this.displayState = state;
        foreach (TaskbarWidgetWindow window in this.windows.Values)
        {
            window.UpdateDisplayState(state);
        }
    }

    public void Reposition()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        HashSet<string> currentFailures = new(StringComparer.Ordinal);
        try
        {
            IReadOnlyList<TaskbarWindowInfo> taskbars = this.SelectTaskbars(this.taskbarProvider.GetAll());
            HashSet<string> selectedKeys = new(StringComparer.OrdinalIgnoreCase);
            foreach (TaskbarWindowInfo taskbar in taskbars)
            {
                string key = GetTaskbarKey(taskbar);
                if (!selectedKeys.Add(key))
                {
                    continue;
                }

                TaskbarWidgetWindow window = this.GetOrCreateWindow(key);
                try
                {
                    PositionWindow(window, taskbar);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    window.Visibility = Visibility.Hidden;
                    this.ReportFailure(exception, currentFailures);
                }
            }

            this.RemoveUnselectedWindows(selectedKeys);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            foreach (TaskbarWidgetWindow window in this.windows.Values)
            {
                window.Visibility = Visibility.Hidden;
            }

            this.ReportFailure(exception, currentFailures);
        }

        this.activeFailures.IntersectWith(currentFailures);
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.maintenanceTimer.Stop();
        this.maintenanceTimer.Tick -= this.OnMaintenanceTimerTick;
        foreach (TaskbarWidgetWindow window in this.windows.Values)
        {
            window.SettingsRequested -= this.OnWindowSettingsRequested;
            if (this.ownedWindows.Contains(window))
            {
                window.Close();
            }
            else
            {
                window.Visibility = Visibility.Hidden;
            }
        }

        this.windows.Clear();
        this.ownedWindows.Clear();
        this.activeFailures.Clear();
        this.disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void PositionWindow(TaskbarWidgetWindow window, TaskbarWindowInfo taskbar)
    {
        TaskbarWidgetPlacement placement = TaskbarWidgetPlacementCalculator.Calculate(taskbar.Geometry);
        if (!placement.IsVisible)
        {
            window.Visibility = Visibility.Hidden;
            return;
        }

        if (!WidgetWindowNativeMethods.IsAttachedToTaskbar(window.NativeHandle, taskbar.Handle))
        {
            WidgetWindowNativeMethods.AttachToTaskbar(window.NativeHandle, taskbar.Handle);
        }

        WidgetWindowNativeMethods.SetBounds(window.NativeHandle, placement.Bounds);
        window.Visibility = Visibility.Visible;
    }

    private static string GetTaskbarKey(TaskbarWindowInfo taskbar)
    {
        return string.IsNullOrWhiteSpace(taskbar.MonitorId)
            ? $"handle:{taskbar.Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture)}"
            : taskbar.MonitorId;
    }

    private IReadOnlyList<TaskbarWindowInfo> SelectTaskbars(IReadOnlyList<TaskbarWindowInfo> taskbars)
    {
        return this.selection.Mode switch
        {
            WidgetTaskbarMode.All => taskbars,
            WidgetTaskbarMode.SpecificMonitor => taskbars
                .Where(taskbar => string.Equals(
                    taskbar.MonitorId,
                    this.selection.MonitorId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            WidgetTaskbarMode.Primary => SelectPrimaryTaskbar(taskbars),
            _ => throw new InvalidOperationException($"Widget taskbar mode '{this.selection.Mode}' is unsupported."),
        };
    }

    private static IReadOnlyList<TaskbarWindowInfo> SelectPrimaryTaskbar(IReadOnlyList<TaskbarWindowInfo> taskbars)
    {
        TaskbarWindowInfo? primary = taskbars.FirstOrDefault(taskbar => taskbar.IsPrimary)
            ?? (taskbars.Count == 0 ? null : taskbars[0]);
        return primary is null ? [] : [primary];
    }

    private TaskbarWidgetWindow GetOrCreateWindow(string key)
    {
        if (this.windows.TryGetValue(key, out TaskbarWidgetWindow? window))
        {
            return window;
        }

        if (this.suppliedWindow is not null && !this.suppliedWindowUsed)
        {
            window = this.suppliedWindow;
            this.suppliedWindowUsed = true;
        }
        else
        {
            window = new TaskbarWidgetWindow();
            this.ownedWindows.Add(window);
        }

        window.SettingsRequested += this.OnWindowSettingsRequested;
        window.UpdateDisplayState(this.displayState);
        window.Show();
        this.windows.Add(key, window);
        return window;
    }

    private void RemoveUnselectedWindows(HashSet<string> selectedKeys)
    {
        foreach (string key in this.windows.Keys.Where(key => !selectedKeys.Contains(key)).ToArray())
        {
            TaskbarWidgetWindow window = this.windows[key];
            window.SettingsRequested -= this.OnWindowSettingsRequested;
            this.windows.Remove(key);
            if (this.ownedWindows.Remove(window))
            {
                window.Close();
            }
            else
            {
                window.Visibility = Visibility.Hidden;
            }
        }
    }

    private void ReportFailure(Exception exception, HashSet<string> currentFailures)
    {
        currentFailures.Add(exception.Message);
        if (this.activeFailures.Add(exception.Message))
        {
            this.PlacementFailed?.Invoke(this, new WidgetPlacementFailedEventArgs(exception));
        }
    }

    private void OnWindowSettingsRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        this.SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMaintenanceTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        this.Reposition();
    }
}
