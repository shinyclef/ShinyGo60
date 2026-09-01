using ShinyGo60.Protocol.Messages;
using ShinyGo60.Protocol.Transport;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;

namespace ShinyGo60.TransportSpike;

internal sealed class UsbSerialKeyboardTransport : IKeyboardTransport
{
    private const ushort Go60VendorId = 0x16C0;
    private const ushort Go60LeftProductId = 0x27DB;

    private SerialDevice? device;
    private DataReader? reader;
    private DataWriter? writer;

    public TransportKind Kind => TransportKind.Usb;

    public bool IsConnected => this.device is not null;

    public string? DeviceName { get; private set; }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (this.IsConnected)
        {
            throw new InvalidOperationException("The USB transport is already connected.");
        }

        string selector = SerialDevice.GetDeviceSelectorFromUsbVidPid(Go60VendorId, Go60LeftProductId);
        DeviceInformationCollection candidates = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken).ConfigureAwait(false);

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
        SerialDevice? openedDevice = await SerialDevice.FromIdAsync(candidate.Id).AsTask(cancellationToken).ConfigureAwait(false);
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
        this.DeviceName = candidate.Name;
    }

    public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        if (this.device is null || this.reader is null || this.writer is null)
        {
            throw new InvalidOperationException("The USB transport is disconnected.");
        }

        if (request.Length != HelloMessageCodec.PacketSize)
        {
            throw new ArgumentException($"A Hello packet must be {HelloMessageCodec.PacketSize} bytes.", nameof(request));
        }

        this.writer.WriteBytes(request.ToArray());
        await this.writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);

        byte[] response = new byte[HelloMessageCodec.PacketSize];
        int responseLength = 0;
        while (responseLength < response.Length)
        {
            uint loaded = await this.reader.LoadAsync((uint)(response.Length - responseLength))
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (loaded == 0)
            {
                throw new EndOfStreamException("The Go60 USB CDC endpoint closed before returning a complete response.");
            }

            int readLength = Math.Min(checked((int)loaded), response.Length - responseLength);
            byte[] chunk = new byte[readLength];
            this.reader.ReadBytes(chunk);
            chunk.CopyTo(response, responseLength);
            responseLength += readLength;
        }

        return response;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        this.writer?.Dispose();
        this.writer = null;
        this.reader?.Dispose();
        this.reader = null;
        this.device?.Dispose();
        this.device = null;
        this.DeviceName = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return this.DisconnectAsync();
    }
}
