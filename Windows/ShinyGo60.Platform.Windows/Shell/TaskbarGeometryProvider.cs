using System.ComponentModel;
using System.Runtime.InteropServices;
using ShinyGo60.Companion.Core.Presentation;

namespace ShinyGo60.Platform.Windows.Shell;

public sealed class TaskbarGeometryProvider : ITaskbarWindowProvider
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";
    private const string DisplayDevicePrefix = @"\\.\DISPLAY";
    private const uint GetTaskbarPositionMessage = 5;
    private const uint DefaultDpi = 96;
    private const uint MonitorInformationPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;

    public TaskbarWindowInfo? GetCurrent()
    {
        IntPtr taskbar = NativeMethods.FindWindow(PrimaryTaskbarClass, null);
        return taskbar == IntPtr.Zero ? null : ReadTaskbar(taskbar, useAppBarPosition: true);
    }

    public IReadOnlyList<TaskbarWindowInfo> GetAll()
    {
        List<TaskbarWindowInfo> taskbars = [];
        IntPtr primaryTaskbar = NativeMethods.FindWindow(PrimaryTaskbarClass, null);
        if (primaryTaskbar != IntPtr.Zero)
        {
            taskbars.Add(ReadTaskbar(primaryTaskbar, useAppBarPosition: true));
        }

        IntPtr previousTaskbar = IntPtr.Zero;
        while (true)
        {
            IntPtr taskbar = NativeMethods.FindWindowEx(
                IntPtr.Zero,
                previousTaskbar,
                SecondaryTaskbarClass,
                null);
            if (taskbar == IntPtr.Zero)
            {
                break;
            }

            taskbars.Add(ReadTaskbar(taskbar, useAppBarPosition: false));
            previousTaskbar = taskbar;
        }

        return taskbars;
    }

    private static TaskbarWindowInfo ReadTaskbar(IntPtr taskbar, bool useAppBarPosition)
    {
        if (!NativeMethods.GetWindowRect(taskbar, out NativeRectangle windowRectangle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the taskbar bounds.");
        }

        if (!NativeMethods.GetClientRect(taskbar, out NativeRectangle clientRectangle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the taskbar client bounds.");
        }

        bool hasAppBarPosition = false;
        NativeAppBarData appBarData = default;
        if (useAppBarPosition)
        {
            appBarData = new NativeAppBarData
            {
                Size = (uint)Marshal.SizeOf<NativeAppBarData>(),
                Window = taskbar,
            };
            hasAppBarPosition = NativeMethods.SHAppBarMessage(GetTaskbarPositionMessage, ref appBarData) != UIntPtr.Zero;
        }

        IntPtr monitor = NativeMethods.MonitorFromWindow(taskbar, MonitorDefaultToNearest);
        NativeMonitorInformation monitorInformation = new()
        {
            Size = Marshal.SizeOf<NativeMonitorInformation>(),
            DeviceName = string.Empty,
        };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the taskbar monitor bounds.");
        }

        PixelRectangle taskbarBounds = windowRectangle.ToPixelRectangle();
        PixelRectangle monitorBounds = monitorInformation.Monitor.ToPixelRectangle();
        bool isPrimary = (monitorInformation.Flags & MonitorInformationPrimary) != 0;
        uint dpi = NativeMethods.GetDpiForWindow(taskbar);
        TaskbarGeometry geometry = new(
            clientRectangle.ToPixelRectangle(),
            hasAppBarPosition ? ConvertEdge(appBarData.Edge) : DetermineEdge(taskbarBounds, monitorBounds),
            dpi == 0 ? DefaultDpi : dpi);
        return new TaskbarWindowInfo(taskbar, geometry)
        {
            MonitorId = monitorInformation.DeviceName,
            DisplayName = CreateDisplayName(monitorInformation.DeviceName, monitorBounds, isPrimary),
            IsPrimary = isPrimary,
        };
    }

    private static string CreateDisplayName(string monitorId, PixelRectangle monitorBounds, bool isPrimary)
    {
        string displayNumber = monitorId.StartsWith(DisplayDevicePrefix, StringComparison.OrdinalIgnoreCase)
            ? monitorId[DisplayDevicePrefix.Length..]
            : monitorId;
        string displayName = string.IsNullOrWhiteSpace(displayNumber) ? "Display" : $"Display {displayNumber}";
        string primaryLabel = isPrimary ? " (primary)" : string.Empty;
        return $"{displayName}{primaryLabel} — {monitorBounds.Width} × {monitorBounds.Height}";
    }

    private static TaskbarEdge ConvertEdge(uint edge)
    {
        return edge switch
        {
            0 => TaskbarEdge.Left,
            1 => TaskbarEdge.Top,
            2 => TaskbarEdge.Right,
            3 => TaskbarEdge.Bottom,
            _ => throw new InvalidOperationException($"Windows reported unsupported taskbar edge {edge}."),
        };
    }

    private static TaskbarEdge DetermineEdge(PixelRectangle taskbar, PixelRectangle monitor)
    {
        if (taskbar.Width >= taskbar.Height)
        {
            int taskbarCenter = taskbar.Top + (taskbar.Height / 2);
            int monitorCenter = monitor.Top + (monitor.Height / 2);
            return taskbarCenter < monitorCenter ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        int horizontalTaskbarCenter = taskbar.Left + (taskbar.Width / 2);
        int horizontalMonitorCenter = monitor.Left + (monitor.Width / 2);
        return horizontalTaskbarCenter < horizontalMonitorCenter ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRectangle
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public PixelRectangle ToPixelRectangle()
        {
            return new PixelRectangle(this.Left, this.Top, this.Right, this.Bottom);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInformation
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeAppBarData
    {
        public uint Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRectangle Rectangle;
        public IntPtr Parameter;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowEx(
            IntPtr parent,
            IntPtr childAfter,
            string className,
            string? windowName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr window, out NativeRectangle rectangle);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInformation information);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("shell32.dll")]
        public static extern UIntPtr SHAppBarMessage(uint message, ref NativeAppBarData data);
    }
}
