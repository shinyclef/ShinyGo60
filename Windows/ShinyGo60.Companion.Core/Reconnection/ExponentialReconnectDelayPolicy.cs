namespace ShinyGo60.Companion.Core.Reconnection;

public sealed class ExponentialReconnectDelayPolicy : IReconnectDelayPolicy
{
    private readonly TimeSpan initialDelay;
    private readonly TimeSpan maximumDelay;

    public ExponentialReconnectDelayPolicy(TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        if (initialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay), "The initial reconnect delay must be positive.");
        }

        if (maximumDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDelay),
                "The maximum reconnect delay cannot be shorter than the initial delay.");
        }

        this.initialDelay = initialDelay;
        this.maximumDelay = maximumDelay;
    }

    public static ExponentialReconnectDelayPolicy Default { get; } = new(
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(10));

    public TimeSpan GetDelay(int failedAttemptCount)
    {
        if (failedAttemptCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failedAttemptCount), "The failed attempt count must be positive.");
        }

        int exponent = Math.Min(failedAttemptCount - 1, 30);
        double milliseconds = this.initialDelay.TotalMilliseconds * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, this.maximumDelay.TotalMilliseconds));
    }
}
