using System.Collections.Concurrent;
using System.Diagnostics;
using ShinyGo60.Companion.Core.Configuration;
using ShinyGo60.Companion.Core.Connections;
using ShinyGo60.Companion.Core.Reconnection;
using ShinyGo60.Companion.Core.Sessions;
using ShinyGo60.Companion.Core.Shortcuts;
using ShinyGo60.Diagnostics;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Protocol.Transport;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Companion;

internal static class CompanionServiceTests
{
    public static async ValueTask RunAsync()
    {
        await VerifySyntheticMomentaryLifecycleAsync();
        await VerifySyntheticPersistentLifecycleAsync();
        await VerifyReconnectRequiresFreshPressAsync();
        await VerifyAutomaticTransportFallbackAsync();
        await VerifyAutomaticTransportSwitchAsync();
        await VerifySilentBluetoothLossAsync();
        await VerifyStopSurvivesSilentTransportLossAsync();
        await VerifyStartupBeforeKeyboardAsync();
        await VerifyAdaptiveBluetoothModesAsync();
        VerifyBluetoothConnectionModePolicy();
        VerifyReconnectBackoff();
    }

    private static async ValueTask VerifySyntheticMomentaryLifecycleAsync()
    {
        LayoutManifest manifest = CreateManifest();
        FakeTransportFactory factory = new(manifest, static (kind, _) => new FakeProtocolKeyboardTransport(kind));
        await using CompanionService service = CreateService(manifest, TransportPreference.Usb, factory);

        await service.StartAsync();
        await WaitUntilAsync(
            () => service.State == CompanionConnectionState.Connected,
            "The fake USB companion session did not connect.");

        AssertEx.Equal(
            ShortcutRouteKind.Pressed,
            service.SubmitShortcutEvent(F23(ShortcutKeyState.Down, isInjected: true)));
        AssertEx.Equal(
            ShortcutRouteKind.RepeatSuppressed,
            service.SubmitShortcutEvent(F23(ShortcutKeyState.Down, isInjected: true)));

        FakeProtocolKeyboardTransport transport = factory.Created[0];
        transport.RaisePacket(
            new ProtocolMessage.BatteryChanged(
                uint.MaxValue,
                new ProtocolMessage.BatteryState(
                    1,
                    50,
                    40,
                    BatteryStateIndicators.LeftAvailable | BatteryStateIndicators.RightAvailable)));
        await Task.Delay(TimeSpan.FromMilliseconds(40));
        AssertEx.Equal(1, factory.Created.Count);
        AssertEx.Equal(CompanionConnectionState.Connected, service.State);
        await WaitUntilAsync(
            () => transport.Count<ProtocolMessage.PressMomentaryLayerCommand>() == 1,
            "The synthetic F23 press did not reach the fake keyboard.");
        await WaitUntilAsync(
            () => transport.Count<ProtocolMessage.RenewMomentaryLayerCommand>() >= 1,
            "The held synthetic F23 key did not renew its lease.");

        AssertEx.Equal(
            ShortcutRouteKind.Released,
            service.SubmitShortcutEvent(F23(ShortcutKeyState.Up, isInjected: true)));
        await WaitUntilAsync(
            () => transport.Count<ProtocolMessage.ReleaseMomentaryLayerCommand>() == 1,
            "The synthetic F23 release did not reach the fake keyboard.");
        await WaitUntilAsync(
            () => service.Status.LayerState?.MomentaryLayerCount == 0,
            "The companion did not converge after the synthetic F23 release.");
        AssertEx.Equal(1, transport.Count<ProtocolMessage.PressMomentaryLayerCommand>());
        AssertEx.Equal("Home", service.Status.LayerState!.EffectiveLayer.Name);
        await service.StopAsync();
    }

    private static async ValueTask VerifySyntheticPersistentLifecycleAsync()
    {
        LayoutManifest manifest = CreateManifest();
        FakeTransportFactory factory = new(manifest, static (kind, _) => new FakeProtocolKeyboardTransport(kind));
        ShortcutBinding binding = new(
            ShortcutGesture.Parse("F22"),
            ShortcutActionKind.GoToLayer,
            1,
            "Navigation");
        await using CompanionService service = CreateService(
            manifest,
            TransportPreference.Usb,
            factory,
            [binding]);

        await service.StartAsync();
        await WaitUntilAsync(
            () => service.State == CompanionConnectionState.Connected,
            "The persistent-shortcut session did not connect.");

        ShortcutKeyEvent down = new("F22", ShortcutModifiers.None, ShortcutKeyState.Down, IsInjected: true);
        ShortcutKeyEvent up = new("F22", ShortcutModifiers.None, ShortcutKeyState.Up, IsInjected: true);
        AssertEx.Equal(ShortcutRouteKind.Pressed, service.SubmitShortcutEvent(down));
        AssertEx.Equal(ShortcutRouteKind.RepeatSuppressed, service.SubmitShortcutEvent(down));
        AssertEx.Equal(ShortcutRouteKind.Released, service.SubmitShortcutEvent(up));

        FakeProtocolKeyboardTransport transport = factory.Created[0];
        await WaitUntilAsync(
            () => transport.Count<ProtocolMessage.SetPersistentLayerCommand>() == 1,
            "The synthetic F22 press did not set the persistent layer.");
        await WaitUntilAsync(
            () => service.Status.LayerState?.PersistentLayer?.Name == "Navigation",
            "The persistent shortcut did not converge on Navigation.");
        AssertEx.Equal(0, transport.Count<ProtocolMessage.ReleaseMomentaryLayerCommand>());
        AssertEx.Equal("Navigation", service.Status.LayerState!.EffectiveLayer.Name);
        await service.StopAsync();
    }

    private static async ValueTask VerifyReconnectRequiresFreshPressAsync()
    {
        LayoutManifest manifest = CreateManifest();
        FakeTransportFactory factory = new(manifest, static (kind, _) => new FakeProtocolKeyboardTransport(kind));
        await using CompanionService service = CreateService(manifest, TransportPreference.Usb, factory);

        await service.StartAsync();
        await WaitUntilAsync(() => factory.Created.Count == 1 && service.State == CompanionConnectionState.Connected, "Initial session failed.");
        AssertEx.Equal(ShortcutRouteKind.Pressed, service.SubmitShortcutEvent(F23(ShortcutKeyState.Down)));
        await WaitUntilAsync(
            () => factory.Created[0].Count<ProtocolMessage.PressMomentaryLayerCommand>() == 1,
            "The initial F23 press was not sent.");

        factory.Created[0].RaiseConnectionLost();
        await WaitUntilAsync(
            () => factory.Created.Count >= 2 && service.State == CompanionConnectionState.Connected,
            "The companion did not reconnect after transport loss.");
        AssertEx.Equal(ShortcutRouteKind.RepeatSuppressed, service.SubmitShortcutEvent(F23(ShortcutKeyState.Down)));
        AssertEx.Equal(ShortcutRouteKind.Ignored, service.SubmitShortcutEvent(F23(ShortcutKeyState.Up)));
        AssertEx.Equal(ShortcutRouteKind.Pressed, service.SubmitShortcutEvent(F23(ShortcutKeyState.Down)));
        await WaitUntilAsync(
            () => factory.Created[1].Count<ProtocolMessage.PressMomentaryLayerCommand>() == 1,
            "A fresh F23 press did not activate after reconnect.");
        AssertEx.Equal(ShortcutRouteKind.Released, service.SubmitShortcutEvent(F23(ShortcutKeyState.Up)));
        await service.StopAsync();
    }

    private static async ValueTask VerifyAutomaticTransportFallbackAsync()
    {
        LayoutManifest manifest = CreateManifest();
        FakeTransportFactory factory = new(
            manifest,
            static (kind, _) => new FakeProtocolKeyboardTransport(kind, failConnection: kind == TransportKind.Usb));
        await using CompanionService service = CreateService(manifest, TransportPreference.Automatic, factory);

        await service.StartAsync();
        await WaitUntilAsync(
            () => service.State == CompanionConnectionState.Connected,
            "The automatic companion did not fall back to Bluetooth.");
        AssertEx.Equal(TransportKind.Bluetooth, service.ActiveTransport);
        AssertEx.Equal(TransportKind.Usb, factory.Created[0].Kind);
        AssertEx.Equal(TransportKind.Bluetooth, factory.Created[1].Kind);
        AssertEx.True(
            factory.Created.Count(item => item.IsConnected) == 1,
            "Only one transport may own the command session.");
        await service.StopAsync();
    }

    private static async ValueTask VerifyAutomaticTransportSwitchAsync()
    {
        LayoutManifest manifest = CreateManifest();
        FakeTransportFactory factory = new(manifest, static (kind, _) => new FakeProtocolKeyboardTransport(kind));
        await using CompanionService service = CreateService(manifest, TransportPreference.Automatic, factory);

        await service.StartAsync();
        await WaitUntilAsync(
            () => service.ActiveTransport == TransportKind.Usb,
            "The automatic companion did not begin on USB.");
        factory.Created[0].RaiseConnectionLost();
        await WaitUntilAsync(
            () => factory.Created.Count >= 2 && service.ActiveTransport == TransportKind.Bluetooth,
            "The automatic companion did not switch from failed USB to Bluetooth.");
        AssertEx.True(
            factory.Created.Count(item => item.IsConnected) == 1,
            "The USB-to-Bluetooth switch left more than one command-owning transport.");

        service.RequestReconnect();
        await WaitUntilAsync(
            () => factory.Created.Count >= 3 && service.ActiveTransport == TransportKind.Usb,
            "A re-scan did not return the automatic companion to USB.");
        AssertEx.True(
            factory.Created.Count(item => item.IsConnected) == 1,
            "The Bluetooth-to-USB switch left more than one command-owning transport.");
        await service.StopAsync();
    }

    private static async ValueTask VerifySilentBluetoothLossAsync()
    {
        LayoutManifest manifest = CreateManifest();
        FakeTransportFactory factory = new(manifest, static (kind, _) => new FakeProtocolKeyboardTransport(kind));
        await using CompanionService service = CreateService(manifest, TransportPreference.Bluetooth, factory);

        await service.StartAsync();
        await WaitUntilAsync(
            () => factory.Created.Count == 1 && service.ActiveTransport == TransportKind.Bluetooth,
            "The Bluetooth health-check test did not connect.");
        factory.Created[0].DropSilently();
        await WaitUntilAsync(
            () => factory.Created.Count >= 2 && service.ActiveTransport == TransportKind.Bluetooth,
            "The bounded health check did not recover a silent Bluetooth loss.");
        await service.StopAsync();
    }

    private static async ValueTask VerifyStartupBeforeKeyboardAsync()
    {
        LayoutManifest manifest = CreateManifest();
        FakeTransportFactory factory = new(
            manifest,
            static (kind, index) => new FakeProtocolKeyboardTransport(kind, failConnection: index < 2));
        await using CompanionService service = CreateService(manifest, TransportPreference.Usb, factory);

        await service.StartAsync();
        await WaitUntilAsync(
            () => factory.Created.Count >= 3 && service.State == CompanionConnectionState.Connected,
            "The companion did not recover when the keyboard appeared after startup.");
        AssertEx.Equal(TransportKind.Usb, service.ActiveTransport);
        AssertEx.True(factory.Created[2].IsConnected, "The third connection attempt should own the live session.");
        await service.StopAsync();
    }

    private static async ValueTask VerifyStopSurvivesSilentTransportLossAsync()
    {
        LayoutManifest manifest = CreateManifest();
        FakeTransportFactory factory = new(manifest, static (kind, _) => new FakeProtocolKeyboardTransport(kind));
        await using CompanionService service = CreateService(manifest, TransportPreference.Usb, factory);

        await service.StartAsync();
        await WaitUntilAsync(
            () => service.State == CompanionConnectionState.Connected,
            "The graceful-stop session did not connect.");
        AssertEx.Equal(ShortcutRouteKind.Pressed, service.SubmitShortcutEvent(F23(ShortcutKeyState.Down)));
        await WaitUntilAsync(
            () => factory.Created[0].Count<ProtocolMessage.PressMomentaryLayerCommand>() == 1,
            "The graceful-stop test did not activate its momentary layer.");

        factory.Created[0].DropSilently();
        await service.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        AssertEx.Equal(CompanionConnectionState.Stopped, service.State);
        AssertEx.Equal(1, factory.Created.Count);
    }

    private static void VerifyReconnectBackoff()
    {
        ExponentialReconnectDelayPolicy policy = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
        AssertEx.Equal(TimeSpan.FromMilliseconds(100), policy.GetDelay(1));
        AssertEx.Equal(TimeSpan.FromMilliseconds(200), policy.GetDelay(2));
        AssertEx.Equal(TimeSpan.FromMilliseconds(800), policy.GetDelay(4));
        AssertEx.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(8));
    }

    private static async ValueTask VerifyAdaptiveBluetoothModesAsync()
    {
        LayoutManifest manifest = CreateManifest();
        FakeTransportFactory bluetoothFactory = new(
            manifest,
            static (kind, _) => new FakeProtocolKeyboardTransport(kind));
        await using CompanionService bluetoothService = CreateService(
            manifest,
            TransportPreference.Bluetooth,
            bluetoothFactory);

        await bluetoothService.StartAsync();
        await WaitUntilAsync(
            () => bluetoothService.State == CompanionConnectionState.Connected,
            "The adaptive Bluetooth test did not connect.");
        FakeProtocolKeyboardTransport bluetoothTransport = bluetoothFactory.Created[0];
        AssertEx.Equal(1, bluetoothTransport.Count<ProtocolMessage.SetBluetoothConnectionModeCommand>());
        AssertEx.Equal(
            BluetoothConnectionMode.Interactive,
            bluetoothTransport.Last<ProtocolMessage.SetBluetoothConnectionModeCommand>().Mode);

        bluetoothService.SetBluetoothConnectionMode(BluetoothConnectionMode.PowerSaving);
        await WaitUntilAsync(
            () => bluetoothTransport.Count<ProtocolMessage.SetBluetoothConnectionModeCommand>() == 2,
            "The companion did not request Bluetooth power-saving mode.");
        AssertEx.Equal(
            BluetoothConnectionMode.PowerSaving,
            bluetoothTransport.Last<ProtocolMessage.SetBluetoothConnectionModeCommand>().Mode);

        bluetoothService.SetBluetoothConnectionMode(BluetoothConnectionMode.PowerSaving);
        await Task.Delay(TimeSpan.FromMilliseconds(40));
        AssertEx.Equal(2, bluetoothTransport.Count<ProtocolMessage.SetBluetoothConnectionModeCommand>());

        bluetoothService.SetBluetoothConnectionMode(BluetoothConnectionMode.Interactive);
        await WaitUntilAsync(
            () => bluetoothTransport.Count<ProtocolMessage.SetBluetoothConnectionModeCommand>() == 3,
            "The companion did not restore interactive Bluetooth mode.");
        await bluetoothService.StopAsync();
        AssertEx.Equal(
            BluetoothConnectionMode.PowerSaving,
            bluetoothTransport.Last<ProtocolMessage.SetBluetoothConnectionModeCommand>().Mode);

        FakeTransportFactory usbFactory = new(manifest, static (kind, _) => new FakeProtocolKeyboardTransport(kind));
        await using CompanionService usbService = CreateService(manifest, TransportPreference.Usb, usbFactory);
        usbService.SetBluetoothConnectionMode(BluetoothConnectionMode.PowerSaving);
        await usbService.StartAsync();
        await WaitUntilAsync(
            () => usbService.State == CompanionConnectionState.Connected,
            "The adaptive USB control test did not connect.");
        AssertEx.Equal(0, usbFactory.Created[0].Count<ProtocolMessage.SetBluetoothConnectionModeCommand>());
        await usbService.StopAsync();
    }

    private static void VerifyBluetoothConnectionModePolicy()
    {
        BluetoothConnectionModePolicy policy = new(TimeSpan.FromSeconds(60));
        AssertEx.Equal(
            BluetoothConnectionMode.Interactive,
            policy.GetMode(sessionLocked: false, TimeSpan.FromSeconds(59.999)));
        AssertEx.Equal(
            BluetoothConnectionMode.PowerSaving,
            policy.GetMode(sessionLocked: false, TimeSpan.FromSeconds(60)));
        AssertEx.Equal(
            BluetoothConnectionMode.PowerSaving,
            policy.GetMode(sessionLocked: true, TimeSpan.Zero));
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => policy.GetMode(sessionLocked: false, TimeSpan.FromMilliseconds(-1)));
    }

    private static CompanionService CreateService(
        LayoutManifest manifest,
        TransportPreference preference,
        IKeyboardTransportFactory factory,
        IReadOnlyList<ShortcutBinding>? bindings = null)
    {
        ShortcutBinding binding = new(
            ShortcutGesture.Parse("F23"),
            ShortcutActionKind.MomentaryLayer,
            1,
            "Navigation");
        ResolvedCompanionConfiguration configuration = new(preference, bindings ?? [binding]);
        CompanionServiceOptions options = new(
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(50),
            5,
            1);
        return new CompanionService(
            manifest,
            configuration,
            factory,
            new ExponentialReconnectDelayPolicy(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)),
            NullDiagnosticSink.Instance,
            options);
    }

    private static ShortcutKeyEvent F23(ShortcutKeyState state, bool isInjected = false)
    {
        return new ShortcutKeyEvent("F23", ShortcutModifiers.None, state, isInjected);
    }

    private static async ValueTask WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2))
            {
                throw new InvalidOperationException(failureMessage);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }

    private static LayoutManifest CreateManifest()
    {
        return new LayoutManifest(
            LayoutManifest.CurrentSchemaVersion,
            ProtocolVersion.Current,
            "sg60-v1-0123456789abcdef0123456789abcdef",
            new string('a', 64),
            "fixture-revision",
            [new LayerDefinition(0, "Home"), new LayerDefinition(1, "Navigation")],
            DateTimeOffset.UnixEpoch);
    }

    private sealed class FakeTransportFactory : IKeyboardTransportFactory
    {
        private readonly LayoutFingerprint layout;
        private readonly Func<TransportKind, int, FakeProtocolKeyboardTransport> createTransport;
        private readonly object syncRoot = new();

        public FakeTransportFactory(
            LayoutManifest manifest,
            Func<TransportKind, int, FakeProtocolKeyboardTransport> createTransport)
        {
            this.layout = LayoutFingerprint.FromLayoutIdentifier(manifest.LayoutIdentifier);
            this.createTransport = createTransport;
        }

        public List<FakeProtocolKeyboardTransport> Created { get; } = [];

        public IKeyboardTransport Create(TransportKind kind)
        {
            lock (this.syncRoot)
            {
                FakeProtocolKeyboardTransport transport = this.createTransport(kind, this.Created.Count);
                transport.Layout = this.layout;
                this.Created.Add(transport);
                return transport;
            }
        }
    }

    private sealed class FakeProtocolKeyboardTransport : IKeyboardTransport, IKeyboardTransportConnectionEvents
    {
        private static int nextSessionId;
        private readonly ConcurrentQueue<ProtocolMessage> requests = new();
        private readonly HashSet<uint> momentaryActivations = [];
        private readonly bool failConnection;
        private EventHandler<KeyboardPacketReceivedEventArgs>? packetReceived;
        private uint sessionId;
        private uint revision = 1;
        private byte? persistentLayer;
        private byte effectiveLayer;

        public FakeProtocolKeyboardTransport(TransportKind kind, bool failConnection = false)
        {
            this.Kind = kind;
            this.failConnection = failConnection;
        }

        public event EventHandler<KeyboardPacketReceivedEventArgs>? PacketReceived
        {
            add => this.packetReceived += value;
            remove => this.packetReceived -= value;
        }

        public event EventHandler<KeyboardTransportConnectionLostEventArgs>? ConnectionLost;

        public TransportKind Kind { get; }

        public bool IsConnected { get; private set; }

        public LayoutFingerprint Layout { get; set; }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.failConnection)
            {
                throw new InvalidOperationException($"Fake {this.Kind} is unavailable.");
            }

            this.IsConnected = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!this.IsConnected)
            {
                throw new IOException($"Fake {this.Kind} is disconnected.");
            }

            if (!ProtocolPacketCodec.TryDecode(request.Span, out ProtocolMessage? message) || message is null)
            {
                throw new InvalidDataException("The fake transport received a malformed request.");
            }

            this.requests.Enqueue(message);
            ProtocolMessage response = this.Respond(message);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(ProtocolPacketCodec.Encode(response));
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            this.IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public int Count<TMessage>()
            where TMessage : ProtocolMessage
        {
            return this.requests.Count(message => message is TMessage);
        }

        public TMessage Last<TMessage>()
            where TMessage : ProtocolMessage
        {
            return this.requests.OfType<TMessage>().Last();
        }

        public void RaiseConnectionLost()
        {
            this.IsConnected = false;
            this.ConnectionLost?.Invoke(
                this,
                new KeyboardTransportConnectionLostEventArgs(new IOException("Synthetic transport loss.")));
        }

        public void DropSilently()
        {
            this.IsConnected = false;
        }

        public void RaisePacket(ProtocolMessage message)
        {
            this.packetReceived?.Invoke(
                this,
                new KeyboardPacketReceivedEventArgs(ProtocolPacketCodec.Encode(message)));
        }

        private ProtocolMessage Respond(ProtocolMessage message)
        {
            return message switch
            {
                ProtocolMessage.HelloRequest hello => this.RespondToHello(hello),
                ProtocolMessage.GetStateRequest request => new ProtocolMessage.StateSnapshot(
                    this.sessionId,
                    request.RequestId,
                    this.LayerState()),
                ProtocolMessage.GetBatteryRequest request => new ProtocolMessage.BatterySnapshot(
                    this.sessionId,
                    request.RequestId,
                    new ProtocolMessage.BatteryState(
                        1,
                        80,
                        70,
                        BatteryStateIndicators.LeftAvailable | BatteryStateIndicators.RightAvailable)),
                ProtocolMessage.PressMomentaryLayerCommand command => this.Press(command),
                ProtocolMessage.RenewMomentaryLayerCommand command => this.Renew(command),
                ProtocolMessage.ReleaseMomentaryLayerCommand command => this.Release(command),
                ProtocolMessage.SetPersistentLayerCommand command => this.SetPersistent(command),
                ProtocolMessage.SetBluetoothConnectionModeCommand command =>
                    this.Result(command.CommandId, CommandStatus.Applied),
                _ => throw new InvalidOperationException($"The fake keyboard does not support {message.Type}.")
            };
        }

        private ProtocolMessage.HelloResult RespondToHello(ProtocolMessage.HelloRequest hello)
        {
            this.sessionId = checked((uint)Interlocked.Increment(ref nextSessionId));
            this.momentaryActivations.Clear();
            this.effectiveLayer = this.persistentLayer ?? 0;
            return new ProtocolMessage.HelloResult(
                hello.ClientNonce,
                HelloStatus.Success,
                hello.RequestedCapabilities,
                this.sessionId,
                this.Layout);
        }

        private ProtocolMessage.CommandResult Press(ProtocolMessage.PressMomentaryLayerCommand command)
        {
            this.momentaryActivations.Add(command.CommandId);
            this.effectiveLayer = command.LayerId;
            this.revision++;
            return this.Result(command.CommandId, CommandStatus.Applied);
        }

        private ProtocolMessage.CommandResult Renew(ProtocolMessage.RenewMomentaryLayerCommand command)
        {
            CommandStatus status = this.momentaryActivations.Contains(command.ActivationId)
                ? CommandStatus.NoChange
                : CommandStatus.AlreadyReleased;
            return this.Result(command.CommandId, status);
        }

        private ProtocolMessage.CommandResult Release(ProtocolMessage.ReleaseMomentaryLayerCommand command)
        {
            bool removed = this.momentaryActivations.Remove(command.ActivationId);
            if (removed)
            {
                this.effectiveLayer = this.persistentLayer ?? 0;
                this.revision++;
            }

            return this.Result(command.CommandId, removed ? CommandStatus.Applied : CommandStatus.AlreadyReleased);
        }

        private ProtocolMessage.CommandResult SetPersistent(ProtocolMessage.SetPersistentLayerCommand command)
        {
            this.persistentLayer = command.LayerId;
            if (this.momentaryActivations.Count == 0)
            {
                this.effectiveLayer = command.LayerId;
            }

            this.revision++;
            return this.Result(command.CommandId, CommandStatus.Applied);
        }

        private ProtocolMessage.CommandResult Result(uint commandId, CommandStatus status)
        {
            return new ProtocolMessage.CommandResult(this.sessionId, commandId, status, this.LayerState());
        }

        private ProtocolMessage.LayerState LayerState()
        {
            LayerStateIndicators indicators =
                (this.persistentLayer.HasValue ? LayerStateIndicators.PersistentLayerActive : LayerStateIndicators.None) |
                (this.momentaryActivations.Count > 0 ? LayerStateIndicators.MomentaryLayerActive : LayerStateIndicators.None);
            return new ProtocolMessage.LayerState(
                this.revision,
                this.effectiveLayer,
                this.persistentLayer,
                checked((byte)this.momentaryActivations.Count),
                indicators);
        }
    }
}
