namespace ShinyGo60.Protocol.Transport;

public interface IKeyboardConnectionHistory
{
    // An interrupted read may return complete records received so far, allowing collection to save its progress.
    ValueTask<ReadOnlyMemory<byte>> ReadConnectionHistoryAsync(
        ConnectionHistoryPosition after, CancellationToken cancellationToken = default);
}
