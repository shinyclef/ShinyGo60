using ShinyGo60.Protocol.Messages;
using ShinyGo60.Protocol.Transport;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;

namespace ShinyGo60.Platform.Windows.Transports;

public sealed class UsbSerialKeyboardTransport : IKeyboardTransport, IKeyboardTransportConnectionEvents
{
    private const ushort Go60VendorId = 0x16C0;
    private const ushort Go60LeftProductId = 0x27DB;

    private SerialDevice? device;
    private DataReader? reader;
    private DataWriter? writer;
    private CancellationTokenSource? readCancellation;
    private Task? readTask;
    private TaskCompletionSource<ReadOnlyMemory<byte>>? pendingResponse;
    private int connectionLostRaised;
    private int stopping;

    public event EventHandler<KeyboardPacketReceivedEventArgs>? PacketReceived;

    public event EventHandler<KeyboardTransportConnectionLostEventArgs>? ConnectionLost;

    public TransportKind Kind => TransportKind.Usb;

    public bool IsConnected => this.device is not null && Volatile.Read(ref this.connectionLostRaised) == 0;

    public string? DeviceName { get; private set; }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (this.device is not null)
        {
            throw new InvalidOperationException("The USB transport is already open.");
        }

        Volatile.Write(ref this.connectionLostRaised, 0);
        Volatile.Write(ref this.stopping, 0);
        string selector = SerialDevice.GetDeviceSelectorFromUsbVidPid(Go60VendorId, Go60LeftProductId);
        DeviceInformationCollection candidates = await DeviceInformation.FindAllAsync(selector)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No Go60 left-side USB CDC endpoint was found.");
        }

        if (candidates.Count > 1)
        {
            string names = string.Join(", ", candidates.Select(candidate => candidate.Name));
            throw new InvalidOperationException($"More than one Go60 USB CDC endpoint was found: {names}.");
        }

        DeviceInformation candidate = candidates[0];
        SerialDevice? openedDevice = await SerialDevice.FromIdAsync(candidate.Id)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (openedDevice is null)
        {
            throw new InvalidOperationException("Windows found the Go60 USB CDC endpoint but could not open it.");
        }

        openedDevice.BaudRate = 115200;
        openedDevice.DataBits = 8;
        openedDevice.Parity = SerialParity.None;
        openedDevice.StopBits = SerialStopBitCount.One;
        openedDevice.Handshake = SerialHandshake.None;
        openedDevice.IsDataTerminalReadyEnabled = true;

        this.device = openedDevice;
        this.reader = new DataReader(openedDevice.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
        this.writer = new DataWriter(openedDevice.OutputStream);
        this.readCancellation = new CancellationTokenSource();
        this.readTask = this.ReadPacketsAsync(this.reader, this.readCancellation.Token);
        this.DeviceName = candidate.Name;
    }

    public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        if (!this.IsConnected || this.reader is null || this.writer is null)
        {
            throw new InvalidOperationException("The USB transport is disconnected.");
        }

        if (request.Length != ProtocolPacketCodec.PacketSize)
        {
            throw new ArgumentException($"A protocol packet must be {ProtocolPacketCodec.PacketSize} bytes.", nameof(request));
        }

        TaskCompletionSource<ReadOnlyMemory<byte>> responseSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref this.pendingResponse, responseSource, null) is not null)
        {
            throw new InvalidOperationException("A USB exchange is already in progress.");
        }

        try
        {
            this.writer.WriteBytes(request.ToArray());
            await this.writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
            return await responseSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref this.pendingResponse, null, responseSource);
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref this.stopping, 1);

        TaskCompletionSource<ReadOnlyMemory<byte>>? responseSource = Interlocked.Exchange(ref this.pendingResponse, null);
        responseSource?.TrySetException(new IOException("The USB transport disconnected."));

        CancellationTokenSource? activeReadCancellation = this.readCancellation;
        this.readCancellation = null;
        Task? activeReadTask = this.readTask;
        this.readTask = null;
        activeReadCancellation?.Cancel();
        if (activeReadTask is not null)
        {
            await activeReadTask.ConfigureAwait(false);
        }

        activeReadCancellation?.Dispose();
        this.writer?.Dispose();
        this.writer = null;
        this.reader?.Dispose();
        this.reader = null;
        this.device?.Dispose();
        this.device = null;
        this.DeviceName = null;
        Volatile.Write(ref this.connectionLostRaised, 0);
        Volatile.Write(ref this.stopping, 0);
    }

    public ValueTask DisposeAsync()
    {
        return this.DisconnectAsync();
    }

    private async Task ReadPacketsAsync(DataReader activeReader, CancellationToken cancellationToken)
    {
        byte[] packet = new byte[ProtocolPacketCodec.PacketSize];
        int packetLength = 0;

        try
        {
            while (true)
            {
                uint loaded = await activeReader
                    .LoadAsync((uint)(packet.Length - packetLength))
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                if (loaded == 0)
                {
                    throw new EndOfStreamException("The Go60 USB CDC endpoint closed while receiving protocol data.");
                }

                int readLength = Math.Min(checked((int)loaded), packet.Length - packetLength);
                byte[] chunk = new byte[readLength];
                activeReader.ReadBytes(chunk);
                chunk.CopyTo(packet, packetLength);
                packetLength += readLength;

                if (packetLength == packet.Length)
                {
                    this.DispatchPacket(packet);
                    packet = new byte[ProtocolPacketCodec.PacketSize];
                    packetLength = 0;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            TaskCompletionSource<ReadOnlyMemory<byte>>? responseSource = Interlocked.Exchange(ref this.pendingResponse, null);
            responseSource?.TrySetException(exception);
            this.RaiseConnectionLost(exception);
        }
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
            this.ConnectionLost?.Invoke(this, new KeyboardTransportConnectionLostEventArgs(cause));
        }
    }
}
