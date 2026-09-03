namespace ShinyGo60.Builder.Core.Keymaps;

public static class KeymapInputFinder
{
    public static IReadOnlyList<string> FindCandidates(string inputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);

        string absoluteDirectory = Path.GetFullPath(inputDirectory);
        if (!Directory.Exists(absoluteDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(absoluteDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".keymap", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string ValidateSelection(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string absolutePath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(absolutePath), ".keymap", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose a MoErgo-exported file with the .keymap extension.");
        }

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("The selected keymap file no longer exists.", absolutePath);
        }

        return absolutePath;
    }
}
