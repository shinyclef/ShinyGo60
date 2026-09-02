using System.Globalization;

namespace ShinyGo60.TransportSpike;

internal sealed record SpikeOptions(
    string ManifestPath,
    TransportRunMode Mode,
    int ExchangeCount,
    TimeSpan Timeout,
    TimeSpan WatchDuration,
    byte ControlLayerId)
{
    private const int DefaultExchangeCount = 5;
    private const int DefaultWatchSeconds = 60;
    private const byte DefaultControlLayerId = 3;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public bool IsWatch => this.Mode is TransportRunMode.WatchUsb or TransportRunMode.WatchBluetooth;

    public bool IsControl => this.Mode is
        TransportRunMode.ControlUsb or
        TransportRunMode.ControlBluetooth or
        TransportRunMode.ControlSwitch or
        TransportRunMode.HoldUsb or
        TransportRunMode.HoldBluetooth or
        TransportRunMode.OwnershipUsb or
        TransportRunMode.OwnershipBluetooth;

    public bool IsOwnership => this.Mode is TransportRunMode.OwnershipUsb or TransportRunMode.OwnershipBluetooth;

    public bool IsPersistentSelection => this.Mode is TransportRunMode.SelectUsb or TransportRunMode.SelectBluetooth;

    public bool IsHold => this.Mode is TransportRunMode.HoldUsb or TransportRunMode.HoldBluetooth;

    public bool UsesLayerId => this.IsControl || this.IsPersistentSelection;

    public static SpikeOptions Parse(string[] args)
    {
        if (args.Length is < 1 or > 3)
        {
            throw new ArgumentException(
                "Usage: ShinyGo60.TransportSpike <layout-manifest.json> " +
                "[usb|bluetooth|both|switch|watch-usb|watch-bluetooth|control-usb|control-bluetooth|" +
                "ownership-usb|ownership-bluetooth|select-usb|select-bluetooth|control-switch|hold-usb|hold-bluetooth] " +
                "[exchange-count|watch-seconds|control-layer-id]");
        }

        string manifestPath = Path.GetFullPath(args[0]);
        TransportRunMode mode = args.Length < 2 ? TransportRunMode.Both : ParseMode(args[1]);
        bool isWatch = mode is TransportRunMode.WatchUsb or TransportRunMode.WatchBluetooth;
        bool isControl = mode is
            TransportRunMode.ControlUsb or
            TransportRunMode.ControlBluetooth or
            TransportRunMode.ControlSwitch or
            TransportRunMode.HoldUsb or
            TransportRunMode.HoldBluetooth or
            TransportRunMode.OwnershipUsb or
            TransportRunMode.OwnershipBluetooth;
        bool isPersistentSelection = mode is TransportRunMode.SelectUsb or TransportRunMode.SelectBluetooth;
        if (isPersistentSelection && args.Length < 3)
        {
            throw new ArgumentException("A select mode requires a layer ID, including 0 for Home.", nameof(args));
        }

        bool usesLayerId = isControl || isPersistentSelection;
        int finalArgument = args.Length < 3
            ? isWatch ? DefaultWatchSeconds : isControl ? DefaultControlLayerId : DefaultExchangeCount
            : int.Parse(args[2], NumberStyles.None, CultureInfo.InvariantCulture);

        if (isControl && finalArgument is < 1 or >= Protocol.Messages.ProtocolPacketCodec.NoLayer)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                $"The control layer ID must be between 1 and {Protocol.Messages.ProtocolPacketCodec.NoLayer - 1}.");
        }

        if (isPersistentSelection && finalArgument is < 0 or >= Protocol.Messages.ProtocolPacketCodec.NoLayer)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                $"The selected layer ID must be between 0 and {Protocol.Messages.ProtocolPacketCodec.NoLayer - 1}.");
        }

        if (!usesLayerId && (finalArgument is < 1 or > 3600 || (!isWatch && finalArgument > 1000)))
        {
            string range = isWatch
                ? "watch duration must be between 1 and 3600 seconds"
                : "exchange count must be between 1 and 1000";
            throw new ArgumentOutOfRangeException(nameof(args), $"The {range}.");
        }

        return new SpikeOptions(
            manifestPath,
            mode,
            isWatch || usesLayerId ? 1 : finalArgument,
            DefaultTimeout,
            TimeSpan.FromSeconds(isWatch ? finalArgument : DefaultWatchSeconds),
            usesLayerId ? checked((byte)finalArgument) : DefaultControlLayerId);
    }

    public IReadOnlyList<Protocol.Transport.TransportKind> GetTransportSequence()
    {
        return this.Mode switch
        {
            TransportRunMode.Usb => [Protocol.Transport.TransportKind.Usb],
            TransportRunMode.Bluetooth => [Protocol.Transport.TransportKind.Bluetooth],
            TransportRunMode.Both => [Protocol.Transport.TransportKind.Usb, Protocol.Transport.TransportKind.Bluetooth],
            TransportRunMode.Switch =>
            [
                Protocol.Transport.TransportKind.Usb,
                Protocol.Transport.TransportKind.Bluetooth,
                Protocol.Transport.TransportKind.Usb,
            ],
            TransportRunMode.WatchUsb => [Protocol.Transport.TransportKind.Usb],
            TransportRunMode.WatchBluetooth => [Protocol.Transport.TransportKind.Bluetooth],
            TransportRunMode.ControlUsb => [Protocol.Transport.TransportKind.Usb],
            TransportRunMode.ControlBluetooth => [Protocol.Transport.TransportKind.Bluetooth],
            TransportRunMode.OwnershipUsb => [Protocol.Transport.TransportKind.Usb],
            TransportRunMode.OwnershipBluetooth => [Protocol.Transport.TransportKind.Bluetooth],
            TransportRunMode.SelectUsb => [Protocol.Transport.TransportKind.Usb],
            TransportRunMode.SelectBluetooth => [Protocol.Transport.TransportKind.Bluetooth],
            TransportRunMode.ControlSwitch => throw new InvalidOperationException(
                "The control-switch diagnostic manages both transports as one sequence."),
            TransportRunMode.HoldUsb => [Protocol.Transport.TransportKind.Usb],
            TransportRunMode.HoldBluetooth => [Protocol.Transport.TransportKind.Bluetooth],
            _ => throw new InvalidOperationException($"Unsupported transport run mode: {this.Mode}."),
        };
    }

    private static TransportRunMode ParseMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "usb" => TransportRunMode.Usb,
            "bluetooth" or "ble" => TransportRunMode.Bluetooth,
            "both" => TransportRunMode.Both,
            "switch" => TransportRunMode.Switch,
            "watch-usb" => TransportRunMode.WatchUsb,
            "watch-bluetooth" or "watch-ble" => TransportRunMode.WatchBluetooth,
            "control-usb" => TransportRunMode.ControlUsb,
            "control-bluetooth" or "control-ble" => TransportRunMode.ControlBluetooth,
            "ownership-usb" => TransportRunMode.OwnershipUsb,
            "ownership-bluetooth" or "ownership-ble" => TransportRunMode.OwnershipBluetooth,
            "select-usb" => TransportRunMode.SelectUsb,
            "select-bluetooth" or "select-ble" => TransportRunMode.SelectBluetooth,
            "control-switch" => TransportRunMode.ControlSwitch,
            "hold-usb" => TransportRunMode.HoldUsb,
            "hold-bluetooth" or "hold-ble" => TransportRunMode.HoldBluetooth,
            _ => throw new ArgumentException(
                $"Unknown transport '{value}'. Use usb, bluetooth, both, switch, watch-usb, watch-bluetooth, " +
                "control-usb, control-bluetooth, ownership-usb, ownership-bluetooth, select-usb, select-bluetooth, " +
                "control-switch, hold-usb, or hold-bluetooth."),
        };
    }
}
