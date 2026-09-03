namespace ShinyGo60.Builder.Core.Workspaces;

public static class BuilderInstallationLocator
{
    public static string FindRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        DirectoryInfo? directory = new(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
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
