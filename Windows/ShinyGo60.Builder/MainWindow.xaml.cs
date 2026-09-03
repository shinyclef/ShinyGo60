using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ShinyGo60.Builder.Core.Build;
using ShinyGo60.Builder.Core.Keymaps;
using ShinyGo60.Builder.Core.Processes;
using ShinyGo60.Builder.Core.Workspaces;

namespace ShinyGo60.Builder;

public partial class MainWindow : Window, IDisposable
{
    private static readonly Brush ReadyBackground = CreateBrush(0xD9, 0xF3, 0xE8);
    private static readonly Brush ReadyForeground = CreateBrush(0x12, 0x65, 0x43);
    private static readonly Brush WarningBackground = CreateBrush(0xFF, 0xED, 0xC7);
    private static readonly Brush WarningForeground = CreateBrush(0x8A, 0x55, 0x12);
    private static readonly Brush ErrorBackground = CreateBrush(0xFA, 0xE0, 0xE0);
    private static readonly Brush ErrorForeground = CreateBrush(0x9F, 0x24, 0x24);
    private static readonly Brush WaitingBackground = CreateBrush(0xE7, 0xEA, 0xEE);
    private static readonly Brush WaitingForeground = CreateBrush(0x45, 0x4D, 0x58);

    private readonly string installationRoot;
    private readonly string[] launchArguments;
    private readonly BuildWorkspaceLayout workspace;
    private readonly FirmwareBuildPipeline buildPipeline;
    private readonly FirmwareBuildPrerequisiteChecker prerequisiteChecker;
    private readonly ManagedBuildCacheCleaner cacheCleaner;
    private CancellationTokenSource? buildCancellation;
    private FirmwareBuildReadinessResult? readiness;
    private string? selectedKeymapPath;
    private string? failureLogPath;
    private bool closeWhenBuildStops;
    private bool disposed;
    private bool isChecking;
    private bool isCleaning;

    public MainWindow(string installationRoot, IReadOnlyList<string> launchArguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        ArgumentNullException.ThrowIfNull(launchArguments);

        this.installationRoot = Path.GetFullPath(installationRoot);
        this.launchArguments = launchArguments.ToArray();
        this.workspace = BuildWorkspaceLayout.FromRepositoryRoot(this.installationRoot);
        SystemProcessRunner processRunner = new();
        this.buildPipeline = new FirmwareBuildPipeline(processRunner);
        this.prerequisiteChecker = new FirmwareBuildPrerequisiteChecker(processRunner);
        this.cacheCleaner = new ManagedBuildCacheCleaner(processRunner);

        this.InitializeComponent();
        this.Loaded += this.OnWindowLoaded;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.Loaded -= this.OnWindowLoaded;
        this.buildCancellation?.Cancel();
        this.buildCancellation?.Dispose();
        this.buildCancellation = null;
        this.disposed = true;
        GC.SuppressFinalize(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (this.buildCancellation is not null)
        {
            MessageBoxResult choice = MessageBox.Show(
                this,
                "Cancel the active firmware build and close the builder? No successful output will be published.",
                "Cancel firmware build",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }
            else
            {
                e.Cancel = true;
                this.closeWhenBuildStops = true;
                this.RequestCancellation();
            }
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        this.Dispose();
        base.OnClosed(e);
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        this.Loaded -= this.OnWindowLoaded;

        bool shouldBuild = this.ResolveInitialKeymap();
        await this.RefreshPrerequisitesAsync();
        if (shouldBuild && this.readiness?.CanBuild == true)
        {
            await this.BuildFirmwareAsync();
        }
    }

    private bool ResolveInitialKeymap()
    {
        if (this.launchArguments.Length > 0)
        {
            if (this.launchArguments.Length != 1)
            {
                this.ShowError("Choose one keymap", "Drop or pass exactly one .keymap file at a time.");
                return false;
            }

            return this.TrySelectKeymap(this.launchArguments[0]);
        }

        IReadOnlyList<string> candidates;
        try
        {
            candidates = KeymapInputFinder.FindCandidates(this.workspace.InputDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            this.ShowError("The Input folder could not be read", exception.Message);
            return false;
        }

        if (candidates.Count == 1)
        {
            return this.TrySelectKeymap(candidates[0]);
        }

        if (candidates.Count > 1)
        {
            this.ShowWaiting(
                "Choose which keymap to build",
                $"{candidates.Count.ToString(CultureInfo.InvariantCulture)} .keymap files were found in Input; none was selected automatically.");
            return this.ChooseKeymap();
        }

        this.ShowWaiting(
            "Add or choose a keymap",
            "Put exactly one MoErgo-exported .keymap in Input, choose one, or drop one onto this window.");
        return false;
    }

    private bool ChooseKeymap()
    {
        try
        {
            Directory.CreateDirectory(this.workspace.InputDirectory);
            OpenFileDialog dialog = new()
            {
                Title = "Choose one exported Go60 keymap",
                Filter = "Go60 keymaps (*.keymap)|*.keymap",
                InitialDirectory = this.workspace.InputDirectory,
                Multiselect = false,
                CheckFileExists = true,
            };
            return dialog.ShowDialog(this) == true && this.TrySelectKeymap(dialog.FileName);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            this.ShowError("The keymap picker could not open", exception.Message);
            return false;
        }
    }

    private bool TrySelectKeymap(string path)
    {
        try
        {
            this.selectedKeymapPath = KeymapInputFinder.ValidateSelection(path);
            this.KeymapNameValue.Text = Path.GetFileName(this.selectedKeymapPath);
            this.KeymapPathValue.Text = this.selectedKeymapPath;
            this.KeymapPathValue.ToolTip = this.selectedKeymapPath;
            this.ResultPanel.Visibility = Visibility.Collapsed;
            this.failureLogPath = null;
            this.OpenFailureLogButton.Visibility = Visibility.Collapsed;
            this.ShowWaiting(
                "Keymap ready",
                "Build firmware to create one matched UF2, manifest, and readable build log.");
            this.UpdateControls();
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            this.ShowError("That file cannot be used", exception.Message);
            return false;
        }
    }

    private async ValueTask RefreshPrerequisitesAsync()
    {
        if (this.IsBusy)
        {
            return;
        }

        this.isChecking = true;
        this.PrerequisiteTitleValue.Text = "Checking Docker Desktop…";
        this.PrerequisiteDetailValue.Text = "Checking the pinned compiler image and available disk space.";
        this.UpdateControls();
        try
        {
            this.readiness = await this.prerequisiteChecker.CheckAsync(this.workspace);
            this.PrerequisiteTitleValue.Text = this.readiness.Summary;
            this.PrerequisiteDetailValue.Text = this.readiness.Detail;
            this.SetupHelpButton.Visibility = this.readiness.CanBuild ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            this.readiness = null;
            this.PrerequisiteTitleValue.Text = "The build environment check failed";
            this.PrerequisiteDetailValue.Text = exception.Message;
            this.SetupHelpButton.Visibility = Visibility.Visible;
        }
        finally
        {
            this.isChecking = false;
            this.UpdateControls();
        }
    }

    private async ValueTask BuildFirmwareAsync()
    {
        if (this.IsBusy || this.selectedKeymapPath is null || this.readiness?.CanBuild != true)
        {
            return;
        }

        CancellationTokenSource cancellation = new();
        this.buildCancellation = cancellation;
        this.failureLogPath = null;
        this.OpenFailureLogButton.Visibility = Visibility.Collapsed;
        this.ResultPanel.Visibility = Visibility.Collapsed;
        this.BuildProgressValue.Visibility = Visibility.Visible;
        this.BuildProgressValue.Value = 0;
        this.SetStatusBadge("BUILDING", WarningBackground, WarningForeground);
        this.StatusTitleValue.Text = "Starting the firmware build…";
        this.StatusDetailValue.Text = "The keyboard does not need to be connected. Docker compiler details are saved to the build log.";
        this.UpdateControls();

        Progress<FirmwareBuildProgress> progress = new(value =>
        {
            this.StatusTitleValue.Text = value.Message;
            this.StatusDetailValue.Text = GetProgressDetail(value.Stage);
            this.BuildProgressValue.Value = value.Percent;
        });

        try
        {
            FirmwareBuildRequest request = PinnedFirmwareBuild.CreateRequest(
                this.installationRoot,
                this.selectedKeymapPath,
                this.workspace.GeneratedDirectory,
                this.workspace.OutputDirectory);
            FirmwareBuildResult result = await this.buildPipeline.BuildAsync(
                request,
                progress,
                cancellation.Token);
            this.ShowSuccess(result);
            this.TryOpenFolder(result.OutputSetDirectory);
        }
        catch (OperationCanceledException)
        {
            this.ShowWaiting(
                "Build canceled",
                "No successful UF2 was published. A cancellation log may be available under Output\\Failures.");
        }
        catch (InvalidDataException exception)
        {
            this.ShowError("The keymap or firmware was not valid", exception.Message);
        }
        catch (FirmwareBuildException exception)
        {
            this.failureLogPath = exception.FailureLogPath;
            this.OpenFailureLogButton.Visibility = this.failureLogPath is null ? Visibility.Collapsed : Visibility.Visible;
            this.ShowError("The firmware build failed", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            this.ShowError("The build could not access a required file", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(this.buildCancellation, cancellation))
            {
                this.buildCancellation = null;
            }

            cancellation.Dispose();
            this.BuildProgressValue.Visibility = Visibility.Collapsed;
            this.UpdateControls();
            if (this.closeWhenBuildStops)
            {
                _ = this.Dispatcher.BeginInvoke(this.Close);
            }
        }
    }

    private static string GetProgressDetail(FirmwareBuildStage stage)
    {
        return stage switch
        {
            FirmwareBuildStage.ValidatingKeymap => "Checking that this is a complete Go60 Layout Editor export.",
            FirmwareBuildStage.PreparingWorkspace => "Copying the exact keymap without rewriting its bindings or behaviors.",
            FirmwareBuildStage.CheckingBuildEnvironment => "Verifying the pinned v25.11 image before it runs.",
            FirmwareBuildStage.CompilingFirmware => "This normally takes about 15–30 seconds with the installed image.",
            FirmwareBuildStage.ValidatingFirmware => "Checking both firmware segments and the embedded layout identity.",
            FirmwareBuildStage.PublishingOutput => "The three output files become visible together only after every check passes.",
            FirmwareBuildStage.Completed => "The finished UF2 is ready to be flashed manually to both halves.",
            _ => string.Empty,
        };
    }

    private void ShowSuccess(FirmwareBuildResult result)
    {
        this.SetStatusBadge("SUCCESS", ReadyBackground, ReadyForeground);
        this.StatusTitleValue.Text = "Firmware is ready";
        this.StatusDetailValue.Text = $"The matched output set was saved in {result.OutputSetDirectory}.";
        this.ResultUf2Value.Text = Path.GetFileName(result.Uf2Path);
        this.ResultUf2Value.ToolTip = result.Uf2Path;
        this.ResultIdentityValue.Text = $"Layout ID: {result.LayoutIdentifier}";
        this.ResultDurationValue.Text =
            $"{FirmwareBuildPrerequisiteChecker.FormatBytes(result.Uf2Size)} · " +
            $"built in {result.Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} seconds";
        this.ResultPanel.Visibility = Visibility.Visible;
    }

    private void ShowWaiting(string title, string detail)
    {
        this.SetStatusBadge("WAITING", WaitingBackground, WaitingForeground);
        this.StatusTitleValue.Text = title;
        this.StatusDetailValue.Text = detail;
    }

    private void ShowError(string title, string detail)
    {
        this.SetStatusBadge("ACTION NEEDED", ErrorBackground, ErrorForeground);
        this.StatusTitleValue.Text = title;
        this.StatusDetailValue.Text = detail;
    }

    private void SetStatusBadge(string text, Brush background, Brush foreground)
    {
        this.StatusBadgeValue.Text = text;
        this.StatusBadgeValue.Foreground = foreground;
        this.StatusBadge.Background = background;
    }

    private void UpdateControls()
    {
        bool busy = this.IsBusy;
        this.BuildButton.IsEnabled = !busy && this.selectedKeymapPath is not null && this.readiness?.CanBuild == true;
        this.CancelButton.IsEnabled = this.buildCancellation is not null && !this.buildCancellation.IsCancellationRequested;
        this.CheckAgainButton.IsEnabled = !busy;
        this.CleanupButton.IsEnabled = !busy;
        this.OpenOutputButton.IsEnabled = !busy;
    }

    private bool IsBusy => this.buildCancellation is not null || this.isChecking || this.isCleaning;

    private void RequestCancellation()
    {
        if (this.buildCancellation is null || this.buildCancellation.IsCancellationRequested)
        {
            return;
        }

        this.StatusTitleValue.Text = "Stopping the firmware build…";
        this.StatusDetailValue.Text = "The temporary container and workspace are being removed.";
        this.buildCancellation.Cancel();
        this.UpdateControls();
    }

    private async void OnBuildClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await this.BuildFirmwareAsync();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        this.RequestCancellation();
    }

    private void OnChooseKeymapClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!this.IsBusy)
        {
            this.ChooseKeymap();
        }
    }

    private async void OnCheckAgainClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await this.RefreshPrerequisitesAsync();
    }

    private void OnOpenInputClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        this.TryOpenFolder(this.workspace.InputDirectory);
    }

    private void OnOpenOutputClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        this.TryOpenFolder(this.workspace.OutputDirectory);
    }

    private void OnOpenSetupHelpClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        string helpPath = Path.Combine(
            this.installationRoot,
            "Custom Firmware",
            "BuildSupport",
            "Docker-v25.11",
            "README.md");
        this.TryOpenFile(helpPath);
    }

    private void OnOpenFailureLogClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (this.failureLogPath is not null)
        {
            this.TryOpenFile(this.failureLogPath);
        }
    }

    private async void OnCleanupClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        MessageBoxResult choice = MessageBox.Show(
            this,
            "Remove only abandoned ShinyGo60 workspaces and its isolated image-construction cache? " +
                "The installed 4.46 GB firmware image and every successful Output folder will be kept.",
            "Clean ShinyGo60 cache",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (choice != MessageBoxResult.OK)
        {
            return;
        }

        this.isCleaning = true;
        this.ShowWaiting("Cleaning ShinyGo60 cache…", "No other Docker images, containers, volumes, or caches will be touched.");
        this.UpdateControls();
        try
        {
            ManagedBuildCacheCleanupResult result = await this.cacheCleaner.CleanAsync(this.workspace);
            this.ShowWaiting(
                "Scoped cache cleanup complete",
                $"Removed {result.RemovedWorkspaceCount} abandoned workspace(s), " +
                    $"{result.RemovedOutputStageCount} incomplete output stage(s), and " +
                    $"{(result.RemovedConstructionCache ? "the isolated construction cache" : "no construction cache")}. " +
                    "The installed firmware image and successful outputs were kept.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            this.ShowError("Cache cleanup could not finish", exception.Message);
        }
        finally
        {
            this.isCleaning = false;
            this.UpdateControls();
        }
    }

    private void OnWindowPreviewDragOver(object sender, DragEventArgs e)
    {
        _ = sender;
        e.Effects = !this.IsBusy && HasOneDroppedFile(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        if (this.IsBusy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1)
        {
            this.ShowError("Drop one keymap", "Drop exactly one .keymap file at a time.");
            return;
        }

        if (this.TrySelectKeymap(paths[0]) && this.readiness?.CanBuild == true)
        {
            await this.BuildFirmwareAsync();
        }
    }

    private static bool HasOneDroppedFile(IDataObject data)
    {
        return data.GetDataPresent(DataFormats.FileDrop) &&
            data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Length == 1 &&
            string.Equals(Path.GetExtension(paths[0]), ".keymap", StringComparison.OrdinalIgnoreCase);
    }

    private void TryOpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            OpenPath(path);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            this.ShowError("Windows could not open the folder", exception.Message);
        }
    }

    private void TryOpenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The requested file could not be found.", path);
            }

            OpenPath(path);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            this.ShowError("Windows could not open the file", exception.Message);
        }
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }
}
