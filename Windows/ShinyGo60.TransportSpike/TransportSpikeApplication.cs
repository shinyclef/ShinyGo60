using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using ShinyGo60.Companion.Core.Control;
using ShinyGo60.Companion.Core.Telemetry;
using ShinyGo60.Platform.Windows.Transports;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Protocol.Transport;

namespace ShinyGo60.TransportSpike;

internal static class TransportSpikeApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            SpikeOptions options = SpikeOptions.Parse(args);
            LayoutManifest manifest = await LayoutManifestJson.ReadAsync(options.ManifestPath).ConfigureAwait(false);
            if (manifest.ProtocolVersion != ProtocolVersion.Current)
            {
                throw new InvalidDataException(
                    $"Manifest protocol {manifest.ProtocolVersion} is unsupported; expected {ProtocolVersion.Current}.");
            }

            if (options.UsesLayerId && options.ControlLayerId >= manifest.Layers.Count)
            {
                throw new InvalidDataException(
                    $"Control layer {options.ControlLayerId} is not present in the manifest, which has " +
                    $"{manifest.Layers.Count} layers numbered 0 through {manifest.Layers.Count - 1}.");
            }

            if (options.Mode == TransportRunMode.ControlSwitch)
            {
                await RunControlTransportSwitchDiagnosticAsync(options, manifest).ConfigureAwait(false);
                return 0;
            }

            foreach (TransportKind kind in options.GetTransportSequence())
            {
                await RunTransportAsync(kind, options, manifest).ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return 1;
        }
    }

    private static async Task RunTransportAsync(
        TransportKind kind,
        SpikeOptions options,
        LayoutManifest manifest)
    {
        await using IKeyboardTransport transport = CreateTransport(kind);
        LayerStateTracker layerTracker = new(manifest);
        BatteryStateTracker batteryTracker = new(manifest);
        using CancellationTokenSource connectTimeout = new(options.Timeout);

        Console.WriteLine($"Connecting over {kind}...");
        await transport.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        Console.WriteLine($"Connected over {kind}.");

        if (options.IsHold)
        {
            await RunLayerHoldDiagnosticAsync(transport, manifest, options).ConfigureAwait(false);
            await transport.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        if (options.IsPersistentSelection)
        {
            await RunPersistentLayerSelectionAsync(transport, manifest, options).ConfigureAwait(false);
            await transport.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        if (options.IsOwnership)
        {
            await RunLayerOwnershipDiagnosticAsync(transport, manifest, options).ConfigureAwait(false);
            await transport.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        if (options.IsControl)
        {
            await RunLayerControlDiagnosticAsync(transport, manifest, options).ConfigureAwait(false);
            await transport.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        if (options.IsWatch)
        {
            await WatchTelemetryAsync(transport, layerTracker, batteryTracker, manifest, options).ConfigureAwait(false);
            await transport.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        TimeSpan[] durations = new TimeSpan[options.ExchangeCount];
        for (int exchange = 1; exchange <= options.ExchangeCount; exchange++)
        {
            using CancellationTokenSource exchangeTimeout = new(options.Timeout);
            SessionSnapshot result = await OpenTelemetrySessionAsync(
                    transport,
                    layerTracker,
                    batteryTracker,
                    manifest,
                    exchangeTimeout.Token)
                .ConfigureAwait(false);
            durations[exchange - 1] = result.Duration;

            Console.WriteLine(
                $"  Snapshot {exchange}: {result.Duration.TotalMilliseconds:F2} ms, session {result.SessionId:X8}, " +
                $"{FormatLayerState(result.LayerState)}, battery revision {result.BatteryState.Revision}, " +
                $"left {FormatBattery(result.BatteryState.Left)}, right {FormatBattery(result.BatteryState.Right)}");
        }

        await transport.DisconnectAsync().ConfigureAwait(false);
        double average = durations.Average(duration => duration.TotalMilliseconds);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{kind} Hello+GetState+GetBattery summary: count={durations.Length}, " +
                $"min={durations.Min().TotalMilliseconds:F2} ms, " +
                $"mean={average:F2} ms, max={durations.Max().TotalMilliseconds:F2} ms"));
    }

    private static async Task RunPersistentLayerSelectionAsync(
        IKeyboardTransport transport,
        LayoutManifest manifest,
        SpikeOptions options)
    {
        ProtocolCapability requestedCapabilities =
            ProtocolCapability.StateTelemetry |
            ProtocolCapability.PersistentLayer;
        LayerCommandStateMachine machine = new(manifest);
        EventHandler<KeyboardPacketReceivedEventArgs> packetHandler =
            (_, args) => HandleLayerControlEvent(machine.StateTracker, args.Packet);
        bool handlerAttached = false;

        try
        {
            uint sessionId;
            using (CancellationTokenSource exchangeTimeout = new(options.Timeout))
            {
                ProtocolMessage.HelloResult helloResult = await OpenProtocolSessionAsync(
                        transport,
                        manifest,
                        requestedCapabilities,
                        exchangeTimeout.Token)
                    .ConfigureAwait(false);
                machine.BeginSession(helloResult);
                sessionId = helloResult.SessionId;
            }

            transport.PacketReceived += packetHandler;
            handlerAttached = true;
            await InitializeLayerStateAsync(transport, machine, sessionId, options.Timeout).ConfigureAwait(false);

            byte selectedLayerId = options.ControlLayerId;
            LayerDefinition selectedLayer = manifest.Layers[selectedLayerId];
            machine.QueuePersistentLayer(selectedLayerId);
            CommandExchange selection = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    $"Select persistent {selectedLayer.Name}",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(selection.Result, CommandStatus.Applied, CommandStatus.NoChange);
            RequirePersistentLayer(machine.StateTracker.CurrentState!, selectedLayerId);
            Console.WriteLine(
                $"Persistent layer {selectedLayer.Id} ({selectedLayer.Name}) remains selected after {transport.Kind} disconnects.");
        }
        finally
        {
            if (handlerAttached)
            {
                transport.PacketReceived -= packetHandler;
            }

            machine.EndSession();
        }
    }

    private static async Task RunLayerHoldDiagnosticAsync(
        IKeyboardTransport transport,
        LayoutManifest manifest,
        SpikeOptions options)
    {
        const byte leaseUnits = ProtocolPacketCodec.MaximumLeaseUnits;
        ProtocolCapability requestedCapabilities =
            ProtocolCapability.StateTelemetry |
            ProtocolCapability.MomentaryLayer;
        LayerCommandStateMachine machine = new(manifest);
        EventHandler<KeyboardPacketReceivedEventArgs> packetHandler =
            (_, args) => HandleLayerControlEvent(machine.StateTracker, args.Packet);
        bool handlerAttached = false;
        uint? activeExternalActivation = null;

        try
        {
            uint sessionId;
            using (CancellationTokenSource exchangeTimeout = new(options.Timeout))
            {
                ProtocolMessage.HelloResult helloResult = await OpenProtocolSessionAsync(
                        transport,
                        manifest,
                        requestedCapabilities,
                        exchangeTimeout.Token)
                    .ConfigureAwait(false);
                machine.BeginSession(helloResult);
                sessionId = helloResult.SessionId;
            }

            transport.PacketReceived += packetHandler;
            handlerAttached = true;
            await InitializeLayerStateAsync(transport, machine, sessionId, options.Timeout).ConfigureAwait(false);

            byte targetLayerId = options.ControlLayerId;
            LayerDefinition targetLayer = manifest.Layers[targetLayerId];
            activeExternalActivation = machine.QueueMomentaryPress(targetLayerId, leaseUnits);
            CommandExchange press = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    $"Hold {targetLayer.Name}",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(press.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 1);
            RequireEffectiveLayer(machine.StateTracker.CurrentState!, targetLayerId);

            Console.WriteLine(
                $"ACTIVE: External {targetLayer.Name} is held with lease renewal. Press Enter to release it normally.");
            await WaitForOperatorWithRenewalAsync(
                    transport,
                    machine,
                    activeExternalActivation.Value,
                    leaseUnits,
                    options.Timeout)
                .ConfigureAwait(false);

            machine.QueueMomentaryRelease(activeExternalActivation.Value);
            CommandExchange release = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    $"Release {targetLayer.Name}",
                    options.Timeout)
                .ConfigureAwait(false);
            activeExternalActivation = null;
            RequireStatus(release.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 0);
            Console.WriteLine($"{transport.Kind} external hold released normally.");
        }
        finally
        {
            if (handlerAttached)
            {
                transport.PacketReceived -= packetHandler;
            }

            if (activeExternalActivation.HasValue)
            {
                Console.Error.WriteLine(
                    "WARNING: The hold diagnostic ended while externally active. Transport cleanup or lease expiry will remove it.");
            }

            machine.EndSession();
        }
    }

    private static async Task RunControlTransportSwitchDiagnosticAsync(
        SpikeOptions options,
        LayoutManifest manifest)
    {
        const byte leaseUnits = ProtocolPacketCodec.MaximumLeaseUnits;
        ProtocolCapability requestedCapabilities =
            ProtocolCapability.StateTelemetry |
            ProtocolCapability.MomentaryLayer;
        await using IKeyboardTransport usb = CreateTransport(TransportKind.Usb);
        await using IKeyboardTransport bluetooth = CreateTransport(TransportKind.Bluetooth);
        LayerCommandStateMachine usbMachine = new(manifest);
        LayerCommandStateMachine bluetoothMachine = new(manifest);

        try
        {
            using (CancellationTokenSource connectTimeout = new(options.Timeout))
            {
                Console.WriteLine("Connecting over Usb...");
                await usb.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
                Console.WriteLine("Connected over Usb.");
            }

            ProtocolMessage.HelloResult usbHello;
            using (CancellationTokenSource exchangeTimeout = new(options.Timeout))
            {
                usbHello = await OpenProtocolSessionAsync(
                        usb,
                        manifest,
                        requestedCapabilities,
                        exchangeTimeout.Token)
                    .ConfigureAwait(false);
            }

            usbMachine.BeginSession(usbHello);
            await InitializeLayerStateAsync(usb, usbMachine, usbHello.SessionId, options.Timeout).ConfigureAwait(false);
            _ = usbMachine.QueueMomentaryPress(options.ControlLayerId, leaseUnits);
            CommandExchange usbPress = await SendNextLayerCommandAsync(
                    usb,
                    usbMachine,
                    "USB hold before Bluetooth handoff",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(usbPress.Result, CommandStatus.Applied);
            RequireMomentaryCount(usbMachine.StateTracker.CurrentState!, 1);

            using (CancellationTokenSource connectTimeout = new(options.Timeout))
            {
                Console.WriteLine("Connecting over Bluetooth while the USB activation remains held...");
                await bluetooth.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
                Console.WriteLine("Connected over Bluetooth.");
            }

            ProtocolMessage.HelloResult bluetoothHello;
            using (CancellationTokenSource exchangeTimeout = new(options.Timeout))
            {
                bluetoothHello = await OpenProtocolSessionAsync(
                        bluetooth,
                        manifest,
                        requestedCapabilities,
                        exchangeTimeout.Token)
                    .ConfigureAwait(false);
            }

            bluetoothMachine.BeginSession(bluetoothHello);
            await InitializeLayerStateAsync(
                    bluetooth,
                    bluetoothMachine,
                    bluetoothHello.SessionId,
                    options.Timeout)
                .ConfigureAwait(false);
            RequireMomentaryCount(bluetoothMachine.StateTracker.CurrentState!, 0);
            RequireEffectiveLayer(bluetoothMachine.StateTracker.CurrentState!, 0);
            Console.WriteLine("  USB-to-Bluetooth handoff removed the USB-owned activation.");

            _ = bluetoothMachine.QueueMomentaryPress(options.ControlLayerId, leaseUnits);
            CommandExchange bluetoothPress = await SendNextLayerCommandAsync(
                    bluetooth,
                    bluetoothMachine,
                    "Bluetooth hold before USB handoff",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(bluetoothPress.Result, CommandStatus.Applied);
            RequireMomentaryCount(bluetoothMachine.StateTracker.CurrentState!, 1);

            using (CancellationTokenSource exchangeTimeout = new(options.Timeout))
            {
                usbHello = await OpenProtocolSessionAsync(
                        usb,
                        manifest,
                        requestedCapabilities,
                        exchangeTimeout.Token)
                    .ConfigureAwait(false);
            }

            usbMachine.BeginSession(usbHello);
            await InitializeLayerStateAsync(usb, usbMachine, usbHello.SessionId, options.Timeout).ConfigureAwait(false);
            RequireMomentaryCount(usbMachine.StateTracker.CurrentState!, 0);
            RequireEffectiveLayer(usbMachine.StateTracker.CurrentState!, 0);
            Console.WriteLine("  Bluetooth-to-USB handoff removed the Bluetooth-owned activation.");
            Console.WriteLine("Transport-switch control diagnostic passed. Home is active with no momentary activation.");

        }
        finally
        {
            usbMachine.EndSession();
            bluetoothMachine.EndSession();
        }
    }

    private static async Task RunLayerOwnershipDiagnosticAsync(
        IKeyboardTransport transport,
        LayoutManifest manifest,
        SpikeOptions options)
    {
        const byte leaseUnits = ProtocolPacketCodec.MaximumLeaseUnits;
        ProtocolCapability requestedCapabilities =
            ProtocolCapability.StateTelemetry |
            ProtocolCapability.PersistentLayer |
            ProtocolCapability.MomentaryLayer;
        LayerCommandStateMachine machine = new(manifest);
        EventHandler<KeyboardPacketReceivedEventArgs> packetHandler =
            (_, args) => HandleLayerControlEvent(machine.StateTracker, args.Packet);
        bool handlerAttached = false;
        uint? activeExternalActivation = null;

        try
        {
            uint sessionId;
            using (CancellationTokenSource exchangeTimeout = new(options.Timeout))
            {
                ProtocolMessage.HelloResult helloResult = await OpenProtocolSessionAsync(
                        transport,
                        manifest,
                        requestedCapabilities,
                        exchangeTimeout.Token)
                    .ConfigureAwait(false);
                machine.BeginSession(helloResult);
                sessionId = helloResult.SessionId;
            }

            transport.PacketReceived += packetHandler;
            handlerAttached = true;
            await InitializeLayerStateAsync(transport, machine, sessionId, options.Timeout).ConfigureAwait(false);

            machine.QueuePersistentLayer(0);
            CommandExchange home = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Ownership-test Home baseline",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(home.Result, CommandStatus.Applied, CommandStatus.NoChange);
            RequirePersistentLayer(machine.StateTracker.CurrentState!, 0);

            byte targetLayerId = options.ControlLayerId;
            LayerDefinition targetLayer = manifest.Layers[targetLayerId];
            Console.WriteLine(
                $"Ownership test uses layer {targetLayer.Id} ({targetLayer.Name}) and waits for operator confirmations.");

            activeExternalActivation = machine.QueueMomentaryPress(targetLayerId, leaseUnits);
            CommandExchange externalFirst = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "External-first press",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(externalFirst.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 1);
            RequireEffectiveLayer(machine.StateTracker.CurrentState!, targetLayerId);

            Console.WriteLine(
                $"WAITING 1: Hold the physical momentary key for {targetLayer.Name}, keep holding it, then confirm to continue.");
            await WaitForOperatorWithRenewalAsync(
                    transport,
                    machine,
                    activeExternalActivation.Value,
                    leaseUnits,
                    options.Timeout)
                .ConfigureAwait(false);

            machine.QueueMomentaryRelease(activeExternalActivation.Value);
            CommandExchange externalFirstRelease = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "External release while physical key is held",
                    options.Timeout)
                .ConfigureAwait(false);
            activeExternalActivation = null;
            RequireStatus(externalFirstRelease.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 0);
            RequireEffectiveLayer(machine.StateTracker.CurrentState!, targetLayerId);
            Console.WriteLine($"  External-first order passed: the physical key retained {targetLayer.Name}.");

            Console.WriteLine($"WAITING 2: Release the physical momentary key for {targetLayer.Name}, then confirm to continue.");
            await WaitForOperatorAsync().ConfigureAwait(false);
            await RefreshLayerStateAsync(transport, machine, options.Timeout).ConfigureAwait(false);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 0);
            RequireEffectiveLayer(machine.StateTracker.CurrentState!, 0);

            Console.WriteLine(
                $"WAITING 3: Hold the physical momentary key for {targetLayer.Name} first, keep holding it, then confirm to continue.");
            await WaitForOperatorAsync().ConfigureAwait(false);
            await RefreshLayerStateAsync(transport, machine, options.Timeout).ConfigureAwait(false);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 0);
            RequireEffectiveLayer(machine.StateTracker.CurrentState!, targetLayerId);

            activeExternalActivation = machine.QueueMomentaryPress(targetLayerId, leaseUnits);
            CommandExchange physicalFirst = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Physical-first external press",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(physicalFirst.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 1);
            RequireEffectiveLayer(machine.StateTracker.CurrentState!, targetLayerId);

            Console.WriteLine(
                $"WAITING 4: Release the physical momentary key for {targetLayer.Name} while the external hold remains, then confirm.");
            await WaitForOperatorWithRenewalAsync(
                    transport,
                    machine,
                    activeExternalActivation.Value,
                    leaseUnits,
                    options.Timeout)
                .ConfigureAwait(false);
            await RefreshLayerStateAsync(transport, machine, options.Timeout).ConfigureAwait(false);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 1);
            RequireEffectiveLayer(machine.StateTracker.CurrentState!, targetLayerId);

            machine.QueueMomentaryRelease(activeExternalActivation.Value);
            CommandExchange physicalFirstRelease = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Final external release",
                    options.Timeout)
                .ConfigureAwait(false);
            activeExternalActivation = null;
            RequireStatus(physicalFirstRelease.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 0);
            RequireEffectiveLayer(machine.StateTracker.CurrentState!, 0);

            Console.WriteLine(
                $"{transport.Kind} same-layer ownership diagnostic passed in both release orders. Home is active.");
        }
        finally
        {
            if (handlerAttached)
            {
                transport.PacketReceived -= packetHandler;
            }

            if (activeExternalActivation.HasValue)
            {
                Console.Error.WriteLine(
                    "WARNING: The interactive test ended during an external hold. Firmware will expire it within five seconds.");
            }

            machine.EndSession();
        }
    }

    private static async Task WaitForOperatorWithRenewalAsync(
        IKeyboardTransport transport,
        LayerCommandStateMachine machine,
        uint activationId,
        byte leaseUnits,
        TimeSpan timeout)
    {
        Task<string?> confirmation = Task.Run(() => Console.ReadLine());
        while (!confirmation.IsCompleted)
        {
            Task completed = await Task.WhenAny(
                    confirmation,
                    Task.Delay(TimeSpan.FromSeconds(2)))
                .ConfigureAwait(false);
            if (completed == confirmation)
            {
                break;
            }

            machine.QueueMomentaryRenewal(activationId, leaseUnits);
            CommandExchange renewal = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Waiting-hold renewal",
                    timeout)
                .ConfigureAwait(false);
            RequireStatus(renewal.Result, CommandStatus.NoChange);
        }

        if (await confirmation.ConfigureAwait(false) is null)
        {
            throw new EndOfStreamException("Operator confirmation input ended during the ownership test.");
        }
    }

    private static async Task WaitForOperatorAsync()
    {
        if (await Task.Run(() => Console.ReadLine()).ConfigureAwait(false) is null)
        {
            throw new EndOfStreamException("Operator confirmation input ended during the ownership test.");
        }
    }

    private static async Task RunLayerControlDiagnosticAsync(
        IKeyboardTransport transport,
        LayoutManifest manifest,
        SpikeOptions options)
    {
        const byte workingLeaseUnits = 20;
        const byte expiryLeaseUnits = 5;
        ProtocolCapability requestedCapabilities =
            ProtocolCapability.StateTelemetry |
            ProtocolCapability.PersistentLayer |
            ProtocolCapability.MomentaryLayer;
        LayerCommandStateMachine machine = new(manifest);
        EventHandler<KeyboardPacketReceivedEventArgs> packetHandler =
            (_, args) => HandleLayerControlEvent(machine.StateTracker, args.Packet);
        bool handlerAttached = false;
        bool targetPersistentLayerMayBeActive = false;

        try
        {
            uint sessionId;
            using (CancellationTokenSource exchangeTimeout = new(options.Timeout))
            {
                ProtocolMessage.HelloResult helloResult = await OpenProtocolSessionAsync(
                        transport,
                        manifest,
                        requestedCapabilities,
                        exchangeTimeout.Token)
                    .ConfigureAwait(false);
                machine.BeginSession(helloResult);
                sessionId = helloResult.SessionId;
            }

            transport.PacketReceived += packetHandler;
            handlerAttached = true;
            await InitializeLayerStateAsync(transport, machine, sessionId, options.Timeout).ConfigureAwait(false);

            byte targetLayerId = options.ControlLayerId;
            LayerDefinition targetLayer = manifest.Layers[targetLayerId];
            Console.WriteLine($"Initial control state: {FormatLayerState(machine.StateTracker.CurrentState!)}");
            Console.WriteLine(
                $"Testing persistent and momentary control with layer {targetLayer.Id} ({targetLayer.Name}).");

            targetPersistentLayerMayBeActive = true;
            machine.QueuePersistentLayer(targetLayerId);
            CommandExchange persistentTarget = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Persistent target",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(persistentTarget.Result, CommandStatus.Applied, CommandStatus.NoChange);
            RequirePersistentLayer(machine.StateTracker.CurrentState!, targetLayerId);
            await VerifyDuplicateCommandAsync(transport, persistentTarget, options.Timeout).ConfigureAwait(false);

            machine.QueuePersistentLayer(0);
            CommandExchange persistentHome = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Persistent Home restore",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(persistentHome.Result, CommandStatus.Applied, CommandStatus.NoChange);
            RequirePersistentLayer(machine.StateTracker.CurrentState!, 0);
            targetPersistentLayerMayBeActive = false;

            uint activationId = machine.QueueMomentaryPress(targetLayerId, workingLeaseUnits);
            CommandExchange momentaryPress = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Momentary press",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(momentaryPress.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 1);
            await VerifyDuplicateCommandAsync(transport, momentaryPress, options.Timeout).ConfigureAwait(false);

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            machine.QueueMomentaryRenewal(activationId, workingLeaseUnits);
            CommandExchange renewal = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Momentary renewal",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(renewal.Result, CommandStatus.NoChange);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 1);

            machine.QueueMomentaryRelease(activationId);
            CommandExchange release = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Momentary release",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(release.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 0);

            uint firstConcurrentActivationId = machine.QueueMomentaryPress(targetLayerId, workingLeaseUnits);
            CommandExchange firstConcurrentPress = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "First simultaneous press",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(firstConcurrentPress.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 1);

            uint secondConcurrentActivationId = machine.QueueMomentaryPress(targetLayerId, workingLeaseUnits);
            CommandExchange secondConcurrentPress = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Second simultaneous press",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(secondConcurrentPress.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 2);

            machine.QueueMomentaryRelease(firstConcurrentActivationId);
            CommandExchange firstConcurrentRelease = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "First simultaneous release",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(firstConcurrentRelease.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 1);
            RequireEffectiveLayer(machine.StateTracker.CurrentState!, targetLayerId);

            machine.QueueMomentaryRelease(secondConcurrentActivationId);
            CommandExchange secondConcurrentRelease = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Final simultaneous release",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(secondConcurrentRelease.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 0);
            RequireEffectiveLayer(machine.StateTracker.CurrentState!, 0);

            _ = machine.QueueMomentaryPress(targetLayerId, workingLeaseUnits);
            CommandExchange replacedPress = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Press before session replacement",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(replacedPress.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 1);

            ProtocolMessage.HelloResult replacementHello;
            using (CancellationTokenSource exchangeTimeout = new(options.Timeout))
            {
                replacementHello = await OpenProtocolSessionAsync(
                        transport,
                        manifest,
                        requestedCapabilities,
                        exchangeTimeout.Token)
                    .ConfigureAwait(false);
            }

            machine.BeginSession(replacementHello);
            await InitializeLayerStateAsync(
                    transport,
                    machine,
                    replacementHello.SessionId,
                    options.Timeout)
                .ConfigureAwait(false);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 0);
            RequireEffectiveLayer(machine.StateTracker.CurrentState!, 0);
            Console.WriteLine("  Session replacement removed the previous session's momentary activation.");

            uint expiringActivationId = machine.QueueMomentaryPress(targetLayerId, expiryLeaseUnits);
            CommandExchange expiringPress = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Short leased press",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(expiringPress.Result, CommandStatus.Applied);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 1);

            int leaseMilliseconds = expiryLeaseUnits * ProtocolPacketCodec.LeaseUnitMilliseconds;
            await Task.Delay(TimeSpan.FromMilliseconds(leaseMilliseconds + 500)).ConfigureAwait(false);
            await RefreshLayerStateAsync(transport, machine, options.Timeout).ConfigureAwait(false);
            RequireMomentaryCount(machine.StateTracker.CurrentState!, 0);
            Console.WriteLine($"  Firmware lease expiry: observed; {FormatLayerState(machine.StateTracker.CurrentState!)}");

            machine.QueueMomentaryRelease(expiringActivationId);
            CommandExchange expiredRelease = await SendNextLayerCommandAsync(
                    transport,
                    machine,
                    "Release after expiry",
                    options.Timeout)
                .ConfigureAwait(false);
            RequireStatus(expiredRelease.Result, CommandStatus.AlreadyReleased);

            Console.WriteLine(
                $"{transport.Kind} layer-control diagnostic passed. Persistent Home is selected and no momentary activation remains.");
        }
        catch
        {
            if (handlerAttached)
            {
                transport.PacketReceived -= packetHandler;
                handlerAttached = false;
            }

            if (targetPersistentLayerMayBeActive)
            {
                await TryRestoreHomeLayerAsync(transport, manifest, options.Timeout).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (handlerAttached)
            {
                transport.PacketReceived -= packetHandler;
            }

            machine.EndSession();
        }
    }

    private static void HandleLayerControlEvent(LayerStateTracker tracker, ReadOnlyMemory<byte> packet)
    {
        if (!ProtocolPacketCodec.TryDecode(packet.Span, out ProtocolMessage? decoded) || decoded is null)
        {
            Console.Error.WriteLine("WARNING: An unsolicited control packet was not a valid protocol event.");
            return;
        }

        if (decoded is ProtocolMessage.LayerChanged changed)
        {
            HandleLayerEvent(tracker, changed);
            return;
        }

        Console.Error.WriteLine($"WARNING: Unexpected unsolicited {decoded.Type} packet during layer control.");
    }

    private static async Task InitializeLayerStateAsync(
        IKeyboardTransport transport,
        LayerCommandStateMachine machine,
        uint sessionId,
        TimeSpan timeout)
    {
        ProtocolMessage.StateSnapshot snapshot = await RequestLayerSnapshotAsync(
                transport,
                sessionId,
                timeout)
            .ConfigureAwait(false);
        LayerTelemetryApplyResult result = machine.StateTracker.Apply(snapshot);
        if (result != LayerTelemetryApplyResult.AppliedSnapshot)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} StateSnapshot could not initialize control state: {result}.");
        }
    }

    private static async Task RefreshLayerStateAsync(
        IKeyboardTransport transport,
        LayerCommandStateMachine machine,
        TimeSpan timeout)
    {
        uint sessionId = machine.StateTracker.CurrentState?.SessionId ??
            throw new InvalidOperationException("The control session has no initialized layer state.");
        ProtocolMessage.StateSnapshot snapshot = await RequestLayerSnapshotAsync(
                transport,
                sessionId,
                timeout)
            .ConfigureAwait(false);
        LayerTelemetryApplyResult result = machine.StateTracker.Apply(snapshot);
        if (result is not LayerTelemetryApplyResult.AppliedSnapshot and
            not LayerTelemetryApplyResult.Applied and
            not LayerTelemetryApplyResult.AppliedAfterGap and
            not LayerTelemetryApplyResult.Duplicate)
        {
            throw new InvalidDataException($"The {transport.Kind} StateSnapshot could not refresh control state: {result}.");
        }
    }

    private static async Task<CommandExchange> SendNextLayerCommandAsync(
        IKeyboardTransport transport,
        LayerCommandStateMachine machine,
        string label,
        TimeSpan timeout)
    {
        ProtocolMessage request = machine.TryStartNextCommand() ??
            throw new InvalidOperationException($"No queued command was available for {label}.");
        using CancellationTokenSource exchangeTimeout = new(timeout);
        ReadOnlyMemory<byte> responseBytes = await transport
            .ExchangeAsync(ProtocolPacketCodec.Encode(request), exchangeTimeout.Token)
            .ConfigureAwait(false);
        if (!ProtocolPacketCodec.TryDecode(responseBytes.Span, out ProtocolMessage? response) || response is null)
        {
            throw new InvalidDataException($"The {transport.Kind} response to {label} was not a valid protocol packet.");
        }

        LayerCommandResponseResult applyResult = machine.ApplyResponse(response);
        if (response is ProtocolMessage.ErrorMessage error)
        {
            throw new InvalidDataException(
                $"The keyboard rejected {label}: {error.Code}, state revision {error.StateRevision}, detail {error.Detail} " +
                $"(state-machine result {applyResult}).");
        }

        if (response is not ProtocolMessage.CommandResult commandResult ||
            applyResult != LayerCommandResponseResult.CommandAccepted)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} response to {label} was not an accepted CommandResult: {applyResult}.");
        }

        Console.WriteLine($"  {label}: {commandResult.Status}; {FormatLayerState(machine.StateTracker.CurrentState!)}");
        return new CommandExchange(request, commandResult);
    }

    private static async Task VerifyDuplicateCommandAsync(
        IKeyboardTransport transport,
        CommandExchange original,
        TimeSpan timeout)
    {
        using CancellationTokenSource exchangeTimeout = new(timeout);
        ReadOnlyMemory<byte> responseBytes = await transport
            .ExchangeAsync(ProtocolPacketCodec.Encode(original.Request), exchangeTimeout.Token)
            .ConfigureAwait(false);
        if (!ProtocolPacketCodec.TryDecode(responseBytes.Span, out ProtocolMessage? decoded) ||
            decoded is not ProtocolMessage.CommandResult duplicate ||
            duplicate.SessionId != original.Result.SessionId ||
            duplicate.CommandId != original.Result.CommandId ||
            duplicate.Status != CommandStatus.Duplicate ||
            duplicate.State != original.Result.State)
        {
            throw new InvalidDataException($"The {transport.Kind} firmware did not return an exact duplicate command result.");
        }

        Console.WriteLine($"  Exact command replay: {duplicate.Status}; no second state change.");
    }

    private static async Task TryRestoreHomeLayerAsync(
        IKeyboardTransport transport,
        LayoutManifest manifest,
        TimeSpan timeout)
    {
        if (!transport.IsConnected)
        {
            Console.Error.WriteLine(
                "WARNING: The control test lost its connection while the target persistent layer may be active. " +
                "Power-cycle the keyboard to clear runtime-only external state.");
            return;
        }

        LayerCommandStateMachine recoveryMachine = new(manifest);
        try
        {
            ProtocolCapability requestedCapabilities =
                ProtocolCapability.StateTelemetry |
                ProtocolCapability.PersistentLayer |
                ProtocolCapability.MomentaryLayer;
            uint sessionId;
            using (CancellationTokenSource exchangeTimeout = new(timeout))
            {
                ProtocolMessage.HelloResult helloResult = await OpenProtocolSessionAsync(
                        transport,
                        manifest,
                        requestedCapabilities,
                        exchangeTimeout.Token)
                    .ConfigureAwait(false);
                recoveryMachine.BeginSession(helloResult);
                sessionId = helloResult.SessionId;
            }

            await InitializeLayerStateAsync(transport, recoveryMachine, sessionId, timeout).ConfigureAwait(false);
            recoveryMachine.QueuePersistentLayer(0);
            CommandExchange restore = await SendNextLayerCommandAsync(
                    transport,
                    recoveryMachine,
                    "Emergency Home restore",
                    timeout)
                .ConfigureAwait(false);
            RequireStatus(restore.Result, CommandStatus.Applied, CommandStatus.NoChange);
            RequirePersistentLayer(recoveryMachine.StateTracker.CurrentState!, 0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"WARNING: Automatic Home restore failed: {exception.Message} Power-cycle the keyboard to clear runtime-only external state.");
        }
        finally
        {
            recoveryMachine.EndSession();
        }
    }

    private static void RequireStatus(
        ProtocolMessage.CommandResult result,
        CommandStatus expected,
        CommandStatus? alternate = null)
    {
        if (result.Status != expected && (!alternate.HasValue || result.Status != alternate.Value))
        {
            string expectedText = alternate.HasValue ? $"{expected} or {alternate.Value}" : expected.ToString();
            throw new InvalidDataException(
                $"Command {result.CommandId} returned {result.Status}; expected {expectedText}.");
        }
    }

    private static void RequirePersistentLayer(LayerTelemetryState state, byte expectedLayerId)
    {
        if (state.PersistentLayer?.Id != expectedLayerId)
        {
            throw new InvalidDataException(
                $"Persistent layer {state.PersistentLayer?.Id.ToString(CultureInfo.InvariantCulture) ?? "none"} was reported; " +
                $"expected {expectedLayerId}.");
        }
    }

    private static void RequireMomentaryCount(LayerTelemetryState state, byte expectedCount)
    {
        if (state.MomentaryLayerCount != expectedCount)
        {
            throw new InvalidDataException(
                $"Momentary activation count {state.MomentaryLayerCount} was reported; expected {expectedCount}.");
        }
    }

    private static void RequireEffectiveLayer(LayerTelemetryState state, byte expectedLayerId)
    {
        if (state.EffectiveLayer.Id != expectedLayerId)
        {
            throw new InvalidDataException(
                $"Effective layer {state.EffectiveLayer.Id} ({state.EffectiveLayer.Name}) was reported; " +
                $"expected {expectedLayerId}.");
        }
    }

    private static string FormatLayerState(LayerTelemetryState state)
    {
        string persistent = state.PersistentLayer is null
            ? "none"
            : $"{state.PersistentLayer.Id} ({state.PersistentLayer.Name})";
        return $"revision {state.Revision}, effective {state.EffectiveLayer.Id} ({state.EffectiveLayer.Name}), " +
               $"persistent {persistent}, momentary count {state.MomentaryLayerCount}";
    }

    private static async Task WatchTelemetryAsync(
        IKeyboardTransport transport,
        LayerStateTracker layerTracker,
        BatteryStateTracker batteryTracker,
        LayoutManifest manifest,
        SpikeOptions options)
    {
        EventHandler<KeyboardPacketReceivedEventArgs> packetHandler =
            (_, args) => HandleTelemetryEvent(layerTracker, batteryTracker, args.Packet);
        transport.PacketReceived += packetHandler;
        try
        {
            using CancellationTokenSource exchangeTimeout = new(options.Timeout);
            SessionSnapshot result = await OpenTelemetrySessionAsync(
                    transport,
                    layerTracker,
                    batteryTracker,
                    manifest,
                    exchangeTimeout.Token)
                .ConfigureAwait(false);
            Console.WriteLine(
                $"Initial state: layer revision {result.LayerState.Revision}, layer {result.LayerState.EffectiveLayer.Id} " +
                $"({result.LayerState.EffectiveLayer.Name}); battery revision {result.BatteryState.Revision}, " +
                $"left {FormatBattery(result.BatteryState.Left)}, right {FormatBattery(result.BatteryState.Right)}.");
            Console.WriteLine(
                $"Watching {transport.Kind} for {options.WatchDuration.TotalSeconds:F0} seconds. " +
                "Battery heartbeats and layer changes will appear below...");
            await Task.Delay(options.WatchDuration).ConfigureAwait(false);
            Console.WriteLine("Telemetry watch complete.");
        }
        finally
        {
            transport.PacketReceived -= packetHandler;
        }
    }

    private static void HandleTelemetryEvent(
        LayerStateTracker layerTracker,
        BatteryStateTracker batteryTracker,
        ReadOnlyMemory<byte> packet)
    {
        if (!ProtocolPacketCodec.TryDecode(packet.Span, out ProtocolMessage? decoded) || decoded is null)
        {
            Console.Error.WriteLine("WARNING: An unsolicited transport packet was not a valid telemetry event.");
            return;
        }

        switch (decoded)
        {
            case ProtocolMessage.LayerChanged changed:
                HandleLayerEvent(layerTracker, changed);
                break;
            case ProtocolMessage.BatteryChanged changed:
                HandleBatteryEvent(batteryTracker, changed);
                break;
            default:
                Console.Error.WriteLine($"WARNING: Unexpected unsolicited {decoded.Type} packet.");
                break;
        }
    }

    private static void HandleLayerEvent(LayerStateTracker tracker, ProtocolMessage.LayerChanged changed)
    {
        LayerTelemetryApplyResult result = tracker.Apply(changed);
        if (result is LayerTelemetryApplyResult.Applied or LayerTelemetryApplyResult.AppliedAfterGap)
        {
            LayerTelemetryState state = tracker.CurrentState!;
            string gap = result == LayerTelemetryApplyResult.AppliedAfterGap ? " (converged after a missed revision)" : string.Empty;
            Console.WriteLine(
                $"  Layer changed: revision {state.Revision}, layer {state.EffectiveLayer.Id} ({state.EffectiveLayer.Name}){gap}");
        }
        else if (result is not LayerTelemetryApplyResult.Duplicate and not LayerTelemetryApplyResult.StaleRevision)
        {
            Console.Error.WriteLine($"WARNING: Layer event was not applied: {result}.");
        }
    }

    private static void HandleBatteryEvent(BatteryStateTracker tracker, ProtocolMessage.BatteryChanged changed)
    {
        BatteryTelemetryApplyResult result = tracker.Apply(changed);
        if (result is BatteryTelemetryApplyResult.Applied or BatteryTelemetryApplyResult.AppliedAfterGap)
        {
            BatteryTelemetryState state = tracker.CurrentState!;
            string gap = result == BatteryTelemetryApplyResult.AppliedAfterGap
                ? " (converged after a missed revision)"
                : string.Empty;
            Console.WriteLine(
                $"  Battery changed: revision {state.Revision}, left {FormatBattery(state.Left)}, " +
                $"right {FormatBattery(state.Right)}{gap}");
        }
        else if (result is not BatteryTelemetryApplyResult.Duplicate and
                 not BatteryTelemetryApplyResult.StaleRevision and
                 not BatteryTelemetryApplyResult.AwaitingSnapshot)
        {
            Console.Error.WriteLine($"WARNING: Battery event was not applied: {result}.");
        }
    }

    private static async Task<ProtocolMessage.HelloResult> OpenProtocolSessionAsync(
        IKeyboardTransport transport,
        LayoutManifest manifest,
        ProtocolCapability requestedCapabilities,
        CancellationToken cancellationToken)
    {
        LayoutFingerprint expectedLayout = LayoutFingerprint.FromLayoutIdentifier(manifest.LayoutIdentifier);
        ushort nonce = CreateNonce();
        ProtocolMessage.HelloRequest hello = new(nonce, requestedCapabilities, expectedLayout);
        ReadOnlyMemory<byte> helloBytes = await transport
            .ExchangeAsync(ProtocolPacketCodec.Encode(hello), cancellationToken)
            .ConfigureAwait(false);
        if (!ProtocolPacketCodec.TryDecode(helloBytes.Span, out ProtocolMessage? decodedHello) ||
            decodedHello is not ProtocolMessage.HelloResult helloResult)
        {
            throw new InvalidDataException($"The {transport.Kind} response was not a valid protocol HelloResult.");
        }

        if (helloResult.ClientNonce != nonce)
        {
            throw new InvalidDataException($"The {transport.Kind} HelloResult did not match the request nonce.");
        }

        if (helloResult.Status != HelloStatus.Success)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} firmware rejected the session: {helloResult.Status}; " +
                $"firmware layout {helloResult.Layout}.");
        }

        if (helloResult.Layout != expectedLayout)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} firmware layout {helloResult.Layout} does not match manifest {expectedLayout}.");
        }

        if (helloResult.SelectedCapabilities != requestedCapabilities)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} session selected {helloResult.SelectedCapabilities}; " +
                $"requested {requestedCapabilities}.");
        }

        return helloResult;
    }

    private static async Task<ProtocolMessage.StateSnapshot> RequestLayerSnapshotAsync(
        IKeyboardTransport transport,
        uint sessionId,
        TimeSpan timeout)
    {
        using CancellationTokenSource exchangeTimeout = new(timeout);
        return await RequestLayerSnapshotAsync(transport, sessionId, exchangeTimeout.Token).ConfigureAwait(false);
    }

    private static async Task<ProtocolMessage.StateSnapshot> RequestLayerSnapshotAsync(
        IKeyboardTransport transport,
        uint sessionId,
        CancellationToken cancellationToken)
    {
        uint requestId = CreateRequestId();
        ProtocolMessage.GetStateRequest getState = new(sessionId, requestId);
        ReadOnlyMemory<byte> snapshotBytes = await transport
            .ExchangeAsync(ProtocolPacketCodec.Encode(getState), cancellationToken)
            .ConfigureAwait(false);
        if (!ProtocolPacketCodec.TryDecode(snapshotBytes.Span, out ProtocolMessage? decodedSnapshot) ||
            decodedSnapshot is not ProtocolMessage.StateSnapshot snapshot)
        {
            throw new InvalidDataException($"The {transport.Kind} response to GetState was not a valid StateSnapshot.");
        }

        if (snapshot.SessionId != sessionId || snapshot.RequestId != requestId)
        {
            throw new InvalidDataException($"The {transport.Kind} StateSnapshot did not match its session and request.");
        }

        return snapshot;
    }

    private static async Task<SessionSnapshot> OpenTelemetrySessionAsync(
        IKeyboardTransport transport,
        LayerStateTracker layerTracker,
        BatteryStateTracker batteryTracker,
        LayoutManifest manifest,
        CancellationToken cancellationToken)
    {
        ProtocolCapability requestedCapabilities =
            ProtocolCapability.StateTelemetry | ProtocolCapability.BatteryTelemetry;
        long started = Stopwatch.GetTimestamp();
        ProtocolMessage.HelloResult helloResult = await OpenProtocolSessionAsync(
                transport,
                manifest,
                requestedCapabilities,
                cancellationToken)
            .ConfigureAwait(false);

        layerTracker.BeginSession(helloResult);
        batteryTracker.BeginSession(helloResult);
        ProtocolMessage.StateSnapshot snapshot = await RequestLayerSnapshotAsync(
                transport,
                helloResult.SessionId,
                cancellationToken)
            .ConfigureAwait(false);
        TimeSpan duration = Stopwatch.GetElapsedTime(started);

        LayerTelemetryApplyResult layerApplyResult = layerTracker.Apply(snapshot);
        if (layerApplyResult != LayerTelemetryApplyResult.AppliedSnapshot)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} StateSnapshot could not initialize layer state: {layerApplyResult}.");
        }

        uint batteryRequestId = CreateRequestId();
        ProtocolMessage.GetBatteryRequest getBattery = new(helloResult.SessionId, batteryRequestId);
        ReadOnlyMemory<byte> batteryBytes = await transport
            .ExchangeAsync(ProtocolPacketCodec.Encode(getBattery), cancellationToken)
            .ConfigureAwait(false);
        duration = Stopwatch.GetElapsedTime(started);
        if (!ProtocolPacketCodec.TryDecode(batteryBytes.Span, out ProtocolMessage? decodedBattery) ||
            decodedBattery is not ProtocolMessage.BatterySnapshot batterySnapshot)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} response to GetBattery was not a valid BatterySnapshot.");
        }

        if (batterySnapshot.SessionId != helloResult.SessionId || batterySnapshot.RequestId != batteryRequestId)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} BatterySnapshot did not match its session and request.");
        }

        BatteryTelemetryApplyResult batteryApplyResult = batteryTracker.Apply(batterySnapshot);
        if (batteryApplyResult != BatteryTelemetryApplyResult.AppliedSnapshot)
        {
            throw new InvalidDataException(
                $"The {transport.Kind} BatterySnapshot could not initialize battery state: {batteryApplyResult}.");
        }

        return new SessionSnapshot(
            helloResult.SessionId,
            layerTracker.CurrentState!,
            batteryTracker.CurrentState!,
            duration);
    }

    private static IKeyboardTransport CreateTransport(TransportKind kind)
    {
        return kind switch
        {
            TransportKind.Usb => new UsbSerialKeyboardTransport(),
            TransportKind.Bluetooth => new BluetoothGattKeyboardTransport(),
            _ => throw new InvalidOperationException($"Unsupported transport kind: {kind}."),
        };
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

    private static string FormatBattery(BatteryReading reading)
    {
        return reading.Status switch
        {
            BatteryReadingStatus.Unavailable => "unavailable",
            BatteryReadingStatus.Fresh => $"{reading.Level}%",
            BatteryReadingStatus.Stale => $"{reading.Level}% (stale)",
            _ => throw new InvalidOperationException($"Unsupported battery status: {reading.Status}."),
        };
    }

    private sealed record SessionSnapshot(
        uint SessionId,
        LayerTelemetryState LayerState,
        BatteryTelemetryState BatteryState,
        TimeSpan Duration);

    private sealed record CommandExchange(
        ProtocolMessage Request,
        ProtocolMessage.CommandResult Result);
}
