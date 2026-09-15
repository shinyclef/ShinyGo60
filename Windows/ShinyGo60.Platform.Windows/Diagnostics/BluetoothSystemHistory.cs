using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using ShinyGo60.Companion.Core.Diagnostics;

namespace ShinyGo60.Platform.Windows.Diagnostics;

public static class BluetoothSystemHistory
{
    public static async Task<IReadOnlyList<BluetoothSystemEvent>> ReadAsync(CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new("wevtutil.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in new[] { "qe", "System", "/q:*[System[Provider[@Name='BTHUSB'] and " +
            "TimeCreated[timediff(@SystemTime) <= 604800000]]]", "/f:xml", "/e:Events", "/c:512", "/rd:true" })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new IOException("Windows event query did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new IOException("Windows Bluetooth events are unavailable: " + await error.ConfigureAwait(false));
            }

            string xml = await output.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(xml))
            {
                return [];
            }

            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            return XDocument.Parse(xml).Descendants(ns + "Event").Select(element =>
            {
                XElement system = element.Element(ns + "System")!;
                return new BluetoothSystemEvent(
                    DateTimeOffset.Parse(system.Element(ns + "TimeCreated")!.Attribute("SystemTime")!.Value, CultureInfo.InvariantCulture),
                    int.Parse(system.Element(ns + "EventID")!.Value, CultureInfo.InvariantCulture),
                    system.Element(ns + "Provider")!.Attribute("Name")!.Value);
            }).ToArray();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
