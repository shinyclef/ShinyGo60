using ShinyGo60.Builder.Core.Workspaces;

namespace ShinyGo60.BuildTool;

internal sealed record BuildToolOptions(
    string RepositoryRoot,
    string KeymapPath,
    string GeneratedDirectory,
    string OutputDirectory,
    bool AllowNetwork)
{
    public static BuildToolOptions Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || arguments[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Pass the path to one exported Go60 .keymap file.");
        }

        string currentDirectory = Environment.CurrentDirectory;
        string keymapPath = Path.GetFullPath(arguments[0], currentDirectory);
        string? repositoryRoot = null;
        string? generatedDirectory = null;
        string? outputDirectory = null;
        bool allowNetwork = false;

        for (int index = 1; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--repository":
                    repositoryRoot = ReadPathOption(arguments, ref index, currentDirectory);
                    break;
                case "--generated":
                    generatedDirectory = ReadPathOption(arguments, ref index, currentDirectory);
                    break;
                case "--output":
                    outputDirectory = ReadPathOption(arguments, ref index, currentDirectory);
                    break;
                case "--allow-network":
                    allowNetwork = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arguments[index]}'.");
            }
        }

        repositoryRoot ??= FindRepositoryRoot(currentDirectory);
        BuildWorkspaceLayout layout = BuildWorkspaceLayout.FromRepositoryRoot(repositoryRoot);
        return new BuildToolOptions(
            repositoryRoot,
            keymapPath,
            generatedDirectory ?? layout.GeneratedDirectory,
            outputDirectory ?? layout.OutputDirectory,
            allowNetwork);
    }

    private static string ReadPathOption(IReadOnlyList<string> arguments, ref int index, string baseDirectory)
    {
        if (++index >= arguments.Count)
        {
            throw new ArgumentException($"Option '{arguments[index - 1]}' requires a path.");
        }

        return Path.GetFullPath(arguments[index], baseDirectory);
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        DirectoryInfo? directory = new(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Custom Firmware", "BuildSupport")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new ArgumentException("Could not find the ShinyGo60 repository. Run from the project folder or pass --repository <path>.");
    }
}
