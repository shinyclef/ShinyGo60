using System.Globalization;

namespace ShinyGo60.TransportSpike;

internal sealed record SpikeOptions(TransportRunMode Mode, int ExchangeCount, TimeSpan Timeout)
{
    private const int DefaultExchangeCount = 5;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static SpikeOptions Parse(string[] args)
    {
        if (args.Length > 2)
        {
            throw new ArgumentException("Usage: ShinyGo60.TransportSpike [usb|bluetooth|both|switch] [exchange-count]");
        }

        TransportRunMode mode = args.Length == 0 ? TransportRunMode.Both : ParseMode(args[0]);
        int exchangeCount = args.Length < 2
            ? DefaultExchangeCount
            : int.Parse(args[1], NumberStyles.None, CultureInfo.InvariantCulture);

        if (exchangeCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "The exchange count must be between 1 and 1000.");
        }

        return new SpikeOptions(mode, exchangeCount, DefaultTimeout);
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
            _ => throw new ArgumentException($"Unknown transport '{value}'. Use usb, bluetooth, both, or switch."),
        };
    }
}
