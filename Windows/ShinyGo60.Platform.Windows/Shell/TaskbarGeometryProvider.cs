using System.ComponentModel;
using System.Runtime.InteropServices;
using ShinyGo60.Companion.Core.Presentation;

namespace ShinyGo60.Platform.Windows.Shell;

public sealed class TaskbarGeometryProvider : ITaskbarWindowProvider
{
    private const uint GetTaskbarPositionMessage = 5;
    private const uint DefaultDpi = 96;
    private const uint MonitorDefaultToNearest = 2;

    public TaskbarWindowInfo? GetCurrent()
    {
        IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            return null;
        }

        if (!NativeMethods.GetWindowRect(taskbar, out NativeRectangle windowRectangle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the taskbar bounds.");
        }

        if (!NativeMethods.GetClientRect(taskbar, out NativeRectangle clientRectangle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the taskbar client bounds.");
        }

        NativeAppBarData appBarData = new()
        {
            Size = (uint)Marshal.SizeOf<NativeAppBarData>(),
            Window = taskbar,
        };
        bool hasAppBarPosition = NativeMethods.SHAppBarMessage(GetTaskbarPositionMessage, ref appBarData) != UIntPtr.Zero;

        IntPtr monitor = NativeMethods.MonitorFromWindow(taskbar, MonitorDefaultToNearest);
        NativeMonitorInformation monitorInformation = new() { Size = Marshal.SizeOf<NativeMonitorInformation>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read the taskbar monitor bounds.");
        }

        PixelRectangle taskbarBounds = windowRectangle.ToPixelRectangle();
        PixelRectangle monitorBounds = monitorInformation.Monitor.ToPixelRectangle();
        uint dpi = NativeMethods.GetDpiForWindow(taskbar);
        TaskbarGeometry geometry = new(
            clientRectangle.ToPixelRectangle(),
            hasAppBarPosition ? ConvertEdge(appBarData.Edge) : DetermineEdge(taskbarBounds, monitorBounds),
            dpi == 0 ? DefaultDpi : dpi);
        return new TaskbarWindowInfo(taskbar, geometry);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInformation
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
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
