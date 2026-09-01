using ShinyGo60.Protocol.Transport;

namespace ShinyGo60.Companion.Core.Sessions;

public interface ICompanionSession : IAsyncDisposable
{
    CompanionConnectionState State { get; }

    TransportKind? ActiveTransport { get; }

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
