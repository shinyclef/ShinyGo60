using Microsoft.Win32;

namespace ShinyGo60.Platform.Windows.Shell;

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ShinyGo60 Companion";

    public static bool IsEnabled()
    {
        using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public static void SetEnabled(
        bool enabled,
        string executablePath,
        string manifestPath,
        string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);

        using RegistryKey runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (!enabled)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string command = string.Join(
            ' ',
            Quote(executablePath),
            "--background",
            Quote(manifestPath),
            Quote(configurationPath));
        runKey.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string Quote(string value)
    {
        if (value.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("Windows startup paths cannot contain a quotation mark.", nameof(value));
        }

        return $"\"{value}\"";
    }
}
