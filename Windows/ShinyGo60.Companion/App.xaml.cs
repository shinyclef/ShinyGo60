using System.IO;
using System.Reflection;
using System.Windows;
using ShinyGo60.Companion.Core.Configuration;
using ShinyGo60.Companion.Core.Connections;
using ShinyGo60.Companion.Core.Presentation;
using ShinyGo60.Companion.Core.Reconnection;
using ShinyGo60.Companion.Core.Sessions;
using ShinyGo60.Companion.Core.Shortcuts;
using ShinyGo60.Diagnostics;
using ShinyGo60.Platform.Windows.Input;
using ShinyGo60.Platform.Windows.Shell;
using ShinyGo60.Platform.Windows.Transports;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;

namespace ShinyGo60.Companion;

public partial class App : Application, IDisposable
{
    private CompanionService? companionService;
    private GlobalKeyboardShortcutSource? shortcutSource;
    private JsonLineDiagnosticSink? diagnosticSink;
    private StreamWriter? diagnosticWriter;
    private CompanionInstanceCoordinator? instanceCoordinator;
    private CompanionApplicationOptions? applicationOptions;
    private LayoutManifest? manifest;
    private MainWindow? settingsWindow;
    private TaskbarGeometryProvider? taskbarProvider;
    private TaskbarWidgetController? widgetController;
    private WindowsUserActivityMonitor? userActivityMonitor;
    private bool showSettingsWhenReady;
    private bool disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            this.applicationOptions = CompanionApplicationOptions.Parse(e.Args);
            this.instanceCoordinator = new CompanionInstanceCoordinator();
            if (!this.instanceCoordinator.IsPrimary)
            {
                if (!this.applicationOptions.StartInBackground)
                {
                    this.instanceCoordinator.SignalShowSettings();
                }

                this.Shutdown();
                return;
            }

            this.instanceCoordinator.ShowSettingsRequested += this.OnShowSettingsRequested;
            this.manifest = await LayoutManifestJson.ReadAsync(this.applicationOptions.ManifestPath);
            ResolvedCompanionConfiguration configuration = await CompanionConfigurationJson.ReadAndResolveAsync(
                this.applicationOptions.ConfigurationPath,
                this.manifest);
            string diagnosticPath = this.OpenDiagnosticLog();
            this.taskbarProvider = new TaskbarGeometryProvider();
            this.userActivityMonitor = new WindowsUserActivityMonitor(
                new BluetoothConnectionModePolicy(BluetoothConnectionModePolicy.DefaultIdleThreshold));
            this.userActivityMonitor.ModeChanged += this.OnBluetoothConnectionModeChanged;
            this.userActivityMonitor.Start();

            this.settingsWindow = new MainWindow(
                this.manifest,
                configuration,
                this.GetWidgetTaskbarOptions(),
                StartupRegistration.IsEnabled(),
                diagnosticPath);
            this.settingsWindow.ReconnectRequested += this.OnReconnectRequested;
            this.settingsWindow.SettingsSaveRequested += this.OnSettingsSaveRequested;
            this.settingsWindow.ExitRequested += this.OnExitRequested;

            this.widgetController = new TaskbarWidgetController(
                this.taskbarProvider,
                configuration.WidgetTaskbar);
            this.widgetController.SettingsRequested += this.OnWidgetSettingsRequested;
            this.widgetController.PlacementFailed += this.OnWidgetPlacementFailed;
            this.widgetController.UpdateDisplayState(CompanionStatusPresenter.Present(CompanionStatus.Stopped));
            this.widgetController.Start();

            if (!this.applicationOptions.StartInBackground || this.showSettingsWhenReady)
            {
                this.settingsWindow.Show();
            }

            await this.StartRuntimeAsync(configuration);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "ShinyGo60 Companion could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            this.Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        this.Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.StopRuntimeAsync().AsTask().GetAwaiter().GetResult();

        if (this.userActivityMonitor is not null)
        {
            this.userActivityMonitor.ModeChanged -= this.OnBluetoothConnectionModeChanged;
            this.userActivityMonitor.Dispose();
            this.userActivityMonitor = null;
        }

        if (this.widgetController is not null)
        {
            this.widgetController.SettingsRequested -= this.OnWidgetSettingsRequested;
            this.widgetController.PlacementFailed -= this.OnWidgetPlacementFailed;
            this.widgetController.Dispose();
            this.widgetController = null;
        }

        this.taskbarProvider = null;

        if (this.settingsWindow is not null)
        {
            this.settingsWindow.ReconnectRequested -= this.OnReconnectRequested;
            this.settingsWindow.SettingsSaveRequested -= this.OnSettingsSaveRequested;
            this.settingsWindow.ExitRequested -= this.OnExitRequested;
            this.settingsWindow.PrepareForExit();
            this.settingsWindow.Close();
            this.settingsWindow = null;
        }

        if (this.instanceCoordinator is not null)
        {
            this.instanceCoordinator.ShowSettingsRequested -= this.OnShowSettingsRequested;
            this.instanceCoordinator.Dispose();
            this.instanceCoordinator = null;
        }

        this.diagnosticSink?.Dispose();
        this.diagnosticSink = null;
        this.diagnosticWriter?.Dispose();
        this.diagnosticWriter = null;
        this.disposed = true;
        GC.SuppressFinalize(this);
    }

    private async ValueTask StartRuntimeAsync(ResolvedCompanionConfiguration configuration)
    {
        if (this.manifest is null || this.diagnosticSink is null)
        {
            throw new InvalidOperationException("The companion application has not finished initialization.");
        }

        this.companionService = new CompanionService(
            this.manifest,
            configuration,
            new WindowsKeyboardTransportFactory(),
            ExponentialReconnectDelayPolicy.Default,
            this.diagnosticSink);
        this.companionService.StatusChanged += this.OnCompanionStatusChanged;
        this.companionService.SetBluetoothConnectionMode(
            this.userActivityMonitor?.CurrentMode ?? BluetoothConnectionMode.Interactive);

        this.shortcutSource = new GlobalKeyboardShortcutSource(configuration.Shortcuts.Select(binding => binding.Gesture));
        this.shortcutSource.KeyChanged += this.OnShortcutKeyChanged;
        this.shortcutSource.Faulted += this.OnShortcutSourceFaulted;
        this.shortcutSource.Start();
        this.companionService.SeedPressedShortcutKeys(this.shortcutSource.GetCurrentlyPressedKeys());
        await this.companionService.StartAsync();
    }

    private async ValueTask StopRuntimeAsync()
    {
        if (this.shortcutSource is not null)
        {
            this.shortcutSource.KeyChanged -= this.OnShortcutKeyChanged;
            this.shortcutSource.Faulted -= this.OnShortcutSourceFaulted;
            this.shortcutSource.Dispose();
            this.shortcutSource = null;
        }

        if (this.companionService is not null)
        {
            this.companionService.StatusChanged -= this.OnCompanionStatusChanged;
            await this.companionService.DisposeAsync().ConfigureAwait(false);
            this.companionService = null;
        }
    }

    private string OpenDiagnosticLog()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShinyGo60",
            "Logs");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"companion-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        this.diagnosticWriter = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            System.Text.Encoding.UTF8);
        this.diagnosticSink = new JsonLineDiagnosticSink(this.diagnosticWriter);
        return path;
    }

    private void OnCompanionStatusChanged(object? sender, CompanionStatusChangedEventArgs e)
    {
        _ = sender;
        this.Dispatcher.BeginInvoke(() =>
        {
            this.settingsWindow?.UpdateStatus(e.Status);
            this.widgetController?.UpdateDisplayState(CompanionStatusPresenter.Present(e.Status));
        });
    }

    private void OnShortcutKeyChanged(object? sender, ShortcutKeyChangedEventArgs e)
    {
        _ = sender;
        if (this.companionService is null)
        {
            return;
        }

        ShortcutRouteKind result = this.companionService.SubmitShortcutEvent(e.KeyEvent);
        this.settingsWindow?.UpdateShortcutActivity(e.KeyEvent, result);
    }

    private void OnShortcutSourceFaulted(object? sender, GlobalShortcutErrorEventArgs e)
    {
        _ = sender;
        this.settingsWindow?.UpdateShortcutFailure(e.Exception.Message);
    }

    private void OnBluetoothConnectionModeChanged(object? sender, BluetoothConnectionModeChangedEventArgs e)
    {
        _ = sender;
        this.companionService?.SetBluetoothConnectionMode(e.Mode);
    }

    private void OnReconnectRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        this.companionService?.RequestReconnect();
    }

    private async void OnSettingsSaveRequested(object? sender, SettingsSaveRequestedEventArgs e)
    {
        _ = sender;
        try
        {
            if (this.manifest is null || this.applicationOptions is null)
            {
                throw new InvalidOperationException("The companion application has not finished initialization.");
            }

            ResolvedCompanionConfiguration configuration = CompanionConfigurationJson.Resolve(e.Configuration, this.manifest);
            await CompanionConfigurationJson.WriteAsync(
                this.applicationOptions.ConfigurationPath,
                e.Configuration,
                this.manifest);
            StartupRegistration.SetEnabled(
                e.StartWithWindows,
                GetApplicationExecutablePath(),
                this.applicationOptions.ManifestPath,
                this.applicationOptions.ConfigurationPath);
            this.widgetController?.SetSelection(configuration.WidgetTaskbar);

            await this.StopRuntimeAsync();
            await this.StartRuntimeAsync(configuration);
            this.settingsWindow?.ApplySavedConfiguration(configuration, e.StartWithWindows);
            this.settingsWindow?.ShowSaveResult("Settings saved and applied.", succeeded: true);
        }
        catch (Exception exception)
        {
            this.settingsWindow?.ShowSaveResult(exception.Message, succeeded: false);
        }
    }

    private void OnWidgetSettingsRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        this.ShowSettingsWindow();
    }

    private void OnShowSettingsRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        this.Dispatcher.BeginInvoke(this.ShowSettingsWindow);
    }

    private void ShowSettingsWindow()
    {
        if (this.settingsWindow is null)
        {
            this.showSettingsWhenReady = true;
            return;
        }

        this.settingsWindow.UpdateWidgetTaskbarOptions(this.GetWidgetTaskbarOptions());
        this.settingsWindow.Show();
        if (this.settingsWindow.WindowState == WindowState.Minimized)
        {
            this.settingsWindow.WindowState = WindowState.Normal;
        }

        this.settingsWindow.Activate();
    }

    private List<WidgetTaskbarOption> GetWidgetTaskbarOptions()
    {
        List<WidgetTaskbarOption> options =
        [
            new WidgetTaskbarOption("All taskbars", WidgetTaskbarSelection.All),
        ];
        if (this.taskbarProvider is null)
        {
            options.Add(new WidgetTaskbarOption("Primary taskbar", WidgetTaskbarSelection.Primary, IsPrimary: true));
            return options;
        }

        try
        {
            foreach (TaskbarWindowInfo taskbar in this.taskbarProvider.GetAll())
            {
                if (string.IsNullOrWhiteSpace(taskbar.MonitorId))
                {
                    if (taskbar.IsPrimary)
                    {
                        options.Add(new WidgetTaskbarOption("Primary taskbar", WidgetTaskbarSelection.Primary, IsPrimary: true));
                    }

                    continue;
                }

                string label = string.IsNullOrWhiteSpace(taskbar.DisplayName)
                    ? taskbar.MonitorId
                    : taskbar.DisplayName;
                options.Add(new WidgetTaskbarOption(
                    label,
                    WidgetTaskbarSelection.ForMonitor(taskbar.MonitorId),
                    taskbar.IsPrimary));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
        }

        if (!options.Any(option => option.IsPrimary))
        {
            options.Add(new WidgetTaskbarOption("Primary taskbar", WidgetTaskbarSelection.Primary, IsPrimary: true));
        }

        return options;
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        this.settingsWindow?.PrepareForExit();
        this.Shutdown();
    }

    private async void OnWidgetPlacementFailed(object? sender, WidgetPlacementFailedEventArgs e)
    {
        _ = sender;
        if (this.diagnosticSink is null)
        {
            return;
        }

        try
        {
            await this.diagnosticSink.WriteAsync(
                new DiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    DiagnosticLevel.Warning,
                    "companion.widget",
                    "placement_failed",
                    e.Exception.Message));
        }
        catch (IOException)
        {
        }
    }

    private static string GetApplicationExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetExtension(processPath), ".exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        string assemblyPath = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("The companion executable path is unavailable.");
        string executablePath = Path.ChangeExtension(assemblyPath, ".exe");
        return File.Exists(executablePath)
            ? executablePath
            : throw new FileNotFoundException("The companion executable could not be found.", executablePath);
    }
}
