using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ShinyGo60.Protocol.Manifests;

namespace ShinyGo60.Builder.Core.Keymaps;

public static partial class Go60KeymapInspector
{
    private const int Go60BindingCount = 60;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async ValueTask<KeymapInspection> InspectAsync(
        string keymapPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keymapPath);

        string sourcePath = Path.GetFullPath(keymapPath);
        if (!string.Equals(Path.GetExtension(sourcePath), ".keymap", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{sourcePath}' is not a .keymap file.");
        }

        byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        return Inspect(sourcePath, sourceBytes);
    }

    public static KeymapInspection Inspect(string keymapPath, byte[] sourceBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keymapPath);
        ArgumentNullException.ThrowIfNull(sourceBytes);

        if (sourceBytes.Length == 0)
        {
            throw new InvalidDataException("The keymap is empty. Export the complete layout from the Go60 Layout Editor.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(sourceBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The keymap is not valid UTF-8. Export it again from the Go60 Layout Editor.", exception);
        }

        if (!ExportSignatureRegex().IsMatch(text))
        {
            throw new InvalidDataException("The keymap does not contain the Go60 Layout Editor export marker.");
        }

        string uncommentedText = MaskComments(text);
        if (!Go60TypeDefinitionRegex().IsMatch(uncommentedText) || !Go60TypeSelectionRegex().IsMatch(uncommentedText))
        {
            throw new InvalidDataException("The keymap does not identify itself as a Go60 export.");
        }

        string keymapBlock = ExtractKeymapBlock(uncommentedText);
        List<string> layerNames = ExtractLayerNodes(keymapBlock);
        Dictionary<string, int> generatedLayerIds = ExtractGeneratedLayerIds(uncommentedText);

        if (generatedLayerIds.Count != layerNames.Count)
        {
            throw new InvalidDataException(
                $"The export defines {generatedLayerIds.Count} layers but its keymap contains {layerNames.Count}. Export the complete layout again.");
        }

        LayerDefinition[] layers = new LayerDefinition[layerNames.Count];
        for (int index = 0; index < layerNames.Count; index++)
        {
            string name = layerNames[index];
            if (!generatedLayerIds.TryGetValue(name, out int id))
            {
                throw new InvalidDataException($"Keymap layer '{name}' has no matching generated LAYER_{name} definition.");
            }

            if (id != index)
            {
                throw new InvalidDataException(
                    $"Keymap layer '{name}' is in position {index} but its generated numeric ID is {id}. Export the layout again.");
            }

            layers[index] = new LayerDefinition(id, name);
        }

        byte[] retainedSourceBytes = sourceBytes.ToArray();
        string keymapSha256 = Convert.ToHexString(SHA256.HashData(retainedSourceBytes)).ToLowerInvariant();
        return new KeymapInspection(Path.GetFullPath(keymapPath), keymapSha256, layers, retainedSourceBytes);
    }

    private static string MaskComments(string text)
    {
        char[] masked = text.ToCharArray();
        ScanState state = ScanState.Normal;

        for (int index = 0; index < masked.Length; index++)
        {
            char current = masked[index];
            char next = index + 1 < masked.Length ? masked[index + 1] : '\0';

            switch (state)
            {
                case ScanState.Normal when current == '/' && next == '/':
                    masked[index] = ' ';
                    masked[++index] = ' ';
                    state = ScanState.LineComment;
                    break;
                case ScanState.Normal when current == '/' && next == '*':
                    masked[index] = ' ';
                    masked[++index] = ' ';
                    state = ScanState.BlockComment;
                    break;
                case ScanState.Normal when current == '"':
                    state = ScanState.String;
                    break;
                case ScanState.Normal when current == '\'':
                    state = ScanState.Character;
                    break;
                case ScanState.LineComment when current is '\r' or '\n':
                    state = ScanState.Normal;
                    break;
                case ScanState.LineComment:
                    masked[index] = ' ';
                    break;
                case ScanState.BlockComment when current == '*' && next == '/':
                    masked[index] = ' ';
                    masked[++index] = ' ';
                    state = ScanState.Normal;
                    break;
                case ScanState.BlockComment:
                    if (current is not ('\r' or '\n'))
                    {
                        masked[index] = ' ';
                    }

                    break;
                case ScanState.String when current == '\\' && next != '\0':
                    index++;
                    break;
                case ScanState.String when current == '"':
                    state = ScanState.Normal;
                    break;
                case ScanState.Character when current == '\\' && next != '\0':
                    index++;
                    break;
                case ScanState.Character when current == '\'':
                    state = ScanState.Normal;
                    break;
            }
        }

        if (state is ScanState.BlockComment or ScanState.String or ScanState.Character)
        {
            throw new InvalidDataException("The keymap contains an unterminated comment or quoted value.");
        }

        return new string(masked);
    }

    private static string ExtractKeymapBlock(string text)
    {
        string? keymapBlock = null;

        foreach (Match match in KeymapNodeRegex().Matches(text))
        {
            int openBrace = text.IndexOf('{', match.Index, match.Length);
            int closeBrace = FindMatchingBrace(text, openBrace);
            if (closeBrace < 0)
            {
                throw new InvalidDataException("The keymap node is incomplete; its closing brace is missing.");
            }

            string candidate = text[openBrace..(closeBrace + 1)];
            if (!CompatibleKeymapRegex().IsMatch(candidate))
            {
                continue;
            }

            if (keymapBlock is not null)
            {
                throw new InvalidDataException("The export contains more than one zmk,keymap node.");
            }

            keymapBlock = candidate;
        }

        return keymapBlock
            ?? throw new InvalidDataException("The export does not contain a complete zmk,keymap node.");
    }

    private static List<string> ExtractLayerNodes(string keymapBlock)
    {
        List<string> layerNames = [];
        HashSet<string> uniqueNames = new(StringComparer.Ordinal);

        foreach (Match match in LayerNodeRegex().Matches(keymapBlock))
        {
            if (GetBraceDepth(keymapBlock, match.Index) != 1)
            {
                continue;
            }

            string name = match.Groups["name"].Value;
            if (!uniqueNames.Add(name))
            {
                throw new InvalidDataException($"The keymap contains more than one layer_{name} node.");
            }

            int openBrace = keymapBlock.IndexOf('{', match.Index, match.Length);
            int closeBrace = FindMatchingBrace(keymapBlock, openBrace);
            if (closeBrace < 0)
            {
                throw new InvalidDataException($"Layer '{name}' is incomplete; its closing brace is missing.");
            }

            string layerBlock = keymapBlock[openBrace..(closeBrace + 1)];
            Match bindings = BindingsRegex().Match(layerBlock);
            if (!bindings.Success)
            {
                throw new InvalidDataException($"Layer '{name}' does not contain a complete bindings property.");
            }

            int bindingCount = BindingReferenceRegex().Count(bindings.Groups["bindings"].Value);
            if (bindingCount != Go60BindingCount)
            {
                throw new InvalidDataException(
                    $"Layer '{name}' contains {bindingCount} bindings; a complete Go60 layer must contain {Go60BindingCount}.");
            }

            layerNames.Add(name);
        }

        if (layerNames.Count == 0)
        {
            throw new InvalidDataException("The zmk,keymap node does not contain any generated layer nodes.");
        }

        return layerNames;
    }

    private static Dictionary<string, int> ExtractGeneratedLayerIds(string text)
    {
        Dictionary<string, int> layerIds = new(StringComparer.Ordinal);
        HashSet<int> numericIds = [];

        foreach (Match match in LayerDefinitionRegex().Matches(text))
        {
            string name = match.Groups["name"].Value;
            if (IsFallbackDefinition(text, match.Index, name))
            {
                continue;
            }

            int id = int.Parse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (!layerIds.TryAdd(name, id))
            {
                throw new InvalidDataException($"The export defines LAYER_{name} more than once.");
            }

            if (!numericIds.Add(id))
            {
                throw new InvalidDataException($"The export assigns numeric layer ID {id} more than once.");
            }
        }

        return layerIds;
    }

    private static bool IsFallbackDefinition(string text, int definitionIndex, string name)
    {
        int definitionLineStart = text.LastIndexOf('\n', Math.Max(0, definitionIndex - 1)) + 1;
        int previousLineEnd = definitionLineStart - 1;
        if (previousLineEnd <= 0)
        {
            return false;
        }

        int previousLineStart = text.LastIndexOf('\n', previousLineEnd - 1) + 1;
        string previousLine = text[previousLineStart..previousLineEnd].Trim();
        return string.Equals(previousLine, $"#ifndef LAYER_{name}", StringComparison.Ordinal);
    }

    private static int FindMatchingBrace(string text, int openBrace)
    {
        int depth = 0;
        ScanState state = ScanState.Normal;

        for (int index = openBrace; index < text.Length; index++)
        {
            char current = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (state == ScanState.String)
            {
                if (current == '\\' && next != '\0')
                {
                    index++;
                }
                else if (current == '"')
                {
                    state = ScanState.Normal;
                }

                continue;
            }

            if (state == ScanState.Character)
            {
                if (current == '\\' && next != '\0')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    state = ScanState.Normal;
                }

                continue;
            }

            if (current == '"')
            {
                state = ScanState.String;
            }
            else if (current == '\'')
            {
                state = ScanState.Character;
            }
            else if (current == '{')
            {
                depth++;
            }
            else if (current == '}' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int GetBraceDepth(string text, int endIndex)
    {
        int depth = 0;
        ScanState state = ScanState.Normal;

        for (int index = 0; index < endIndex; index++)
        {
            char current = text[index];
            char next = index + 1 < endIndex ? text[index + 1] : '\0';

            if (state == ScanState.String)
            {
                if (current == '\\' && next != '\0')
                {
                    index++;
                }
                else if (current == '"')
                {
                    state = ScanState.Normal;
                }

                continue;
            }

            if (state == ScanState.Character)
            {
                if (current == '\\' && next != '\0')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    state = ScanState.Normal;
                }

                continue;
            }

            if (current == '"')
            {
                state = ScanState.String;
            }
            else if (current == '\'')
            {
                state = ScanState.Character;
            }
            else if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
            }
        }

        return depth;
    }

    [GeneratedRegex(@"\bGENERATED\s+BY\s+(?:THE\s+)?GO60\s+LAYOUT\s+EDITOR\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExportSignatureRegex();

    [GeneratedRegex(@"(?m)^[ \t]*#[ \t]*define[ \t]+KB_TYPE_GO_60[ \t]+[0-9]+[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Go60TypeDefinitionRegex();

    [GeneratedRegex(@"(?m)^[ \t]*#[ \t]*define[ \t]+KB_TYPE[ \t]+KB_TYPE_GO_60[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Go60TypeSelectionRegex();

    [GeneratedRegex(@"(?m)^[ \t]*(?:[A-Za-z_][A-Za-z0-9_]*[ \t]*:[ \t]*)?keymap\s*\{")]
    private static partial Regex KeymapNodeRegex();

    [GeneratedRegex(@"\bcompatible\s*=\s*\""zmk,keymap\""\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex CompatibleKeymapRegex();

    [GeneratedRegex(@"(?m)^[ \t]*layer_(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{")]
    private static partial Regex LayerNodeRegex();

    [GeneratedRegex(@"\bbindings\s*=\s*<(?<bindings>.*?)>\s*;", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BindingsRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])&[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.CultureInvariant)]
    private static partial Regex BindingReferenceRegex();

    [GeneratedRegex(
        @"(?m)^[ \t]*#[ \t]*define[ \t]+LAYER_(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]+(?<id>0|[1-9][0-9]*)[ \t]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LayerDefinitionRegex();

    private enum ScanState
    {
        Normal,
        LineComment,
        BlockComment,
        String,
        Character,
    }
}
