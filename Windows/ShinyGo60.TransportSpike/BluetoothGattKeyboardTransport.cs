using ShinyGo60.Protocol.Messages;
using ShinyGo60.Protocol.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace ShinyGo60.TransportSpike;

internal sealed class BluetoothGattKeyboardTransport : IKeyboardTransport
{
    private static readonly Guid ServiceId = new("5A9C0000-7F76-4C2A-9C46-9B7317F6A1E0");
    private static readonly Guid MessageCharacteristicId = new("5A9C0001-7F76-4C2A-9C46-9B7317F6A1E0");

    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattCharacteristic? characteristic;
    private TaskCompletionSource<ReadOnlyMemory<byte>>? pendingResponse;

    public event EventHandler<KeyboardPacketReceivedEventArgs>? PacketReceived;

    public TransportKind Kind => TransportKind.Bluetooth;

    public bool IsConnected => this.device is not null && this.characteristic is not null;

    public string? DeviceName => this.device?.Name;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (this.IsConnected)
        {
            throw new InvalidOperationException("The Bluetooth transport is already connected.");
        }

        string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        DeviceInformationCollection candidates = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken).ConfigureAwait(false);
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
        if (this.characteristic is null)
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
        TaskCompletionSource<ReadOnlyMemory<byte>>? responseSource = Interlocked.Exchange(ref this.pendingResponse, null);
        responseSource?.TrySetException(new IOException("The Bluetooth transport disconnected."));

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
            this.device?.Dispose();
            this.device = null;
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
            // DisconnectAsync has already released local resources. A remote CCC cleanup failure is non-fatal during disposal.
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
            type == ProtocolMessageType.LayerChanged)
        {
            this.PacketReceived?.Invoke(this, new KeyboardPacketReceivedEventArgs(packet));
            return;
        }

        this.pendingResponse?.TrySetResult(packet);
    }
}
