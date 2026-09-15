using System.Diagnostics;
using System.Globalization;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Protocol.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace ShinyGo60.Platform.Windows.Transports;

public sealed class BluetoothGattKeyboardTransport : IKeyboardTransport, IKeyboardTransportConnectionEvents, IKeyboardConnectionHistory
{
    private static readonly Guid ServiceId = new("5A9C0000-7F76-4C2A-9C46-9B7317F6A1E0");
    private static readonly Guid MessageCharacteristicId = new("5A9C0001-7F76-4C2A-9C46-9B7317F6A1E0");
    private static readonly Guid HistoryCharacteristicId = new("5A9C0002-7F76-4C2A-9C46-9B7317F6A1E0");
    private static readonly Guid HistoryInfoCharacteristicId = new("5A9C0003-7F76-4C2A-9C46-9B7317F6A1E0");
    private static readonly Guid HistoryContextCharacteristicId = new("5A9C0004-7F76-4C2A-9C46-9B7317F6A1E0");

    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattCharacteristic? characteristic;
    private GattCharacteristic? historyCharacteristic;
    private GattCharacteristic? historyInfoCharacteristic;
    private GattCharacteristic? historyContextCharacteristic;
    private bool historyDiscovered;
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
        long started = Stopwatch.GetTimestamp();
        try
        {
            await this.ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            this.AddFailureDetails(exception, "connect", started);
            throw;
        }
    }

    private async ValueTask ConnectCoreAsync(CancellationToken cancellationToken)
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
                discoveryOutcomes.Add(
                    $"{services.Status}, ATT {FormatProtocolError(services.ProtocolError)} ({services.Services.Count} matching services)");
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
            InvalidOperationException failure = characteristics.Status != GattCommunicationStatus.Success
                ? CreateGattFailure("discover_characteristic", characteristics.Status, characteristics.ProtocolError)
                : new InvalidOperationException($"The paired Go60 exposes {characteristics.Characteristics.Count} message characteristics; expected one.");
            failure.Data["connectionStatus"] = this.device.ConnectionStatus.ToString();
            await this.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            throw failure;
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
        GattWriteResult subscriptionResult = await this.characteristic
            .WriteClientCharacteristicConfigurationDescriptorWithResultAsync(GattClientCharacteristicConfigurationDescriptorValue.Indicate)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (subscriptionResult.Status != GattCommunicationStatus.Success)
        {
            InvalidOperationException failure = CreateGattFailure("subscribe", subscriptionResult.Status, subscriptionResult.ProtocolError);
            failure.Data["connectionStatus"] = this.device.ConnectionStatus.ToString();
            await this.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            throw failure;
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

        long started = Stopwatch.GetTimestamp();
        string operation = "write";
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
                throw CreateGattFailure(operation, writeResult.Status, writeResult.ProtocolError);
            }

            operation = "wait_for_indication";
            return await responseSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            this.AddFailureDetails(exception, operation, started);
            throw;
        }
        finally
        {
            Interlocked.CompareExchange(ref this.pendingResponse, null, responseSource);
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadConnectionHistoryAsync(
        ConnectionHistoryPosition after, CancellationToken cancellationToken = default)
    {
        GattDeviceService activeService = this.service ?? throw new InvalidOperationException("The Bluetooth transport is disconnected.");
        if (!this.historyDiscovered)
        {
            GattCharacteristicsResult result = await activeService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (result.Status != GattCommunicationStatus.Success)
            {
                throw CreateGattFailure("discover_history", result.Status, result.ProtocolError);
            }

            this.historyInfoCharacteristic = result.Characteristics.SingleOrDefault(value => value.Uuid == HistoryInfoCharacteristicId);
            this.historyContextCharacteristic = result.Characteristics.SingleOrDefault(value => value.Uuid == HistoryContextCharacteristicId);
            this.historyCharacteristic = result.Characteristics.SingleOrDefault(value => value.Uuid == HistoryCharacteristicId);
            this.historyDiscovered = true;
        }

        if (this.historyInfoCharacteristic is null)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        byte[] discovery = await ReadHistoryValueAsync(this.historyInfoCharacteristic, cancellationToken).ConfigureAwait(false);
        if (discovery.Length != 20 || discovery[0] != ConnectionHistory.FormatVersion || discovery[1] != ConnectionHistory.RecordSize)
        {
            throw new NotSupportedException("This firmware exposes an unsupported connection history format.");
        }

        if (this.historyCharacteristic is null || this.historyContextCharacteristic is null)
        {
            throw new InvalidDataException("The firmware connection history service is incomplete.");
        }

        using DataWriter writer = new() { ByteOrder = ByteOrder.LittleEndian };
        writer.WriteUInt32(after.BootId);
        writer.WriteUInt32(after.CriticalSequence);
        writer.WriteUInt32(after.RoutineSequence);
        GattWriteResult start = await this.historyCharacteristic.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (start.Status != GattCommunicationStatus.Success)
        {
            throw CreateGattFailure("start_history", start.Status, start.ProtocolError);
        }

        byte[] info = await ReadHistoryValueAsync(this.historyInfoCharacteristic, cancellationToken).ConfigureAwait(false);
        byte[] context = await ReadHistoryValueAsync(this.historyContextCharacteristic, cancellationToken).ConfigureAwait(false);
        if (info.Length != 20 || context.Length != 20)
        {
            throw new InvalidDataException("The connection history headers are incomplete.");
        }

        using MemoryStream snapshot = new();
        snapshot.Write(info);
        snapshot.Write(context);
        int capacity = info[2] + info[3];
        try
        {
            for (int index = 0; index <= capacity; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] record = await ReadHistoryValueAsync(this.historyCharacteristic, cancellationToken).ConfigureAwait(false);
                if (record.Length == 0)
                {
                    return snapshot.ToArray();
                }

                if (index == capacity || record.Length != ConnectionHistory.RecordSize)
                {
                    throw new InvalidDataException("The connection history exceeded its advertised capacity or returned an invalid record.");
                }

                snapshot.Write(record);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && snapshot.Length > ConnectionHistory.HeaderSize)
        {
            // Save complete records already received. The next snapshot resumes from their persisted positions.
            return snapshot.ToArray();
        }

        throw new InvalidDataException("The connection history did not end.");
    }

    private static async Task<byte[]> ReadHistoryValueAsync(GattCharacteristic characteristic, CancellationToken cancellationToken)
    {
        GattReadResult read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (read.Status != GattCommunicationStatus.Success)
        {
            throw CreateGattFailure("read_history", read.Status, read.ProtocolError);
        }

        using DataReader reader = DataReader.FromBuffer(read.Value);
        byte[] bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        return bytes;
    }
    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref this.stopping, 1);
        this.historyCharacteristic = null;
        this.historyInfoCharacteristic = null;
        this.historyContextCharacteristic = null;
        this.historyDiscovered = false;
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

    private static string FormatProtocolError(byte? error) => error.HasValue ? $"0x{error.Value:X2}" : "unavailable";

    private static InvalidOperationException CreateGattFailure(string operation, GattCommunicationStatus status, byte? protocolError)
    {
        InvalidOperationException exception = new($"Bluetooth {operation} failed: {status}; ATT {FormatProtocolError(protocolError)}.");
        exception.Data["operation"] = operation;
        exception.Data["gattStatus"] = status.ToString();
        exception.Data["attError"] = FormatProtocolError(protocolError);
        return exception;
    }

    private void AddFailureDetails(Exception exception, string operation, long started)
    {
        if (!exception.Data.Contains("operation"))
        {
            exception.Data["operation"] = operation;
        }

        exception.Data["transportElapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
        if (!exception.Data.Contains("connectionStatus"))
        {
            exception.Data["connectionStatus"] = this.device?.ConnectionStatus.ToString() ?? "unavailable";
        }
    }
}
