using System.Globalization;

namespace ShinyGo60.TransportSpike;

internal sealed record SpikeOptions(
    string ManifestPath,
    TransportRunMode Mode,
    int ExchangeCount,
    TimeSpan Timeout,
    TimeSpan WatchDuration)
{
    private const int DefaultExchangeCount = 5;
    private const int DefaultWatchSeconds = 60;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public bool IsWatch => this.Mode is TransportRunMode.WatchUsb or TransportRunMode.WatchBluetooth;

    public static SpikeOptions Parse(string[] args)
    {
        if (args.Length is < 1 or > 3)
        {
            throw new ArgumentException(
                "Usage: ShinyGo60.TransportSpike <layout-manifest.json> " +
                "[usb|bluetooth|both|switch|watch-usb|watch-bluetooth] [exchange-count|watch-seconds]");
        }

        string manifestPath = Path.GetFullPath(args[0]);
        TransportRunMode mode = args.Length < 2 ? TransportRunMode.Both : ParseMode(args[1]);
        bool isWatch = mode is TransportRunMode.WatchUsb or TransportRunMode.WatchBluetooth;
        int finalArgument = args.Length < 3
            ? isWatch ? DefaultWatchSeconds : DefaultExchangeCount
            : int.Parse(args[2], NumberStyles.None, CultureInfo.InvariantCulture);

        if (finalArgument is < 1 or > 3600 || (!isWatch && finalArgument > 1000))
        {
            string range = isWatch ? "watch duration must be between 1 and 3600 seconds" : "exchange count must be between 1 and 1000";
            throw new ArgumentOutOfRangeException(nameof(args), $"The {range}.");
        }

        return new SpikeOptions(
            manifestPath,
            mode,
            isWatch ? 1 : finalArgument,
            DefaultTimeout,
            TimeSpan.FromSeconds(isWatch ? finalArgument : DefaultWatchSeconds));
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
            _ => throw new ArgumentException(
                $"Unknown transport '{value}'. Use usb, bluetooth, both, switch, watch-usb, or watch-bluetooth."),
        };
    }
}
