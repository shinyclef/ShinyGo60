using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ShinyGo60.Companion.Core.Shortcuts;

namespace ShinyGo60.Platform.Windows.Input;

public sealed class GlobalKeyboardShortcutSource : IDisposable
{
    private const int LowLevelKeyboardHook = 13;
    private const int KeyDownMessage = 0x0100;
    private const int KeyUpMessage = 0x0101;
    private const int SystemKeyDownMessage = 0x0104;
    private const int SystemKeyUpMessage = 0x0105;
    private const uint InjectedFlag = 0x10;
    private const int KeyPressedBit = 0x8000;
    private const int ControlKey = 0x11;
    private const int ShiftKey = 0x10;
    private const int AltKey = 0x12;
    private const int LeftWindowsKey = 0x5B;
    private const int RightWindowsKey = 0x5C;

    private readonly Dictionary<int, string> configuredKeys;
    private readonly HookProcedure hookProcedure;
    private SafeWindowsHookHandle? hook;

    public GlobalKeyboardShortcutSource(IEnumerable<ShortcutGesture> configuredGestures)
    {
        ArgumentNullException.ThrowIfNull(configuredGestures);
        this.configuredKeys = [];
        foreach (ShortcutGesture gesture in configuredGestures)
        {
            ArgumentNullException.ThrowIfNull(gesture);
            int virtualKey = VirtualKeyFromName(gesture.Key);
            this.configuredKeys.TryAdd(virtualKey, gesture.Key);
        }

        if (this.configuredKeys.Count == 0)
        {
            throw new ArgumentException("At least one configured shortcut is required.", nameof(configuredGestures));
        }

        this.hookProcedure = this.ProcessHook;
    }

    public event EventHandler<ShortcutKeyChangedEventArgs>? KeyChanged;

    public event EventHandler<GlobalShortcutErrorEventArgs>? Faulted;

    public bool IsRunning => this.hook is { IsInvalid: false, IsClosed: false };

    public void Start()
    {
        if (this.IsRunning)
        {
            throw new InvalidOperationException("Global shortcut capture is already running.");
        }

        IntPtr module = NativeMethods.GetModuleHandle(null);
        SafeWindowsHookHandle installedHook = NativeMethods.SetWindowsHookEx(
            LowLevelKeyboardHook,
            this.hookProcedure,
            module,
            0);
        if (installedHook.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            installedHook.Dispose();
            throw new Win32Exception(error, "Windows could not install the global keyboard shortcut hook.");
        }

        this.hook = installedHook;
    }

    public IReadOnlyList<string> GetCurrentlyPressedKeys()
    {
        return this.configuredKeys
            .Where(pair => IsKeyPressed(pair.Key))
            .Select(pair => pair.Value)
            .ToArray();
    }

    public void Stop()
    {
        this.hook?.Dispose();
        this.hook = null;
    }

    public void Dispose()
    {
        this.Stop();
    }

    private static bool IsKeyPressed(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & KeyPressedBit) != 0;
    }

    private static int VirtualKeyFromName(string key)
    {
        if (key.Length == 1)
        {
            char character = key[0];
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return character;
            }
        }

        if (key[0] == 'F' && int.TryParse(key.AsSpan(1), out int functionKey) && functionKey is >= 1 and <= 24)
        {
            return 0x70 + functionKey - 1;
        }

        return key switch
        {
            "Backspace" => 0x08,
            "Tab" => 0x09,
            "Enter" => 0x0D,
            "Escape" => 0x1B,
            "Space" => 0x20,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "End" => 0x23,
            "Home" => 0x24,
            "Insert" => 0x2D,
            "Delete" => 0x2E,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "The shortcut key cannot be mapped to a Windows virtual key."),
        };
    }

    private IntPtr ProcessHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            try
            {
                int messageId = unchecked((int)(long)message);
                ShortcutKeyState? state = messageId switch
                {
                    KeyDownMessage or SystemKeyDownMessage => ShortcutKeyState.Down,
                    KeyUpMessage or SystemKeyUpMessage => ShortcutKeyState.Up,
                    _ => null,
                };
                if (state.HasValue)
                {
                    LowLevelKeyboardInput input = Marshal.PtrToStructure<LowLevelKeyboardInput>(data);
                    if (this.configuredKeys.TryGetValue(checked((int)input.VirtualKey), out string? key))
                    {
                        ShortcutKeyEvent keyEvent = new(
                            key,
                            ReadModifiers(),
                            state.Value,
                            (input.Flags & InjectedFlag) != 0);
                        this.KeyChanged?.Invoke(this, new ShortcutKeyChangedEventArgs(keyEvent));
                    }
                }
            }
            catch (Exception exception)
            {
                try
                {
                    this.Faulted?.Invoke(this, new GlobalShortcutErrorEventArgs(exception));
                }
                catch
                {
                    // Exceptions cannot cross the unmanaged Windows hook callback boundary.
                }
            }
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private static ShortcutModifiers ReadModifiers()
    {
        ShortcutModifiers modifiers = ShortcutModifiers.None;
        if (IsKeyPressed(ControlKey))
        {
            modifiers |= ShortcutModifiers.Control;
        }

        if (IsKeyPressed(AltKey))
        {
            modifiers |= ShortcutModifiers.Alt;
        }

        if (IsKeyPressed(ShiftKey))
        {
            modifiers |= ShortcutModifiers.Shift;
        }

        if (IsKeyPressed(LeftWindowsKey) || IsKeyPressed(RightWindowsKey))
        {
            modifiers |= ShortcutModifiers.Windows;
        }

        return modifiers;
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelKeyboardInput
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInformation;
    }

    private sealed class SafeWindowsHookHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeWindowsHookHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.UnhookWindowsHookEx(this.handle);
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern SafeWindowsHookHandle SetWindowsHookEx(
            int hookId,
            HookProcedure hookProcedure,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
