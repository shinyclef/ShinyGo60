namespace ShinyGo60.Companion.Core.Reconnection;

public interface IReconnectDelayPolicy
{
    TimeSpan GetDelay(int failedAttemptCount);
}
