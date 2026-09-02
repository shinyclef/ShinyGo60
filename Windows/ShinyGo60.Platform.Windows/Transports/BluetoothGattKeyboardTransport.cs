using ShinyGo60.Protocol.Messages;
using ShinyGo60.Protocol.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace ShinyGo60.Platform.Windows.Transports;

public sealed class BluetoothGattKeyboardTransport : IKeyboardTransport, IKeyboardTransportConnectionEvents
{
    private static readonly Guid ServiceId = new("5A9C0000-7F76-4C2A-9C46-9B7317F6A1E0");
    private static readonly Guid MessageCharacteristicId = new("5A9C0001-7F76-4C2A-9C46-9B7317F6A1E0");

    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattCharacteristic? characteristic;
    private TaskCompletionSource<ReadOnlyMemory<byte>>? pendingResponse;
    private int connectionLostRaised;
    private int stopping;

    public event EventHandler<KeyboardPacketReceivedEventArgs>? PacketReceived;

    public event EventHandler<KeyboardTransportConnectionLostEventArgs>? ConnectionLost;

    public TransportKind Kind => TransportKind.Bluetooth;

    public bool IsConnected =>
        this.device is not null && this.characteristic is not null && Volatile.Read(ref this.connectionLostRaised) == 0;

    public string? DeviceName => this.device?.Name;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (this.device is not null)
        {
            throw new InvalidOperationException("The Bluetooth transport is already open.");
        }

        Volatile.Write(ref this.connectionLostRaised, 0);
        Volatile.Write(ref this.stopping, 0);
        string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        DeviceInformationCollection candidates = await DeviceInformation.FindAllAsync(selector)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        int pairedCandidateCount = 0;
        List<string> discoveryOutcomes = [];

        foreach (DeviceInformation candidate in candidates)
        {
            pairedCandidateCount++;
            BluetoothLEDevice? candidateDevice = await BluetoothLEDevice.FromIdAsync(candidate.Id)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (candidateDevice is null)
            {
                discoveryOutcomes.Add("device open failed");
                continue;
            }

            GattDeviceServicesResult services = await candidateDevice
                .GetGattServicesForUuidAsync(ServiceId, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count != 1)
            {
                discoveryOutcomes.Add($"{services.Status} ({services.Services.Count} matching services)");
                foreach (GattDeviceService unusableService in services.Services)
                {
                    unusableService.Dispose();
                }

                candidateDevice.Dispose();
                if (services.Status == GattCommunicationStatus.Success && services.Services.Count > 1)
                {
                    throw new InvalidOperationException("A paired Go60 exposes more than one ShinyGo60 Bluetooth service.");
                }

                continue;
            }

            if (this.device is not null)
            {
                foreach (GattDeviceService duplicateService in services.Services)
                {
                    duplicateService.Dispose();
                }

                candidateDevice.Dispose();
                await this.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("More than one paired Go60 exposes the ShinyGo60 Bluetooth service.");
            }

            this.device = candidateDevice;
            this.service = services.Services[0];
        }

        if (this.device is null || this.service is null)
        {
            if (pairedCandidateCount == 0)
            {
                throw new InvalidOperationException("Windows reports no paired Bluetooth LE devices.");
            }

            string outcomeSummary = string.Join(", ", discoveryOutcomes);
            throw new InvalidOperationException(
                $"Windows reports {pairedCandidateCount} paired Bluetooth LE device(s), but none exposed the ShinyGo60 service. " +
                $"Discovery results: {outcomeSummary}.");
        }

        this.device.ConnectionStatusChanged += this.OnConnectionStatusChanged;
        GattCharacteristicsResult characteristics = await this.service
            .GetCharacteristicsForUuidAsync(MessageCharacteristicId, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count != 1)
        {
            await this.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The paired Go60 does not expose one usable ShinyGo60 message characteristic.");
        }

        GattCharacteristic selectedCharacteristic = characteristics.Characteristics[0];
        GattCharacteristicProperties requiredProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.Indicate;
        if ((selectedCharacteristic.CharacteristicProperties & requiredProperties) != requiredProperties)
        {
            await this.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The ShinyGo60 Bluetooth characteristic has incompatible properties.");
        }

        this.characteristic = selectedCharacteristic;
        this.characteristic.ValueChanged += this.OnValueChanged;
        GattCommunicationStatus subscriptionStatus = await this.characteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Indicate)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (subscriptionStatus != GattCommunicationStatus.Success)
        {
            await this.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Windows could not enable encrypted ShinyGo60 indications: {subscriptionStatus}.");
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        if (!this.IsConnected || this.characteristic is null)
        {
            throw new InvalidOperationException("The Bluetooth transport is disconnected.");
        }

        if (request.Length != ProtocolPacketCodec.PacketSize)
        {
            throw new ArgumentException($"A protocol packet must be {ProtocolPacketCodec.PacketSize} bytes.", nameof(request));
        }

        TaskCompletionSource<ReadOnlyMemory<byte>> responseSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref this.pendingResponse, responseSource, null) is not null)
        {
            throw new InvalidOperationException("A Bluetooth exchange is already in progress.");
        }

        try
        {
            using DataWriter writer = new();
            writer.WriteBytes(request.ToArray());
            GattWriteResult writeResult = await this.characteristic
                .WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (writeResult.Status != GattCommunicationStatus.Success)
            {
                throw new InvalidOperationException($"The encrypted Bluetooth write failed: {writeResult.Status}.");
            }

            return await responseSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref this.pendingResponse, null, responseSource);
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref this.stopping, 1);
        TaskCompletionSource<ReadOnlyMemory<byte>>? responseSource = Interlocked.Exchange(ref this.pendingResponse, null);
        responseSource?.TrySetException(new IOException("The Bluetooth transport disconnected."));

        BluetoothLEDevice? activeDevice = this.device;
        if (activeDevice is not null)
        {
            activeDevice.ConnectionStatusChanged -= this.OnConnectionStatusChanged;
        }

        GattCharacteristic? activeCharacteristic = this.characteristic;
        this.characteristic = null;
        if (activeCharacteristic is not null)
        {
            activeCharacteristic.ValueChanged -= this.OnValueChanged;
        }

        try
        {
            if (activeCharacteristic is not null)
            {
                await activeCharacteristic
                    .WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            this.service?.Dispose();
            this.service = null;
            activeDevice?.Dispose();
            this.device = null;
            Volatile.Write(ref this.connectionLostRaised, 0);
            Volatile.Write(ref this.stopping, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // Local resources are released in DisconnectAsync even if remote subscription cleanup fails.
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        _ = args;
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            this.RaiseConnectionLost(new IOException("The Go60 Bluetooth connection was lost."));
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        _ = sender;
        using DataReader reader = DataReader.FromBuffer(args.CharacteristicValue);
        byte[] value = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(value);
        this.DispatchPacket(value);
    }

    private void DispatchPacket(ReadOnlyMemory<byte> packet)
    {
        if (ProtocolPacketCodec.TryReadHeader(packet.Span, out _, out ProtocolMessageType type) &&
            type is ProtocolMessageType.LayerChanged or ProtocolMessageType.BatteryChanged)
        {
            this.PacketReceived?.Invoke(this, new KeyboardPacketReceivedEventArgs(packet));
            return;
        }

        this.pendingResponse?.TrySetResult(packet);
    }

    private void RaiseConnectionLost(Exception cause)
    {
        if (Volatile.Read(ref this.stopping) == 0 && Interlocked.Exchange(ref this.connectionLostRaised, 1) == 0)
        {
            TaskCompletionSource<ReadOnlyMemory<byte>>? responseSource = Interlocked.Exchange(ref this.pendingResponse, null);
            responseSource?.TrySetException(cause);
            this.ConnectionLost?.Invoke(this, new KeyboardTransportConnectionLostEventArgs(cause));
        }
    }
}
