using ShinyGo60.Protocol.Messages;

namespace ShinyGo60.Companion.Core.Connections;

public sealed class BluetoothConnectionModePolicy
{
    public static readonly TimeSpan DefaultIdleThreshold = TimeSpan.FromSeconds(60);

    public BluetoothConnectionModePolicy(TimeSpan idleThreshold)
    {
        if (idleThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleThreshold), "The idle threshold must be positive.");
        }

        this.IdleThreshold = idleThreshold;
    }

    public TimeSpan IdleThreshold { get; }

    public BluetoothConnectionMode GetMode(bool sessionLocked, TimeSpan idleDuration)
    {
        if (idleDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleDuration), "The idle duration cannot be negative.");
        }

        return sessionLocked || idleDuration >= this.IdleThreshold
            ? BluetoothConnectionMode.PowerSaving
            : BluetoothConnectionMode.Interactive;
    }
}
