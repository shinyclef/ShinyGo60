using ShinyGo60.Protocol.Messages;

namespace ShinyGo60.Companion;

public sealed class BluetoothConnectionModeChangedEventArgs : EventArgs
{
    public BluetoothConnectionModeChangedEventArgs(BluetoothConnectionMode mode)
    {
        this.Mode = mode;
    }

    public BluetoothConnectionMode Mode { get; }
}
