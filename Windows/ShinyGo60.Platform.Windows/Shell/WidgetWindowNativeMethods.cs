using System.ComponentModel;
using System.Runtime.InteropServices;
using ShinyGo60.Companion.Core.Presentation;

namespace ShinyGo60.Platform.Windows.Shell;

public static class WidgetWindowNativeMethods
{
    private const int WindowStyleIndex = -16;
    private const int ExtendedWindowStyleIndex = -20;
    private const long ChildWindowStyle = 0x40000000L;
    private const long PopupWindowStyle = 0x80000000L;
    private const long ToolWindowStyle = 0x00000080L;
    private const long NoActivateStyle = 0x08000000L;
    private const uint NoActivatePositionFlag = 0x0010;
    private const uint ShowWindowPositionFlag = 0x0040;
    private static readonly IntPtr TopWindow = IntPtr.Zero;

    public static void MakeNonActivating(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            throw new ArgumentException("The widget window handle is missing.", nameof(window));
        }

        IntPtr currentStyle = NativeMethods.GetWindowLongPtr(window, ExtendedWindowStyleIndex);
        IntPtr updatedStyle = new(currentStyle.ToInt64() | ToolWindowStyle | NoActivateStyle);
        SetWindowStyle(window, ExtendedWindowStyleIndex, updatedStyle);
    }

    public static bool IsAttachedToTaskbar(IntPtr window, IntPtr taskbar)
    {
        return NativeMethods.IsWindow(window) &&
            NativeMethods.IsWindow(taskbar) &&
            NativeMethods.GetParent(window) == taskbar;
    }

    public static void AttachToTaskbar(IntPtr window, IntPtr taskbar)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
        {
            throw new ArgumentException("The widget window handle is invalid.", nameof(window));
        }

        if (taskbar == IntPtr.Zero || !NativeMethods.IsWindow(taskbar))
        {
            throw new ArgumentException("The taskbar window handle is invalid.", nameof(taskbar));
        }

        if (NativeMethods.GetParent(window) != taskbar)
        {
            Marshal.SetLastPInvokeError(0);
            IntPtr previousParent = NativeMethods.SetParent(window, taskbar);
            int error = Marshal.GetLastPInvokeError();
            if (previousParent == IntPtr.Zero && error != 0)
            {
                throw new Win32Exception(error, "Windows could not attach the widget to the taskbar.");
            }
        }

        IntPtr currentStyle = NativeMethods.GetWindowLongPtr(window, WindowStyleIndex);
        IntPtr childStyle = new((currentStyle.ToInt64() & ~PopupWindowStyle) | ChildWindowStyle);
        SetWindowStyle(window, WindowStyleIndex, childStyle);
    }

    public static void SetBounds(IntPtr window, PixelRectangle bounds)
    {
        if (window == IntPtr.Zero)
        {
            throw new ArgumentException("The widget window handle is missing.", nameof(window));
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "The widget bounds must have a positive size.");
        }

        if (!NativeMethods.SetWindowPos(
                window,
                TopWindow,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                NoActivatePositionFlag | ShowWindowPositionFlag))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not position the taskbar widget.");
        }
    }

    private static void SetWindowStyle(IntPtr window, int index, IntPtr value)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr previousValue = NativeMethods.SetWindowLongPtr(window, index, value);
        int error = Marshal.GetLastPInvokeError();
        if (previousValue == IntPtr.Zero && error != 0)
        {
            throw new Win32Exception(error, "Windows could not configure the taskbar widget.");
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int left,
            int top,
            int width,
            int height,
            uint flags);
    }
}
