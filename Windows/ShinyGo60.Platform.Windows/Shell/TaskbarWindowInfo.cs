using ShinyGo60.Companion.Core.Presentation;

namespace ShinyGo60.Platform.Windows.Shell;

public sealed record TaskbarWindowInfo(IntPtr Handle, TaskbarGeometry Geometry);
