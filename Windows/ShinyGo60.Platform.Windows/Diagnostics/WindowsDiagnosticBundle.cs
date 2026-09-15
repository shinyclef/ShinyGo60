using System.Runtime.InteropServices;
using ShinyGo60.Companion.Core.Diagnostics;
using ShinyGo60.Protocol.Manifests;

namespace ShinyGo60.Platform.Windows.Diagnostics;

public static class WindowsDiagnosticBundle
{
    public static async Task SaveAsync(string logDirectory, string activeLogPath, string destination,
        string companionVersion, LayoutManifest manifest, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BluetoothAdapterDiagnostic> adapters = [];
        string adapterStatus;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            adapters = await BluetoothAdapterDiagnostics.ReadAsync(timeout.Token).ConfigureAwait(false);
            adapterStatus = adapters.Count == 0 ? "No adapters reported by Windows" : "Collected";
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            adapterStatus = $"Unavailable: {exception.GetType().Name} (0x{exception.HResult:X8})";
        }

        DiagnosticBundleEnvironment environment = new(companionVersion, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
            manifest.ProtocolVersion.ToString(), manifest.LayoutIdentifier, adapterStatus, adapters);
        await DiagnosticBundleExporter.ExportAsync(logDirectory, activeLogPath, destination, environment, cancellationToken).ConfigureAwait(false);
    }
}
