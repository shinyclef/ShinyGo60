using ShinyGo60.Companion.Core.Connections;
using ShinyGo60.Protocol.Transport;

namespace ShinyGo60.Platform.Windows.Transports;

public sealed class WindowsKeyboardTransportFactory : IKeyboardTransportFactory
{
    public IKeyboardTransport Create(TransportKind kind)
    {
        return kind switch
        {
            TransportKind.Usb => new UsbSerialKeyboardTransport(),
            TransportKind.Bluetooth => new BluetoothGattKeyboardTransport(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The transport kind is unsupported."),
        };
    }
}
