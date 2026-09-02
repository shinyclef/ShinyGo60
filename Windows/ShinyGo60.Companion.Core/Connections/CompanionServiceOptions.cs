using ShinyGo60.Protocol.Messages;

namespace ShinyGo60.Companion.Core.Connections;

public sealed record CompanionServiceOptions(
    TimeSpan ConnectTimeout,
    TimeSpan ExchangeTimeout,
    TimeSpan RenewalInterval,
    TimeSpan BluetoothHealthCheckInterval,
    byte MomentaryLeaseUnits,
    int TimeoutRetryCount)
{
    public static CompanionServiceOptions Default { get; } = new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(30),
        ProtocolPacketCodec.MaximumLeaseUnits,
        1);

    public void Validate()
    {
        if (this.ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(this.ConnectTimeout), "The connection timeout must be positive.");
        }

        if (this.ExchangeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(this.ExchangeTimeout), "The exchange timeout must be positive.");
        }

        if (this.MomentaryLeaseUnits is 0 or > ProtocolPacketCodec.MaximumLeaseUnits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.MomentaryLeaseUnits),
                $"The lease must be between 1 and {ProtocolPacketCodec.MaximumLeaseUnits} units.");
        }

        if (this.BluetoothHealthCheckInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.BluetoothHealthCheckInterval),
                "The Bluetooth health-check interval must be positive.");
        }

        TimeSpan leaseDuration = TimeSpan.FromMilliseconds(
            this.MomentaryLeaseUnits * ProtocolPacketCodec.LeaseUnitMilliseconds);
        if (this.RenewalInterval <= TimeSpan.Zero || this.RenewalInterval >= leaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.RenewalInterval),
                "The renewal interval must be positive and shorter than the firmware lease.");
        }

        if (this.TimeoutRetryCount is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(this.TimeoutRetryCount), "The timeout retry count must be between zero and three.");
        }
    }
}
