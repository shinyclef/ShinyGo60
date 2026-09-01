using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
            foreach (TransportKind kind in options.GetTransportSequence())
            {
                await RunTransportAsync(kind, options).ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return 1;
        }
    }

    private static async Task RunTransportAsync(TransportKind kind, SpikeOptions options)
    {
        await using IKeyboardTransport transport = CreateTransport(kind);
        using CancellationTokenSource connectTimeout = new(options.Timeout);

        Console.WriteLine($"Connecting over {kind}...");
        await transport.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        Console.WriteLine($"Connected over {kind}.");

        TimeSpan[] durations = new TimeSpan[options.ExchangeCount];
        for (uint sequence = 1; sequence <= options.ExchangeCount; sequence++)
        {
            uint challenge = CreateChallenge();
            HelloMessage request = new(HelloMessageCodec.CurrentVersion, HelloMessageType.Hello, sequence, challenge);
            byte[] requestBytes = HelloMessageCodec.Encode(request);

            using CancellationTokenSource exchangeTimeout = new(options.Timeout);
            long started = Stopwatch.GetTimestamp();
            ReadOnlyMemory<byte> responseBytes = await transport
                .ExchangeAsync(requestBytes, exchangeTimeout.Token)
                .ConfigureAwait(false);
            durations[sequence - 1] = Stopwatch.GetElapsedTime(started);

            if (!HelloMessageCodec.TryDecode(responseBytes.Span, out HelloMessage response) ||
                response.Type != HelloMessageType.HelloResult || response.Sequence != sequence ||
                response.Challenge != challenge)
            {
                throw new InvalidDataException($"The {kind} response did not match request {sequence}.");
            }

            Console.WriteLine($"  Hello {sequence}: {durations[sequence - 1].TotalMilliseconds:F2} ms");
        }

        await transport.DisconnectAsync().ConfigureAwait(false);
        double average = durations.Average(duration => duration.TotalMilliseconds);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{kind} summary: count={durations.Length}, min={durations.Min().TotalMilliseconds:F2} ms, " +
                $"mean={average:F2} ms, max={durations.Max().TotalMilliseconds:F2} ms"));
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

    private static uint CreateChallenge()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
