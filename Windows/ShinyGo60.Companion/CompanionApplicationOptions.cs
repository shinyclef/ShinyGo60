using System.IO;

namespace ShinyGo60.Companion;

internal sealed record CompanionApplicationOptions(
    string ManifestPath,
    string ConfigurationPath,
    bool StartInBackground)
{
    public static CompanionApplicationOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool startInBackground = args.Length > 0 && string.Equals(args[0], "--background", StringComparison.Ordinal);
        int pathArgumentOffset = startInBackground ? 1 : 0;
        int pathArgumentCount = args.Length - pathArgumentOffset;
        if (pathArgumentCount == 0)
        {
            string applicationDirectory = AppContext.BaseDirectory;
            return new CompanionApplicationOptions(
                Path.Combine(applicationDirectory, "layout-manifest.json"),
                Path.Combine(applicationDirectory, "companion-settings.json"),
                startInBackground);
        }

        if (pathArgumentCount != 2)
        {
            throw new ArgumentException(
                "Usage: ShinyGo60.Companion [--background] <layout-manifest.json> <companion-settings.json>");
        }

        return new CompanionApplicationOptions(
            Path.GetFullPath(args[pathArgumentOffset]),
            Path.GetFullPath(args[pathArgumentOffset + 1]),
            startInBackground);
    }
}
