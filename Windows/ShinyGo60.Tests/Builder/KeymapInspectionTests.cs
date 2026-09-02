using System.Text;
using ShinyGo60.Builder.Core.Keymaps;
using ShinyGo60.Builder.Core.Manifests;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Builder;

internal static class KeymapInspectionTests
{
    private const string FirmwareRevision = "11454d23596afbdb06380a1125371b19ab65675c";

    private static readonly ProtocolVersion ProtocolVersion = ShinyGo60.Protocol.ProtocolVersion.Current;

    private static readonly string[] ExpectedCurrentLayers =
    [
        "Home",
        "NoHRM",
        "Qwerty",
        "Navigation",
        "Keypad",
        "Shortcuts",
        "WindowsAndSymbols",
        "Gaming",
        "GamingShortcuts",
        "Magic",
        "Mouse",
        "MouseSlow",
        "MouseFast",
        "MouseWarp",
        "LeftPinky",
        "LeftRingy",
        "LeftMiddy",
        "LeftIndex",
        "RightPinky",
        "RightRingy",
        "RightMiddy",
        "RightIndex",
    ];

    public static async ValueTask RunAsync()
    {
        await VerifyCurrentKeymapAsync();
        await VerifyLayerReorderingAsync();
        await VerifyFormattingVariationAsync();
        VerifyMalformedExportFailure();
        VerifyAmbiguousLayerFailure();
        VerifyInvalidUtf8Failure();
    }

    private static async ValueTask VerifyCurrentKeymapAsync()
    {
        string repositoryRoot = FindRepositoryRoot();
        string keymapPath = Path.Combine(
            repositoryRoot,
            "Key Configuration",
            "TailorKey v4.2m⁶ Bilateral - Gallium - Shinyclef.keymap");
        byte[] sourceBeforeGeneration = await File.ReadAllBytesAsync(keymapPath);
        KeymapInspection inspection = await Go60KeymapInspector.InspectAsync(keymapPath);

        AssertEx.Equal(ExpectedCurrentLayers.Length, inspection.Layers.Count);
        for (int index = 0; index < ExpectedCurrentLayers.Length; index++)
        {
            AssertEx.Equal(index, inspection.Layers[index].Id);
            AssertEx.Equal(ExpectedCurrentLayers[index], inspection.Layers[index].Name);
        }

        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"ShinyGo60-Step7-{Guid.NewGuid():N}");
        string destination = Path.Combine(temporaryRoot, "生成 output with spaces");

        try
        {
            DateTimeOffset builtAtUtc = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
            LayoutArtifacts artifacts = await LayoutArtifactGenerator.GenerateAsync(
                keymapPath,
                destination,
                ProtocolVersion,
                FirmwareRevision,
                builtAtUtc);

            byte[] copiedKeymap = await File.ReadAllBytesAsync(artifacts.KeymapPath);
            byte[] sourceAfterGeneration = await File.ReadAllBytesAsync(keymapPath);
            AssertEx.SequenceEqual(sourceBeforeGeneration, copiedKeymap);
            AssertEx.SequenceEqual(sourceBeforeGeneration, sourceAfterGeneration);

            LayoutManifest manifest = await LayoutManifestJson.ReadAsync(artifacts.ManifestPath);
            AssertEx.Equal(LayoutManifest.CurrentSchemaVersion, manifest.SchemaVersion);
            AssertEx.Equal(ProtocolVersion, manifest.ProtocolVersion);
            AssertEx.Equal(inspection.KeymapSha256, manifest.KeymapSha256);
            AssertEx.Equal(LayoutIdentity.Create(ProtocolVersion, sourceBeforeGeneration), manifest.LayoutIdentifier);
            AssertEx.Equal(FirmwareRevision, manifest.FirmwareRevision);
            AssertEx.Equal(builtAtUtc, manifest.BuiltAtUtc);
            AssertEx.Equal(ExpectedCurrentLayers.Length, manifest.Layers.Count);

            string firmwareHeader = await File.ReadAllTextAsync(artifacts.FirmwareHeaderPath);
            AssertEx.True(
                firmwareHeader.Contains($"\"{manifest.LayoutIdentifier}\"", StringComparison.Ordinal),
                "The firmware header should contain the manifest layout identifier.");
            AssertEx.True(
                firmwareHeader.Contains($"\"{manifest.KeymapSha256}\"", StringComparison.Ordinal),
                "The firmware header should contain the exact keymap hash.");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static async ValueTask VerifyLayerReorderingAsync()
    {
        string firstPath = FixturePath("LayerOrderA.keymap");
        string reorderedPath = FixturePath("LayerOrderB.keymap");
        KeymapInspection first = await Go60KeymapInspector.InspectAsync(firstPath);
        KeymapInspection reordered = await Go60KeymapInspector.InspectAsync(reorderedPath);

        AssertEx.Equal("Home", first.Layers[0].Name);
        AssertEx.Equal("Navigation", reordered.Layers[0].Name);

        string firstIdentifier = LayoutIdentity.Create(ProtocolVersion, first.SourceBytes.Span);
        string reorderedIdentifier = LayoutIdentity.Create(ProtocolVersion, reordered.SourceBytes.Span);
        AssertEx.True(
            !string.Equals(firstIdentifier, reorderedIdentifier, StringComparison.Ordinal),
            "Reordering layers should change the layout identifier.");

        string nextProtocolIdentifier = LayoutIdentity.Create(new ProtocolVersion(1, 1), first.SourceBytes.Span);
        AssertEx.True(
            !string.Equals(firstIdentifier, nextProtocolIdentifier, StringComparison.Ordinal),
            "Changing the protocol version should change the layout identifier.");
    }

    private static async ValueTask VerifyFormattingVariationAsync()
    {
        KeymapInspection inspection = await Go60KeymapInspector.InspectAsync(FixturePath("FutureFormatting.keymap"));
        AssertEx.Equal(1, inspection.Layers.Count);
        AssertEx.Equal(new LayerDefinition(0, "Base"), inspection.Layers[0]);
    }

    private static void VerifyMalformedExportFailure()
    {
        string path = FixturePath("MalformedBindingCount.keymap");
        byte[] bytes = File.ReadAllBytes(path);
        InvalidDataException exception = AssertEx.Throws<InvalidDataException>(
            () => Go60KeymapInspector.Inspect(path, bytes));

        AssertEx.True(
            exception.Message.Contains("60", StringComparison.Ordinal),
            "A truncated Go60 layer should report the required binding count.");
    }

    private static void VerifyInvalidUtf8Failure()
    {
        string path = Path.Combine(Path.GetTempPath(), "invalid.keymap");
        InvalidDataException exception = AssertEx.Throws<InvalidDataException>(
            () => Go60KeymapInspector.Inspect(path, [0xC3, 0x28]));

        AssertEx.True(
            exception.Message.Contains("UTF-8", StringComparison.Ordinal),
            "Invalid text should produce an actionable UTF-8 error.");
    }

    private static void VerifyAmbiguousLayerFailure()
    {
        string path = FixturePath("LayerOrderA.keymap");
        string validText = File.ReadAllText(path);
        string ambiguousText = validText.Replace(
            "#define LAYER_Navigation 1",
            "#define LAYER_Navigation 0",
            StringComparison.Ordinal);
        InvalidDataException exception = AssertEx.Throws<InvalidDataException>(
            () => Go60KeymapInspector.Inspect(path, Encoding.UTF8.GetBytes(ambiguousText)));

        AssertEx.True(
            exception.Message.Contains("numeric layer ID 0", StringComparison.Ordinal),
            "Duplicate numeric layer IDs should produce an actionable error.");
    }

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "Keymaps", fileName);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DEVELOPMENT_PLAN.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the ShinyGo60 repository root.");
    }
}
