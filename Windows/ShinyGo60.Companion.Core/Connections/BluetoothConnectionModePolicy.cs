using ShinyGo60.Protocol.Messages;
using ShinyGo60.Companion.Core.Configuration;

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
    public bool Enabled { get; init; } = true;
    public bool UseIdleWhenLocked { get; init; } = true;

    public static BluetoothConnectionModePolicy FromSettings(AdaptiveBluetoothSettings settings)
    {
        settings.Validate();
        return new BluetoothConnectionModePolicy(TimeSpan.FromSeconds(settings.IdleAfterSeconds))
        {
            Enabled = settings.Enabled,
            UseIdleWhenLocked = settings.UseIdleWhenLocked,
        };
    }

    public BluetoothConnectionMode GetMode(bool sessionLocked, TimeSpan idleDuration)
    {
        if (idleDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleDuration), "The idle duration cannot be negative.");
        }

        return !this.Enabled || (sessionLocked && this.UseIdleWhenLocked) || idleDuration >= this.IdleThreshold
            ? BluetoothConnectionMode.PowerSaving
            : BluetoothConnectionMode.Interactive;
    }
}
