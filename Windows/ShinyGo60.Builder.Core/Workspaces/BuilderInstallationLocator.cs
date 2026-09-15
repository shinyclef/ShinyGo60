namespace ShinyGo60.Builder.Core.Workspaces;

public static class BuilderInstallationLocator
{
    public static string FindRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        DirectoryInfo? directory = new(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            string sourceFile = Path.Combine(directory.FullName, "firmware-source.txt");
            if (File.Exists(sourceFile))
            {
                string sourceRoot = File.ReadAllText(sourceFile).Trim();
                if (!Path.IsPathFullyQualified(sourceRoot))
                {
                    throw new InvalidDataException("firmware-source.txt must contain the full path to the Go60 workspace.");
                }

                sourceRoot = Path.GetFullPath(sourceRoot);
                if (!HasRequiredBuildFiles(sourceRoot))
                {
                    throw new DirectoryNotFoundException(
                        $"The configured Go60 firmware workspace is unavailable: {sourceRoot}. Restore it or update firmware-source.txt.");
                }

                return sourceRoot;
            }

            if (HasRequiredBuildFiles(directory.FullName))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "The ShinyGo60 firmware support files could not be found. Keep the builder inside its supplied folder.");
    }

    private static bool HasRequiredBuildFiles(string root)
    {
        return File.Exists(Path.Combine(
                root,
                "Custom Firmware",
                "BuildSupport",
                "Templates",
                "v25.11",
                "config",
                "default.nix")) &&
            File.Exists(Path.Combine(root, "Custom Firmware", "Module", "zephyr", "module.yml"));
    }
}
