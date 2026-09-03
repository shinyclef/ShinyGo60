using System.Globalization;
using ShinyGo60.Builder.Core.Build;
using ShinyGo60.Builder.Core.Processes;

namespace ShinyGo60.BuildTool;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        BuildToolOptions options;
        try
        {
            options = BuildToolOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintUsage();
            return 2;
        }

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            FirmwareBuildRequest request = PinnedFirmwareBuild.CreateRequest(
                options.RepositoryRoot,
                options.KeymapPath,
                options.GeneratedDirectory,
                options.OutputDirectory) with
            {
                AllowNetwork = options.AllowNetwork,
            };

            Console.WriteLine("Building ShinyGo60 firmware. The keyboard does not need to be connected.");
            Console.WriteLine(options.AllowNetwork ? "Container network access: enabled." : "Container network access: disabled.");

            FirmwareBuildPipeline pipeline = new(new SystemProcessRunner());
            FirmwareBuildResult result = await pipeline.BuildAsync(request, cancellationToken: cancellation.Token);

            Console.WriteLine("Firmware build succeeded.");
            Console.WriteLine($"Output: {result.OutputSetDirectory}");
            Console.WriteLine($"UF2: {result.Uf2Path}");
            Console.WriteLine($"Manifest: {result.ManifestPath}");
            Console.WriteLine($"Log: {result.LogPath}");
            Console.WriteLine($"Layout ID: {result.LayoutIdentifier}");
            Console.WriteLine($"UF2 SHA-256: {result.Uf2Sha256}");
            Console.WriteLine($"Duration: {result.Duration.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)} seconds");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Firmware build canceled. No successful output set was published.");
            return 130;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine($"Keymap or firmware validation failed: {exception.Message}");
            return 3;
        }
        catch (FirmwareBuildException exception)
        {
            Console.Error.WriteLine(exception.Message);
            if (exception.FailureLogPath is not null)
            {
                Console.Error.WriteLine($"Failure log: {exception.FailureLogPath}");
            }

            return 4;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"File or environment error: {exception.Message}");
            return 5;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Permission error: {exception.Message}");
            return 5;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  ShinyGo60.BuildTool <layout.keymap> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --repository <path>  ShinyGo60 project root; defaults to the current project.");
        Console.WriteLine("  --generated <path>   Disposable workspace root; defaults to Custom Firmware/Generated.");
        Console.WriteLine("  --output <path>      Successful output-set root; defaults to Output.");
        Console.WriteLine("  --allow-network      Allow container networking; normal cached builds keep it disabled.");
    }
}
