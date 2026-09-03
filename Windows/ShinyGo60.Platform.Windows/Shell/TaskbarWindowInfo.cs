using ShinyGo60.Companion.Core.Presentation;

namespace ShinyGo60.Platform.Windows.Shell;

public sealed record TaskbarWindowInfo(IntPtr Handle, TaskbarGeometry Geometry)
{
    public string MonitorId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool IsPrimary { get; init; }
}
