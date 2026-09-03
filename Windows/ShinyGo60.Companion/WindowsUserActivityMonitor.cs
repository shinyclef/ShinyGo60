using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using ShinyGo60.Companion.Core.Connections;
using ShinyGo60.Protocol.Messages;

namespace ShinyGo60.Companion;

public sealed class WindowsUserActivityMonitor : IDisposable
{
    private readonly BluetoothConnectionModePolicy policy;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer pollTimer;
    private bool sessionLocked;
    private bool started;
    private bool disposed;

    public WindowsUserActivityMonitor(BluetoothConnectionModePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        this.policy = policy;
        this.dispatcher = Dispatcher.CurrentDispatcher;
        this.pollTimer = new DispatcherTimer(DispatcherPriority.Background, this.dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        this.pollTimer.Tick += this.OnPollTimerTick;
        this.CurrentMode = BluetoothConnectionMode.Interactive;
    }

    public event EventHandler<BluetoothConnectionModeChangedEventArgs>? ModeChanged;

    public BluetoothConnectionMode CurrentMode { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.started)
        {
            return;
        }

        SystemEvents.SessionSwitch += this.OnSessionSwitch;
        this.started = true;
        this.EvaluateMode();
        this.pollTimer.Start();
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.pollTimer.Stop();
        this.pollTimer.Tick -= this.OnPollTimerTick;
        if (this.started)
        {
            SystemEvents.SessionSwitch -= this.OnSessionSwitch;
        }

        this.disposed = true;
        GC.SuppressFinalize(this);
    }

    private void EvaluateMode()
    {
        BluetoothConnectionMode mode;
        if (this.sessionLocked)
        {
            mode = BluetoothConnectionMode.PowerSaving;
        }
        else if (!TryReadIdleDuration(out TimeSpan idleDuration))
        {
            return;
        }
        else
        {
            mode = this.policy.GetMode(sessionLocked: false, idleDuration);
        }

        if (mode == this.CurrentMode)
        {
            return;
        }

        this.CurrentMode = mode;
        this.ModeChanged?.Invoke(this, new BluetoothConnectionModeChangedEventArgs(mode));
    }

    private void OnPollTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        this.EvaluateMode();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _ = sender;
        bool? locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock => true,
            SessionSwitchReason.SessionUnlock => false,
            _ => null,
        };
        if (!locked.HasValue)
        {
            return;
        }

        _ = this.dispatcher.BeginInvoke(() =>
        {
            if (this.disposed)
            {
                return;
            }

            this.sessionLocked = locked.Value;
            this.EvaluateMode();
        });
    }

    private static bool TryReadIdleDuration(out TimeSpan idleDuration)
    {
        NativeLastInputInfo information = new()
        {
            Size = checked((uint)Marshal.SizeOf<NativeLastInputInfo>()),
        };
        if (!NativeMethods.GetLastInputInfo(ref information))
        {
            idleDuration = default;
            return false;
        }

        uint elapsedMilliseconds = unchecked((uint)Environment.TickCount - information.TickCount);
        idleDuration = TimeSpan.FromMilliseconds(elapsedMilliseconds);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetLastInputInfo(ref NativeLastInputInfo information);
    }
}
