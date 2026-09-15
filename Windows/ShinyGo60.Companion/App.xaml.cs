using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ShinyGo60.Companion.Core.Diagnostics;
using ShinyGo60.Companion.Core.Configuration;
using ShinyGo60.Companion.Core.Connections;
using ShinyGo60.Companion.Core.Presentation;
using ShinyGo60.Companion.Core.Reconnection;
using ShinyGo60.Companion.Core.Sessions;
using ShinyGo60.Companion.Core.Shortcuts;
using ShinyGo60.Diagnostics;
using ShinyGo60.Platform.Windows.Input;
using ShinyGo60.Platform.Windows.Diagnostics;
using ShinyGo60.Platform.Windows.Shell;
using ShinyGo60.Platform.Windows.Transports;
using ShinyGo60.Protocol;
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
    private string diagnosticPath = string.Empty;
    private readonly CancellationTokenSource issueCancellation = new();
    private Task issueRefresh = Task.CompletedTask;
    private DispatcherTimer? issueTimer;
    private CompanionConnectionState? lastIssueConnectionState;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            bool verifyPackage = e.Args.Length == 1 && e.Args[0] == "--verify-package";
            this.applicationOptions = CompanionApplicationOptions.Parse(verifyPackage ? [] : e.Args);
            this.manifest = await LayoutManifestJson.ReadAsync(this.applicationOptions.ManifestPath);
            if (this.manifest.ProtocolVersion != ProtocolVersion.Current)
            {
                throw new InvalidDataException(
                    $"Manifest protocol {this.manifest.ProtocolVersion} is unsupported; expected {ProtocolVersion.Current}.");
            }
            ResolvedCompanionConfiguration configuration = await CompanionConfigurationJson.ReadAndResolveAsync(
                this.applicationOptions.ConfigurationPath, this.manifest);
            if (verifyPackage)
            {
                this.Shutdown();
                return;
            }
            this.instanceCoordinator = new CompanionInstanceCoordinator();
            if (!this.instanceCoordinator.IsPrimary)
            {
                Version currentVersion = typeof(App).Assembly.GetName().Version!;
                (Version Version, Guid Build)? active = CompanionInstanceCoordinator.ReadActiveBuild();
                if (active.HasValue && active.Value.Version < currentVersion)
                {
                    this.instanceCoordinator.RequestExitForUpdate();
                    for (int attempt = 0; attempt < 100 && !this.instanceCoordinator.TryBecomePrimary(); attempt++)
                    {
                        await Task.Delay(100);
                    }
                    this.instanceCoordinator.TryBecomePrimary();
                }

                if (!this.instanceCoordinator.IsPrimary)
                {
                    bool sameBuild = active.HasValue && active.Value.Version == currentVersion &&
                        active.Value.Build == typeof(App).Assembly.ManifestModule.ModuleVersionId;
                    this.instanceCoordinator.SignalShowSettings();
                    if (!sameBuild)
                    {
                        string running = active.HasValue ? $"Companion {active.Value.Version}" : "An older or unidentified companion";
                        MessageBox.Show($"{running} is still running. Version {currentVersion} has not started. " +
                            "Exit the running companion, then launch this version again.", "Companion version conflict",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }

                    this.Shutdown(sameBuild ? 0 : 2);
                    return;
                }
            }

            this.instanceCoordinator.ShowSettingsRequested += this.OnShowSettingsRequested;
            this.instanceCoordinator.ExitForUpdateRequested += this.OnExitForUpdateRequested;
            this.diagnosticPath = this.OpenDiagnosticLog();
            await this.diagnosticSink!.WriteAsync(new DiagnosticEvent(DateTimeOffset.UtcNow, DiagnosticLevel.Information,
                "companion.app", "application_started", "Started the companion.", new Dictionary<string, string>
                {
                    ["version"] = typeof(App).Assembly.GetName().Version!.ToString(),
                    ["buildId"] = typeof(App).Assembly.ManifestModule.ModuleVersionId.ToString(),
                }));
            if (StartupRegistration.IsEnabled())
            {
                StartupRegistration.SetEnabled(true, GetApplicationExecutablePath(),
                    this.applicationOptions.ManifestPath, this.applicationOptions.ConfigurationPath);
            }
            this.taskbarProvider = new TaskbarGeometryProvider();
            this.userActivityMonitor = new WindowsUserActivityMonitor(
                BluetoothConnectionModePolicy.FromSettings(configuration.AdaptiveBluetooth));
            this.userActivityMonitor.ModeChanged += this.OnBluetoothConnectionModeChanged;
            this.userActivityMonitor.Start();

            this.settingsWindow = new MainWindow(
                this.manifest,
                configuration,
                this.GetWidgetTaskbarOptions(),
                StartupRegistration.IsEnabled(),
                this.diagnosticPath);
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
            this.issueTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            this.issueTimer.Tick += this.OnIssueTimerTick;
            this.issueTimer.Start();
            this.RequestIssueRefresh();
        }
        catch (Exception exception)
        {
            if (e.Args.Length == 1 && e.Args[0] == "--verify-package")
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "package-verification-error.txt"), exception.ToString());
                this.Shutdown(1);
                return;
            }
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

        this.issueTimer?.Stop();
        this.issueCancellation.Cancel();
        this.issueRefresh.GetAwaiter().GetResult();
        this.issueCancellation.Dispose();
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
            this.instanceCoordinator.ExitForUpdateRequested -= this.OnExitForUpdateRequested;
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
            this.diagnosticSink,
            historyCheckpointPath: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShinyGo60", "connection-history-position.json"));
        this.companionService.StatusChanged += this.OnCompanionStatusChanged;
        this.companionService.SetBluetoothConnectionMode(
            this.userActivityMonitor?.CurrentMode ?? BluetoothConnectionMode.Interactive);

        AdaptiveBluetoothSettings latency = configuration.AdaptiveBluetooth;
        await this.diagnosticSink.WriteAsync(new DiagnosticEvent(DateTimeOffset.UtcNow, DiagnosticLevel.Information,
            "companion.bluetooth", "latency_settings", "Applied Bluetooth latency settings.", new Dictionary<string, string>
            {
                ["enabled"] = latency.Enabled.ToString(),
                ["activeLatency"] = latency.ActiveLatency.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["idleLatency"] = latency.IdleLatency.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["idleAfterSeconds"] = latency.IdleAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["minimumSwitchSeconds"] = latency.MinimumSwitchSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["useIdleWhenLocked"] = latency.UseIdleWhenLocked.ToString(),
            }));

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
            if (this.lastIssueConnectionState != e.Status.ConnectionState)
            {
                this.lastIssueConnectionState = e.Status.ConnectionState;
                this.RequestIssueRefresh();
            }
        });
    }

    private void OnIssueTimerTick(object? sender, EventArgs e) => this.RequestIssueRefresh();

    private void RequestIssueRefresh()
    {
        if (!this.issueRefresh.IsCompleted || this.issueCancellation.IsCancellationRequested)
        {
            return;
        }

        this.issueRefresh = Task.Run(async () =>
        {
            CancellationToken token = this.issueCancellation.Token;
            try
            {
                string directory = Path.GetDirectoryName(this.diagnosticPath)!;
                IReadOnlyList<DiagnosticEvent> events = await ConnectionIssueLogReader.ReadAsync(directory, this.diagnosticPath, token).ConfigureAwait(false);
                IReadOnlyList<BluetoothSystemEvent> windows = [];
                string status = "Uses the last seven days of local logs and up to 512 recent Windows Bluetooth events.";
                try
                {
                    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    windows = await BluetoothSystemHistory.ReadAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (!token.IsCancellationRequested &&
                    exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                        OperationCanceledException or System.Xml.XmlException)
                {
                    status = "Firmware/companion history loaded. Windows Bluetooth events unavailable: " + exception.GetType().Name + ".";
                }

                ConnectionIssueReport report = new(DateTimeOffset.UtcNow, ConnectionIssueAnalyzer.Analyze(events, windows), status, windows);
                string path = Path.Combine(directory, "connection-issues.json");
                await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(report), token).ConfigureAwait(false);
                File.Move(path + ".tmp", path, overwrite: true);
                _ = this.Dispatcher.BeginInvoke(() => this.settingsWindow?.UpdateConnectionIssues(report));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Application shutdown cancels this optional background read.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _ = this.Dispatcher.BeginInvoke(() => this.settingsWindow?.ShowIssueHistoryError("Issue history unavailable: " + exception.Message));
            }
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
            this.userActivityMonitor!.UpdatePolicy(BluetoothConnectionModePolicy.FromSettings(configuration.AdaptiveBluetooth));
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

    private void OnExitForUpdateRequested(object? sender, EventArgs e)
    {
        this.Dispatcher.BeginInvoke(() => this.OnExitRequested(this, EventArgs.Empty));
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
