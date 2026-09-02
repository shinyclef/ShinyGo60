using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using ShinyGo60.Companion.Core.Configuration;
using ShinyGo60.Companion.Core.Connections;
using ShinyGo60.Companion.Core.Control;
using ShinyGo60.Companion.Core.Reconnection;
using ShinyGo60.Companion.Core.Shortcuts;
using ShinyGo60.Companion.Core.Telemetry;
using ShinyGo60.Diagnostics;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Protocol.Transport;

namespace ShinyGo60.Companion.Core.Sessions;

public sealed class CompanionService : ICompanionSession
{
    private const string DiagnosticComponent = "companion.service";
    private const ProtocolCapability RequiredCapabilities =
        ProtocolCapability.StateTelemetry |
        ProtocolCapability.PersistentLayer |
        ProtocolCapability.MomentaryLayer |
        ProtocolCapability.BatteryTelemetry;

    private readonly LayoutManifest manifest;
    private readonly ResolvedCompanionConfiguration configuration;
    private readonly IKeyboardTransportFactory transportFactory;
    private readonly IReconnectDelayPolicy reconnectDelayPolicy;
    private readonly IDiagnosticSink diagnosticSink;
    private readonly CompanionServiceOptions options;
    private readonly ShortcutRouter shortcutRouter;
    private readonly Channel<ServiceEvent> events;
    private readonly object lifecycleSync = new();
    private readonly object shortcutSync = new();
    private readonly object statusSync = new();
    private CompanionStatus currentStatus = CompanionStatus.Stopped;
    private Task? runTask;
    private long generation;
    private bool acceptingShortcuts;
    private bool disposed;

    public CompanionService(
        LayoutManifest manifest,
        ResolvedCompanionConfiguration configuration,
        IKeyboardTransportFactory transportFactory,
        IReconnectDelayPolicy reconnectDelayPolicy,
        IDiagnosticSink diagnosticSink,
        CompanionServiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(reconnectDelayPolicy);
        ArgumentNullException.ThrowIfNull(diagnosticSink);
        LayoutManifestJson.Validate(manifest);
        if (manifest.ProtocolVersion != ProtocolVersion.Current)
        {
            throw new InvalidDataException(
                $"Manifest protocol {manifest.ProtocolVersion} is unsupported; expected {ProtocolVersion.Current}.");
        }

        if (!Enum.IsDefined(configuration.TransportPreference))
        {
            throw new ArgumentException("The transport preference is unsupported.", nameof(configuration));
        }

        ValidateBindings(manifest, configuration.Shortcuts);
        this.manifest = manifest;
        this.configuration = configuration;
        this.transportFactory = transportFactory;
        this.reconnectDelayPolicy = reconnectDelayPolicy;
        this.diagnosticSink = diagnosticSink;
        this.options = options ?? CompanionServiceOptions.Default;
        this.options.Validate();
        this.shortcutRouter = new ShortcutRouter(configuration.Shortcuts);
        this.events = Channel.CreateUnbounded<ServiceEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    }

    public event EventHandler<CompanionStatusChangedEventArgs>? StatusChanged;

    public CompanionConnectionState State => this.Status.ConnectionState;

    public TransportKind? ActiveTransport => this.Status.ConnectionState == CompanionConnectionState.Connected
        ? this.Status.Transport
        : null;

    public CompanionStatus Status
    {
        get
        {
            lock (this.statusSync)
            {
                return this.currentStatus;
            }
        }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (this.runTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("The companion service is already running.");
            }

            this.UpdateStatus(
                CompanionConnectionState.Connecting,
                null,
                "Starting Go60 discovery",
                reconnectAttempt: 0);
            this.runTask = Task.Run(this.RunAsync, CancellationToken.None);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task? activeTask;
        lock (this.lifecycleSync)
        {
            activeTask = this.runTask;
            if (activeTask is null || activeTask.IsCompleted)
            {
                this.runTask = null;
                this.UpdateStatus(
                    CompanionConnectionState.Stopped,
                    null,
                    "Stopped",
                    reconnectAttempt: 0);
                return;
            }

            this.events.Writer.TryWrite(StopRequestedEvent.Instance);
        }

        await activeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (this.lifecycleSync)
        {
            if (ReferenceEquals(this.runTask, activeTask))
            {
                this.runTask = null;
            }
        }
    }

    public ShortcutRouteKind SubmitShortcutEvent(ShortcutKeyEvent keyEvent)
    {
        ArgumentNullException.ThrowIfNull(keyEvent);
        lock (this.shortcutSync)
        {
            ShortcutRoute route = this.shortcutRouter.Route(keyEvent);
            if (route.Kind is not ShortcutRouteKind.Pressed and not ShortcutRouteKind.Released)
            {
                return route.Kind;
            }

            if (!this.acceptingShortcuts)
            {
                if (route.Kind == ShortcutRouteKind.Pressed)
                {
                    this.shortcutRouter.ForgetActiveBindings();
                }

                return ShortcutRouteKind.Ignored;
            }

            this.events.Writer.TryWrite(new ShortcutActionEvent(this.generation, route, keyEvent.IsInjected));
            return route.Kind;
        }
    }

    public void SeedPressedShortcutKeys(IEnumerable<string> keys)
    {
        lock (this.shortcutSync)
        {
            this.shortcutRouter.SeedPressedKeys(keys);
        }
    }

    public void RequestReconnect()
    {
        lock (this.lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (this.runTask is { IsCompleted: false })
            {
                this.events.Writer.TryWrite(ReconnectRequestedEvent.Instance);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (this.lifecycleSync)
        {
            if (this.disposed)
            {
                return;
            }
        }

        await this.StopAsync().ConfigureAwait(false);
        lock (this.lifecycleSync)
        {
            this.disposed = true;
        }
    }

    private async Task RunAsync()
    {
        int failedRoundCount = 0;
        try
        {
            while (true)
            {
                LifecycleRequest pendingRequest = this.ConsumePendingLifecycleRequest();
                if (pendingRequest == LifecycleRequest.Stop)
                {
                    return;
                }

                bool restartImmediately = pendingRequest == LifecycleRequest.Reconnect;
                Exception? lastFailure = null;
                foreach (TransportKind kind in this.GetTransportOrder())
                {
                    if (restartImmediately)
                    {
                        break;
                    }

                    this.UpdateStatus(
                        CompanionConnectionState.Connecting,
                        kind,
                        $"Connecting over {kind}",
                        failedRoundCount);
                    await this.WriteDiagnosticAsync(
                        DiagnosticLevel.Information,
                        "connection_attempt",
                        $"Trying the {kind} transport.",
                        new Dictionary<string, string> { ["transport"] = kind.ToString() }).ConfigureAwait(false);

                    SessionAttemptResult result = await this.RunTransportSessionAsync(kind).ConfigureAwait(false);
                    if (result.WasConnected)
                    {
                        failedRoundCount = 0;
                    }

                    if (result.Outcome == SessionOutcome.Stop)
                    {
                        return;
                    }

                    if (result.Outcome == SessionOutcome.Reconnect)
                    {
                        restartImmediately = true;
                        break;
                    }

                    lastFailure = result.Failure;
                    await this.WriteDiagnosticAsync(
                        DiagnosticLevel.Warning,
                        "connection_failed",
                        $"The {kind} transport ended: {result.Failure!.Message}",
                        new Dictionary<string, string>
                        {
                            ["transport"] = kind.ToString(),
                            ["phase"] = result.WasConnected ? "connected" : "discovery",
                        }).ConfigureAwait(false);
                }

                if (restartImmediately)
                {
                    continue;
                }

                failedRoundCount++;
                TimeSpan delay = this.reconnectDelayPolicy.GetDelay(failedRoundCount);
                string detail = lastFailure is null
                    ? $"No Go60 transport is available; retrying in {delay.TotalSeconds:0.#} seconds"
                    : $"{lastFailure.Message} Retrying in {delay.TotalSeconds:0.#} seconds";
                this.UpdateStatus(
                    CompanionConnectionState.Disconnected,
                    null,
                    detail,
                    failedRoundCount);
                LifecycleRequest request = await this.WaitForReconnectAsync(delay).ConfigureAwait(false);
                if (request == LifecycleRequest.Stop)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            this.UpdateStatus(
                CompanionConnectionState.Disconnected,
                null,
                $"The companion stopped unexpectedly: {exception.Message}",
                failedRoundCount);
            await this.WriteDiagnosticAsync(
                DiagnosticLevel.Error,
                "service_failed",
                exception.Message).ConfigureAwait(false);
        }
        finally
        {
            this.DeactivateShortcutGeneration();
            this.UpdateStatus(
                CompanionConnectionState.Stopped,
                null,
                "Stopped",
                reconnectAttempt: 0);
        }
    }

    private async Task<SessionAttemptResult> RunTransportSessionAsync(TransportKind kind)
    {
        long sessionGeneration = this.BeginShortcutGeneration();
        IKeyboardTransport? transport = null;
        EventHandler<KeyboardPacketReceivedEventArgs>? packetHandler = null;
        EventHandler<KeyboardTransportConnectionLostEventArgs>? connectionLostHandler = null;
        CancellationTokenSource? renewalCancellation = null;
        Task? renewalTask = null;
        Task? healthCheckTask = null;
        LayerCommandStateMachine commandMachine = new(this.manifest);
        BatteryStateTracker batteryTracker = new(this.manifest);
        Dictionary<ShortcutBinding, uint> momentaryActivations = [];
        bool connected = false;

        try
        {
            transport = this.transportFactory.Create(kind);
            packetHandler = (_, args) =>
                this.events.Writer.TryWrite(new TransportPacketEvent(sessionGeneration, args.Packet));
            transport.PacketReceived += packetHandler;
            if (transport is IKeyboardTransportConnectionEvents connectionEvents)
            {
                connectionLostHandler = (_, args) =>
                    this.events.Writer.TryWrite(new TransportLostEvent(sessionGeneration, args.Cause));
                connectionEvents.ConnectionLost += connectionLostHandler;
            }

            using (CancellationTokenSource connectTimeout = new(this.options.ConnectTimeout))
            {
                await transport.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            }

            await this.OpenProtocolSessionAsync(transport, commandMachine, batteryTracker).ConfigureAwait(false);
            connected = true;
            this.ActivateShortcutGeneration(sessionGeneration);
            this.UpdateConnectedStatus(kind, commandMachine, batteryTracker, $"Connected over {kind}");
            await this.WriteDiagnosticAsync(
                DiagnosticLevel.Information,
                "connected",
                $"The companion established a validated {kind} session.",
                new Dictionary<string, string> { ["transport"] = kind.ToString() }).ConfigureAwait(false);

            renewalCancellation = new CancellationTokenSource();
            renewalTask = this.ScheduleRenewalsAsync(sessionGeneration, renewalCancellation.Token);
            if (kind == TransportKind.Bluetooth)
            {
                healthCheckTask = this.ScheduleHealthChecksAsync(sessionGeneration, renewalCancellation.Token);
            }

            long lastTransportActivity = Stopwatch.GetTimestamp();
            while (true)
            {
                ServiceEvent serviceEvent = await this.events.Reader.ReadAsync().ConfigureAwait(false);
                bool transportActivity = false;
                switch (serviceEvent)
                {
                    case StopRequestedEvent:
                        await this.TryReleaseMomentaryActivationsAsync(
                            transport,
                            commandMachine,
                            batteryTracker,
                            momentaryActivations,
                            kind,
                            "stop").ConfigureAwait(false);
                        return new SessionAttemptResult(SessionOutcome.Stop, null, connected);
                    case ReconnectRequestedEvent:
                        await this.TryReleaseMomentaryActivationsAsync(
                            transport,
                            commandMachine,
                            batteryTracker,
                            momentaryActivations,
                            kind,
                            "reconnect").ConfigureAwait(false);
                        return new SessionAttemptResult(SessionOutcome.Reconnect, null, connected);
                    case SessionServiceEvent sessionEvent when sessionEvent.Generation != sessionGeneration:
                        continue;
                    case TransportLostEvent lost:
                        throw new IOException($"The {kind} connection was lost.", lost.Cause);
                    case TransportPacketEvent packet:
                        ApplyTransportPacket(packet.Packet, commandMachine, batteryTracker);
                        this.UpdateConnectedStatus(kind, commandMachine, batteryTracker, $"Connected over {kind}");
                        transportActivity = true;
                        break;
                    case ShortcutActionEvent shortcut:
                        await this.HandleShortcutActionAsync(
                            shortcut,
                            commandMachine,
                            momentaryActivations).ConfigureAwait(false);
                        break;
                    case RenewMomentariesEvent:
                        this.QueueMomentaryRenewals(commandMachine, momentaryActivations);
                        break;
                    case BluetoothHealthCheckEvent
                        when Stopwatch.GetElapsedTime(lastTransportActivity) >= this.options.BluetoothHealthCheckInterval:
                        await this.RefreshLayerHealthAsync(transport, commandMachine).ConfigureAwait(false);
                        this.UpdateConnectedStatus(kind, commandMachine, batteryTracker, $"Connected over {kind}");
                        transportActivity = true;
                        break;
                    case BluetoothHealthCheckEvent:
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported companion event {serviceEvent.GetType().Name}.");
                }

                bool commandExchanged = await this.DrainCommandsAsync(
                    transport,
                    commandMachine,
                    batteryTracker,
                    momentaryActivations,
                    kind).ConfigureAwait(false);
                if (transportActivity || commandExchanged)
                {
                    lastTransportActivity = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (Exception exception)
        {
            return new SessionAttemptResult(SessionOutcome.Failed, exception, connected);
        }
        finally
        {
            this.DeactivateShortcutGeneration(sessionGeneration);
            renewalCancellation?.Cancel();
            if (renewalTask is not null)
            {
                try
                {
                    await renewalTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (renewalCancellation!.IsCancellationRequested)
                {
                }
            }

            if (healthCheckTask is not null)
            {
                try
                {
                    await healthCheckTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (renewalCancellation!.IsCancellationRequested)
                {
                }
            }

            renewalCancellation?.Dispose();
            commandMachine.EndSession();
            batteryTracker.EndSession();
            if (transport is not null)
            {
                if (packetHandler is not null)
                {
                    transport.PacketReceived -= packetHandler;
                }

                if (connectionLostHandler is not null && transport is IKeyboardTransportConnectionEvents connectionEvents)
                {
                    connectionEvents.ConnectionLost -= connectionLostHandler;
                }

                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task OpenProtocolSessionAsync(
        IKeyboardTransport transport,
        LayerCommandStateMachine commandMachine,
        BatteryStateTracker batteryTracker)
    {
        LayoutFingerprint expectedLayout = LayoutFingerprint.FromLayoutIdentifier(this.manifest.LayoutIdentifier);
        ushort nonce = CreateNonce();
        ProtocolMessage.HelloResult hello = await this.ExchangeForAsync<ProtocolMessage.HelloResult>(
            transport,
            new ProtocolMessage.HelloRequest(nonce, RequiredCapabilities, expectedLayout)).ConfigureAwait(false);
        if (hello.ClientNonce != nonce)
        {
            throw new InvalidDataException("The Go60 Hello response did not match the request nonce.");
        }

        if (hello.Status != HelloStatus.Success)
        {
            throw new InvalidDataException(
                $"The Go60 rejected the manifest-bound session: {hello.Status}; firmware layout {hello.Layout}.");
        }

        if (hello.Layout != expectedLayout)
        {
            throw new InvalidDataException(
                $"The Go60 layout {hello.Layout} does not match manifest {expectedLayout}.");
        }

        if (hello.SelectedCapabilities != RequiredCapabilities)
        {
            throw new InvalidDataException(
                $"The Go60 selected {hello.SelectedCapabilities}; the companion requires {RequiredCapabilities}.");
        }

        commandMachine.BeginSession(hello);
        batteryTracker.BeginSession(hello);
        uint stateRequestId = CreateRequestId();
        ProtocolMessage.StateSnapshot stateSnapshot = await this.ExchangeForAsync<ProtocolMessage.StateSnapshot>(
            transport,
            new ProtocolMessage.GetStateRequest(hello.SessionId, stateRequestId)).ConfigureAwait(false);
        if (stateSnapshot.SessionId != hello.SessionId || stateSnapshot.RequestId != stateRequestId)
        {
            throw new InvalidDataException("The initial layer snapshot did not match its session and request.");
        }

        LayerTelemetryApplyResult layerResult = commandMachine.StateTracker.Apply(stateSnapshot);
        if (layerResult != LayerTelemetryApplyResult.AppliedSnapshot)
        {
            throw new InvalidDataException($"The initial layer snapshot was rejected: {layerResult}.");
        }

        uint batteryRequestId = CreateRequestId();
        ProtocolMessage.BatterySnapshot batterySnapshot = await this.ExchangeForAsync<ProtocolMessage.BatterySnapshot>(
            transport,
            new ProtocolMessage.GetBatteryRequest(hello.SessionId, batteryRequestId)).ConfigureAwait(false);
        if (batterySnapshot.SessionId != hello.SessionId || batterySnapshot.RequestId != batteryRequestId)
        {
            throw new InvalidDataException("The initial battery snapshot did not match its session and request.");
        }

        BatteryTelemetryApplyResult batteryResult = batteryTracker.Apply(batterySnapshot);
        if (batteryResult != BatteryTelemetryApplyResult.AppliedSnapshot)
        {
            throw new InvalidDataException($"The initial battery snapshot was rejected: {batteryResult}.");
        }
    }

    private async Task<TMessage> ExchangeForAsync<TMessage>(IKeyboardTransport transport, ProtocolMessage request)
        where TMessage : ProtocolMessage
    {
        using CancellationTokenSource timeout = new(this.options.ExchangeTimeout);
        ReadOnlyMemory<byte> responseBytes = await transport
            .ExchangeAsync(ProtocolPacketCodec.Encode(request), timeout.Token)
            .ConfigureAwait(false);
        if (!ProtocolPacketCodec.TryDecode(responseBytes.Span, out ProtocolMessage? response) || response is not TMessage expected)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} response to {request.Type} was not a valid {typeof(TMessage).Name}.");
        }

        return expected;
    }

    private async Task HandleShortcutActionAsync(
        ShortcutActionEvent shortcut,
        LayerCommandStateMachine commandMachine,
        Dictionary<ShortcutBinding, uint> momentaryActivations)
    {
        ShortcutBinding binding = shortcut.Route.Binding!;
        if (shortcut.Route.Kind == ShortcutRouteKind.Pressed)
        {
            if (binding.Action == ShortcutActionKind.GoToLayer)
            {
                commandMachine.QueuePersistentLayer(binding.TargetLayerId);
            }
            else
            {
                uint activationId = commandMachine.QueueMomentaryPress(
                    binding.TargetLayerId,
                    this.options.MomentaryLeaseUnits);
                momentaryActivations.Add(binding, activationId);
            }
        }
        else if (binding.Action == ShortcutActionKind.MomentaryLayer &&
                 momentaryActivations.Remove(binding, out uint activationId))
        {
            commandMachine.QueueMomentaryRelease(activationId);
        }

        await this.WriteDiagnosticAsync(
            DiagnosticLevel.Information,
            shortcut.Route.Kind == ShortcutRouteKind.Pressed ? "shortcut_pressed" : "shortcut_released",
            $"A configured {binding.Action} shortcut changed state for layer {binding.TargetLayerName}.",
            new Dictionary<string, string>
            {
                ["action"] = binding.Action.ToString(),
                ["layerId"] = binding.TargetLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["layerName"] = binding.TargetLayerName,
                ["injected"] = shortcut.IsInjected.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }).ConfigureAwait(false);
    }

    private void QueueMomentaryRenewals(
        LayerCommandStateMachine commandMachine,
        IReadOnlyDictionary<ShortcutBinding, uint> momentaryActivations)
    {
        foreach (uint activationId in momentaryActivations.Values)
        {
            commandMachine.QueueMomentaryRenewal(activationId, this.options.MomentaryLeaseUnits);
        }
    }

    private async Task ReleaseMomentaryActivationsAsync(
        IKeyboardTransport transport,
        LayerCommandStateMachine commandMachine,
        BatteryStateTracker batteryTracker,
        Dictionary<ShortcutBinding, uint> momentaryActivations,
        TransportKind kind)
    {
        foreach (uint activationId in momentaryActivations.Values)
        {
            commandMachine.QueueMomentaryRelease(activationId);
        }

        momentaryActivations.Clear();
        await this.DrainCommandsAsync(
            transport,
            commandMachine,
            batteryTracker,
            momentaryActivations,
            kind).ConfigureAwait(false);
    }

    private async Task TryReleaseMomentaryActivationsAsync(
        IKeyboardTransport transport,
        LayerCommandStateMachine commandMachine,
        BatteryStateTracker batteryTracker,
        Dictionary<ShortcutBinding, uint> momentaryActivations,
        TransportKind kind,
        string lifecycleAction)
    {
        try
        {
            await this.ReleaseMomentaryActivationsAsync(
                transport,
                commandMachine,
                batteryTracker,
                momentaryActivations,
                kind).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            momentaryActivations.Clear();
            await this.WriteDiagnosticAsync(
                DiagnosticLevel.Warning,
                "momentary_release_failed",
                $"Could not release every momentary layer before {lifecycleAction}; firmware leases remain bounded.",
                new Dictionary<string, string>
                {
                    ["transport"] = kind.ToString(),
                    ["lifecycleAction"] = lifecycleAction,
                    ["error"] = exception.Message,
                }).ConfigureAwait(false);
        }
    }

    private async Task<bool> DrainCommandsAsync(
        IKeyboardTransport transport,
        LayerCommandStateMachine commandMachine,
        BatteryStateTracker batteryTracker,
        IDictionary<ShortcutBinding, uint> momentaryActivations,
        TransportKind kind)
    {
        bool exchanged = false;
        ProtocolMessage? command;
        while ((command = commandMachine.TryStartNextCommand()) is not null)
        {
            exchanged = true;
            ProtocolMessage response = await this.ExchangeCommandWithRetryAsync(transport, command).ConfigureAwait(false);
            LayerCommandResponseResult result = commandMachine.ApplyResponse(response);
            RemoveInactiveMomentary(command, response, momentaryActivations);
            if (result == LayerCommandResponseResult.CommandRejected)
            {
                throw new InvalidDataException($"The Go60 rejected {command.Type}; a fresh session is required.");
            }

            if (result != LayerCommandResponseResult.CommandAccepted)
            {
                throw new InvalidDataException($"The Go60 returned an unusable {command.Type} response: {result}.");
            }

            LayerTelemetryState state = commandMachine.StateTracker.CurrentState
                ?? throw new InvalidOperationException("An accepted layer command did not produce layer state.");
            await this.WriteDiagnosticAsync(
                DiagnosticLevel.Information,
                "command_completed",
                $"Applied {command.Type} over {kind}.",
                new Dictionary<string, string>
                {
                    ["transport"] = kind.ToString(),
                    ["command"] = command.Type.ToString(),
                    ["status"] = result.ToString(),
                    ["stateRevision"] = state.Revision.ToString(CultureInfo.InvariantCulture),
                    ["effectiveLayerId"] = state.EffectiveLayer.Id.ToString(CultureInfo.InvariantCulture),
                    ["momentaryCount"] = state.MomentaryLayerCount.ToString(CultureInfo.InvariantCulture),
                }).ConfigureAwait(false);
            this.UpdateConnectedStatus(kind, commandMachine, batteryTracker, $"Applied {command.Type} over {kind}");
        }

        return exchanged;
    }

    private async Task RefreshLayerHealthAsync(
        IKeyboardTransport transport,
        LayerCommandStateMachine commandMachine)
    {
        LayerTelemetryState currentState = commandMachine.StateTracker.CurrentState
            ?? throw new InvalidOperationException("A Bluetooth health check requires initialized layer state.");
        uint requestId = CreateRequestId();
        ProtocolMessage.StateSnapshot snapshot = await this.ExchangeForAsync<ProtocolMessage.StateSnapshot>(
            transport,
            new ProtocolMessage.GetStateRequest(currentState.SessionId, requestId)).ConfigureAwait(false);
        if (snapshot.SessionId != currentState.SessionId || snapshot.RequestId != requestId)
        {
            throw new InvalidDataException("The Bluetooth health-check snapshot did not match its session and request.");
        }

        LayerTelemetryApplyResult result = commandMachine.StateTracker.Apply(snapshot);
        if (result is not LayerTelemetryApplyResult.AppliedSnapshot and
            not LayerTelemetryApplyResult.AppliedAfterGap and
            not LayerTelemetryApplyResult.Duplicate)
        {
            throw new InvalidDataException($"The Bluetooth health-check snapshot was rejected: {result}.");
        }
    }

    private async Task<ProtocolMessage> ExchangeCommandWithRetryAsync(
        IKeyboardTransport transport,
        ProtocolMessage command)
    {
        byte[] encodedCommand = ProtocolPacketCodec.Encode(command);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using CancellationTokenSource timeout = new(this.options.ExchangeTimeout);
                ReadOnlyMemory<byte> responseBytes = await transport
                    .ExchangeAsync(encodedCommand, timeout.Token)
                    .ConfigureAwait(false);
                if (!ProtocolPacketCodec.TryDecode(responseBytes.Span, out ProtocolMessage? response) || response is null)
                {
                    throw new InvalidDataException($"The {transport.Kind} response to {command.Type} was malformed.");
                }

                return response;
            }
            catch (OperationCanceledException) when (attempt < this.options.TimeoutRetryCount)
            {
                await this.WriteDiagnosticAsync(
                    DiagnosticLevel.Warning,
                    "command_retry",
                    $"Retrying {command.Type} after an acknowledgement timeout.",
                    new Dictionary<string, string>
                    {
                        ["transport"] = transport.Kind.ToString(),
                        ["attempt"] = (attempt + 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }).ConfigureAwait(false);
            }
            catch (TimeoutException) when (attempt < this.options.TimeoutRetryCount)
            {
                await this.WriteDiagnosticAsync(
                    DiagnosticLevel.Warning,
                    "command_retry",
                    $"Retrying {command.Type} after an acknowledgement timeout.",
                    new Dictionary<string, string>
                    {
                        ["transport"] = transport.Kind.ToString(),
                        ["attempt"] = (attempt + 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }).ConfigureAwait(false);
            }
        }
    }

    private static void ApplyTransportPacket(
        ReadOnlyMemory<byte> packet,
        LayerCommandStateMachine commandMachine,
        BatteryStateTracker batteryTracker)
    {
        if (!ProtocolPacketCodec.TryDecode(packet.Span, out ProtocolMessage? message) || message is null)
        {
            throw new InvalidDataException("The Go60 sent a malformed telemetry packet.");
        }

        switch (message)
        {
            case ProtocolMessage.LayerChanged changed:
                LayerTelemetryApplyResult layerResult = commandMachine.StateTracker.Apply(changed);
                if (layerResult is not LayerTelemetryApplyResult.Applied and
                    not LayerTelemetryApplyResult.AppliedAfterGap and
                    not LayerTelemetryApplyResult.Duplicate and
                    not LayerTelemetryApplyResult.StaleRevision and
                    not LayerTelemetryApplyResult.WrongSession)
                {
                    throw new InvalidDataException($"The Go60 layer event was rejected: {layerResult}.");
                }

                break;
            case ProtocolMessage.BatteryChanged changed:
                BatteryTelemetryApplyResult batteryResult = batteryTracker.Apply(changed);
                if (batteryResult is not BatteryTelemetryApplyResult.Applied and
                    not BatteryTelemetryApplyResult.AppliedAfterGap and
                    not BatteryTelemetryApplyResult.Duplicate and
                    not BatteryTelemetryApplyResult.StaleRevision and
                    not BatteryTelemetryApplyResult.WrongSession)
                {
                    throw new InvalidDataException($"The Go60 battery event was rejected: {batteryResult}.");
                }

                break;
            default:
                throw new InvalidDataException($"The Go60 sent unexpected unsolicited message {message.Type}.");
        }
    }

    private static void RemoveInactiveMomentary(
        ProtocolMessage command,
        ProtocolMessage response,
        IDictionary<ShortcutBinding, uint> momentaryActivations)
    {
        uint? inactiveActivation = command switch
        {
            ProtocolMessage.PressMomentaryLayerCommand press
                when response is ProtocolMessage.ErrorMessage ||
                     response is ProtocolMessage.CommandResult { Status: CommandStatus.AlreadyReleased } => press.CommandId,
            ProtocolMessage.RenewMomentaryLayerCommand renew
                when response is ProtocolMessage.CommandResult { Status: CommandStatus.AlreadyReleased } => renew.ActivationId,
            _ => null,
        };
        if (inactiveActivation.HasValue)
        {
            ShortcutBinding? inactiveBinding = momentaryActivations
                .FirstOrDefault(pair => pair.Value == inactiveActivation.Value)
                .Key;
            if (inactiveBinding is not null)
            {
                momentaryActivations.Remove(inactiveBinding);
            }
        }
    }

    private async Task ScheduleRenewalsAsync(long sessionGeneration, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(this.options.RenewalInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            this.events.Writer.TryWrite(new RenewMomentariesEvent(sessionGeneration));
        }
    }

    private async Task ScheduleHealthChecksAsync(long sessionGeneration, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(this.options.BluetoothHealthCheckInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            this.events.Writer.TryWrite(new BluetoothHealthCheckEvent(sessionGeneration));
        }
    }

    private TransportKind[] GetTransportOrder()
    {
        return this.configuration.TransportPreference switch
        {
            TransportPreference.Automatic => [TransportKind.Usb, TransportKind.Bluetooth],
            TransportPreference.Usb => [TransportKind.Usb],
            TransportPreference.Bluetooth => [TransportKind.Bluetooth],
            _ => throw new InvalidOperationException(
                $"Unsupported transport preference {this.configuration.TransportPreference}."),
        };
    }

    private long BeginShortcutGeneration()
    {
        lock (this.shortcutSync)
        {
            this.acceptingShortcuts = false;
            this.shortcutRouter.ForgetActiveBindings();
            return ++this.generation;
        }
    }

    private void ActivateShortcutGeneration(long sessionGeneration)
    {
        lock (this.shortcutSync)
        {
            if (this.generation == sessionGeneration)
            {
                this.acceptingShortcuts = true;
            }
        }
    }

    private void DeactivateShortcutGeneration(long? sessionGeneration = null)
    {
        lock (this.shortcutSync)
        {
            if (!sessionGeneration.HasValue || this.generation == sessionGeneration.Value)
            {
                this.acceptingShortcuts = false;
                this.shortcutRouter.ForgetActiveBindings();
                this.generation++;
            }
        }
    }

    private LifecycleRequest ConsumePendingLifecycleRequest()
    {
        LifecycleRequest request = LifecycleRequest.None;
        while (this.events.Reader.TryRead(out ServiceEvent? serviceEvent))
        {
            if (serviceEvent is StopRequestedEvent)
            {
                return LifecycleRequest.Stop;
            }

            if (serviceEvent is ReconnectRequestedEvent)
            {
                request = LifecycleRequest.Reconnect;
            }
        }

        return request;
    }

    private async Task<LifecycleRequest> WaitForReconnectAsync(TimeSpan delay)
    {
        using CancellationTokenSource waitCancellation = new();
        Task delayTask = Task.Delay(delay, waitCancellation.Token);
        while (true)
        {
            Task<ServiceEvent> eventTask = this.events.Reader.ReadAsync(waitCancellation.Token).AsTask();
            Task completed = await Task.WhenAny(delayTask, eventTask).ConfigureAwait(false);
            if (completed == delayTask)
            {
                waitCancellation.Cancel();
                try
                {
                    await eventTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                return LifecycleRequest.None;
            }

            ServiceEvent serviceEvent = await eventTask.ConfigureAwait(false);
            if (serviceEvent is StopRequestedEvent)
            {
                waitCancellation.Cancel();
                return LifecycleRequest.Stop;
            }

            if (serviceEvent is ReconnectRequestedEvent)
            {
                waitCancellation.Cancel();
                return LifecycleRequest.Reconnect;
            }
        }
    }

    private void UpdateConnectedStatus(
        TransportKind kind,
        LayerCommandStateMachine commandMachine,
        BatteryStateTracker batteryTracker,
        string detail)
    {
        this.UpdateStatus(
            CompanionConnectionState.Connected,
            kind,
            detail,
            reconnectAttempt: 0,
            commandMachine.StateTracker.CurrentState,
            batteryTracker.CurrentState);
    }

    private void UpdateStatus(
        CompanionConnectionState state,
        TransportKind? transport,
        string detail,
        int reconnectAttempt,
        LayerTelemetryState? layerState = null,
        BatteryTelemetryState? batteryState = null)
    {
        CompanionStatus status;
        lock (this.statusSync)
        {
            status = new CompanionStatus(
                state,
                transport,
                layerState ?? this.currentStatus.LayerState,
                batteryState ?? this.currentStatus.BatteryState,
                detail,
                reconnectAttempt);
            this.currentStatus = status;
        }

        this.StatusChanged?.Invoke(this, new CompanionStatusChangedEventArgs(status));
    }

    private ValueTask WriteDiagnosticAsync(
        DiagnosticLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        return this.diagnosticSink.WriteAsync(
            new DiagnosticEvent(DateTimeOffset.UtcNow, level, DiagnosticComponent, eventName, message, properties));
    }

    private static ushort CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        ushort nonce;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            nonce = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        }
        while (nonce == 0);

        return nonce;
    }

    private static uint CreateRequestId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        uint requestId;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            requestId = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        while (requestId == 0);

        return requestId;
    }

    private static void ValidateBindings(LayoutManifest sourceManifest, IReadOnlyList<ShortcutBinding> bindings)
    {
        if (bindings is null || bindings.Count == 0)
        {
            throw new ArgumentException("At least one shortcut binding is required.", nameof(bindings));
        }

        foreach (ShortcutBinding binding in bindings)
        {
            if (binding.TargetLayerId >= sourceManifest.Layers.Count)
            {
                throw new ArgumentException(
                    $"Shortcut '{binding.Gesture}' targets missing layer {binding.TargetLayerId}.",
                    nameof(bindings));
            }

            LayerDefinition layer = sourceManifest.Layers[binding.TargetLayerId];
            if (!string.Equals(layer.Name, binding.TargetLayerName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Shortcut '{binding.Gesture}' layer name '{binding.TargetLayerName}' does not match manifest layer '{layer.Name}'.",
                    nameof(bindings));
            }
        }
    }

    private enum LifecycleRequest
    {
        None,
        Stop,
        Reconnect,
    }

    private enum SessionOutcome
    {
        Failed,
        Stop,
        Reconnect,
    }

    private abstract record ServiceEvent;

    private abstract record SessionServiceEvent(long Generation) : ServiceEvent;

    private sealed record ShortcutActionEvent(
        long Generation,
        ShortcutRoute Route,
        bool IsInjected) : SessionServiceEvent(Generation);

    private sealed record TransportPacketEvent(
        long Generation,
        ReadOnlyMemory<byte> Packet) : SessionServiceEvent(Generation);

    private sealed record TransportLostEvent(
        long Generation,
        Exception Cause) : SessionServiceEvent(Generation);

    private sealed record RenewMomentariesEvent(long Generation) : SessionServiceEvent(Generation);

    private sealed record BluetoothHealthCheckEvent(long Generation) : SessionServiceEvent(Generation);

    private sealed record StopRequestedEvent : ServiceEvent
    {
        public static StopRequestedEvent Instance { get; } = new();
    }

    private sealed record ReconnectRequestedEvent : ServiceEvent
    {
        public static ReconnectRequestedEvent Instance { get; } = new();
    }

    private sealed record SessionAttemptResult(
        SessionOutcome Outcome,
        Exception? Failure,
        bool WasConnected);
}
