namespace ShinyGo60.Builder.Core.Workspaces;

public sealed record BuildWorkspaceLayout(
    string InputDirectory,
    string GeneratedDirectory,
    string OutputDirectory)
{
    public static BuildWorkspaceLayout FromRepositoryRoot(string repositoryRoot)
    {
        string absoluteRoot = Path.GetFullPath(repositoryRoot);

        return new(
            Path.Combine(absoluteRoot, "Input"),
            Path.Combine(absoluteRoot, "Custom Firmware", "Generated"),
            Path.Combine(absoluteRoot, "Output"));
    }
}
